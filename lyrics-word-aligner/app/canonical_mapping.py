from __future__ import annotations

import re
import statistics

from .transcript_match import compare_transcripts, normalize_words


TIME_STEP = 0.02


def _quantize(value: float) -> float:
    return round(round(max(0.0, float(value)) / TIME_STEP) * TIME_STEP, 3)


def _canonical_units(lines) -> tuple[list[dict], list[str]]:
    units: list[dict] = []
    normalized: list[str] = []
    for line_index, line in enumerate(lines):
        for display in re.findall(r"\S+", line.text):
            tokens = normalize_words(display)
            if not tokens:
                continue
            first = len(normalized)
            normalized.extend(tokens)
            units.append({"line_index": line_index, "display": display,
                          "first": first, "end": len(normalized)})
    return units, normalized


def assign_canonical_timings(lines, aligned_words: list[dict]) -> dict:
    """Map the one EasyAligner CTC path onto untouched display tokens."""
    units, expected = _canonical_units(lines)
    recognized: list[tuple[str, dict]] = []
    for word in aligned_words:
        for token in normalize_words(str(word.get("word", ""))):
            recognized.append((token, word))
    comparison = compare_transcripts(
        " ".join(expected), " ".join(token for token, _ in recognized))
    mapped = {
        int(operation["expected_index"]): recognized[int(operation["recognized_index"])][1]
        for operation in comparison["operations"]
        if operation["type"] in {"match", "approximate"}
        and "expected_index" in operation and "recognized_index" in operation
    }
    missing = [index for index in range(len(expected)) if index not in mapped]
    if missing:
        raise ValueError(
            f"EasyAligner-Zuordnung ist unvollständig: {len(missing)}/{len(expected)} "
            "kanonische Wörter besitzen keinen akustischen Zeitstempel.")

    for line in lines:
        line.words = []
    flat_words: list[dict] = []
    for unit in units:
        evidence = [mapped[index] for index in range(unit["first"], unit["end"])]
        start = _quantize(min(float(word["start"]) for word in evidence))
        end = _quantize(max(float(word.get("end", word["start"])) for word in evidence))
        item = {
            "word": unit["display"], "start": start, "end": max(start, end),
            "timing_source": "easyaligner-global-direct",
            "probability": round(statistics.mean(
                float(word.get("probability", 0.0)) for word in evidence), 4),
            "alignment_grid_ms": 20,
        }
        technical = [str(word["normalized"]).strip() for word in evidence
                     if str(word.get("normalized", "")).strip()]
        if technical:
            item["technical_text"] = " ".join(technical)
        lines[unit["line_index"]].words.append(item)
        flat_words.append(item)

    for current, following in zip(flat_words, flat_words[1:]):
        current["end"] = max(float(current["start"]), min(
            float(current["end"]), float(following["start"])))
    for line in lines:
        if not line.words:
            raise ValueError(f"Zeile ohne alignierbare Wörter: {line.text}")
        line.timestamp = float(line.words[0]["start"])
        line.status = "aligned"
        line.reason = None
    return {
        "canonical_words": len(expected), "display_words": len(units),
        "aligned_model_words": len(aligned_words),
        "mapping_similarity": comparison["similarity"],
        "mapping_edit_distance": comparison["edit_distance"], "time_grid_ms": 20,
    }
