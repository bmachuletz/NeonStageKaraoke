from __future__ import annotations
import math
from .models import AlignmentConfig, LrcLine


def repair_collapsed_timings(lines: list[LrcLine], cfg: AlignmentConfig) -> int:
    """Replace unusable repeated-word alignments with a monotonic line window."""
    repaired = 0
    previous_end = 0.0
    for index, line in enumerate(lines):
        if not line.words:
            continue
        if any(word.get("timing_source") == "asr-repetition-anchor" for word in line.words):
            previous_end = max(previous_end, float(line.words[-1]["end"]))
            continue
        durations = [float(word["end"]) - float(word["start"]) for word in line.words]
        collapsed = sum(duration < 0.03 for duration in durations)
        nonpositive = any(duration <= 0 for duration in durations)
        collapsed_threshold = max(3, math.ceil(len(durations) * 0.25))
        starts = [float(word["start"]) for word in line.words]
        internal_overlap = any(
            float(left["end"]) > float(right["start"]) + 0.005
            for left, right in zip(line.words, line.words[1:]))
        if (not nonpositive and collapsed < collapsed_threshold and not internal_overlap):
            previous_end = max(previous_end, float(line.words[-1]["end"]))
            continue

        next_timestamp = lines[index + 1].timestamp if index + 1 < len(lines) else None
        start = max(previous_end, min(starts[0], line.timestamp))
        if next_timestamp is not None:
            end = max(start + len(line.words) * 0.08, next_timestamp)
        else:
            end = max(start + len(line.words) * 0.12, max(float(word["end"]) for word in line.words))
        weights = [max(1, len(str(word["word"]))) for word in line.words]
        total_weight = sum(weights)
        cursor = start
        for word, weight in zip(line.words, weights):
            word_end = end if word is line.words[-1] else cursor + (end - start) * weight / total_weight
            word["start"] = round(cursor, 3)
            word["end"] = round(word_end, 3)
            word["timing_source"] = "geometric-repair"
            cursor = word_end
        previous_end = end
        repaired += 1
    return repaired
