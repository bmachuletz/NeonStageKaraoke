from __future__ import annotations

import os
import math
from dataclasses import dataclass, field
from typing import Callable

import numpy as np


def _enabled(name: str, default: bool = False) -> bool:
    value = os.getenv(name)
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


@dataclass(frozen=True, slots=True)
class CandidateSelectionConfig:
    enabled: bool = False
    include_original_mix: bool = False
    blend_ratios: tuple[float, ...] = ()
    minimum_improvement: float = 0.01
    coverage_weight: float = 0.35
    matching_weight: float = 0.35
    similarity_weight: float = 0.30
    alignment_quality_weight: float = 0.15
    pitch_quality_weight: float = 0.08
    keep_candidate_files: bool = False

    @classmethod
    def from_environment(cls) -> "CandidateSelectionConfig":
        ratios = tuple(
            float(item.strip())
            for item in os.getenv("LRC_ALIGNMENT_BLEND_RATIOS", "0.10,0.15").split(",")
            if item.strip()
        ) if _enabled("LRC_ALIGNMENT_CANDIDATE_BLEND") else ()
        config = cls(
            enabled=_enabled("LRC_ALIGNMENT_CANDIDATES"),
            include_original_mix=_enabled("LRC_ALIGNMENT_CANDIDATE_ORIGINAL"),
            blend_ratios=ratios,
            minimum_improvement=float(os.getenv("LRC_ALIGNMENT_MIN_IMPROVEMENT", "0.01")),
            coverage_weight=float(os.getenv("LRC_ALIGNMENT_WEIGHT_COVERAGE", "0.35")),
            matching_weight=float(os.getenv("LRC_ALIGNMENT_WEIGHT_MATCHING", "0.35")),
            similarity_weight=float(os.getenv("LRC_ALIGNMENT_WEIGHT_SIMILARITY", "0.30")),
            alignment_quality_weight=float(os.getenv(
                "LRC_ALIGNMENT_WEIGHT_ALIGNMENT_QUALITY", "0.15")),
            pitch_quality_weight=float(os.getenv(
                "LRC_ALIGNMENT_WEIGHT_PITCH_QUALITY", "0.08")),
            keep_candidate_files=_enabled("LRC_ALIGNMENT_KEEP_CANDIDATES"),
        )
        config.validate()
        return config

    def validate(self) -> None:
        if self.minimum_improvement < 0 or self.minimum_improvement > 1:
            raise ValueError("LRC_ALIGNMENT_MIN_IMPROVEMENT muss zwischen 0 und 1 liegen")
        if any(ratio <= 0 or ratio >= 1 for ratio in self.blend_ratios):
            raise ValueError("Vocal/Original-Mischungsverhältnisse müssen zwischen 0 und 1 liegen")
        if min(self.coverage_weight, self.matching_weight, self.similarity_weight,
               self.alignment_quality_weight) < 0:
            raise ValueError("Kandidaten-Gewichtungen dürfen nicht negativ sein")
        if not 0 <= self.pitch_quality_weight <= .10:
            raise ValueError(
                "LRC_ALIGNMENT_WEIGHT_PITCH_QUALITY darf höchstens 0.10 betragen")
        if self.coverage_weight + self.matching_weight + self.similarity_weight <= 0:
            raise ValueError("Mindestens eine Kandidaten-Gewichtung muss positiv sein")


@dataclass(slots=True)
class AudioAlignmentCandidate:
    id: str
    display_name: str
    audio: np.ndarray
    type: str
    legacy: bool = False
    metadata: dict = field(default_factory=dict)


def timed_anchor_alignment_quality(lines: list, activity: list[tuple[float, float]]) -> float | None:
    """Measure whether a candidate supports existing LRC anchors.

    Untimed lyrics deliberately return ``None`` so the optional metric cannot
    invent a quality penalty where no temporal reference exists.
    """
    anchors = [float(line.source_timestamp if line.source_timestamp is not None
                     else line.timestamp)
               for line in lines if getattr(line, "timed_input", False)]
    if len(anchors) < 2 or not activity:
        return None
    scores = []
    for index, start in enumerate(anchors):
        end = anchors[index + 1] if index + 1 < len(anchors) else start + 6.0
        containing = [region for region in activity
                      if region[0] <= start + .35 and region[1] >= start - .35]
        if containing:
            distance = min(abs(region[0] - start) for region in containing)
        else:
            distance = min(abs(region[0] - start) for region in activity)
        onset = math.exp(-distance / .55)
        window = max(.1, end - start)
        overlap = sum(max(0.0, min(end, upper) - max(start, lower))
                      for lower, upper in activity if upper > start and lower < end)
        coverage = min(1.0, overlap / window)
        scores.append(.60 * onset + .40 * coverage)
    return float(sum(scores) / len(scores))


def blend_audio(vocals: np.ndarray, original: np.ndarray, ratio: float,
                *, maximum_length_difference: int = 160) -> np.ndarray:
    """Blend against the original timeline, tolerating only resampler-sized tail differences.

    Source separation and MP3 decoding can round the final duration by one or a few
    samples. The beginning must never move: vocals are cropped or zero-padded only at
    the end, and the returned candidate always has the original signal's shape.
    """
    if vocals.ndim != original.ndim or vocals.shape[1:] != original.shape[1:]:
        raise ValueError("Vocal- und Originalsignal müssen dieselbe Kanalzahl besitzen")
    difference = len(vocals) - len(original)
    if abs(difference) > maximum_length_difference:
        raise ValueError(
            f"Vocal- und Originalsignal unterscheiden sich um {abs(difference)} Samples; "
            f"zulässig sind höchstens {maximum_length_difference}")
    if difference > 0:
        vocals = vocals[:len(original)]
    elif difference < 0:
        padding = [(0, -difference)] + [(0, 0)] * (vocals.ndim - 1)
        vocals = np.pad(vocals, padding, mode="constant")
    if ratio <= 0 or ratio >= 1:
        raise ValueError("Mischungsverhältnis muss zwischen 0 und 1 liegen")
    mixed = vocals.astype(np.float64) * (1.0 - ratio) + original.astype(np.float64) * ratio
    peak = float(np.max(np.abs(mixed))) if len(mixed) else 0.0
    if peak > 1.0:
        mixed /= peak
    return np.ascontiguousarray(mixed, dtype=np.float32)


def transcript_score(comparison: dict, config: CandidateSelectionConfig,
                     alignment_quality: float | None = None,
                     pitch_quality: float | None = None) -> tuple[float, dict]:
    expected = max(1, int(comparison.get("expected_words", 0)))
    coverage = min(1.0, int(comparison.get("recognized_words", 0)) / expected)
    matching = min(1.0, int(comparison.get("matching_words", 0)) / expected)
    similarity = min(1.0, max(0.0, float(comparison.get("similarity", 0.0))))
    weight = config.coverage_weight + config.matching_weight + config.similarity_weight
    total = (coverage * config.coverage_weight + matching * config.matching_weight +
             similarity * config.similarity_weight)
    normalized_alignment = None
    if alignment_quality is not None:
        normalized_alignment = min(1.0, max(0.0, float(alignment_quality)))
        total += normalized_alignment * config.alignment_quality_weight
        weight += config.alignment_quality_weight
    normalized_pitch = None
    if pitch_quality is not None:
        normalized_pitch = min(1.0, max(0.0, float(pitch_quality)))
        total += normalized_pitch * config.pitch_quality_weight
        weight += config.pitch_quality_weight
    score = total / weight
    return score, {
        "lyrics_coverage": round(coverage, 4),
        "matching_word_coverage": round(matching, 4),
        "similarity": round(similarity, 4),
        "alignment_quality": (round(normalized_alignment, 4)
                              if normalized_alignment is not None else None),
        "tonal_pitch_quality": (round(normalized_pitch, 4)
                                if normalized_pitch is not None else None),
    }


def select_alignment_candidate(
    candidates: list[AudioAlignmentCandidate],
    evaluate: Callable[[AudioAlignmentCandidate], dict],
    config: CandidateSelectionConfig,
) -> tuple[AudioAlignmentCandidate, dict | None, dict]:
    if not candidates or not candidates[0].legacy:
        raise ValueError("Der erste Alignment-Kandidat muss der bestehende Pipeline-Kandidat sein")
    legacy = candidates[0]
    if not config.enabled:
        return legacy, None, {"enabled": False, "selected_candidate": legacy.id,
                              "reason": "feature-disabled", "candidates": []}

    diagnostics: list[dict] = []
    evaluated: list[tuple[AudioAlignmentCandidate, dict, float]] = []
    for candidate in candidates:
        try:
            result = evaluate(candidate)
            score, parts = transcript_score(
                result["comparison"], config, result.get("alignment_quality"),
                result.get("pitch_quality"))
            diagnostics.append({"id": candidate.id, "type": candidate.type, "status": "success",
                                "score": round(score, 4), **parts,
                                "pitch_quality_diagnostics": result.get(
                                    "pitch_quality_diagnostics"),
                                "metadata": candidate.metadata})
            evaluated.append((candidate, result, score))
        except Exception as error:  # Optional candidates may fail independently.
            diagnostics.append({"id": candidate.id, "type": candidate.type, "status": "failed",
                                "error": str(error)[:500], "metadata": candidate.metadata})

    legacy_result = next((item for item in evaluated if item[0].legacy), None)
    if legacy_result is None:
        return legacy, None, {"enabled": True, "selected_candidate": legacy.id,
                              "reason": "all-evaluations-failed-legacy-fallback",
                              "minimum_improvement": config.minimum_improvement,
                              "candidates": diagnostics}
    winner = max(evaluated, key=lambda item: item[2])
    required = legacy_result[2] + config.minimum_improvement
    if winner[0].legacy or winner[2] < required:
        selected = legacy_result
        reason = "legacy-best" if winner[0].legacy else "minimum-improvement-not-reached"
    else:
        selected = winner
        reason = "alternative-clearly-better"
    return selected[0], selected[1], {
        "enabled": True,
        "selected_candidate": selected[0].id,
        "reason": reason,
        "existing_candidate_score": round(legacy_result[2], 4),
        "selected_candidate_score": round(selected[2], 4),
        "minimum_improvement": config.minimum_improvement,
        "candidates": diagnostics,
    }


def select_stage_stem_candidate(
    candidate_ids: set[str],
    diagnostics: dict,
    *,
    baseline_id: str = "existing-pipeline",
    minimum_improvement: float = 0.05,
) -> tuple[str, dict]:
    """Choose a real separator pair for playback from ASR diagnostics.

    Alignment may deliberately use a vocal/original blend. Such a blend is not
    a controllable karaoke stem and must never be exported to Stage. This helper
    considers only candidates for which the caller owns both the vocal and the
    complementary instrumental output.
    """
    if baseline_id not in candidate_ids:
        raise ValueError("Der bisherige Separator muss als Stem-Fallback vorhanden sein")
    if minimum_improvement < 0 or minimum_improvement > 1:
        raise ValueError("Die minimale Stem-Verbesserung muss zwischen 0 und 1 liegen")

    successful = {
        str(item.get("id")): item
        for item in diagnostics.get("candidates", [])
        if item.get("status") == "success" and item.get("id") in candidate_ids
    }
    baseline = successful.get(baseline_id)
    baseline_score = float(baseline["score"]) if baseline is not None else None
    alternatives = [item for key, item in successful.items() if key != baseline_id]
    if not alternatives:
        return baseline_id, {
            "enabled": True,
            "selected_candidate": baseline_id,
            "reason": "no-successful-separator-alternative",
            "baseline_score": baseline_score,
            "minimum_improvement": minimum_improvement,
        }

    winner = max(alternatives, key=lambda item: float(item["score"]))
    winner_score = float(winner["score"])
    if baseline_score is None or winner_score >= baseline_score + minimum_improvement:
        selected = str(winner["id"])
        reason = ("baseline-evaluation-failed" if baseline_score is None
                  else "separator-clearly-better")
    else:
        selected = baseline_id
        reason = "minimum-improvement-not-reached"
    return selected, {
        "enabled": True,
        "selected_candidate": selected,
        "reason": reason,
        "baseline_score": baseline_score,
        "alternative_score": winner_score,
        "minimum_improvement": minimum_improvement,
    }
