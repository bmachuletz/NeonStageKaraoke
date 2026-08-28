from __future__ import annotations

import os
from dataclasses import dataclass, field
from enum import StrEnum
from pathlib import Path


DEFAULT_STAGE_SEPARATOR_MODEL = (
    "mel_band_roformer_karaoke_aufr33_viperx_sdr_10.1956.ckpt"
)
DEFAULT_ANALYSIS_SEPARATOR_MODELS = (
    "model_bs_roformer_ep_317_sdr_12.9755.ckpt",
    "model_mel_band_roformer_ep_3005_sdr_11.4360.ckpt",
)


def enabled(name: str, default: bool = False) -> bool:
    value = os.getenv(name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


class StemPurpose(StrEnum):
    ANALYSIS = "analysis"
    STAGE = "stage"


@dataclass(frozen=True, slots=True)
class StemArtifact:
    path: Path
    purpose: StemPurpose
    role: str
    separator: str
    model: str
    candidate_id: str
    candidate_score: float | None = None
    track_id: str | None = None
    metadata: dict = field(default_factory=dict)

    def report(self) -> dict:
        return {
            "purpose": self.purpose.value,
            "role": self.role,
            "separator": self.separator,
            "model": self.model,
            "candidate_id": self.candidate_id,
            "candidate_score": self.candidate_score,
            "track_id": self.track_id,
            "path": self.path.name,
            "metadata": self.metadata,
        }


@dataclass(frozen=True, slots=True)
class SeparationConfig:
    analysis_enabled: bool
    analysis_models: tuple[str, ...]
    stage_model: str
    original_mix_mode: str
    original_mix_ratio: float
    keep_candidates: bool

    @classmethod
    def from_environment(cls) -> "SeparationConfig":
        raw_models = os.getenv(
            "LRC_ANALYSIS_SEPARATOR_MODELS",
            ",".join(DEFAULT_ANALYSIS_SEPARATOR_MODELS),
        )
        models = tuple(dict.fromkeys(
            item.strip() for item in raw_models.split(",") if item.strip()
        ))
        mode = os.getenv("LRC_ORIGINAL_MIX_BLEND_MODE", "fallback").strip().lower()
        if mode not in {"off", "candidate", "fallback"}:
            raise ValueError(
                "LRC_ORIGINAL_MIX_BLEND_MODE muss off, candidate oder fallback sein")
        ratio = float(os.getenv("LRC_ORIGINAL_MIX_BLEND_RATIO", "0.15"))
        if not 0 < ratio < 1:
            raise ValueError(
                "LRC_ORIGINAL_MIX_BLEND_RATIO muss zwischen 0 und 1 liegen")
        config = cls(
            analysis_enabled=enabled("LRC_ANALYSIS_SEPARATOR_ENABLED", True),
            analysis_models=models,
            stage_model=os.getenv(
                "LRC_STAGE_SEPARATOR_MODEL",
                os.getenv("UVR_MODEL", DEFAULT_STAGE_SEPARATOR_MODEL),
            ).strip(),
            original_mix_mode=mode,
            original_mix_ratio=ratio,
            keep_candidates=enabled("LRC_ALIGNMENT_KEEP_CANDIDATES"),
        )
        if config.analysis_enabled and not config.analysis_models:
            raise ValueError(
                "Mindestens ein LRC_ANALYSIS_SEPARATOR_MODELS-Modell ist erforderlich")
        if not config.stage_model:
            raise ValueError("LRC_STAGE_SEPARATOR_MODEL darf nicht leer sein")
        return config


def separator_family(model: str) -> str:
    lowered = model.lower()
    if "bs_roformer" in lowered:
        return "bs-roformer"
    if "mel_band_roformer" in lowered:
        return "mel-band-roformer"
    return "audio-separator"
