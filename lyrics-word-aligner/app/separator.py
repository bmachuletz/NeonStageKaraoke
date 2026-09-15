from __future__ import annotations
import os
import gc
import re
from dataclasses import dataclass
from pathlib import Path

from .stem_roles import DEFAULT_STAGE_SEPARATOR_MODEL

KARAOKE_MODEL = os.getenv(
    "LRC_STAGE_SEPARATOR_MODEL",
    os.getenv("UVR_MODEL", DEFAULT_STAGE_SEPARATOR_MODEL),
)


@dataclass(frozen=True)
class StemPaths:
    vocals: Path
    instrumental: Path


def _stem_role(path: Path) -> str | None:
    """Read the separator's output role, not words in the input filename.

    A second separation pass commonly receives ``song.instrumental.flac``.
    audio-separator keeps that input stem in both output filenames, so a broad
    ``"instrumental" in name`` check misclassified the recovered Vocal output.
    Parenthesised output labels and final role suffixes are authoritative.
    """
    name = path.stem.casefold()
    labels = re.findall(r"\(([^()]*)\)", name)
    role = labels[-1].strip() if labels else name
    if role in {"vocals", "vocal"} or role.endswith("_vocals"):
        return "vocals"
    if role in {"instrumental", "no vocals", "no_vocals"} or any(
            role.endswith(suffix) for suffix in ("_instrumental", "_no vocals", "_no_vocals")):
        return "instrumental"
    return None


def separate_stems(audio_path: str | Path, output_dir: str | Path, *, model_name: str | None = None) -> StemPaths:
    """Split audio into sample-aligned vocal and instrumental stems."""
    from audio_separator.separator import Separator

    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    model_dir = Path(os.getenv("MODEL_CACHE", "/models")) / "audio-separator"
    model_dir.mkdir(parents=True, exist_ok=True)

    separator = Separator(
        model_file_dir=str(model_dir),
        output_dir=str(output_dir),
        output_format="WAV",
    )
    try:
        separator.load_model(model_name or KARAOKE_MODEL)
        outputs = separator.separate(str(audio_path))
    finally:
        # Candidate separation and ASR run sequentially on an 8 GB GPU. Release
        # the separator weights and allocator cache before loading Qwen/Whisper.
        del separator
        gc.collect()
        try:
            import torch
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except ImportError:
            pass

    vocals: list[Path] = []
    instrumentals: list[Path] = []
    for item in outputs:
        path = Path(item)
        if not path.is_absolute():
            path = output_dir / path
        if not path.exists():
            continue
        role = _stem_role(path)
        if role == "instrumental":
            instrumentals.append(path)
        elif role == "vocals":
            vocals.append(path)
    if not vocals or not instrumentals:
        raise RuntimeError(f"Vocal- oder Instrumental-Spur fehlt in Separator-Ausgabe: {outputs}")
    return StemPaths(vocals[0], instrumentals[0])


def separate_vocals(audio_path: str | Path, output_dir: str | Path) -> Path:
    """Compatibility helper for analyses that only consume the vocal stem."""
    return separate_stems(audio_path, output_dir).vocals
