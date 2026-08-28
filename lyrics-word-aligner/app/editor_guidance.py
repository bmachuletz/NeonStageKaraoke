from __future__ import annotations

import math
import statistics
from copy import deepcopy

from .transcript_match import normalize_words


def build_editor_guided_candidate(generated: list, editor_lines: list,
                                  manual_ranges: list[tuple[float, float]], *,
                                  maximum_influence_seconds: float = 45.0,
                                  maximum_shift_seconds: float = 1.5
                                  ) -> tuple[list, dict]:
    """Calibrate untouched lines from exact human-aligned neighbouring lines.

    This is deliberately a candidate generator, not an unconditional rewrite.
    The caller still has to compare the result with the vocal activity and the
    independent alignment candidates. Human lines themselves are never moved.
    """
    result = deepcopy(generated)
    summary = {
        "enabled": True,
        "method": "local-manual-residual-field-v1",
        "manual_anchor_lines": 0,
        "manual_anchor_words": 0,
        "guided_lines": 0,
        "lines": [],
    }
    if not manual_ranges or len(generated) != len(editor_lines):
        summary["enabled"] = False
        summary["reason"] = "no-compatible-manual-reference"
        return result, summary
    if [normalize_words(line.text) for line in generated] != [
            normalize_words(line.text) for line in editor_lines]:
        summary["enabled"] = False
        summary["reason"] = "canonical-text-changed"
        return result, summary

    anchors: list[dict] = []
    protected: set[int] = set()
    for line_index, (automatic, editor) in enumerate(zip(generated, editor_lines)):
        if not automatic.words or not editor.words:
            continue
        editor_start = float(editor.timestamp)
        editor_end = float(editor.words[-1].get("end", editor.words[-1]["start"]))
        if not any(abs(editor_start - start) <= .002 and editor_end <= end + .002
                   for start, end in manual_ranges):
            continue
        if len(automatic.words) != len(editor.words):
            continue
        if [normalize_words(str(word.get("word", ""))) for word in automatic.words] != [
                normalize_words(str(word.get("word", ""))) for word in editor.words]:
            continue

        start_residuals = []
        end_residuals = []
        for predicted, reference in zip(automatic.words, editor.words):
            start_residuals.append(float(reference["start"]) - float(predicted["start"]))
            end_residuals.append(
                float(reference.get("end", reference["start"])) -
                float(predicted.get("end", predicted["start"])))
        start_shift = statistics.median(start_residuals)
        end_shift = statistics.median(end_residuals)
        if max(abs(start_shift), abs(end_shift)) > maximum_shift_seconds:
            continue
        # A correction varying wildly inside one supposedly aligned line is
        # not a song-level calibration signal; it is a local recognition error.
        spread = max(
            max(start_residuals) - min(start_residuals),
            max(end_residuals) - min(end_residuals),
        )
        if spread > .65:
            continue
        protected.add(line_index)
        anchors.append({
            "line": line_index,
            "time": (editor_start + editor_end) * .5,
            "start_shift": start_shift,
            "end_shift": end_shift,
            "words": len(editor.words),
        })

    summary["manual_anchor_lines"] = len(anchors)
    summary["manual_anchor_words"] = sum(anchor["words"] for anchor in anchors)
    if not anchors:
        summary["enabled"] = False
        summary["reason"] = "no-reliable-manual-anchor-lines"
        return result, summary

    for line_index, line in enumerate(result):
        if line_index in protected or not line.words:
            continue
        center = (float(line.words[0]["start"]) +
                  float(line.words[-1].get("end", line.words[-1]["start"]))) * .5
        usable = [anchor for anchor in anchors
                  if abs(float(anchor["time"]) - center) <= maximum_influence_seconds]
        if not usable:
            continue
        before = max((anchor for anchor in usable if anchor["time"] <= center),
                     key=lambda item: item["time"], default=None)
        after = min((anchor for anchor in usable if anchor["time"] >= center),
                    key=lambda item: item["time"], default=None)
        if before is not None and after is not None and before is not after:
            span = max(.001, float(after["time"]) - float(before["time"]))
            weight = min(1.0, max(0.0, (center - float(before["time"])) / span))
            start_shift = _lerp(before["start_shift"], after["start_shift"], weight)
            end_shift = _lerp(before["end_shift"], after["end_shift"], weight)
            confidence = 1.0
            source_lines = [int(before["line"]) + 1, int(after["line"]) + 1]
        else:
            nearest = before or after
            assert nearest is not None
            distance = abs(float(nearest["time"]) - center)
            confidence = math.exp(-distance / 24.0)
            if confidence < .22:
                continue
            # Extrapolation fades towards the untouched result. Interpolation
            # between two human anchors retains the full measured correction.
            start_shift = float(nearest["start_shift"]) * confidence
            end_shift = float(nearest["end_shift"]) * confidence
            source_lines = [int(nearest["line"]) + 1]

        first_start = float(line.words[0]["start"])
        last_end = float(line.words[-1].get("end", line.words[-1]["start"]))
        duration = last_end - first_start
        corrected_duration = duration + end_shift - start_shift
        if duration <= .001 or corrected_duration < len(line.words) * .035:
            continue
        previous_end = -math.inf
        valid = True
        for word in line.words:
            relative_start = (float(word["start"]) - first_start) / duration
            relative_end = (float(word.get("end", word["start"])) - first_start) / duration
            corrected_start = first_start + relative_start * duration + _lerp(
                start_shift, end_shift, relative_start)
            corrected_end = first_start + relative_end * duration + _lerp(
                start_shift, end_shift, relative_end)
            if corrected_start < previous_end - .001 or corrected_end - corrected_start < .035:
                valid = False
                break
            word["start"] = round(corrected_start, 6)
            word["end"] = round(corrected_end, 6)
            word["timing_source"] = "editor-guided-calibration"
            word["editor_guidance_confidence"] = round(confidence, 4)
            previous_end = corrected_end
        if not valid:
            result[line_index] = deepcopy(generated[line_index])
            continue
        line.timestamp = float(line.words[0]["start"])
        summary["guided_lines"] += 1
        summary["lines"].append({
            "line": line_index + 1,
            "source_anchor_lines": source_lines,
            "start_shift_ms": round(start_shift * 1000.0, 2),
            "end_shift_ms": round(end_shift * 1000.0, 2),
            "confidence": round(confidence, 4),
        })
    return result, summary


def _lerp(left: float, right: float, weight: float) -> float:
    return float(left) + (float(right) - float(left)) * float(weight)
