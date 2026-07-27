from __future__ import annotations
import os
import subprocess
from pathlib import Path
import numpy as np
import soundfile as sf


def ffmpeg_to_mono16k(source: str | Path, target: str | Path) -> Path:
    subprocess.run([
        os.getenv("FFMPEG_PATH", "ffmpeg"), "-y", "-i", str(source),
        "-vn", "-ar", "16000", "-ac", "1", "-c:a", "pcm_s16le",
        "-loglevel", "error", str(target),
    ], check=True)
    return Path(target)


def ffmpeg_to_flac(source: str | Path, target: str | Path, *, mute_before: float | None = None) -> Path:
    command = [
        os.getenv("FFMPEG_PATH", "ffmpeg"), "-y", "-i", str(source),
        "-vn",
    ]
    if mute_before is not None and mute_before > 0:
        fade_end = mute_before + 0.12
        command += ["-af", f"volume=0:enable='lt(t,{mute_before:.3f})',afade=t=in:st={mute_before:.3f}:d={fade_end-mute_before:.3f}"]
    command += [
        "-c:a", "flac", "-compression_level", "5",
        "-map_metadata", "-1", "-loglevel", "error", str(target),
    ]
    subprocess.run(command, check=True)
    return Path(target)


def load_audio(path: str | Path) -> np.ndarray:
    data, rate = sf.read(path, dtype="float32", always_2d=False)
    if rate != 16000:
        raise ValueError(f"Erwartet werden 16 kHz, erhalten: {rate}")
    if data.ndim > 1:
        data = data.mean(axis=1)
    return np.ascontiguousarray(data, dtype=np.float32)


def select_alignment_audio(vocals: np.ndarray, mix: np.ndarray,
                           *, minimum_rms_ratio: float = 0.05) -> tuple[np.ndarray, dict]:
    """Fall back to the mix when source separation removed nearly all vocals."""
    vocal_rms = float(np.sqrt(np.mean(np.square(vocals, dtype=np.float64)))) if len(vocals) else 0.0
    mix_rms = float(np.sqrt(np.mean(np.square(mix, dtype=np.float64)))) if len(mix) else 0.0
    ratio = vocal_rms / max(mix_rms, 1e-12)
    use_mix = mix_rms > 1e-8 and ratio < minimum_rms_ratio
    return (mix if use_mix else vocals), {
        "source": "original-mix" if use_mix else "separated-vocals",
        "vocal_rms": round(vocal_rms, 8),
        "mix_rms": round(mix_rms, 8),
        "vocal_to_mix_rms_ratio": round(ratio, 5),
        "minimum_vocal_to_mix_rms_ratio": minimum_rms_ratio,
        "fallback_used": use_mix,
        "method": "separation-energy-sanity-v1",
    }
