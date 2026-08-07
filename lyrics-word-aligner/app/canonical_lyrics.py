from __future__ import annotations

import argparse
import json
import math
import re
from difflib import SequenceMatcher
from pathlib import Path

from .lrc import TIME_RE, parse_lrc, render_enhanced_lrc
from .transcript_match import compare_transcripts, normalize_words


_CLOCK = r"\d{1,3}:\d{2}(?:[.:]\d{1,3})?"
_TIMED_WORD_RE = re.compile(
    rf"<(?P<start>{_CLOCK}),(?P<end>{_CLOCK})>(?P<word>.*?)(?=(?:\s*<{_CLOCK},{_CLOCK}>)|$)"
)


def _seconds(value: str) -> float:
    minutes, seconds = value.split(":", 1)
    return int(minutes) * 60 + float(seconds.replace(":", ".", 1))


def parse_timed_words(path: str | Path) -> list[dict]:
    """Read Neon Stage enhanced-LRC word intervals without changing their text."""
    words: list[dict] = []
    for raw in Path(path).read_text(encoding="utf-8-sig").splitlines():
        for match in _TIMED_WORD_RE.finditer(TIME_RE.sub("", raw).strip()):
            text = match.group("word").strip()
            normalized = normalize_words(text)
            if not normalized:
                continue
            start = _seconds(match.group("start"))
            end = max(start + 0.02, _seconds(match.group("end")))
            # A rendered word normally maps to one normalized token. If an
            # imported enhanced LRC contains more, retain the measured window
            # by dividing it deterministically between those tokens.
            for index, token in enumerate(normalized):
                token_start = start + (end - start) * index / len(normalized)
                token_end = start + (end - start) * (index + 1) / len(normalized)
                words.append({
                    "word": token,
                    "start": round(token_start, 3),
                    "end": round(token_end, 3),
                })
    return words


def _interpolate_missing_starts(starts: list[float | None], source_starts: list[float]) -> None:
    known = [index for index, value in enumerate(starts) if value is not None]
    if not known:
        return
    for index, value in enumerate(starts):
        if value is not None:
            continue
        left = max((candidate for candidate in known if candidate < index), default=None)
        right = min((candidate for candidate in known if candidate > index), default=None)
        if left is not None and right is not None:
            source_span = source_starts[right] - source_starts[left]
            ratio = ((source_starts[index] - source_starts[left]) / source_span
                     if source_span > 0.001 else (index - left) / (right - left))
            starts[index] = float(starts[left]) + (float(starts[right]) - float(starts[left])) * ratio
        elif left is not None:
            starts[index] = float(starts[left]) + max(
                0.08, source_starts[index] - source_starts[left])
        elif right is not None:
            starts[index] = max(0.0, float(starts[right]) - max(
                0.08, source_starts[right] - source_starts[index]))


def _local_occurrence_anchor(tokens: list[str], acoustic_words: list[dict],
                             source_start: float, minimum_start: float, *,
                             search_radius: float = 4.0) -> dict | None:
    """Find this particular repeated line near its own LRC occurrence.

    Whole-song edit distance is intentionally still used for the global
    compatibility gate. It is ambiguous for identical choruses, though: one
    deletion can shift every later occurrence. This local pass gives each line
    a bounded temporal ownership window and compares short acoustic sequences
    only inside that window.
    """
    if not tokens:
        return None
    first_time = max(minimum_start, source_start - search_radius)
    last_time = source_start + search_radius
    minimum_length = max(1, len(tokens) - 3)
    maximum_length = len(tokens) + 3
    candidates: list[dict] = []
    normalized_acoustic = [normalize_words(str(word.get("word", "")))
                           for word in acoustic_words]
    normalized_acoustic = [parts[0] if parts else "" for parts in normalized_acoustic]
    for index, word in enumerate(acoustic_words):
        start = float(word["start"])
        if start < first_time or start > last_time:
            continue
        for length in range(minimum_length, maximum_length + 1):
            sequence = normalized_acoustic[index:index + length]
            if len(sequence) < minimum_length:
                continue
            lexical = SequenceMatcher(None, tokens, sequence).ratio()
            if lexical < 0.50:
                continue
            temporal = math.exp(-abs(start - source_start) / 1.5)
            first_word = SequenceMatcher(None, tokens[0], sequence[0]).ratio()
            score = 0.70 * lexical + 0.20 * temporal + 0.10 * first_word
            candidates.append({
                "start": start,
                "score": score,
                "lexical_similarity": lexical,
                "temporal_support": temporal,
                "first_word_similarity": first_word,
                "acoustic_index": index,
                "acoustic_words": length,
            })
    if not candidates:
        return None
    best = max(candidates, key=lambda item: (item["score"], item["lexical_similarity"],
                                             -abs(item["start"] - source_start)))
    return best if best["score"] >= 0.62 else None


def transfer_canonical_lines(headers: list[str], canonical_lines: list,
                             acoustic_words: list[dict], *, minimum_coverage: float = 0.55
                             ) -> tuple[list[str], list, dict]:
    """Place parsed canonical lyric lines on an acoustically measured word scaffold."""
    if not acoustic_words:
        raise ValueError("Das akustische LRC enthält keine Wortzeitfenster.")

    expected_text = " ".join(line.text for line in canonical_lines)
    recognized_text = " ".join(str(word["word"]) for word in acoustic_words)
    comparison = compare_transcripts(expected_text, recognized_text, min_repetitions=2)
    mapped = {
        int(operation["expected_index"]): operation
        for operation in comparison["operations"]
        if operation["type"] in {"match", "approximate"}
        and "recognized_index" in operation
    }
    expected_count = int(comparison["expected_words"])
    coverage = len(mapped) / max(1, expected_count)
    if coverage < minimum_coverage:
        raise ValueError(
            f"Kanonischer Text und Volltranskript passen nur zu {coverage:.1%} zusammen; "
            f"mindestens {minimum_coverage:.1%} sind erforderlich.")

    source_starts = [float(line.source_timestamp if line.source_timestamp is not None
                           else line.timestamp) for line in canonical_lines]
    proposed: list[float | None] = []
    local_anchors: list[dict] = []
    expected_cursor = 0
    directly_anchored = 0
    for line in canonical_lines:
        tokens = normalize_words(line.text)
        indices = range(expected_cursor, expected_cursor + len(tokens))
        anchors = [(offset, mapped[index]) for offset, index in enumerate(indices)
                   if index in mapped]
        expected_cursor += len(tokens)
        global_start = None
        if anchors:
            offset, operation = anchors[0]
            recognized_index = int(operation["recognized_index"])
            if recognized_index < len(acoustic_words):
                # When the first recognized token in a line is not its first canonical
                # token, leave a small local lead-in for the missing words. The regular
                # aligner will measure the exact word bounds inside this window.
                global_start = float(acoustic_words[recognized_index]["start"]) - min(
                    0.8, offset * 0.18)
        source_start = source_starts[len(proposed)]
        local = _local_occurrence_anchor(
            tokens, acoustic_words, source_start,
            (float(proposed[-1]) + 0.04 if proposed and proposed[-1] is not None else 0.0),
        )
        if local is not None:
            proposed.append(max(0.0, float(local["start"])))
            directly_anchored += 1
            local_anchors.append({
                "line": len(proposed),
                "source_start": round(source_start, 3),
                "selected_start": round(float(local["start"]), 3),
                "global_start": round(global_start, 3) if global_start is not None else None,
                "score": round(float(local["score"]), 4),
                "lexical_similarity": round(float(local["lexical_similarity"]), 4),
            })
        else:
            proposed.append(max(0.0, global_start) if global_start is not None else None)
            if global_start is not None:
                directly_anchored += 1

    _interpolate_missing_starts(proposed, source_starts)
    if any(value is None for value in proposed):
        raise ValueError("Die kanonischen Lyrics konnten nicht auf das Zeitgerüst verteilt werden.")

    previous = -0.04
    for line, value in zip(canonical_lines, proposed):
        start = max(0.0, float(value), previous + 0.04)
        line.timestamp = round(start, 3)
        line.source_timestamp = line.timestamp
        line.words = []
        line.status = "pending"
        line.reason = None
        line.timed_input = True
        previous = start

    report = {
        "method": "canonical-text-occurrence-aware-acoustic-scaffold-v2",
        "canonical_lines": len(canonical_lines),
        "canonical_words": expected_count,
        "acoustic_words": len(acoustic_words),
        "mapped_words": len(mapped),
        "mapping_coverage": round(coverage, 4),
        "directly_anchored_lines": directly_anchored,
        "locally_anchored_lines": len(local_anchors),
        "local_occurrence_anchors": local_anchors,
        "interpolated_lines": len(canonical_lines) - directly_anchored,
        "transcript_similarity": comparison["similarity"],
    }
    return headers, canonical_lines, report


def transfer_canonical_text(canonical_path: str | Path, acoustic_path: str | Path,
                            *, minimum_coverage: float = 0.55) -> tuple[list[str], list, dict]:
    """Read both LRC files and retain canonical text on the acoustic timing scaffold."""
    headers, canonical_lines = parse_lrc(canonical_path)
    acoustic_words = parse_timed_words(acoustic_path)
    return transfer_canonical_lines(
        headers, canonical_lines, acoustic_words, minimum_coverage=minimum_coverage)


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Transfer canonical lyric spelling onto acoustic full-transcription timing")
    parser.add_argument("--canonical", required=True, type=Path)
    parser.add_argument("--acoustic", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--minimum-coverage", type=float, default=0.55)
    args = parser.parse_args()

    headers, lines, report = transfer_canonical_text(
        args.canonical, args.acoustic, minimum_coverage=args.minimum_coverage)
    args.output.write_text(render_enhanced_lrc(headers, lines), encoding="utf-8")
    if args.report:
        args.report.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n",
                               encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))


if __name__ == "__main__":
    main()
