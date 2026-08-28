from __future__ import annotations

from pathlib import Path

import numpy as np
import soundfile as sf

from .separator import StemPaths


def _load_pair(paths: StemPaths) -> tuple[np.ndarray, np.ndarray, int]:
    vocals, vocal_rate = sf.read(paths.vocals, dtype="float32", always_2d=True)
    instrumental, instrumental_rate = sf.read(
        paths.instrumental, dtype="float32", always_2d=True)
    if vocal_rate != instrumental_rate:
        raise ValueError("Vocal- und Instrumental-Stem haben verschiedene Sampleraten")
    if vocals.shape[1] != instrumental.shape[1]:
        raise ValueError("Vocal- und Instrumental-Stem haben verschiedene Kanalzahlen")
    length = min(len(vocals), len(instrumental))
    return vocals[:length], instrumental[:length], vocal_rate


def _fit_length(values: np.ndarray, length: int) -> np.ndarray:
    if len(values) > length:
        return values[:length]
    if len(values) < length:
        return np.pad(values, ((0, length - len(values)), (0, 0)), mode="constant")
    return values


def _crossfade_mask(length: int, sample_rate: int, intervals: list[tuple[float, float]],
                    *, padding_seconds: float, fade_seconds: float) -> np.ndarray:
    mask = np.zeros(length, dtype=np.float32)
    fade_samples = max(1, round(fade_seconds * sample_rate))
    for start, end in intervals:
        first = max(0, round((start - padding_seconds) * sample_rate))
        last = min(length, round((end + padding_seconds) * sample_rate))
        if last <= first:
            continue
        local = np.ones(last - first, dtype=np.float32)
        fade = min(fade_samples, max(1, (last - first) // 2))
        local[:fade] = np.linspace(0.0, 1.0, fade, endpoint=True, dtype=np.float32)
        local[-fade:] = np.linspace(1.0, 0.0, fade, endpoint=True, dtype=np.float32)
        mask[first:last] = np.maximum(mask[first:last], local)
    return mask


def _normalize_intervals(intervals: list[tuple[float, float]], *, duration: float,
                         padding_seconds: float) -> list[tuple[float, float]]:
    """Sort, clamp and merge intervals whose padded crossfades touch."""
    valid = sorted(
        (max(0.0, float(start)), min(duration, float(end)))
        for start, end in intervals
        if float(end) > 0.0 and float(start) < duration and float(end) > float(start)
    )
    merged: list[tuple[float, float]] = []
    joining_gap = max(0.0, padding_seconds * 2.0)
    for start, end in valid:
        if not merged or start > merged[-1][1] + joining_gap:
            merged.append((start, end))
            continue
        merged[-1] = (merged[-1][0], max(merged[-1][1], end))
    return merged


def create_hybrid_stems(
    primary: StemPaths,
    alternative: StemPaths,
    intervals: list[tuple[float, float]],
    output_dir: str | Path,
    *,
    padding_seconds: float = 0.20,
    fade_seconds: float = 0.12,
) -> tuple[StemPaths, dict]:
    """Crossfade a better separator pair into selected local song intervals."""
    if not intervals:
        return primary, {"enabled": False, "reason": "no-intervals", "intervals": []}
    primary_vocals, primary_instrumental, sample_rate = _load_pair(primary)
    alternative_vocals, alternative_instrumental, alternative_rate = _load_pair(alternative)
    if sample_rate != alternative_rate:
        raise ValueError("Separator-Kandidaten haben verschiedene Sampleraten")
    if primary_vocals.shape[1] != alternative_vocals.shape[1]:
        raise ValueError("Separator-Kandidaten haben verschiedene Kanalzahlen")
    length = min(len(primary_vocals), len(primary_instrumental))
    alternative_vocals = _fit_length(alternative_vocals, length)
    alternative_instrumental = _fit_length(alternative_instrumental, length)
    normalized_intervals = _normalize_intervals(
        intervals, duration=length / sample_rate,
        padding_seconds=padding_seconds)
    if not normalized_intervals:
        return primary, {"enabled": False, "reason": "no-valid-intervals", "intervals": []}
    mask = _crossfade_mask(
        length, sample_rate, normalized_intervals,
        padding_seconds=padding_seconds, fade_seconds=fade_seconds)
    weight = mask[:, np.newaxis]
    vocals = primary_vocals * (1.0 - weight) + alternative_vocals * weight
    instrumental = (primary_instrumental * (1.0 - weight)
                    + alternative_instrumental * weight)

    output = Path(output_dir)
    output.mkdir(parents=True, exist_ok=True)
    paths = StemPaths(output / "hybrid-vocals.wav", output / "hybrid-instrumental.wav")
    sf.write(paths.vocals, vocals, sample_rate, subtype="PCM_16")
    sf.write(paths.instrumental, instrumental, sample_rate, subtype="PCM_16")
    return paths, {
        "enabled": True,
        "method": "sample-aligned-separator-crossfade-v1",
        "intervals": [
            {"start": round(start, 3), "end": round(end, 3)}
            for start, end in normalized_intervals
        ],
        "input_interval_count": len(intervals),
        "normalized_interval_count": len(normalized_intervals),
        "padding_ms": round(padding_seconds * 1000),
        "crossfade_ms": round(fade_seconds * 1000),
        "sample_rate": sample_rate,
        "channels": int(vocals.shape[1]),
        "hybridized_samples": int(np.count_nonzero(mask > 0.0)),
    }
