from __future__ import annotations

import numpy as np


def move_ownership_seams_to_quiet_audio(
        windows: list[tuple[int, int, float, float]], audio,
        *, sample_rate: int = 16000) -> list[tuple[int, int, float, float]]:
    """Move overlap ownership seams into a genuine low-energy vocal gap.

    The inference windows remain unchanged, so both decoders retain their full
    context.  Only the point deciding which chunk owns an overlapping word is
    moved.  If an overlap contains continuous singing, the original midpoint
    is deliberately retained instead of cutting at an arbitrary phoneme dip.
    """
    if len(windows) < 2:
        return list(windows)
    signal = np.asarray(audio, dtype=np.float32).reshape(-1)
    if signal.size == 0:
        return list(windows)

    adjusted = [list(window) for window in windows]
    frame_samples = max(1, round(.08 * sample_rate))
    hop_samples = max(1, round(.01 * sample_rate))
    squared = np.square(signal.astype(np.float64, copy=False))
    prefix = np.concatenate(([0.0], np.cumsum(squared)))

    for index in range(len(adjusted) - 1):
        left = adjusted[index]
        right = adjusted[index + 1]
        overlap_start = max(int(left[0]), int(right[0]))
        overlap_end = min(int(left[1]), int(right[1]))
        if overlap_end - overlap_start < frame_samples * 3:
            continue

        # Keep enough decoder context on both sides of the ownership seam.
        margin = max(frame_samples, round((overlap_end - overlap_start) * .12))
        first_center = overlap_start + margin
        last_center = overlap_end - margin
        centers = np.arange(first_center, last_center + 1, hop_samples, dtype=np.int64)
        if centers.size == 0:
            continue
        starts = np.maximum(0, centers - frame_samples // 2)
        ends = np.minimum(signal.size, starts + frame_samples)
        rms = np.sqrt(np.maximum(0.0, (prefix[ends] - prefix[starts]) /
                                 np.maximum(1, ends - starts)))
        median = float(np.median(rms))
        quiet_index = int(np.argmin(rms))
        quiet = float(rms[quiet_index])

        # A local minimum is not automatically a pause: within sustained singing
        # consonants often have much less energy than vowels.  Move only for a
        # pronounced valley, otherwise preserve the deterministic midpoint.
        if median <= 1e-9 or quiet > median * .32:
            continue
        seam = float(centers[quiet_index]) / sample_rate
        left[3] = seam
        right[2] = seam

    return [tuple(window) for window in adjusted]
