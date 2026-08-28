from __future__ import annotations

import io
import json
import os
import re
import urllib.error
import urllib.request

import numpy as np
import soundfile as sf

from .boundary_evidence import LeakageReference, banded_boundary_step


def candidate_pitch_quality(vocal_audio, *, sample_rate: int = 16000) -> dict:
    """Return an optional, weak tonal score; unpitched vocals return no score."""
    url = os.getenv("BASIC_PITCH_URL", "").strip().rstrip("/")
    if not url:
        return {"eligible": False, "reason": "sidecar-url-not-configured"}
    try:
        evidence = _request_evidence(url, vocal_audio, sample_rate)
    except (OSError, ValueError, urllib.error.URLError, json.JSONDecodeError) as error:
        return {"eligible": False, "reason": "sidecar-unavailable",
                "error": str(error)[:500]}
    duration = max(.001, len(vocal_audio) / sample_rate)
    intervals = sorted((max(0.0, float(note["start"])),
                        min(duration, float(note["end"])))
                       for note in evidence["notes"] if float(note["end"]) > 0)
    covered = 0.0
    if intervals:
        start, end = intervals[0]
        for lower, upper in intervals[1:]:
            if lower <= end:
                end = max(end, upper)
            else:
                covered += max(0.0, end - start)
                start, end = lower, upper
        covered += max(0.0, end - start)
    tonal_ratio = min(1.0, covered / duration)
    minimum_ratio = float(os.getenv(
        "LRC_BASIC_PITCH_SEPARATOR_MIN_TONAL_RATIO", ".05"))
    if tonal_ratio < minimum_ratio:
        return {"eligible": False, "reason": "insufficient-tonal-activity",
                "tonal_ratio": round(tonal_ratio, 4)}
    confidences = [float(note.get("confidence", note.get("amplitude", 0)))
                   for note in evidence["notes"]]
    quality = float(np.median(confidences)) if confidences else 0.0
    return {"eligible": True, "quality": round(quality, 4),
            "tonal_ratio": round(tonal_ratio, 4), "note_events": len(intervals)}


def analyze_and_refine_line_onsets(lines: list, vocal_audio, instrumental_audio,
                                   *, sample_rate: int = 16000,
                                   use_for_alignment: bool = True) -> dict:
    """Fuse pitch onsets/releases with leakage-independent vocal boundaries."""
    url = os.getenv("BASIC_PITCH_URL", "").strip().rstrip("/")
    summary = {
        "enabled": bool(url), "applied": 0, "applied_onsets": 0,
        "applied_drift_lines": 0, "applied_internal_boundaries": 0,
        "applied_syllable_boundaries": 0, "applied_releases": 0,
        "method": "basic-pitch-musical-evidence-v4",
        "profile": "pipeline-evidence", "details": [],
        "internal_boundary_details": [], "syllable_boundary_details": [],
        "release_details": [],
        "use_for_alignment": use_for_alignment,
    }
    if not url:
        return {**summary, "reason": "sidecar-url-not-configured"}
    try:
        evidence = _request_evidence(url, vocal_audio, sample_rate)
    except (OSError, ValueError, urllib.error.URLError, json.JSONDecodeError) as error:
        return {**summary, "reason": "sidecar-unavailable", "error": str(error)[:500]}
    notes = evidence["notes"]
    onsets = evidence["onsets"]
    summary["service"] = evidence["service"]
    summary["schema_version"] = evidence.get("schema_version", 1)
    summary["note_events"] = len(notes)
    summary["raw_onset_peaks"] = len(onsets)
    summary["contour"] = evidence.get("contour", [])
    summary["polyphony"] = evidence.get("polyphony", {})
    summary["notes"] = notes
    if not notes and not onsets:
        return {**summary, "reason": "no-pitch-events"}

    reference = (LeakageReference(np.asarray(instrumental_audio, dtype=np.float32), sample_rate)
                 if instrumental_audio is not None else None)
    summary["leakage_reference_available"] = reference is not None
    maximum_shift = float(os.getenv("BASIC_PITCH_MAX_ONSET_SHIFT", "0.18"))
    minimum_shift = float(os.getenv("BASIC_PITCH_MIN_ONSET_SHIFT", "0.045"))
    minimum_amplitude = float(os.getenv("BASIC_PITCH_MIN_AMPLITUDE", "0.25"))
    minimum_raw_confidence = float(os.getenv("BASIC_PITCH_MIN_RAW_ONSET_CONFIDENCE", "0.35"))
    minimum_evidence = float(os.getenv("BASIC_PITCH_MIN_INDEPENDENT_DB", "1.5"))
    pitch_range = _robust_vocal_pitch_range(notes, minimum_amplitude)
    summary["vocal_pitch_range"] = pitch_range
    summary["raw_onsets_outside_vocal_range"] = sum(
        not _pitch_in_range(item.get("pitch"), pitch_range) for item in onsets)
    trusted_onsets = [item for item in onsets
                      if _pitch_in_range(item.get("pitch"), pitch_range)]

    decisions = []
    for index, line in enumerate(lines):
        if not line.words:
            continue
        first = line.words[0]
        current = float(first["start"])
        candidates = [{"time": float(item["time"]), "confidence": float(item["confidence"]),
                       "pitch": item.get("pitch"), "source": "raw-onset"}
                      for item in trusted_onsets
                      if abs(float(item["time"]) - current) <= maximum_shift
                      and float(item.get("confidence", 0)) >= minimum_raw_confidence
                      ]
        if not candidates:
            candidates = [{"time": float(item["start"]),
                           "confidence": float(item.get("amplitude", 0)),
                           "pitch": item.get("pitch"), "source": "decoded-note"}
                          for item in notes
                          if abs(float(item["start"]) - current) <= maximum_shift
                          and float(item.get("amplitude", 0)) >= minimum_amplitude]
        if not candidates:
            continue
        candidate = min(candidates, key=lambda item: (
            abs(item["time"] - current), -item["confidence"]))
        target = candidate["time"]
        boundary = banded_boundary_step(
            vocal_audio, target, reference=reference, sample_rate=sample_rate)
        lead = (_consonant_lead(
            vocal_audio, target, current, reference, sample_rate, minimum_evidence)
                if candidate["source"] == "raw-onset" else None)
        if lead is not None:
            target, boundary = lead
            candidate["source"] = "raw-onset+consonant-lead"
        shift = target - current
        if abs(shift) < minimum_shift:
            continue
        independent = float(boundary.get("independent_db", 0)) if boundary else 0.0
        previous_end = (float(lines[index - 1].words[-1]["end"])
                        if index > 0 and lines[index - 1].words else 0.0)
        accepted = (use_for_alignment and independent >= minimum_evidence
                    and target >= previous_end - 0.01
                    and float(first["end"]) - target >= 0.03)
        detail = {
            "kind": "onset", "line": index + 1, "old": round(current, 3),
            "candidate": round(target, 3), "shift_ms": round(shift * 1000, 1),
            "pitch": candidate.get("pitch"), "confidence": candidate["confidence"],
            "candidate_source": candidate["source"],
            "independent_db": round(independent, 2), "accepted": accepted,
        }
        summary["details"].append(detail)
        decisions.append((index, target, shift, candidate, detail))

    drift = _fit_drift([detail for *_rest, detail in decisions if detail["accepted"]])
    summary["drift"] = drift
    for index, target, shift, candidate, detail in decisions:
        if not detail["accepted"]:
            continue
        line = lines[index]
        full_line = (drift["supported"]
                     and abs(shift - _drift_at(drift, float(line.words[0]["start"]))) <= .045)
        next_start = (float(lines[index + 1].words[0]["start"])
                      if index + 1 < len(lines) and lines[index + 1].words else None)
        if full_line and next_start is not None:
            full_line = float(line.words[-1]["end"]) + shift <= next_start + .01
        if full_line:
            _shift_line(line, shift)
            summary["applied_drift_lines"] += 1
            detail["application"] = "whole-line-drift"
        else:
            line.words[0]["start"] = round(target, 3)
            line.timestamp = target
            detail["application"] = "first-word-onset"
        line.words[0]["basic_pitch_onset_shift_ms"] = detail["shift_ms"]
        line.words[0]["basic_pitch_pitch"] = candidate.get("pitch")
        summary["applied_onsets"] += 1

    _refine_internal_word_boundaries(
        lines, trusted_onsets, vocal_audio, reference, sample_rate, summary,
        allow_changes=use_for_alignment)
    _refine_syllable_boundaries(
        lines, notes, vocal_audio, reference, sample_rate, summary,
        allow_changes=use_for_alignment)
    _refine_releases(lines, notes, vocal_audio, reference, sample_rate, summary,
                     allow_changes=use_for_alignment)
    summary["repetition_fingerprints"] = _repetition_fingerprints(lines, notes)
    summary["pitch_timeline"] = _pitch_timeline(lines, notes)
    summary["word_pitch_evidence"] = _word_pitch_evidence(lines, notes)
    summary["alignment_confidence"] = _alignment_confidence(summary)
    summary["applied"] = (summary["applied_onsets"]
                          + summary["applied_internal_boundaries"]
                          + summary["applied_syllable_boundaries"]
                          + summary["applied_releases"])
    summary["reason"] = "completed"
    return summary


def _consonant_lead(vocal_audio, pitch_onset: float, current: float,
                    reference: LeakageReference | None, sample_rate: int,
                    minimum_evidence: float) -> tuple[float, dict] | None:
    """Find a leakage-independent consonant attack shortly before voicing."""
    maximum_lead = float(os.getenv("BASIC_PITCH_MAX_CONSONANT_LEAD", ".12"))
    if maximum_lead < .025:
        return None
    lower = max(0.0, current - float(os.getenv("BASIC_PITCH_MAX_ONSET_SHIFT", ".18")),
                pitch_onset - maximum_lead)
    measured = []
    for timestamp in np.arange(lower, pitch_onset - .019, .01):
        evidence = banded_boundary_step(
            vocal_audio, float(timestamp), reference=reference, sample_rate=sample_rate)
        # Real band-split evidence is required here. This prevents a generic
        # broadband fluctuation from masquerading as a leading consonant.
        if not evidence or not evidence.get("bands"):
            continue
        strength = float(evidence.get("independent_db", 0))
        if strength >= minimum_evidence:
            measured.append((float(timestamp), strength, evidence))
    if not measured:
        return None
    peak = max(item[1] for item in measured)
    floor = max(minimum_evidence, peak * .55)
    selected = next((item for item in measured if item[1] >= floor), None)
    if selected is None or pitch_onset - selected[0] < .025:
        return None
    selected[2]["pitch_onset"] = round(pitch_onset, 3)
    selected[2]["consonant_lead_ms"] = round((pitch_onset - selected[0]) * 1000, 1)
    return selected[0], selected[2]


def _robust_vocal_pitch_range(notes: list[dict], minimum_amplitude: float) -> dict:
    pitches = np.asarray([int(note["pitch"]) for note in notes
                          if float(note.get("amplitude", 0)) >= minimum_amplitude],
                         dtype=np.float32)
    if len(pitches) < 12:
        return {"supported": False, "minimum_midi": None, "maximum_midi": None,
                "events": int(len(pitches)), "method": "insufficient-decoded-notes"}
    margin = float(os.getenv("BASIC_PITCH_VOCAL_RANGE_MARGIN_SEMITONES", "7"))
    lower = max(0, int(np.floor(np.percentile(pitches, 2) - margin)))
    upper = min(127, int(np.ceil(np.percentile(pitches, 98) + margin)))
    return {"supported": True, "minimum_midi": lower, "maximum_midi": upper,
            "events": int(len(pitches)), "median_midi": round(float(np.median(pitches)), 2),
            "method": "decoded-note-percentiles-with-margin-v1"}


def _pitch_in_range(pitch, pitch_range: dict) -> bool:
    if pitch is None or not pitch_range.get("supported"):
        return True
    return (int(pitch_range["minimum_midi"]) <= int(pitch)
            <= int(pitch_range["maximum_midi"]))


def _fit_drift(details: list[dict]) -> dict:
    result = {"supported": False, "anchors": len(details),
              "reason": "insufficient-independent-onsets"}
    minimum = int(os.getenv("BASIC_PITCH_DRIFT_MIN_ANCHORS", "6"))
    if len(details) < minimum:
        return result
    times = np.asarray([float(item["old"]) for item in details])
    shifts = np.asarray([float(item["shift_ms"]) / 1000 for item in details])
    span = float(times.max() - times.min())
    if span < float(os.getenv("BASIC_PITCH_DRIFT_MIN_SPAN_SECONDS", "60")):
        return {**result, "reason": "insufficient-song-span", "span_seconds": round(span, 2)}
    slope, intercept = np.polyfit(times, shifts, 1)
    residuals = shifts - (slope * times + intercept)
    median = float(np.median(residuals))
    mad = float(np.median(np.abs(residuals - median)))
    keep = np.abs(residuals - median) <= max(.025, 3 * mad)
    if int(keep.sum()) >= minimum and not bool(keep.all()):
        slope, intercept = np.polyfit(times[keep], shifts[keep], 1)
        residuals = shifts[keep] - (slope * times[keep] + intercept)
        mad = float(np.median(np.abs(residuals - np.median(residuals))))
    drift_change = float(slope * span)
    supported = (int(keep.sum()) >= minimum and mad <= .035
                 and abs(drift_change) >= float(os.getenv(
                     "BASIC_PITCH_DRIFT_MIN_CHANGE_SECONDS", ".08"))
                 and abs(slope) <= .004)
    return {
        "supported": supported, "anchors": int(keep.sum()),
        "span_seconds": round(span, 2), "slope_ms_per_minute": round(slope * 60000, 2),
        "change_ms": round(drift_change * 1000, 1), "residual_mad_ms": round(mad * 1000, 1),
        "slope": float(slope), "intercept": float(intercept),
        "reason": "robust-linear-drift" if supported else "no-stable-linear-drift",
    }


def _drift_at(drift: dict, timestamp: float) -> float:
    return float(drift.get("slope", 0)) * timestamp + float(drift.get("intercept", 0))


def _shift_line(line, shift: float) -> None:
    for word in line.words:
        word["start"] = round(float(word["start"]) + shift, 3)
        word["end"] = round(float(word["end"]) + shift, 3)
        for syllable in word.get("syllables", []):
            syllable["start"] = round(float(syllable["start"]) + shift, 3)
            syllable["end"] = round(float(syllable["end"]) + shift, 3)
    line.timestamp = float(line.words[0]["start"])


def _refine_internal_word_boundaries(lines: list, onsets: list[dict], vocal_audio,
                                     reference: LeakageReference | None, sample_rate: int,
                                     summary: dict, *, allow_changes: bool = True) -> None:
    """Move an existing word start, never create a word from a pitch event.

    A melodic onset alone is deliberately insufficient: it may be a pitch
    change or melisma within the preceding word.  The vocal stem must also
    contain a local positive boundary which is not explained by the
    instrumental stem.
    """
    radius = float(os.getenv("BASIC_PITCH_INTERNAL_WORD_RADIUS", ".075"))
    minimum_shift = float(os.getenv("BASIC_PITCH_INTERNAL_WORD_MIN_SHIFT", ".03"))
    minimum_confidence = float(os.getenv(
        "BASIC_PITCH_INTERNAL_WORD_MIN_CONFIDENCE", ".55"))
    minimum_evidence = float(os.getenv(
        "BASIC_PITCH_INTERNAL_WORD_MIN_INDEPENDENT_DB", "3.0"))
    minimum_duration = float(os.getenv("BASIC_PITCH_INTERNAL_MIN_DURATION", ".055"))
    mode = os.getenv("BASIC_PITCH_INTERNAL_WORD_MODE", "shadow").strip().lower()
    if not allow_changes:
        mode = "shadow"
    summary["internal_word_mode"] = mode
    summary["supported_internal_boundaries"] = 0
    summary["confirmed_internal_boundaries"] = 0
    for line_index, line in enumerate(lines):
        for word_index in range(1, len(line.words)):
            previous, word = line.words[word_index - 1], line.words[word_index]
            current = float(word["start"])
            candidates = [item for item in onsets
                          if abs(float(item["time"]) - current) <= radius
                          and float(item.get("confidence", 0)) >= minimum_confidence]
            if not candidates:
                continue
            measured = []
            for candidate in sorted(candidates, key=lambda item: (
                    abs(float(item["time"]) - current),
                    -float(item.get("confidence", 0))))[:3]:
                target = float(candidate["time"])
                boundary = banded_boundary_step(
                    vocal_audio, target, reference=reference, sample_rate=sample_rate)
                independent = float(boundary.get("independent_db", 0)) if boundary else 0.0
                measured.append((independent, -abs(target - current), target, candidate))
            independent, _distance, target, candidate = max(measured)
            shift = target - current
            supported = (independent >= minimum_evidence
                         and target >= float(previous["start"]) + minimum_duration
                         and target <= float(word["end"]) - minimum_duration)
            confirmation = supported and abs(shift) < minimum_shift
            if not supported and abs(shift) < minimum_shift:
                continue
            accepted = supported and not confirmation and mode == "select"
            detail = {
                "kind": "internal-word-onset", "line": line_index + 1,
                "word": word_index + 1, "text": str(word.get("word", "")),
                "old": round(current, 3), "candidate": round(target, 3),
                "shift_ms": round(shift * 1000, 1),
                "pitch": candidate.get("pitch"),
                "confidence": float(candidate.get("confidence", 0)),
                "independent_db": round(independent, 2), "supported": supported,
                "accepted": accepted,
                "application": ("selected" if accepted else
                                "confirmed-existing" if confirmation else "shadow"),
            }
            summary["internal_boundary_details"].append(detail)
            if supported:
                summary["supported_internal_boundaries"] += 1
            if confirmation:
                summary["confirmed_internal_boundaries"] += 1
            if not accepted:
                continue
            old_gap = current - float(previous["end"])
            word["start"] = round(target, 3)
            word["basic_pitch_internal_shift_ms"] = detail["shift_ms"]
            if word.get("syllables"):
                word["syllables"][0]["start"] = round(target, 3)
            # Most aligned words share a boundary. Preserve deliberate pauses,
            # but keep formerly touching/overlapping neighbours contiguous.
            if old_gap <= .03:
                previous["end"] = round(target, 3)
                if previous.get("syllables"):
                    previous["syllables"][-1]["end"] = round(target, 3)
            summary["applied_internal_boundaries"] += 1


def _refine_syllable_boundaries(lines: list, notes: list[dict], vocal_audio,
                                reference: LeakageReference | None, sample_rate: int,
                                summary: dict, *, allow_changes: bool = True) -> None:
    """Refine only pre-existing syllable boundaries with note+attack evidence."""
    radius = float(os.getenv("BASIC_PITCH_SYLLABLE_RADIUS", ".06"))
    minimum_shift = float(os.getenv("BASIC_PITCH_SYLLABLE_MIN_SHIFT", ".025"))
    minimum_amplitude = float(os.getenv("BASIC_PITCH_SYLLABLE_MIN_AMPLITUDE", ".45"))
    minimum_evidence = float(os.getenv(
        "BASIC_PITCH_SYLLABLE_MIN_INDEPENDENT_DB", "3.5"))
    minimum_pitch_change = int(os.getenv("BASIC_PITCH_SYLLABLE_MIN_SEMITONES", "2"))
    minimum_duration = float(os.getenv("BASIC_PITCH_INTERNAL_MIN_DURATION", ".055"))
    mode = os.getenv("BASIC_PITCH_SYLLABLE_MODE", "shadow").strip().lower()
    if not allow_changes:
        mode = "shadow"
    summary["syllable_mode"] = mode
    summary["supported_syllable_boundaries"] = 0
    summary["confirmed_syllable_boundaries"] = 0
    ordered = sorted(notes, key=lambda item: float(item["start"]))
    for line_index, line in enumerate(lines):
        for word_index, word in enumerate(line.words):
            syllables = word.get("syllables", [])
            for syllable_index in range(1, len(syllables)):
                current = float(syllables[syllable_index]["start"])
                candidates = []
                for note_index, note in enumerate(ordered):
                    target = float(note["start"])
                    if target > current + radius:
                        break
                    if (note_index == 0 or abs(target - current) > radius
                            or float(note.get("amplitude", 0)) < minimum_amplitude):
                        continue
                    prior = ordered[note_index - 1]
                    if target - float(prior["end"]) > .15:
                        continue
                    pitch_change = abs(int(note["pitch"]) - int(prior["pitch"]))
                    if pitch_change >= minimum_pitch_change:
                        candidates.append((note, pitch_change))
                if not candidates:
                    continue
                note, pitch_change = min(candidates, key=lambda item: (
                    abs(float(item[0]["start"]) - current), -item[1]))
                target = float(note["start"])
                shift = target - current
                boundary = banded_boundary_step(
                    vocal_audio, target, reference=reference, sample_rate=sample_rate)
                independent = float(boundary.get("independent_db", 0)) if boundary else 0.0
                previous, syllable = syllables[syllable_index - 1], syllables[syllable_index]
                supported = (independent >= minimum_evidence
                             and target >= float(previous["start"]) + minimum_duration
                             and target <= float(syllable["end"]) - minimum_duration)
                confirmation = supported and abs(shift) < minimum_shift
                if not supported and abs(shift) < minimum_shift:
                    continue
                accepted = supported and not confirmation and mode == "select"
                detail = {
                    "kind": "syllable-onset", "line": line_index + 1,
                    "word": word_index + 1, "syllable": syllable_index + 1,
                    "old": round(current, 3), "candidate": round(target, 3),
                    "shift_ms": round(shift * 1000, 1), "pitch": note.get("pitch"),
                    "pitch_change": pitch_change,
                    "amplitude": float(note.get("amplitude", 0)),
                    "independent_db": round(independent, 2), "supported": supported,
                    "accepted": accepted,
                    "application": ("selected" if accepted else
                                    "confirmed-existing" if confirmation else "shadow"),
                }
                summary["syllable_boundary_details"].append(detail)
                if supported:
                    summary["supported_syllable_boundaries"] += 1
                if confirmation:
                    summary["confirmed_syllable_boundaries"] += 1
                if not accepted:
                    continue
                previous["end"] = round(target, 3)
                syllable["start"] = round(target, 3)
                syllable["basic_pitch_shift_ms"] = detail["shift_ms"]
                summary["applied_syllable_boundaries"] += 1


def _refine_releases(lines: list, notes: list[dict], vocal_audio,
                     reference: LeakageReference | None, sample_rate: int, summary: dict,
                     *, allow_changes: bool = True) -> None:
    radius = float(os.getenv("BASIC_PITCH_MAX_RELEASE_SHIFT", ".18"))
    minimum_shift = float(os.getenv("BASIC_PITCH_MIN_RELEASE_SHIFT", ".06"))
    minimum_amplitude = float(os.getenv("BASIC_PITCH_MIN_RELEASE_AMPLITUDE", ".30"))
    minimum_drop = float(os.getenv("BASIC_PITCH_MIN_RELEASE_DROP_DB", "1.5"))
    for index, line in enumerate(lines):
        if not line.words:
            continue
        final = line.words[-1]
        current = float(final["end"])
        candidates = [note for note in notes
                      if abs(float(note["end"]) - current) <= radius
                      and float(note.get("amplitude", 0)) >= minimum_amplitude]
        if not candidates:
            continue
        note = min(candidates, key=lambda item: (
            abs(float(item["end"]) - current), -float(item.get("amplitude", 0))))
        target = float(note["end"])
        shift = target - current
        if abs(shift) < minimum_shift:
            continue
        boundary = banded_boundary_step(
            vocal_audio, target, reference=reference, sample_rate=sample_rate)
        independent = float(boundary.get("independent_db", 0)) if boundary else 0.0
        next_start = (float(lines[index + 1].words[0]["start"])
                      if index + 1 < len(lines) and lines[index + 1].words else None)
        accepted = (allow_changes and independent <= -minimum_drop
                    and target >= float(final["start"]) + .05
                    and (next_start is None or target <= next_start - .06))
        detail = {
            "kind": "release", "line": index + 1, "old": round(current, 3),
            "candidate": round(target, 3), "shift_ms": round(shift * 1000, 1),
            "pitch": note.get("pitch"), "amplitude": note.get("amplitude"),
            "independent_db": round(independent, 2), "accepted": accepted,
        }
        summary["release_details"].append(detail)
        if not accepted:
            continue
        final["end"] = round(target, 3)
        final["basic_pitch_release_shift_ms"] = detail["shift_ms"]
        if final.get("syllables"):
            final["syllables"][-1]["end"] = round(target, 3)
        summary["applied_releases"] += 1


def _repetition_fingerprints(lines: list, notes: list[dict]) -> dict:
    """Compare relative pitch contours of repeated lyric lines."""
    groups: dict[str, list[tuple[int, list[int]]]] = {}
    for index, line in enumerate(lines):
        if not line.words:
            continue
        text = getattr(line, "text", " ".join(
            str(word.get("word", "")) for word in line.words))
        key = re.sub(r"[^\w]+", " ", str(text).casefold()).strip()
        if not key:
            continue
        start, end = float(line.words[0]["start"]), float(line.words[-1]["end"])
        pitches = [int(note["pitch"]) for note in notes
                   if start - .08 <= float(note["start"]) <= end + .08]
        collapsed = []
        for pitch in pitches:
            if not collapsed or pitch != collapsed[-1]:
                collapsed.append(pitch)
        if len(collapsed) >= 3:
            origin = collapsed[0]
            groups.setdefault(key, []).append((index + 1, [pitch - origin for pitch in collapsed[:24]]))
    comparisons = []
    group_summaries = []
    minimum_similarity = float(os.getenv(
        "BASIC_PITCH_REPETITION_MIN_SIMILARITY", ".72"))
    for text, occurrences in groups.items():
        if len(occurrences) < 2:
            continue
        reference_line, reference = occurrences[0]
        similarities = []
        for line_number, contour in occurrences[1:]:
            distance = _contour_distance(reference, contour)
            similarity = max(0.0, 1.0 - distance / 12.0)
            similarities.append(similarity)
            comparisons.append({
                "text": text, "reference_line": reference_line, "line": line_number,
                "similarity": round(similarity, 3),
                "placement_supported": similarity >= minimum_similarity,
                "reference_notes": len(reference), "notes": len(contour),
            })
        median = float(np.median(similarities)) if similarities else 0.0
        group_summaries.append({
            "text": text, "occurrences": len(occurrences),
            "reference_line": reference_line,
            "median_similarity": round(median, 3),
            "placement_supported": median >= minimum_similarity,
            "outlier_lines": [item["line"] for item in comparisons
                              if item["text"] == text
                              and not item["placement_supported"]],
        })
    return {
        "method": "relative-pitch-dtw-v1", "repeated_groups": sum(
            len(items) >= 2 for items in groups.values()),
        "minimum_similarity": minimum_similarity,
        "groups": group_summaries, "comparisons": comparisons,
    }


def _pitch_timeline(lines: list, notes: list[dict]) -> dict:
    """Keep a compact note-event track for review and future editor tooling.

    These are quantized note events, not a phase-continuous F0 curve.  They are
    useful for melody/segment inspection but intentionally make no promise that
    they are sufficient for high-quality vocal transposition.
    """
    minimum_amplitude = float(os.getenv("BASIC_PITCH_TIMELINE_MIN_AMPLITUDE", ".25"))
    maximum_events = int(os.getenv("BASIC_PITCH_TIMELINE_MAX_EVENTS", "5000"))
    ranges = []
    for line_index, line in enumerate(lines):
        if line.words:
            ranges.append((float(line.words[0]["start"]),
                           float(line.words[-1]["end"]), line_index + 1))
    events = []
    pitches = []
    for note in notes:
        amplitude = float(note.get("amplitude", 0))
        if amplitude < minimum_amplitude:
            continue
        start, end, midi = (float(note["start"]), float(note["end"]),
                            int(note["pitch"]))
        line_number = next((number for lower, upper, number in ranges
                            if lower - .08 <= start <= upper + .08), None)
        events.append({
            "start": round(start, 3), "end": round(end, 3), "midi": midi,
            "amplitude": round(amplitude, 3), "line": line_number,
        })
        pitches.append(midi)
        if len(events) >= maximum_events:
            break
    return {
        "method": "basic-pitch-note-events-v1",
        "representation": "quantized-note-events-not-continuous-f0",
        "events": events, "event_count": len(events),
        "truncated": len(events) < sum(
            float(note.get("amplitude", 0)) >= minimum_amplitude for note in notes),
        "midi_min": min(pitches) if pitches else None,
        "midi_max": max(pitches) if pitches else None,
        "midi_median": round(float(np.median(pitches)), 2) if pitches else None,
    }


def _word_pitch_evidence(lines: list, notes: list[dict]) -> dict:
    """Summarize voiced note coverage per existing lyric word without mutation."""
    minimum_amplitude = float(os.getenv("BASIC_PITCH_WORD_MIN_AMPLITUDE", ".25"))
    maximum_entries = int(os.getenv("BASIC_PITCH_WORD_MAX_ENTRIES", "5000"))
    usable = [note for note in notes
              if float(note.get("amplitude", 0)) >= minimum_amplitude]
    entries = []
    sustained = 0
    for line_index, line in enumerate(lines):
        for word_index, word in enumerate(line.words):
            start, end = float(word["start"]), float(word["end"])
            duration = max(.001, end - start)
            overlapping = [note for note in usable
                           if float(note["end"]) > start and float(note["start"]) < end]
            if not overlapping:
                continue
            intervals = sorted((max(start, float(note["start"])),
                                min(end, float(note["end"]))) for note in overlapping)
            covered = 0.0
            cursor_start, cursor_end = intervals[0]
            for lower, upper in intervals[1:]:
                if lower <= cursor_end:
                    cursor_end = max(cursor_end, upper)
                else:
                    covered += cursor_end - cursor_start
                    cursor_start, cursor_end = lower, upper
            covered += cursor_end - cursor_start
            weights = np.asarray([max(.001, min(end, float(note["end"]))
                                      - max(start, float(note["start"])))
                                  for note in overlapping])
            pitches = np.asarray([int(note["pitch"]) for note in overlapping])
            dominant = int(round(float(np.average(pitches, weights=weights))))
            longest = max(overlapping, key=lambda note: min(end, float(note["end"]))
                          - max(start, float(note["start"])))
            longest_duration = min(end, float(longest["end"])) - max(
                start, float(longest["start"]))
            is_sustained = longest_duration >= max(.35, duration * .55)
            sustained += int(is_sustained)
            entries.append({
                "line": line_index + 1, "word": word_index + 1,
                "text": str(word.get("word", "")),
                "start": round(start, 3), "end": round(end, 3),
                "voiced_coverage": round(min(1.0, covered / duration), 3),
                "note_count": len(overlapping), "dominant_midi": dominant,
                "midi_min": int(pitches.min()), "midi_max": int(pitches.max()),
                "pitch_span_semitones": int(pitches.max() - pitches.min()),
                "sustained": is_sustained,
                "longest_note_ms": round(longest_duration * 1000, 1),
            })
            if len(entries) >= maximum_entries:
                break
        if len(entries) >= maximum_entries:
            break
    return {
        "method": "word-overlap-note-summary-v1", "entries": entries,
        "words_with_pitch": len(entries), "sustained_words": sustained,
        "truncated": len(entries) >= maximum_entries,
        "representation": "alignment-and-editor-evidence-not-transposition-f0",
    }


def _alignment_confidence(summary: dict) -> dict:
    """Aggregate pitch support per line; this is evidence strength, not truth."""
    per_line: dict[int, dict] = {}

    def item(line: int) -> dict:
        return per_line.setdefault(line, {
            "line": line, "onset": False, "release": False,
            "confirmed_internal": 0, "supported_internal": 0,
            "repetition": None, "pitch_events": 0, "review_reasons": [],
        })

    for detail in summary.get("details", []):
        target = item(int(detail["line"]))
        if detail.get("accepted"):
            target["onset"] = True
            if abs(float(detail.get("shift_ms", 0))) >= 150:
                target["review_reasons"].append("large-onset-shift")
    for detail in summary.get("release_details", []):
        target = item(int(detail["line"]))
        if detail.get("accepted"):
            target["release"] = True
            if abs(float(detail.get("shift_ms", 0))) >= 150:
                target["review_reasons"].append("large-release-shift")
    for detail in summary.get("internal_boundary_details", []):
        target = item(int(detail["line"]))
        target["supported_internal"] += int(bool(detail.get("supported")))
        target["confirmed_internal"] += int(
            detail.get("application") == "confirmed-existing")
    repetitions = summary.get("repetition_fingerprints", {})
    for comparison in repetitions.get("comparisons", []):
        target = item(int(comparison["line"]))
        target["repetition"] = bool(comparison.get("placement_supported"))
        if not target["repetition"]:
            target["review_reasons"].append("repetition-contour-outlier")
    for event in summary.get("pitch_timeline", {}).get("events", []):
        if event.get("line") is not None:
            item(int(event["line"]))["pitch_events"] += 1

    strong = 0
    review_lines = []
    lines = []
    for line_number in sorted(per_line):
        value = per_line[line_number]
        components = [value["onset"], value["release"],
                      value["confirmed_internal"] > 0,
                      value["repetition"] is True]
        available = sum([value["pitch_events"] > 0, True,
                         value["supported_internal"] > 0,
                         value["repetition"] is not None])
        score = (0.35 * int(value["onset"]) + 0.20 * int(value["release"])
                 + 0.25 * int(value["confirmed_internal"] > 0)
                 + 0.20 * int(value["repetition"] is True))
        value["evidence_score"] = round(score, 3)
        value["evidence_components"] = sum(components)
        value["available_components"] = available
        strong += int(score >= .55 and not value["review_reasons"])
        if value["review_reasons"]:
            review_lines.append({"line": line_number,
                                 "reasons": value["review_reasons"]})
        lines.append(value)
    return {
        "method": "basic-pitch-evidence-strength-v1",
        "meaning": "independent-support-strength-not-ground-truth-accuracy",
        "measured_lines": len(lines), "strongly_supported_lines": strong,
        "review_lines": review_lines, "lines": lines,
    }


def _contour_distance(left: list[int], right: list[int]) -> float:
    rows, columns = len(left) + 1, len(right) + 1
    costs = np.full((rows, columns), np.inf, dtype=np.float32)
    costs[0, 0] = 0.0
    for row in range(1, rows):
        for column in range(1, columns):
            local = min(12.0, abs(left[row - 1] - right[column - 1]))
            costs[row, column] = local + min(
                costs[row - 1, column], costs[row, column - 1], costs[row - 1, column - 1])
    return float(costs[-1, -1] / max(len(left), len(right)))


def _request_evidence(url: str, audio, sample_rate: int) -> dict:
    wave = io.BytesIO()
    sf.write(wave, np.asarray(audio, dtype=np.float32), sample_rate,
             format="WAV", subtype="PCM_16")
    boundary = "----neonstage-basic-pitch-boundary"
    payload = (f"--{boundary}\r\n"
               'Content-Disposition: form-data; name="audio"; filename="vocals.wav"\r\n'
               "Content-Type: audio/wav\r\n\r\n").encode() + wave.getvalue() + \
              f"\r\n--{boundary}--\r\n".encode()
    request = urllib.request.Request(
        f"{url}/analyze", data=payload, method="POST",
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
    with urllib.request.urlopen(request, timeout=float(os.getenv(
            "BASIC_PITCH_TIMEOUT_SECONDS", "300"))) as response:
        result = json.loads(response.read())
    notes, onsets = result.get("notes", []), result.get("onsets", [])
    if not isinstance(notes, list) or not isinstance(onsets, list):
        raise ValueError("Basic Pitch response contains invalid evidence lists")
    contour = result.get("contour", [])
    if not isinstance(contour, list):
        raise ValueError("Basic Pitch response contains an invalid contour")
    return {
        "notes": notes,
        "onsets": onsets,
        "contour": contour,
        "polyphony": result.get("polyphony", {}),
        "schema_version": result.get("schema_version", 1),
        "service": str(result.get("model", "spotify/basic-pitch")),
    }
