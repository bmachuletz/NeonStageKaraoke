from __future__ import annotations
from .models import AlignmentConfig, LrcLine
from .consensus import ACOUSTIC_SOURCES


VERIFIED_ACOUSTIC_SOURCES = ACOUSTIC_SOURCES - {None}
TIMESTAMP_OVERRIDE_SOURCES = VERIFIED_ACOUSTIC_SOURCES | {
    "vocal-activity-repair", "anchor-tail-vocal-activity", "lrc-chorus-anchor",
    "instrumental-chorus-stable-ts",
}


def _all_words_from(words: list[dict], sources: set) -> bool:
    return bool(words) and all(
        word.get("timing_source") in sources for word in words
    )


def _first_word_from(words: list[dict], sources: set) -> bool:
    return bool(words) and words[0].get("timing_source") in sources


def validate(lines: list[LrcLine], cfg: AlignmentConfig, *, repaired_lines: int = 0) -> dict:
    ok = 0
    uncertain = 0
    line_overlaps: list[dict] = []
    for index, line in enumerate(lines):
        reasons: list[str] = []
        words = line.words
        if not words:
            reasons.append("keine Wortzeitstempel")
        else:
            starts = [float(w["start"]) for w in words]
            ends = [float(w["end"]) for w in words]
            if any(b < a for a, b in zip(starts, starts[1:])):
                reasons.append("Wortzeiten nicht monoton")
            if any(e < s for s, e in zip(starts, ends)):
                reasons.append("negativer Wortzeitraum")
            if sum(e - s < 0.03 for s, e in zip(starts, ends)) >= max(2, len(words) // 4):
                reasons.append("zu viele Wörter ohne messbare Dauer")
            if any(e - s > cfg.max_word_duration for s, e in zip(starts, ends)):
                reasons.append("unplausibel langes Wort")
            reference = line.source_timestamp
            acoustically_located = _first_word_from(words, TIMESTAMP_OVERRIDE_SOURCES)
            acoustically_verified = _all_words_from(words, VERIFIED_ACOUSTIC_SOURCES)
            # LRCLIB line timestamps are search hints, not ground truth. A bounded
            # vocal-energy placement may legitimately find the phrase later.
            if (reference is not None and abs(starts[0] - reference) > cfg.max_start_deviation
                    and not acoustically_located):
                reasons.append("erstes Wort zu weit vom LRC-Zeitpunkt entfernt")
            if index + 1 < len(lines):
                limit = lines[index + 1].timestamp + cfg.post_roll + 0.75
                next_words = lines[index + 1].words
                # Lead, backing vocals and echoed refrains can overlap by
                # design.  Accept this only when both lines have complete
                # acoustic evidence; an LRC hint or geometric interpolation
                # alone must never suppress the geometry warning.
                acoustic_overlap = (
                    acoustically_verified
                    and _all_words_from(next_words, VERIFIED_ACOUSTIC_SOURCES)
                )
                if ends[-1] > limit and not acoustic_overlap:
                    reasons.append("ragt stark in nächste Zeile")
            if index > 0 and lines[index - 1].words and starts[0] < float(lines[index - 1].words[-1]["end"]) - 0.001:
                previous_end = float(lines[index - 1].words[-1]["end"])
                overlap_ms = round((previous_end - starts[0]) * 1000, 1)
                line_overlaps.append({"previous_line": index, "next_line": index + 1,
                                      "overlap_ms": overlap_ms})
                reasons.append("überlappt vorherige Lyrics-Zeile")
        if reasons:
            line.status = "uncertain"
            line.reason = "; ".join(reasons)
            uncertain += 1
        else:
            line.status = "ok"
            ok += 1
    total_words = sum(len(line.words) for line in lines)
    measurable_words = sum(
        float(word["end"]) - float(word["start"]) >= 0.03
        for line in lines for word in line.words
    )
    heuristic_sources = {"vocal-activity-repair", "anchor-context-vocal-activity",
                         "anchor-tail-vocal-activity", "geometric-repair",
                         "lrc-chorus-anchor", "instrumental-chorus-stable-ts",
                         "stable-ts-interpolated", "overlap-display-lane-fallback"}
    heuristic_words = sum(
        word.get("timing_source") in heuristic_sources
        for line in lines for word in line.words
    )
    coverage = measurable_words / total_words if total_words else 0.0
    acoustic_coverage = (total_words - heuristic_words) / total_words if total_words else 0.0
    line_ratio = ok / len(lines) if lines else 0.0
    # Later acoustic passes (CTC, SOFA, EasyAligner, Stable-ts) can replace a
    # provisional geometric repair completely.  Penalising the historical
    # repair counter here made a successfully verified final line look
    # heuristic forever.  The quality gate must describe the emitted LRC.
    final_repaired_lines = sum(
        any(word.get("timing_source") == "geometric-repair" for word in line.words)
        for line in lines
    )
    repair_penalty = min(0.35, final_repaired_lines / max(1, len(lines)))
    score = round(100.0 * max(0.0,
                              0.45 * line_ratio + 0.25 * coverage + 0.30 * acoustic_coverage
                              - repair_penalty), 1)
    trustworthy_coverage = acoustic_coverage >= 0.90
    if score >= 92 and uncertain == 0 and trustworthy_coverage and not line_overlaps:
        grade = "excellent"
    elif score >= 80 and uncertain <= max(1, len(lines) // 20) and trustworthy_coverage:
        grade = "good"
    elif score >= 65:
        grade = "review"
    else:
        grade = "reject"
    return {
        "lines": len(lines), "ok": ok, "uncertain": uncertain,
        "quality": {
            "score": score, "grade": grade,
            "publishable": grade in {"excellent", "good"} and not line_overlaps,
            "word_duration_coverage": round(coverage, 4),
            "acoustically_aligned_word_coverage": round(acoustic_coverage, 4),
            "heuristically_placed_words": heuristic_words,
            "geometrically_repaired_lines": final_repaired_lines,
            "line_overlap_conflicts": len(line_overlaps),
        },
        "line_overlaps": line_overlaps,
    }
