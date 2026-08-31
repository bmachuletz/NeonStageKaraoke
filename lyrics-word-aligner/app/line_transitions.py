from __future__ import annotations

import math
from dataclasses import dataclass

import numpy as np


@dataclass(frozen=True, slots=True)
class LineTransition:
    kind: str
    previous_end: float
    next_start: float
    release_candidate: float
    onset_candidate: float
    confidence: float


def analyze_line_transitions(
    lines: list,
    vocals: np.ndarray,
    instrumental: np.ndarray | None = None,
    *,
    sample_rate: int = 16000,
    frame_seconds: float = .04,
    hop_seconds: float = .01,
    minimum_pause: float = .055,
    minimum_attack: float = .06,
    minimum_release_shift: float = .04,
    minimum_onset_shift: float = .04,
    maximum_early_onset_shift: float = .12,
    minimum_onset_duration: float = .08,
    release_padding: float = .035,
) -> dict:
    """Classify and locally reconcile consecutive sung lines.

    A local release trim can invalidate the evidence for the following phrase
    onset. This pass therefore decides both boundaries together. It is
    intentionally conservative: only a sustained low-energy valley followed by
    a strong, sustained attack can classify a transition as ``separated``.
    Legato or ambiguous transitions remain unchanged and later refinements
    stay free to use their own phoneme-aware evidence.
    """
    vocal_signal = np.asarray(vocals, dtype=np.float32)
    if vocal_signal.ndim > 1:
        vocal_signal = vocal_signal.reshape(vocal_signal.shape[0], -1).mean(axis=1)
    instrumental_signal = None
    if instrumental is not None:
        instrumental_signal = np.asarray(instrumental, dtype=np.float32)
        if instrumental_signal.ndim > 1:
            instrumental_signal = instrumental_signal.reshape(
                instrumental_signal.shape[0], -1).mean(axis=1)

    report = {
        "method": "joint-line-transition-v1",
        "version": 1,
        "enabled": vocal_signal.size >= sample_rate,
        "minimum_pause_seconds": minimum_pause,
        "transitions": [],
        "legato": 0,
        "separated": 0,
        "ambiguous": 0,
        "adjusted": 0,
    }
    if not report["enabled"]:
        report["reason"] = "vocal-stem-unavailable-or-too-short"
        return report

    transitions: list[LineTransition] = []
    previous = next((line for line in lines if line.words), None)
    for line in lines:
        if not line.words or line is previous:
            continue
        transition = _classify(
            previous, line, vocal_signal, instrumental_signal,
            sample_rate=sample_rate, frame_seconds=frame_seconds,
            hop_seconds=hop_seconds, minimum_pause=minimum_pause,
            minimum_attack=minimum_attack,
            maximum_early_onset_shift=maximum_early_onset_shift)
        transitions.append(transition)
        previous = line

    for transition in transitions:
        detail = {
            "kind": transition.kind,
            "previous_end": round(transition.previous_end, 3),
            "next_start": round(transition.next_start, 3),
            "release_candidate": round(transition.release_candidate, 3),
            "onset_candidate": round(transition.onset_candidate, 3),
            "confidence": round(transition.confidence, 4),
            "status": "reported",
        }
        report[transition.kind] = report.get(transition.kind, 0) + 1
        if transition.kind != "separated" or transition.confidence < .68:
            report["ambiguous"] += transition.kind == "separated"
            report["transitions"].append(detail)
            continue

        release_delta = transition.previous_end - transition.release_candidate
        onset_delta = transition.onset_candidate - transition.next_start
        significant_onset_shift = False
        if abs(onset_delta) >= minimum_onset_shift:
            onset_delta = (minimum_onset_shift if onset_delta > 0
                           else -minimum_onset_shift)
            significant_onset_shift = True
            transition = LineTransition(
                transition.kind, transition.previous_end, transition.next_start,
                transition.release_candidate,
                transition.next_start + onset_delta, transition.confidence)
        if release_delta >= minimum_release_shift:
            release_delta = max(release_delta, .05)
            transition = LineTransition(
                transition.kind, transition.previous_end, transition.next_start,
                transition.previous_end - release_delta,
                transition.onset_candidate, transition.confidence)
        detail.update({
            "release_shift_ms": round(release_delta * 1000, 1),
            "onset_shift_ms": round(onset_delta * 1000, 1),
        })
        significant_onset_shift = (significant_onset_shift
                                   or onset_delta >= minimum_onset_shift
                                   or onset_delta <= -minimum_onset_shift)
        if release_delta < minimum_release_shift and not significant_onset_shift:
            report["transitions"].append(detail)
            continue

        # Locate the actual owning words lazily again. This keeps the
        # classifier pure and makes the mutation step easy to audit.
        source = following = None
        for line in lines:
            if not line.words:
                continue
            if math.isclose(float(line.words[-1]["end"]), transition.previous_end,
                            abs_tol=.002):
                source = line
            if math.isclose(float(line.words[0]["start"]), transition.next_start,
                            abs_tol=.002):
                following = line
            if source is not None and following is not None:
                break
        if source is None or following is None:
            report["ambiguous"] += 1
            report["separated"] -= 1
            detail["status"] = "owner-not-found"
            report["transitions"].append(detail)
            continue
        if (getattr(source, "manual_adjusted", False)
                or source.words[-1].get("editor_word_manual_adjusted")
                or getattr(following, "manual_adjusted", False)
                or following.words[0].get("editor_word_manual_adjusted")):
            detail["status"] = "manual-boundary-protected"
            report["transitions"].append(detail)
            continue

        final_word = source.words[-1]
        first_word = following.words[0]
        if release_delta >= minimum_release_shift:
            candidate = transition.previous_end - release_delta
            if float(final_word["end"]) - candidate >= .03:
                final_word["pre_joint_line_transition_end"] = round(
                    float(final_word["end"]), 3)
                final_word["end"] = round(candidate, 3)
                final_word["joint_line_transition_release_shift_ms"] = round(
                    (float(final_word["pre_joint_line_transition_end"])
                     - candidate) * 1000, 1)
                detail["status"] = "corrected-release"
                report["adjusted"] += 1
        if significant_onset_shift:
            candidate = transition.onset_candidate
            candidate_allowed = (
                candidate >= 0.0
                and candidate >= transition.next_start - maximum_early_onset_shift
                and candidate >= transition.next_start - minimum_onset_shift
                and candidate <= transition.next_start + minimum_onset_shift
                and float(first_word["end"]) - candidate >= minimum_onset_duration
                and candidate >= (float(final_word["end"])
                                  if release_delta < minimum_release_shift
                                  else transition.release_candidate))
            if candidate_allowed:
                first_word["pre_joint_line_transition_start"] = round(
                    float(first_word["start"]), 3)
                first_word["start"] = round(candidate, 3)
                first_word["joint_line_transition_onset_shift_ms"] = round(
                    (candidate - float(
                        first_word["pre_joint_line_transition_start"])) * 1000, 1)
                following.timestamp = candidate
                detail["status"] = (detail["status"] + "+onset"
                                    if detail["status"] != "reported"
                                    else "corrected-onset")
                report["adjusted"] += 1
        report["transitions"].append(detail)

    return report


def _classify(
    previous,
    following,
    vocals: np.ndarray,
    instrumental: np.ndarray | None,
    *,
    sample_rate: int,
    frame_seconds: float,
    hop_seconds: float,
    minimum_pause: float,
    minimum_attack: float,
    maximum_early_onset_shift: float,
) -> LineTransition:
    previous_end = float(previous.words[-1]["end"])
    next_start = float(following.words[0]["start"])
    lower = max(0, min(previous_end - .12, next_start - .10))
    upper = max(previous_end + .30, next_start + .20)
    centers = np.arange(lower, upper + .0001, hop_seconds, dtype=np.float64)
    if len(centers) < 5:
        return LineTransition("ambiguous", previous_end, next_start,
                              previous_end, next_start, 0)

    vocal_db = np.asarray([
        _rms_db(vocals, float(center), sample_rate, frame_seconds)
        for center in centers])
    contrast_db = None
    if instrumental is not None:
        contrast_db = np.asarray([
            _rms_db(vocals, float(center), sample_rate, frame_seconds)
            - _rms_db(instrumental, float(center), sample_rate, frame_seconds)
            for center in centers])

    release_index = int(np.argmin(np.abs(centers - previous_end)))
    onset_index = int(np.argmin(np.abs(centers - next_start)))
    valley_mask = centers >= centers[release_index] - .04
    valley_mask &= centers <= centers[onset_index] + .04
    if np.count_nonzero(valley_mask) < 3:
        return LineTransition("ambiguous", previous_end, next_start,
                              previous_end, next_start, 0)

    valley_indices = np.flatnonzero(valley_mask)
    attack_indices = np.flatnonzero(centers > centers[valley_indices[-1]])
    attack_indices = attack_indices[:8]
    if len(attack_indices) < 3:
        return LineTransition("ambiguous", previous_end, next_start,
                              previous_end, next_start, 0)

    strongest_valley = int(valley_indices[np.argmin(vocal_db[valley_indices])])
    strongest_attack = int(attack_indices[np.argmax(vocal_db[attack_indices])])
    if strongest_attack <= strongest_valley:
        return LineTransition("ambiguous", previous_end, next_start,
                              previous_end, next_start, 0)

    valley_center = float(centers[strongest_valley])
    attack_center = float(centers[strongest_attack])
    pre_valley = ((centers >= valley_center - .08)
                  & (centers < valley_center - .02))
    at_attack = ((centers >= attack_center)
                 & (centers <= attack_center + .05))
    after_attack = ((centers > attack_center + .03)
                    & (centers <= attack_center + .18))
    if np.count_nonzero(pre_valley) < 2 or np.count_nonzero(at_attack) < 2:
        return LineTransition("ambiguous", previous_end, next_start,
                              previous_end, next_start, 0)

    pre_level = float(np.median(vocal_db[pre_valley]))
    valley_level = float(vocal_db[strongest_valley])
    attack_level = float(np.median(vocal_db[at_attack]))
    sustain_level = (float(np.median(vocal_db[after_attack]))
                     if np.count_nonzero(after_attack) else attack_level)
    rise = attack_level - valley_level
    sustained_rise = sustain_level - valley_level
    pause = attack_center - valley_center
    confidence = min(1.0, max(0.0, .18 + rise / 18 + sustained_rise / 22))
    if contrast_db is not None:
        attack_contrast = float(np.median(contrast_db[at_attack]))
        valley_contrast = float(contrast_db[strongest_valley])
        confidence += min(.25, max(-.10, (attack_contrast - valley_contrast) / 24))

    if pause < minimum_pause and next_start - previous_end <= minimum_pause:
        return LineTransition("legato", previous_end, next_start,
                              previous_end, next_start, min(.65, confidence))
    if rise < 6 or sustained_rise < 4 or valley_level >= pre_level - 2:
        return LineTransition("ambiguous", previous_end, next_start,
                              previous_end, next_start, min(.55, confidence))
    if next_start - previous_end <= minimum_pause:
        return LineTransition("legato", previous_end, next_start,
                              previous_end, next_start, min(.65, confidence))
    if attack_center - next_start > .24 or valley_center - previous_end > .24:
        return LineTransition("ambiguous", previous_end, next_start,
                              previous_end, next_start, min(.60, confidence))
    if next_start - previous_end > 1.2:
        return LineTransition("ambiguous", previous_end, next_start,
                              previous_end, next_start, min(.60, confidence))
    if (attack_center - valley_center) < minimum_attack:
        return LineTransition("ambiguous", previous_end, next_start,
                              previous_end, next_start, min(.55, confidence))

    return LineTransition(
        "separated", previous_end, next_start,
        max(float(previous.words[-1]["start"]) + .05,
             min(previous_end, valley_center + frame_seconds / 2)),
        max(next_start - maximum_early_onset_shift, attack_center), min(1.0, confidence))


def _rms_db(signal: np.ndarray, center: float, sample_rate: int,
            frame_seconds: float) -> float:
    half = max(1, int(round(frame_seconds * sample_rate / 2)))
    sample = int(round(center * sample_rate))
    window = signal[max(0, sample - half):min(len(signal), sample + half)]
    if not len(window):
        return -120.0
    rms = float(np.sqrt(np.mean(np.square(window)) + 1e-12))
    return 20.0 * math.log10(rms + 1e-12)
