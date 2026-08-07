from __future__ import annotations

import math

import numpy as np


def refine_syllable_boundaries(
        audio,
        syllables: list[dict],
        *,
        sample_rate: int = 16000,
        prior_is_acoustic: bool = False,
) -> tuple[list[dict], dict]:
    """Refine internal syllable boundaries with local acoustic change points.

    The text aligners remain responsible for the word window and syllable
    order.  This function only searches close to an existing internal
    boundary.  It combines spectral-envelope change, positive spectral flux
    and energy-envelope change; no single energy threshold may move a
    boundary.  This is deliberately conservative because note changes and
    accompaniment leakage are not necessarily linguistic boundaries.
    """
    summary = {
        "method": "joint-monotone-multiband-change-point-v2",
        "attempted_boundaries": 0,
        "refined_boundaries": 0,
        "mean_confidence": 0.0,
    }
    if audio is None or len(syllables) < 2:
        return syllables, summary

    word_start = float(syllables[0]["start"])
    word_end = float(syllables[-1]["end"])
    if word_end - word_start < 0.16:
        return syllables, summary

    signal = _mono(audio)
    first_sample = max(0, int(math.floor(word_start * sample_rate)))
    last_sample = min(len(signal), int(math.ceil(word_end * sample_rate)))
    if last_sample - first_sample < int(0.12 * sample_rate):
        return syllables, summary

    features = _change_features(signal[first_sample:last_sample], sample_rate)
    if features is None:
        return syllables, summary
    times, evidence, support = features
    times = times + first_sample / sample_rate

    duration = word_end - word_start
    average_part = duration / len(syllables)
    search_radius = min(0.13 if prior_is_acoustic else 0.22,
                        max(0.045, average_part * (0.42 if prior_is_acoustic else 0.68)))
    minimum_part = min(0.055, max(0.025, average_part * 0.28))
    accepted_confidences: list[float] = []
    priors = [float(part["end"]) for part in syllables[:-1]]
    threshold = 1.55 if prior_is_acoustic else 1.35
    candidate_rows: list[list[dict]] = []
    for index, prior in enumerate(priors):
        summary["attempted_boundaries"] += 1
        lower = max(word_start + minimum_part, prior - search_radius)
        upper = min(word_end - minimum_part, prior + search_radius)
        candidates = np.flatnonzero((times >= lower) & (times <= upper))
        row = [{"time": prior, "score": 0.0, "evidence": 0.0,
                "shift": 0.0, "is_prior": True}]
        if len(candidates):
            for best_index in _local_peak_indices(candidates, evidence):
                peak_evidence = float(evidence[best_index])
                independent_support = int(support[best_index])
                shift = float(times[best_index] - prior)
                normalized_shift = shift / max(search_radius, 1e-6)
                score = peak_evidence - threshold - 0.55 * normalized_shift * normalized_shift
                if independent_support >= 2 and peak_evidence >= threshold and score > 0.0:
                    row.append({
                        "time": float(times[best_index]), "score": score,
                        "evidence": peak_evidence, "shift": shift, "is_prior": False,
                        "support": independent_support,
                    })
        candidate_rows.append(row)

    selected = _select_monotone_boundaries(candidate_rows, minimum_part)
    if selected is None:
        return syllables, summary

    for index, choice in enumerate(selected):
        if choice["is_prior"]:
            # The acoustic evidence confirms the CTC boundary, but rewriting a
            # sub-frame difference would only create version noise.
            continue
        shift = float(choice["shift"])
        if prior_is_acoustic and abs(shift) < 0.012:
            continue
        peak_evidence = float(choice["evidence"])
        confidence = min(0.94, 0.48 + 0.10 * (peak_evidence - threshold + 1.0)
                         + 0.06 * (int(choice["support"]) - 1))
        boundary = round(float(choice["time"]), 3)
        left = syllables[index]
        right = syllables[index + 1]
        prior_source = left.get("boundary_source", "duration-prior")
        left["end"] = boundary
        right["start"] = boundary
        left["boundary_source"] = "acoustic-change-point"
        left["boundary_prior_source"] = prior_source
        left["boundary_shift_ms"] = round(shift * 1000, 1)
        left["boundary_confidence"] = round(confidence, 3)
        left["boundary_independent_support"] = int(choice["support"])
        summary["refined_boundaries"] += 1
        accepted_confidences.append(confidence)

    if accepted_confidences:
        summary["mean_confidence"] = round(
            sum(accepted_confidences) / len(accepted_confidences), 3)
    return syllables, summary


def _local_peak_indices(indices: np.ndarray, evidence: np.ndarray,
                        maximum: int = 8) -> list[int]:
    """Return a small, deterministic set of distinct change-point candidates."""
    selected = []
    available = {int(index) for index in indices}
    for index in indices:
        current = int(index)
        if (current - 1 not in available or evidence[current] >= evidence[current - 1]) and (
                current + 1 not in available or evidence[current] >= evidence[current + 1]):
            selected.append(current)
    return sorted(selected, key=lambda index: (-float(evidence[index]), index))[:maximum]


def _select_monotone_boundaries(candidate_rows: list[list[dict]],
                                minimum_part: float) -> list[dict] | None:
    """Choose all boundaries jointly while preserving syllable order.

    A strong candidate for one syllable must not steal the time owned by its
    neighbour. Keeping the prior always has score zero, so weak acoustic
    evidence cannot make the result worse merely to complete a path.
    """
    if not candidate_rows:
        return []
    scores: list[list[float]] = []
    parents: list[list[int | None]] = []
    for row_index, row in enumerate(candidate_rows):
        row_scores = [-math.inf] * len(row)
        row_parents: list[int | None] = [None] * len(row)
        if row_index == 0:
            row_scores = [float(candidate["score"]) for candidate in row]
        else:
            previous = candidate_rows[row_index - 1]
            for current_index, current in enumerate(row):
                allowed = [index for index, prior in enumerate(previous)
                           if scores[-1][index] > -math.inf
                           and float(current["time"]) - float(prior["time"])
                           >= minimum_part]
                if allowed:
                    parent = max(allowed, key=lambda index: scores[-1][index])
                    row_scores[current_index] = (scores[-1][parent]
                                                 + float(current["score"]))
                    row_parents[current_index] = parent
        scores.append(row_scores)
        parents.append(row_parents)
    if max(scores[-1]) == -math.inf:
        return None
    index = max(range(len(scores[-1])), key=lambda current: scores[-1][current])
    result = []
    for row_index in range(len(candidate_rows) - 1, -1, -1):
        result.append(candidate_rows[row_index][index])
        parent = parents[row_index][index]
        if row_index and parent is None:
            return None
        index = 0 if parent is None else parent
    return list(reversed(result))


def _mono(audio) -> np.ndarray:
    signal = np.asarray(audio, dtype=np.float32)
    if signal.ndim > 1:
        signal = signal.mean(axis=tuple(range(1, signal.ndim)))
    return signal.reshape(-1)


def _change_features(signal: np.ndarray, sample_rate: int) -> tuple[
        np.ndarray, np.ndarray, np.ndarray] | None:
    frame_length = max(128, int(round(0.025 * sample_rate)))
    hop = max(32, int(round(0.005 * sample_rate)))
    if len(signal) < frame_length * 2:
        return None
    frame_count = 1 + (len(signal) - frame_length) // hop
    frames = np.lib.stride_tricks.sliding_window_view(signal, frame_length)[::hop][:frame_count]
    frames = frames * np.hanning(frame_length).astype(np.float32)
    n_fft = 1 << (frame_length - 1).bit_length()
    power = np.abs(np.fft.rfft(frames, n=n_fft, axis=1)) ** 2
    frequencies = np.fft.rfftfreq(n_fft, 1.0 / sample_rate)

    # Logarithmic bands approximate the slowly varying vocal tract envelope.
    # Normalising each frame suppresses pure loudness and pitch-energy changes.
    edges = np.geomspace(80.0, min(6200.0, sample_rate * 0.47), 25)
    bands = []
    for lower, upper in zip(edges[:-1], edges[1:]):
        selected = (frequencies >= lower) & (frequencies < upper)
        bands.append(power[:, selected].mean(axis=1) if np.any(selected)
                     else np.zeros(frame_count, dtype=np.float32))
    spectrum = np.log1p(np.stack(bands, axis=1))
    spectrum -= spectrum.mean(axis=1, keepdims=True)
    spectrum /= np.linalg.norm(spectrum, axis=1, keepdims=True) + 1e-7
    energy = np.log(np.sqrt(np.mean(np.square(frames), axis=1)) + 1e-7)

    flank = max(2, int(round(0.020 * sample_rate / hop)))
    spectral_change = np.zeros(frame_count, dtype=np.float32)
    positive_flux = np.zeros(frame_count, dtype=np.float32)
    energy_change = np.zeros(frame_count, dtype=np.float32)
    energy_valley = np.zeros(frame_count, dtype=np.float32)
    for index in range(flank, frame_count - flank):
        left = spectrum[index - flank:index].mean(axis=0)
        right = spectrum[index:index + flank].mean(axis=0)
        spectral_change[index] = np.linalg.norm(right - left) / math.sqrt(spectrum.shape[1])
        positive_flux[index] = np.maximum(0.0, right - left).mean()
        left_energy = float(energy[index - flank:index].mean())
        right_energy = float(energy[index:index + flank].mean())
        energy_change[index] = abs(right_energy - left_energy)
        energy_valley[index] = max(0.0, (left_energy + right_energy) * 0.5 - float(energy[index]))

    spectral_z = _robust_positive_z(spectral_change)
    flux_z = _robust_positive_z(positive_flux)
    energy_change_z = _robust_positive_z(energy_change)
    energy_valley_z = _robust_positive_z(energy_valley)
    evidence = (0.58 * spectral_z + 0.18 * flux_z
                + 0.14 * energy_change_z + 0.10 * energy_valley_z)
    support = ((spectral_z >= 1.2).astype(np.uint8)
               + (flux_z >= 1.0).astype(np.uint8)
               + (np.maximum(energy_change_z, energy_valley_z) >= 1.0).astype(np.uint8))
    # Three-frame smoothing avoids selecting a single FFT-frame glitch.
    evidence = np.convolve(evidence, np.ones(3, dtype=np.float32) / 3.0, mode="same")
    support = np.maximum.reduce((support, np.roll(support, 1), np.roll(support, -1)))
    support[[0, -1]] = 0
    centers = (np.arange(frame_count) * hop + frame_length / 2) / sample_rate
    return centers, evidence, support


def _robust_positive_z(values: np.ndarray) -> np.ndarray:
    median = float(np.median(values))
    mad = float(np.median(np.abs(values - median)))
    scale = max(1e-6, 1.4826 * mad)
    return np.maximum(0.0, (values - median) / scale)
