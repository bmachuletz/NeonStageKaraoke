from __future__ import annotations

from dataclasses import dataclass
import math
import re

import numpy as np


IPA_VOWELS = frozenset("aeiouyɑɐɒæəɚɛɜɞɪɨɔɵʊʌʉɯœøɶɤɘɝ")
PLOSIVES = frozenset("pbtdkgqɢʔ")
FRICATIVES = frozenset("fvθðszʃʒçxχɣhɦɸβ")
NASALS = frozenset("mnɲŋɳɴ")
LIQUIDS = frozenset("lrɹɾʀʁjw")


@dataclass(slots=True)
class VoicingTrack:
    times: np.ndarray
    f0: np.ndarray
    probability: np.ndarray
    voiced: np.ndarray
    sample_rate: int
    frame_length: int
    hop_length: int


def serialize_voicing_evidence(track: VoicingTrack | None, summary: dict,
                               *, maximum_points: int = 12000) -> dict:
    """Compact pYIN F0/voicing evidence for reports and later pitch tooling."""
    if track is None:
        return {**summary, "family": "pyin", "track": [],
                "representation": "local-monophonic-f0-and-voicing"}
    stride = max(1, int(np.ceil(len(track.times) / max(1, maximum_points))))
    points = []
    for index in range(0, len(track.times), stride):
        f0 = float(track.f0[index])
        points.append({
            "time": round(float(track.times[index]), 6),
            "f0_hz": round(f0, 4) if np.isfinite(f0) else None,
            "probability": round(float(track.probability[index]), 5),
            "voiced": bool(track.voiced[index]),
        })
    return {
        **summary,
        "family": "pyin",
        "representation": "local-monophonic-f0-and-voicing",
        "track": points,
        "track_points": len(points),
        "source_frames": len(track.times),
        "stride": stride,
        "truncated": stride > 1,
    }


def analyze_voicing(audio, sample_rate: int = 16000,
                    *, hop_seconds: float = 0.010,
                    intervals: list[tuple[float, float]] | None = None
                    ) -> tuple[VoicingTrack | None, dict]:
    """Measure F0/voicing on the exact unpadded sample timeline.

    pYIN runs on the already separated Stage vocal stem. ``center=False`` and
    explicit frame-center conversion avoid the hidden half-window timestamp
    offset which otherwise looks like a systematic karaoke latency.
    """
    signal = _mono(audio)
    frame_length = 1024
    hop_length = max(1, int(round(sample_rate * hop_seconds)))
    summary = {
        "enabled": True,
        "method": "targeted-pyin-voicing-10ms-v1.2",
        "sample_rate": sample_rate,
        "frame_length_samples": frame_length,
        "hop_length_samples": hop_length,
        "frame_center_offset_samples": frame_length // 2,
        "center_padding": False,
        "timeline": "absolute-source-samples",
    }
    if len(signal) < frame_length:
        return None, {**summary, "enabled": False, "reason": "audio-too-short"}
    requested = _merge_intervals(intervals or [(0.0, len(signal) / sample_rate)])
    requested = [(max(0.0, start), min(len(signal) / sample_rate, end))
                 for start, end in requested if end > start]
    if not requested:
        return None, {**summary, "enabled": False, "reason": "no-sustain-windows"}
    all_times = []
    all_f0 = []
    all_voiced = []
    all_probability = []
    try:
        import librosa

        for start, end in requested:
            first_sample = max(0, int(math.floor(start * sample_rate)))
            last_sample = min(len(signal), int(math.ceil(end * sample_rate)))
            segment = signal[first_sample:last_sample]
            if len(segment) < frame_length:
                continue
            f0, voiced, probability = librosa.pyin(
                segment, fmin=55.0, fmax=min(900.0, sample_rate * 0.45),
                sr=sample_rate, frame_length=frame_length, hop_length=hop_length,
                n_thresholds=24, resolution=0.2,
                center=False, fill_na=np.nan)
            frame_count = min(len(f0), len(voiced), len(probability))
            all_times.append((np.arange(frame_count, dtype=np.float64) * hop_length
                              + frame_length / 2 + first_sample) / sample_rate)
            all_f0.append(np.asarray(f0[:frame_count], dtype=np.float32))
            all_voiced.append(np.asarray(voiced[:frame_count], dtype=bool))
            all_probability.append(np.nan_to_num(
                np.asarray(probability[:frame_count], dtype=np.float32), nan=0.0))
    except (ImportError, RuntimeError, ValueError) as error:
        return None, {**summary, "enabled": False,
                      "reason": f"pyin-failed: {str(error)[:400]}"}
    if not all_times:
        return None, {**summary, "enabled": False, "reason": "windows-too-short"}
    times = np.concatenate(all_times)
    f0 = np.concatenate(all_f0)
    voiced = np.concatenate(all_voiced)
    probability = np.concatenate(all_probability)
    order = np.argsort(times, kind="stable")
    times, f0, voiced, probability = (
        values[order] for values in (times, f0, voiced, probability))
    frame_count = len(times)
    track = VoicingTrack(
        times=times,
        f0=f0, probability=probability, voiced=voiced,
        sample_rate=sample_rate,
        frame_length=frame_length,
        hop_length=hop_length,
    )
    return track, {
        **summary,
        "frames": frame_count,
        "windows": len(requested),
        "analyzed_seconds": round(sum(end - start for start, end in requested), 3),
        "voiced_frames": int(np.count_nonzero(track.voiced)),
        "voiced_share": round(float(np.mean(track.voiced)), 4),
        "mean_voiced_probability": round(float(np.mean(
            track.probability[track.voiced])), 4) if np.any(track.voiced) else 0.0,
    }


def sustain_voicing_intervals(lines: list, duration: float,
                              *, look_behind: float = 0.16,
                              look_ahead: float = 0.48) -> list[tuple[float, float]]:
    """Return only windows in which a held word release is plausible."""
    intervals = []
    for line_index, line in enumerate(lines):
        for word_index, word in enumerate(line.words):
            start = float(word["start"])
            end = float(word["end"])
            next_start = _next_word_start(lines, line_index, word_index)
            is_line_final = word_index == len(line.words) - 1
            has_measured_sustain = "acoustic_end" in word
            has_room = next_start is None or next_start - end >= 0.18
            if not has_measured_sustain and not (
                    is_line_final and has_room and end - start >= 0.35):
                continue
            upper = min(duration, end + look_ahead)
            if next_start is not None:
                upper = min(upper, next_start - 0.12)
            lower = max(0.0, float(word.get("acoustic_end", end)) - look_behind)
            if upper - lower >= 0.08:
                intervals.append((lower, upper))
    return _merge_intervals(intervals)


def compare_timing_reference(lines: list, reference) -> dict:
    """Describe timing movement against the immutable Enhanced-LRC input.

    This is intentionally not a quality verdict: an editor version may be a
    gold reference or merely a rough draft. The metrics make both improvement
    experiments and regressions observable without silently treating either
    side as truth.
    """
    summary = {
        "available": reference is not None,
        "method": "word-boundary-reference-delta-v1.2",
        "reference": "enhanced-lrc-input",
    }
    if reference is None:
        return {**summary, "reason": "no-enhanced-reference"}
    reference_lines = getattr(reference, "lines", None)
    if not isinstance(reference_lines, list) or len(reference_lines) != len(lines):
        return {**summary, "comparable": False, "reason": "line-count-mismatch"}
    start_deltas = []
    end_deltas = []
    details = []
    for index, (current, source) in enumerate(zip(lines, reference_lines)):
        current_words = current.words
        source_words = source.words
        if len(current_words) != len(source_words) or [
                _normal_word(word.get("word", "")) for word in current_words] != [
                _normal_word(word.get("word", "")) for word in source_words]:
            return {**summary, "comparable": False,
                    "reason": f"word-sequence-mismatch-line-{index + 1}"}
        line_deltas = []
        for current_word, source_word in zip(current_words, source_words):
            start_delta = (float(current_word["start"])
                           - float(source_word["start"])) * 1000
            end_delta = (float(current_word["end"])
                         - float(source_word["end"])) * 1000
            start_deltas.append(start_delta)
            end_deltas.append(end_delta)
            line_deltas.append(max(abs(start_delta), abs(end_delta)))
        if line_deltas:
            details.append({"line": index + 1,
                            "maximum_absolute_delta_ms": round(max(line_deltas), 1)})
    absolute_starts = np.abs(np.asarray(start_deltas, dtype=np.float64))
    absolute_ends = np.abs(np.asarray(end_deltas, dtype=np.float64))
    combined = np.concatenate((absolute_starts, absolute_ends)) if len(
        absolute_starts) else np.array([], dtype=np.float64)
    return {
        **summary,
        "comparable": True,
        "words": len(start_deltas),
        "median_absolute_start_delta_ms": _percentile(absolute_starts, 50),
        "p95_absolute_start_delta_ms": _percentile(absolute_starts, 95),
        "median_absolute_end_delta_ms": _percentile(absolute_ends, 50),
        "p95_absolute_end_delta_ms": _percentile(absolute_ends, 95),
        "maximum_absolute_delta_ms": round(float(np.max(combined)), 1)
        if len(combined) else 0.0,
        "boundaries_changed_over_20ms": int(np.count_nonzero(
            np.round(combined, 1) > 20.0)),
        "largest_line_deltas": sorted(
            details, key=lambda item: -item["maximum_absolute_delta_ms"])[:20],
    }


def refine_ipa_phone_path(audio, aligned_words: list[dict],
                          voicing: VoicingTrack | None,
                          *, mode: str = "select", sample_rate: int = 16000,
                          search_radius: float = 0.055,
                          minimum_path_improvement: float = 0.08) -> dict:
    """Refine IPA phone onsets with a local duration-aware monotone path.

    Each word is independent. The IPA/CTC path supplies labels and priors;
    multi-resolution DSP supplies a second observation family. A complete path
    is accepted only when every moved boundary has stronger class-specific
    evidence and all phone durations remain positive.
    """
    normalized_mode = mode.strip().lower()
    if normalized_mode not in {"off", "shadow", "select"}:
        raise ValueError("LRC_MICRO_BOUNDARY_MODE muss off, shadow oder select sein.")
    summary = {
        "enabled": normalized_mode != "off",
        "mode": normalized_mode,
        "method": "class-aware-multires-duration-viterbi-v1.2",
        "attempted_words": 0,
        "candidate_words": 0,
        "applied_words": 0,
        "moved_phone_onsets": 0,
        "details": [],
        "timebase": {
            "sample_rate": sample_rate,
            "candidate_step_samples": max(1, int(round(sample_rate * 0.0025))),
            "analysis_flanks_ms": [12.5, 25.0, 45.0],
            "frame_coordinates": "absolute-source-sample-centers",
            "implicit_padding": False,
        },
    }
    if normalized_mode == "off":
        summary["reason"] = "feature-disabled"
        return summary
    signal = _mono(audio)
    for word_index, word in enumerate(aligned_words):
        phones = word.get("phonemes")
        if not isinstance(phones, list) or len(phones) < 2:
            continue
        summary["attempted_words"] += 1
        result = _refine_word_phone_path(
            signal, phones, voicing, sample_rate=sample_rate,
            search_radius=search_radius,
            minimum_path_improvement=minimum_path_improvement)
        detail = {"word": word_index + 1,
                  "text": word.get("word", ""), **result}
        summary["details"].append(detail)
        if not result["candidate"]:
            continue
        summary["candidate_words"] += 1
        if normalized_mode != "select":
            continue
        selected = result["selected_starts"]
        original = [float(phone["start"]) for phone in phones]
        for index, (phone, new_start) in enumerate(zip(phones, selected)):
            if abs(new_start - original[index]) < 0.001:
                continue
            phone["micro_original_start"] = round(original[index], 4)
            phone["start"] = round(new_start, 4)
            phone["micro_shift_ms"] = round((new_start - original[index]) * 1000, 1)
            phone["micro_boundary_source"] = "multires-duration-viterbi-v1.2"
            if index and float(phones[index - 1]["end"]) > new_start:
                phones[index - 1]["end"] = round(new_start, 4)
            summary["moved_phone_onsets"] += 1
        word["start"] = float(phones[0]["start"])
        word["phoneme_word_start_candidate"] = float(phones[0]["start"])
        word["micro_boundary_refined"] = True
        summary["applied_words"] += 1
    return summary


def refine_sustain_releases_with_voicing(
        lines: list,
        voicing: VoicingTrack | None,
        *, mode: str = "select",
        maximum_extension: float = 0.45,
        maximum_shortening: float = 0.12,
) -> dict:
    """Use connected F0/voicing evidence to micro-adjust held vowel releases."""
    normalized_mode = mode.strip().lower()
    report = {
        "enabled": voicing is not None and normalized_mode != "off",
        "mode": normalized_mode,
        "method": "connected-pyin-sustain-release-v1.2",
        "attempted_words": 0,
        "candidate_words": 0,
        "applied_words": 0,
        "details": [],
    }
    if voicing is None or normalized_mode == "off":
        report["reason"] = "voicing-unavailable" if voicing is None else "feature-disabled"
        return report
    for line_index, line in enumerate(lines):
        for word_index, word in enumerate(line.words):
            has_prior_sustain = "acoustic_end" in word
            is_internal_word = word_index + 1 < len(line.words)
            lexical_end = float(word.get("acoustic_end", word["end"]))
            current_end = float(word["end"])
            next_start = _next_word_start(lines, line_index, word_index)
            upper = current_end + maximum_extension
            if next_start is not None:
                upper = min(upper, next_start - 0.12)
            if upper <= lexical_end + 0.06:
                continue
            # The pYIN track is intentionally sparse: only plausible sustain
            # windows are analysed.  Do not count every unrelated word in the
            # song as an attempted release correction.
            if not np.any((voicing.times >= lexical_end - 0.12)
                          & (voicing.times <= upper)):
                continue
            report["attempted_words"] += 1
            candidate = _connected_voiced_release(voicing, lexical_end, upper)
            if candidate is None:
                continue
            release, confidence, tail_probability = candidate
            delta = release - current_end
            apply = False
            reason = "within-tolerance"
            if delta >= 0.018 and delta <= maximum_extension and confidence >= 0.58:
                apply, reason = True, "confirmed-later-voiced-release"
            elif (has_prior_sustain and -maximum_shortening <= delta <= -0.018
                  and confidence >= 0.68 and tail_probability <= 0.12):
                apply, reason = True, "confirmed-earlier-voiced-release"
            activity_end = word.get("sustain_activity_end")
            if (apply and is_internal_word and delta > 0
                    and activity_end is not None
                    and release > float(activity_end) + 0.12):
                # F0 can remain stable in separator residue or a harmonic
                # instrument after the lexical vocal has stopped. For an
                # internal word, unlike a final held note, pitch alone may not
                # cross far beyond the independently measured vocal island.
                apply, reason = False, "internal-release-beyond-vocal-activity"
            detail = {
                "line": line_index + 1, "word": word_index + 1,
                "text": word.get("word", ""), "old_end": round(current_end, 3),
                "candidate_end": round(release, 3),
                "delta_ms": round(delta * 1000, 1),
                "confidence": round(confidence, 4),
                "post_release_voiced_probability": round(tail_probability, 4),
                "eligible": apply, "reason": reason,
            }
            report["details"].append(detail)
            if not apply:
                continue
            report["candidate_words"] += 1
            if normalized_mode != "select":
                continue
            word["end"] = round(release, 3)
            word["pyin_release_original"] = round(current_end, 3)
            word["pyin_release_confidence"] = round(confidence, 4)
            word["sustain_extension_ms"] = round((release - lexical_end) * 1000)
            report["applied_words"] += 1
    return report


def _refine_word_phone_path(signal: np.ndarray, phones: list[dict],
                            voicing: VoicingTrack | None, *, sample_rate: int,
                            search_radius: float,
                            minimum_path_improvement: float) -> dict:
    priors = [float(phone["start"]) for phone in phones]
    classes = [_phone_class(str(phone.get("phone", ""))) for phone in phones]
    candidate_rows = [
        _boundary_candidates(signal, prior, phone_class, voicing,
                             sample_rate, search_radius)
        for prior, phone_class in zip(priors, classes)
    ]
    scores: list[list[float]] = []
    parents: list[list[int | None]] = []
    for index, row in enumerate(candidate_rows):
        row_scores = [-math.inf] * len(row)
        row_parents: list[int | None] = [None] * len(row)
        if index == 0:
            row_scores = [float(candidate["objective"]) for candidate in row]
        else:
            prior_duration = max(0.008, priors[index] - priors[index - 1])
            minimum_duration = _minimum_phone_duration(classes[index - 1])
            for current_index, current in enumerate(row):
                options = []
                for previous_index, previous in enumerate(candidate_rows[index - 1]):
                    duration = float(current["time"]) - float(previous["time"])
                    if scores[index - 1][previous_index] == -math.inf or duration < minimum_duration:
                        continue
                    duration_scale = max(0.025, prior_duration * 0.55)
                    duration_penalty = min(
                        0.55, 0.10 * ((duration - prior_duration) / duration_scale) ** 2)
                    options.append((scores[index - 1][previous_index]
                                    + float(current["objective"]) - duration_penalty,
                                    previous_index))
                if options:
                    row_scores[current_index], row_parents[current_index] = max(options)
        scores.append(row_scores)
        parents.append(row_parents)
    if not scores or max(scores[-1]) == -math.inf:
        return {"candidate": False, "reason": "no-monotone-path"}
    selected_index = max(range(len(scores[-1])), key=lambda value: scores[-1][value])
    selected = []
    for row_index in range(len(candidate_rows) - 1, -1, -1):
        selected.append(candidate_rows[row_index][selected_index])
        parent = parents[row_index][selected_index]
        selected_index = 0 if parent is None else parent
    selected.reverse()
    baseline = [next(candidate for candidate in row if candidate["is_prior"])
                for row in candidate_rows]
    baseline_score = sum(float(candidate["objective"]) for candidate in baseline)
    selected_score = max(scores[-1])
    improvement = (selected_score - baseline_score) / len(phones)
    moved = [(index, candidate, baseline[index])
             for index, candidate in enumerate(selected)
             if abs(float(candidate["time"]) - priors[index]) >= 0.004]
    weak = [index for index, candidate, original in moved
            if float(candidate["evidence"]) < _minimum_evidence(classes[index])
            or float(candidate["evidence"]) - float(original["evidence"]) < 0.08]
    eligible = bool(moved) and not weak and improvement >= minimum_path_improvement
    return {
        "candidate": eligible,
        "reason": ("better-monotone-path" if eligible else
                   "weak-moved-boundary" if weak else
                   "minimum-improvement-not-reached"),
        "path_improvement": round(improvement, 4),
        "moved_boundaries": len(moved),
        "selected_starts": [round(float(candidate["time"]), 4)
                            for candidate in selected],
        "boundaries": [{
            "phone": phones[index].get("phone", ""), "class": classes[index],
            "old": round(priors[index], 4),
            "new": round(float(candidate["time"]), 4),
            "evidence": round(float(candidate["evidence"]), 4),
            "prior_evidence": round(float(baseline[index]["evidence"]), 4),
        } for index, candidate in enumerate(selected)],
    }


def _boundary_candidates(signal: np.ndarray, prior: float, phone_class: str,
                         voicing: VoicingTrack | None, sample_rate: int,
                         radius: float) -> list[dict]:
    step_samples = max(1, int(round(sample_rate * 0.0025)))
    center = int(round(prior * sample_rate))
    radius_samples = int(round(radius * sample_rate))
    positions = range(max(0, center - radius_samples),
                      min(len(signal), center + radius_samples) + 1, step_samples)
    candidates = []
    for position in positions:
        time = position / sample_rate
        evidence = _multires_boundary_score(
            signal, time, phone_class, voicing, sample_rate)
        shift = time - prior
        objective = evidence - 0.16 * (shift / max(radius, 1e-6)) ** 2
        candidates.append({"time": time, "evidence": evidence,
                           "objective": objective, "is_prior": False})
    prior_evidence = _multires_boundary_score(
        signal, prior, phone_class, voicing, sample_rate)
    candidates.append({"time": prior, "evidence": prior_evidence,
                       "objective": prior_evidence, "is_prior": True})
    ranked = sorted(candidates, key=lambda item: (-float(item["objective"]),
                                                   abs(float(item["time"]) - prior)))
    selected = ranked[:6]
    if not any(item["is_prior"] for item in selected):
        selected.append(next(item for item in candidates if item["is_prior"]))
    return sorted(selected, key=lambda item: float(item["time"]))


def _multires_boundary_score(signal: np.ndarray, time: float, phone_class: str,
                             voicing: VoicingTrack | None,
                             sample_rate: int) -> float:
    scores = []
    for flank_seconds in (0.0125, 0.025, 0.045):
        center = int(round(time * sample_rate))
        flank = max(64, int(round(flank_seconds * sample_rate)))
        guard = max(8, int(round(0.0025 * sample_rate)))
        left = signal[max(0, center - flank):max(0, center - guard)]
        right = signal[min(len(signal), center + guard):min(len(signal), center + flank)]
        if len(left) < 64 or len(right) < 64:
            continue
        left_rms = float(np.sqrt(np.mean(np.square(left)) + 1e-12))
        right_rms = float(np.sqrt(np.mean(np.square(right)) + 1e-12))
        energy_rise = 20 * math.log10((right_rms + 1e-8) / (left_rms + 1e-8))
        left_bands = _band_envelope(left, sample_rate)
        right_bands = _band_envelope(right, sample_rate)
        spectral_change = float(np.linalg.norm(right_bands - left_bands)
                                / math.sqrt(len(left_bands)))
        flux = float(np.maximum(0.0, right_bands - left_bands).mean())
        hf_left = _high_frequency_share(left, sample_rate)
        hf_right = _high_frequency_share(right, sample_rate)
        hf_rise = 10 * math.log10((hf_right + 1e-8) / (hf_left + 1e-8))
        energy_score = float(np.clip((energy_rise + 1.0) / 10.0, 0.0, 1.0))
        spectral_score = float(np.clip((spectral_change - 0.06) / 0.30, 0.0, 1.0))
        flux_score = float(np.clip(flux / 0.12, 0.0, 1.0))
        hf_score = float(np.clip((hf_rise + 1.0) / 10.0, 0.0, 1.0))
        voicing_rise = _voicing_rise(voicing, time, flank_seconds)
        if phone_class == "plosive":
            value = 0.48 * flux_score + 0.34 * hf_score + 0.18 * energy_score
        elif phone_class == "fricative":
            value = 0.43 * hf_score + 0.32 * flux_score + 0.25 * spectral_score
        elif phone_class == "vowel":
            value = 0.38 * energy_score + 0.30 * spectral_score + 0.32 * voicing_rise
        elif phone_class in {"nasal", "liquid"}:
            value = 0.30 * energy_score + 0.34 * spectral_score + 0.36 * voicing_rise
        else:
            value = 0.34 * flux_score + 0.40 * spectral_score + 0.26 * energy_score
        scores.append(value)
    return float(np.median(scores)) if scores else 0.0


def _band_envelope(values: np.ndarray, sample_rate: int) -> np.ndarray:
    size = len(values)
    n_fft = 1 << (size - 1).bit_length()
    spectrum = np.abs(np.fft.rfft(values * np.hanning(size), n=n_fft)) ** 2
    frequencies = np.fft.rfftfreq(n_fft, 1.0 / sample_rate)
    edges = np.geomspace(70.0, min(7000.0, sample_rate * 0.47), 25)
    bands = np.array([
        float(spectrum[(frequencies >= lower) & (frequencies < upper)].mean())
        if np.any((frequencies >= lower) & (frequencies < upper)) else 0.0
        for lower, upper in zip(edges[:-1], edges[1:])
    ], dtype=np.float64)
    result = np.log1p(bands)
    result -= result.mean()
    norm = float(np.linalg.norm(result))
    return result / norm if norm > 1e-9 else np.zeros_like(result)


def _high_frequency_share(values: np.ndarray, sample_rate: int) -> float:
    spectrum = np.abs(np.fft.rfft(values * np.hanning(len(values)))) ** 2
    frequencies = np.fft.rfftfreq(len(values), 1.0 / sample_rate)
    total = float(spectrum[(frequencies >= 80) & (frequencies <= 7500)].sum())
    high = float(spectrum[(frequencies >= 2500) & (frequencies <= 7500)].sum())
    return high / max(total, 1e-12)


def _voicing_rise(track: VoicingTrack | None, time: float, flank: float) -> float:
    if track is None or not len(track.times):
        return 0.0
    left = track.probability[(track.times >= time - flank) & (track.times < time)]
    right = track.probability[(track.times >= time) & (track.times <= time + flank)]
    if not len(left) or not len(right):
        return 0.0
    return float(np.clip((float(np.mean(right)) - float(np.mean(left)) + 0.08) / 0.55,
                         0.0, 1.0))


def _connected_voiced_release(track: VoicingTrack, lexical_end: float,
                              upper_bound: float) -> tuple[float, float, float] | None:
    selected = np.flatnonzero((track.times >= lexical_end - 0.12)
                              & (track.times <= upper_bound))
    if not len(selected):
        return None
    active = track.voiced[selected] & (track.probability[selected] >= 0.38)
    seed_positions = np.flatnonzero(active & (track.times[selected] <= lexical_end + 0.14))
    if not len(seed_positions):
        return None
    cursor = int(seed_positions[-1])
    last_active = cursor
    maximum_gap = max(1, int(round(0.12 * track.sample_rate / track.hop_length)))
    gap = 0
    for position in range(cursor + 1, len(selected)):
        if active[position]:
            last_active = position
            gap = 0
        else:
            gap += 1
            if gap > maximum_gap:
                break
    active_indices = selected[cursor:last_active + 1][active[cursor:last_active + 1]]
    if not len(active_indices):
        return None
    release = min(upper_bound, float(track.times[selected[last_active]]) + 0.04)
    confidence = float(np.mean(track.probability[active_indices]))
    tail = track.probability[(track.times > release) & (track.times <= release + 0.10)]
    tail_probability = float(np.mean(tail)) if len(tail) else 0.0
    return release, confidence, tail_probability


def _next_word_start(lines: list, line_index: int, word_index: int) -> float | None:
    words = lines[line_index].words
    if word_index + 1 < len(words):
        return float(words[word_index + 1]["start"])
    for following in lines[line_index + 1:]:
        if following.words:
            return float(following.words[0]["start"])
    return None


def _merge_intervals(intervals: list[tuple[float, float]],
                     maximum_gap: float = 0.04) -> list[tuple[float, float]]:
    merged: list[list[float]] = []
    for start, end in sorted(intervals):
        if not merged or start - merged[-1][1] > maximum_gap:
            merged.append([float(start), float(end)])
        else:
            merged[-1][1] = max(merged[-1][1], float(end))
    return [(start, end) for start, end in merged]


def _normal_word(value: str) -> str:
    return "".join(character.lower() for character in str(value)
                   if character.isalnum())


def _percentile(values: np.ndarray, percentile: float) -> float:
    return round(float(np.percentile(values, percentile)), 1) if len(values) else 0.0


def _phone_class(phone: str) -> str:
    normalized = re.sub(r"[\dˈˌ.'\-_͡]", "", phone.lower())
    if any(character in IPA_VOWELS for character in normalized):
        return "vowel"
    if any(character in PLOSIVES for character in normalized):
        return "plosive"
    if any(character in FRICATIVES for character in normalized):
        return "fricative"
    if any(character in NASALS for character in normalized):
        return "nasal"
    if any(character in LIQUIDS for character in normalized):
        return "liquid"
    return "other"


def _minimum_phone_duration(phone_class: str) -> float:
    return {"plosive": 0.008, "fricative": 0.014, "vowel": 0.025,
            "nasal": 0.018, "liquid": 0.014}.get(phone_class, 0.012)


def _minimum_evidence(phone_class: str) -> float:
    return {"plosive": 0.60, "fricative": 0.60, "vowel": 0.56,
            "nasal": 0.55, "liquid": 0.55}.get(phone_class, 0.60)


def _mono(audio) -> np.ndarray:
    signal = np.asarray(audio, dtype=np.float32)
    if signal.ndim > 1:
        signal = signal.mean(axis=tuple(range(1, signal.ndim)))
    return signal.reshape(-1)
