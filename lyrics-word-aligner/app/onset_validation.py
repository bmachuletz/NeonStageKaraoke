from __future__ import annotations

import numpy as np


def validate_line_onsets(audio: np.ndarray, lines: list, sample_rate: int = 16000,
                         *, window_seconds: float = 0.22) -> dict:
    """Measure spectral-flux evidence near aligned line starts without moving them."""
    frame, hop = 400, 160  # 25 ms / 10 ms
    if len(audio) < frame * 2:
        return {"method": "vocal-spectral-flux-v1", "checked_lines": 0, "supported_lines": 0,
                "measurements": []}
    window = np.hanning(frame).astype(np.float32)
    previous = None
    flux = []
    for offset in range(0, len(audio) - frame + 1, hop):
        spectrum = np.abs(np.fft.rfft(audio[offset:offset + frame] * window))
        value = 0.0 if previous is None else float(np.maximum(0, spectrum - previous).sum())
        flux.append(value)
        previous = spectrum
    values = np.asarray(flux, dtype=np.float64)
    if not np.any(values > 0):
        return {"method": "vocal-spectral-flux-v1", "checked_lines": 0, "supported_lines": 0,
                "measurements": []}
    global_floor = float(np.percentile(values, 65))
    radius = max(1, int(window_seconds * sample_rate / hop))
    measurements = []
    for index, line in enumerate(lines):
        if not line.words:
            continue
        expected = float(line.words[0]["start"])
        center = int(expected * sample_rate / hop)
        first, end = max(1, center - radius), min(len(values) - 1, center + radius + 1)
        peaks = [position for position in range(first, end)
                 if values[position] >= values[position - 1] and values[position] >= values[position + 1]
                 and values[position] >= global_floor]
        if not peaks:
            continue
        peak = min(peaks, key=lambda position: (abs(position - center), -values[position]))
        local = values[first:end]
        prominence = float(values[peak] / max(1e-9, np.percentile(local, 50)))
        detected = peak * hop / sample_rate
        delta_ms = round((detected - expected) * 1000, 1)
        measurements.append({"line": index + 1, "expected": round(expected, 3),
                             "detected": round(detected, 3), "delta_ms": delta_ms,
                             "prominence": round(prominence, 3),
                             "supported": abs(delta_ms) <= 120 and prominence >= 1.15})
    supported = sum(item["supported"] for item in measurements)
    deltas = [abs(float(item["delta_ms"])) for item in measurements]
    return {"method": "vocal-spectral-flux-v1", "checked_lines": len(measurements),
            "supported_lines": supported,
            "median_absolute_delta_ms": round(float(np.median(deltas)), 1) if deltas else None,
            "measurements": measurements}


def apply_supported_onset_refinements(lines: list, report: dict, *, minimum_prominence: float = 3.0,
                                      minimum_advance_ms: float = 80,
                                      maximum_advance_ms: float = 220,
                                      previous_tolerance_ms: float = 30) -> dict:
    """Move only a first-word edge backed by a strong, unambiguous earlier onset."""
    applied = []
    by_line = {int(item["line"]): item for item in report.get("measurements", [])}
    for line_number, item in by_line.items():
        advance = -float(item["delta_ms"])
        if not (minimum_advance_ms <= advance <= maximum_advance_ms):
            continue
        if float(item["prominence"]) < minimum_prominence:
            continue
        index = line_number - 1
        line = lines[index]
        if not line.words:
            continue
        detected = float(item["detected"])
        if index > 0 and lines[index - 1].words:
            previous_end = float(lines[index - 1].words[-1]["end"])
            if detected < previous_end - previous_tolerance_ms / 1000:
                continue
        first = line.words[0]
        if float(first["end"]) - detected < 0.03:
            continue
        old = float(first["start"])
        first["start"] = round(detected, 3)
        first["onset_refinement_ms"] = round((detected - old) * 1000, 1)
        first["onset_prominence"] = item["prominence"]
        line.timestamp = detected
        applied.append({"line": line_number, "old": round(old, 3),
                        "new": round(detected, 3), "delta_ms": round((detected - old) * 1000, 1)})
    return {"method": "strong-unambiguous-vocal-onset-v1", "applied": len(applied),
            "refinements": applied}
