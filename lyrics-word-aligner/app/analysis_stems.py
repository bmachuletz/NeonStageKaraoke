from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

import numpy as np

from .audio import ffmpeg_to_mono16k, load_audio, select_alignment_audio
from .candidate_selection import AudioAlignmentCandidate, blend_audio
from .separator import StemPaths, separate_stems
from .stem_roles import SeparationConfig, separator_family


@dataclass(slots=True)
class AnalysisCandidates:
    candidates: list[AudioAlignmentCandidate]
    stem_pairs: dict[str, StemPaths]
    accompaniment_audio: dict[str, np.ndarray]
    mix_audio: np.ndarray
    errors: list[dict]
    legacy_fallback: bool


def _mono(path: Path, output: Path) -> np.ndarray:
    return load_audio(ffmpeg_to_mono16k(path, output))


def build_analysis_candidates(
    audio_path: Path,
    temporary_directory: Path,
    stage_stems: StemPaths | None,
    config: SeparationConfig,
    *,
    minimum_rms_ratio: float,
) -> AnalysisCandidates:
    """Create independent all-vocal candidates without changing Stage stems.

    Separator models are loaded one after another by ``separate_stems``. Its
    explicit cleanup therefore keeps the 8 GiB execution contract intact.
    """
    temporary_directory.mkdir(parents=True, exist_ok=True)
    mix_audio = _mono(audio_path, temporary_directory / "original-mix-16k.wav")
    candidates: list[AudioAlignmentCandidate] = []
    pairs: dict[str, StemPaths] = {}
    accompaniments: dict[str, np.ndarray] = {}
    errors: list[dict] = []

    if config.analysis_enabled:
        for index, model in enumerate(config.analysis_models):
            candidate_id = f"analysis-separator-{index + 1}"
            try:
                pair = separate_stems(
                    audio_path,
                    temporary_directory / candidate_id,
                    model_name=model,
                )
                vocal = _mono(pair.vocals, temporary_directory / f"{candidate_id}-vocals-16k.wav")
                accompaniment = _mono(
                    pair.instrumental,
                    temporary_directory / f"{candidate_id}-accompaniment-16k.wav",
                )
                candidates.append(AudioAlignmentCandidate(
                    candidate_id,
                    f"Analysis Vocals · {separator_family(model)}",
                    vocal,
                    "analysis-vocal-separator",
                    legacy=not candidates,
                    metadata={
                        "purpose": "analysis",
                        "model": model,
                        "separator": separator_family(model),
                    },
                ))
                pairs[candidate_id] = pair
                accompaniments[candidate_id] = accompaniment
            except Exception as error:
                errors.append({
                    "id": candidate_id,
                    "status": "failed",
                    "purpose": "analysis",
                    "model": model,
                    "error": str(error)[:500],
                })

    legacy_fallback = not candidates
    if not candidates:
        fallback_path = stage_stems.vocals if stage_stems is not None else audio_path
        fallback = _mono(fallback_path, temporary_directory / "legacy-analysis-vocals-16k.wav")
        fallback, selection = select_alignment_audio(
            fallback, mix_audio, minimum_rms_ratio=minimum_rms_ratio)
        blend_fallback = (config.original_mix_mode == "fallback"
                          and stage_stems is not None
                          and not selection["fallback_used"])
        if blend_fallback:
            fallback = blend_audio(fallback, mix_audio, config.original_mix_ratio)
        candidates.append(AudioAlignmentCandidate(
            "legacy-analysis-fallback",
            "Legacy-Analysefallback",
            fallback,
            ("original-mix-fallback" if selection["fallback_used"] else
             "vocal-original-blend-fallback" if blend_fallback else
             "legacy-stage-vocals"),
            legacy=True,
            metadata={
                "purpose": "analysis",
                "energy_fallback_used": selection["fallback_used"],
                "original_mix_ratio": (config.original_mix_ratio
                                       if blend_fallback else None),
                "model": "provided-stage-stems" if stage_stems is not None else "none",
            },
        ))
        if stage_stems is not None and not blend_fallback:
            pairs[candidates[0].id] = stage_stems
            accompaniments[candidates[0].id] = _mono(
                stage_stems.instrumental,
                temporary_directory / "legacy-analysis-accompaniment-16k.wav",
            )

    # Original and blends are analysis-only hypotheses. They have no
    # complementary pair and can therefore never leak into Stage export.
    if config.original_mix_mode == "candidate":
        candidates.append(AudioAlignmentCandidate(
            "analysis-original-mix",
            "Originalmix",
            mix_audio,
            "original-mix",
            metadata={"purpose": "analysis", "fallback_only": False},
        ))
        base = candidates[0]
        candidates.append(AudioAlignmentCandidate(
            "analysis-original-blend",
            f"Analysis Vocals + {config.original_mix_ratio:.0%} Originalmix",
            blend_audio(base.audio, mix_audio, config.original_mix_ratio),
            "vocal-original-blend",
            metadata={
                "purpose": "analysis",
                "original_mix_ratio": config.original_mix_ratio,
                "fallback_only": False,
            },
        ))

    return AnalysisCandidates(
        candidates, pairs, accompaniments, mix_audio, errors, legacy_fallback)
