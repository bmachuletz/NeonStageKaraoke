from __future__ import annotations
import math
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
    vocal_onset_conflicts = 0
    word_overlap_conflicts = 0
    compressed_word_runs = 0
    for index, line in enumerate(lines):
        reasons: list[str] = []
        words = line.words
        if not words:
            reasons.append("keine Wortzeitstempel")
        else:
            starts = [float(w["start"]) for w in words]
            ends = [float(w["end"]) for w in words]
            if words[0].get("window_edge_fallback"):
                reasons.append("erstes Wort liegt unbestätigt am Analysefensterrand")
            if words[0].get("stage_vocal_onset_conflict_ms") is not None:
                reasons.append("Zeileneinsatz liegt außerhalb der Stage-Vocalspur")
                vocal_onset_conflicts += 1
            if any(b < a for a, b in zip(starts, starts[1:])):
                reasons.append("Wortzeiten nicht monoton")
            overlaps = [
                round((float(left["end"]) - float(right["start"])) * 1000, 1)
                for left, right in zip(words, words[1:])
                if float(left["end"]) > float(right["start"]) + 0.001
            ]
            if overlaps:
                reasons.append("Wörter überlappen innerhalb der Zeile")
                word_overlap_conflicts += len(overlaps)
            if any(e < s for s, e in zip(starts, ends)):
                reasons.append("negativer Wortzeitraum")
            if sum(e - s < 0.03 for s, e in zip(starts, ends)) >= max(
                    3, math.ceil(len(words) / 3)):
                reasons.append("zu viele Wörter ohne messbare Dauer")
            # Short function words are normal in fast singing. Treat them as a
            # decoder-collapse only when two sub-frame words are also packed
            # together without an audible inter-word gap.
            short = [end - start <= 0.055 for start, end in zip(starts, ends)]
            local_compressed_runs = sum(
                left and right and float(words[index + 1]["start"]) -
                float(words[index]["end"]) <= 0.04
                for index, (left, right) in enumerate(zip(short, short[1:])))
            if local_compressed_runs:
                reasons.append("aufeinanderfolgende Wörter unplausibel komprimiert")
                compressed_word_runs += local_compressed_runs
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
            lane = max(0, int(getattr(line, "voice_lane", 0)))
            next_line = next((candidate for candidate in lines[index + 1:]
                              if max(0, int(getattr(candidate, "voice_lane", 0))) == lane), None)
            if next_line is not None:
                limit = next_line.timestamp + cfg.post_roll + 0.75
                next_words = next_line.words
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
            previous_line = next((candidate for candidate in reversed(lines[:index])
                                  if max(0, int(getattr(candidate, "voice_lane", 0))) == lane), None)
            if (previous_line is not None and previous_line.words
                    and starts[0] < float(previous_line.words[-1]["end"]) - 0.001):
                previous_end = float(previous_line.words[-1]["end"])
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
        (word.get("timing_source") in heuristic_sources
         or bool(word.get("window_edge_fallback")))
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
    compression_penalty = min(0.12, compressed_word_runs / max(1, total_words) * 0.50)
    score = round(100.0 * max(0.0,
                              0.45 * line_ratio + 0.25 * coverage + 0.30 * acoustic_coverage
                              - repair_penalty - compression_penalty), 1)
    trustworthy_coverage = acoustic_coverage >= 0.90
    if (score >= 92 and uncertain == 0 and trustworthy_coverage
            and not line_overlaps and vocal_onset_conflicts == 0
            and word_overlap_conflicts == 0):
        grade = "excellent"
    elif (score >= 80 and uncertain <= max(1, len(lines) // 20)
          and trustworthy_coverage and vocal_onset_conflicts == 0
          and word_overlap_conflicts == 0):
        grade = "good"
    elif score >= 65:
        grade = "review"
    else:
        grade = "reject"
    return {
        "lines": len(lines), "ok": ok, "uncertain": uncertain,
        "quality": {
            "score": score, "grade": grade,
            "publishable": (grade in {"excellent", "good"} and not line_overlaps
                            and vocal_onset_conflicts == 0
                            and word_overlap_conflicts == 0),
            "word_duration_coverage": round(coverage, 4),
            "acoustically_aligned_word_coverage": round(acoustic_coverage, 4),
            "heuristically_placed_words": heuristic_words,
            "geometrically_repaired_lines": final_repaired_lines,
            "line_overlap_conflicts": len(line_overlaps),
            "stage_vocal_onset_conflicts": vocal_onset_conflicts,
            "word_overlap_conflicts": word_overlap_conflicts,
            "compressed_word_runs": compressed_word_runs,
        },
        "line_overlaps": line_overlaps,
    }
