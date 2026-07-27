from __future__ import annotations

import statistics

from .transcript_match import normalize_words


def calibrate_source_timestamps(lines: list, recognized_words: list[dict], comparison: dict,
                                *, minimum_lines: int = 4,
                                minimum_shift: float = 2.0) -> dict:
    """Correct a globally trimmed intro/version offset before forced alignment.

    LRCLIB timings can belong to the same recording with a trimmed intro.  A
    forced aligner constrained by those hints then searches every phrase in the
    wrong window.  Estimate one robust offset from independently timestamped
    ASR words spread over multiple lyric lines and move all input hints together.
    """
    timed_words: list[tuple[str, dict]] = []
    for word in recognized_words:
        for normalized in normalize_words(str(word.get("word", ""))):
            timed_words.append((normalized, word))
    operations = {
        int(operation["expected_index"]): operation
        for operation in comparison.get("operations", [])
        if operation.get("type") in {"match", "approximate"}
        and "expected_index" in operation and "recognized_index" in operation
    }
    candidates: list[dict] = []
    expected_cursor = 0
    for line_index, line in enumerate(lines):
        tokens = normalize_words(line.text)
        source = line.source_timestamp
        indices = range(expected_cursor, expected_cursor + len(tokens))
        expected_cursor += len(tokens)
        if source is None:
            continue
        mapped = [operations[index] for index in indices if index in operations]
        if len(mapped) < max(2, min(4, len(tokens))):
            continue
        recognized_indices = [int(operation["recognized_index"]) for operation in mapped]
        if recognized_indices != sorted(recognized_indices):
            continue
        first_index = recognized_indices[0]
        if first_index >= len(timed_words):
            continue
        detected = float(timed_words[first_index][1]["start"])
        candidates.append({
            "line": line_index + 1,
            "source": round(float(source), 3),
            "detected": round(detected, 3),
            "offset": detected - float(source),
            "matched_words": len(mapped),
        })
    if len(candidates) < minimum_lines:
        return {"applied": False, "reason": "zu-wenige-zeitanker",
                "candidate_lines": len(candidates), "method": "stable-ts-global-offset-v1"}
    offsets = [candidate["offset"] for candidate in candidates]
    median = statistics.median(offsets)
    deviations = [abs(offset - median) for offset in offsets]
    mad = statistics.median(deviations)
    tolerance = max(0.8, min(2.0, 3.0 * mad))
    inliers = [candidate for candidate in candidates
               if abs(candidate["offset"] - median) <= tolerance]
    if len(inliers) < minimum_lines:
        return {"applied": False, "reason": "zeitanker-nicht-konsistent",
                "candidate_lines": len(candidates), "inlier_lines": len(inliers),
                "median_offset": round(median, 3), "mad": round(mad, 3),
                "method": "stable-ts-global-offset-v1"}
    offset = statistics.median(candidate["offset"] for candidate in inliers)
    source_span = max(candidate["source"] for candidate in inliers) - min(
        candidate["source"] for candidate in inliers)
    if abs(offset) < minimum_shift:
        return {"applied": False, "reason": "kein-relevanter-globaler-versatz",
                "offset": round(offset, 3), "inlier_lines": len(inliers),
                "source_span": round(source_span, 3), "method": "stable-ts-global-offset-v1"}
    if source_span < 15.0:
        return {"applied": False, "reason": "zeitanker-decken-zu-wenig-audio-ab",
                "offset": round(offset, 3), "inlier_lines": len(inliers),
                "source_span": round(source_span, 3), "method": "stable-ts-global-offset-v1"}

    for line in lines:
        line.timestamp = round(float(line.timestamp) + offset, 3)
        if line.source_timestamp is not None:
            line.source_timestamp = round(float(line.source_timestamp) + offset, 3)
        for word in line.words:
            word["start"] = round(float(word["start"]) + offset, 3)
            word["end"] = round(float(word["end"]) + offset, 3)
    return {
        "applied": True,
        "offset": round(offset, 3),
        "candidate_lines": len(candidates),
        "inlier_lines": len(inliers),
        "source_span": round(source_span, 3),
        "mad": round(mad, 3),
        "anchors": [{**candidate, "offset": round(candidate["offset"], 3)}
                    for candidate in inliers[:20]],
        "method": "stable-ts-global-offset-v1",
    }
