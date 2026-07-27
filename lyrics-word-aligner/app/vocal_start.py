from __future__ import annotations

import tempfile
from pathlib import Path

import numpy as np

from .aligner import QwenWordAligner, SAMPLE_RATE
from .audio import ffmpeg_to_mono16k, load_audio
from .models import AlignmentConfig, LrcLine
from .separator import separate_vocals


def detect_first_vocal(
    audio_path: Path,
    text: str,
    *,
    language: str,
    separator: bool,
    device: str,
    search_seconds: float,
) -> dict:
    """Find the supplied first lyric line in a broad intro window.

    Unlike the normal LRC pipeline this does not use a candidate timestamp to
    define the audio window.  The measured start can therefore be used to rank
    competing LRCLIB variants without feeding their timing back into the model.
    """
    text = " ".join(text.split())
    if not text:
        raise ValueError("Für die Vocal-Start-Analyse wird die erste Textzeile benötigt.")
    if search_seconds < 5 or search_seconds > 90:
        raise ValueError("search_seconds muss zwischen 5 und 90 liegen.")

    with tempfile.TemporaryDirectory(prefix="vocal-start-") as temp:
        temp_dir = Path(temp)
        source = separate_vocals(audio_path, temp_dir / "separated") if separator else audio_path
        wav = ffmpeg_to_mono16k(source, temp_dir / "vocals-16k.wav")
        audio = load_audio(wav)
        actual_seconds = min(search_seconds, len(audio) / SAMPLE_RATE)
        intro = np.ascontiguousarray(audio[: int(actual_seconds * SAMPLE_RATE)], dtype=np.float32)

        line = LrcLine(0.0, text, text)
        cfg = AlignmentConfig(language=language, pre_roll=0, post_roll=0, last_line_duration=actual_seconds)
        aligner = QwenWordAligner(device=device)
        try:
            aligner.align(intro, [line], cfg)
        finally:
            aligner.close()

    if not line.words:
        raise RuntimeError("Im Intro wurde kein Wortzeitpunkt erkannt.")

    first = line.words[0]
    return {
        "text": text,
        "first_word": first["word"],
        "first_word_start": float(first["start"]),
        "first_word_end": float(first["end"]),
        "search_seconds": actual_seconds,
        "alignment_device": device,
        "separation": "uvr-karaoke" if separator else "none",
        "words": line.words,
    }
