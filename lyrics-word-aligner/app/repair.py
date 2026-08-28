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
        if line.source_end_boundary is not None:
            next_timestamp = min(float(next_timestamp), float(line.source_end_boundary)) \
                if next_timestamp is not None else float(line.source_end_boundary)
        start = max(previous_end, min(starts[0], line.timestamp))
        # The next line is a hard ceiling, never a duration target.  Using it
        # as the target filled every instrumental pause after a collapsed line
        # with geometrically stretched words.  Retain the furthest measured
        # edge and add only the minimum space needed for a usable monotone run.
        observed_end = max(float(word["end"]) for word in line.words)
        minimum_end = start + len(line.words) * 0.08
        end = max(minimum_end, observed_end)
        if next_timestamp is not None:
            end = min(end, float(next_timestamp))
        # A corrupt input can put the following timestamp at/before this line.
        # Keep positive word geometry so the validator can report the actual
        # inter-line conflict instead of producing zero-duration words.
        end = max(start + len(line.words) * 0.03, end)
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


def constrain_final_words_to_source_boundaries(lines: list[LrcLine]) -> int:
    """Keep late sustain refinements inside explicit empty LRC markers.

    Phoneme/voicing analysis runs after the general geometry repair and may
    legitimately extend a held final vowel.  A timestamped empty LRC entry is
    stronger evidence, though: it explicitly ends the phrase before an
    instrumental gap.  Clamp only a final word that already starts before the
    marker; moving a complete word across the marker would hide a larger
    alignment conflict that should remain visible to the quality gate.
    """
    constrained = 0
    for line in lines:
        if not line.words or line.source_end_boundary is None:
            continue
        boundary = float(line.source_end_boundary)
        final = line.words[-1]
        start = float(final["start"])
        end = float(final["end"])
        if start < boundary < end:
            release_confidence = float(final.get("sustain_release_confidence", 0.0))
            measured_release = max(
                float(final.get("sustain_activity_end", end)),
                float(final.get("sustain_tonal_release", end)))
            # Empty LRC markers are useful priors, not infallible acoustic
            # truth. Preserve a clearly measured held release which continues
            # well beyond an inaccurate marker (e.g. a delayed final vowel).
            if (release_confidence >= 0.70
                    and end >= boundary + 0.35
                    and measured_release >= boundary + 0.20):
                final["source_boundary_overridden"] = round(boundary, 3)
                # The empty source marker has been acoustically disproved. If
                # it remains on the line, enhanced-LRC export emits a second
                # timestamp inside the held word and downstream document
                # validation correctly rejects the apparent line overlap.
                line.source_end_boundary = None
                continue
            final["end"] = round(max(start + 0.04, boundary), 3)
            final["source_boundary_trim_ms"] = round((end - boundary) * 1000, 1)
            constrained += 1
    return constrained
