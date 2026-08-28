import numpy as np
import pytest

from app.candidate_selection import (AudioAlignmentCandidate, CandidateSelectionConfig,
                                     blend_audio, select_alignment_candidate,
                                     select_stage_stem_candidate, transcript_score,
                                     timed_anchor_alignment_quality)


def candidate(name: str, value: float = 0.0, *, legacy: bool = False):
    return AudioAlignmentCandidate(name, name, np.full(160, value, dtype=np.float32), name, legacy)


def comparison(score: float):
    # Equal components make the configured weighted score deterministic.
    words = round(score * 100)
    return {"comparison": {"expected_words": 100, "recognized_words": words,
                            "matching_words": words, "similarity": score}}


def config(**changes):
    values = {"enabled": True, "minimum_improvement": 0.05}
    values.update(changes)
    return CandidateSelectionConfig(**values)


def test_disabled_uses_legacy_without_evaluating():
    calls = []
    selected, result, diagnostics = select_alignment_candidate(
        [candidate("legacy", legacy=True)], lambda item: calls.append(item),
        CandidateSelectionConfig(enabled=False))
    assert selected.id == "legacy"
    assert result is None
    assert calls == []
    assert diagnostics["reason"] == "feature-disabled"


def test_clearly_better_alternative_wins():
    scores = {"legacy": .70, "full": .84}
    selected, _, diagnostics = select_alignment_candidate(
        [candidate("legacy", legacy=True), candidate("full")],
        lambda item: comparison(scores[item.id]), config())
    assert selected.id == "full"
    assert diagnostics["reason"] == "alternative-clearly-better"


def test_tie_prefers_legacy():
    selected, _, _ = select_alignment_candidate(
        [candidate("legacy", legacy=True), candidate("other")],
        lambda _: comparison(.8), config())
    assert selected.id == "legacy"


def test_minimal_improvement_keeps_legacy():
    scores = {"legacy": .70, "other": .74}
    selected, _, diagnostics = select_alignment_candidate(
        [candidate("legacy", legacy=True), candidate("other")],
        lambda item: comparison(scores[item.id]), config(minimum_improvement=.05))
    assert selected.id == "legacy"
    assert diagnostics["reason"] == "minimum-improvement-not-reached"


def test_failed_optional_candidate_does_not_abort():
    def evaluate(item):
        if not item.legacy:
            raise RuntimeError("separator failed")
        return comparison(.7)
    selected, _, diagnostics = select_alignment_candidate(
        [candidate("legacy", legacy=True), candidate("other")], evaluate, config())
    assert selected.id == "legacy"
    assert diagnostics["candidates"][1]["status"] == "failed"


def test_stage_stems_use_better_real_separator_but_not_alignment_blend():
    diagnostics = {
        "candidates": [
            {"id": "existing-pipeline", "status": "success", "score": .12},
            {"id": "vocals-original-0.10", "status": "success", "score": .82},
            {"id": "alternative-separator", "status": "success", "score": .76},
        ]
    }
    selected, result = select_stage_stem_candidate(
        {"existing-pipeline", "alternative-separator"}, diagnostics)
    assert selected == "alternative-separator"
    assert result["reason"] == "separator-clearly-better"


def test_stage_stems_keep_baseline_when_separator_gain_is_too_small():
    diagnostics = {
        "candidates": [
            {"id": "existing-pipeline", "status": "success", "score": .72},
            {"id": "alternative-separator", "status": "success", "score": .75},
        ]
    }
    selected, result = select_stage_stem_candidate(
        {"existing-pipeline", "alternative-separator"}, diagnostics,
        minimum_improvement=.05)
    assert selected == "existing-pipeline"
    assert result["reason"] == "minimum-improvement-not-reached"


def test_all_evaluations_fail_with_complete_legacy_fallback():
    selected, result, diagnostics = select_alignment_candidate(
        [candidate("legacy", legacy=True), candidate("other")],
        lambda _: (_ for _ in ()).throw(RuntimeError("ASR failed")), config())
    assert selected.id == "legacy"
    assert result is None
    assert diagnostics["reason"] == "all-evaluations-failed-legacy-fallback"


def test_blend_preserves_shape_dtype_original_and_prevents_clipping():
    vocals = np.array([1.0, -1.0, .5], dtype=np.float32)
    original = np.array([1.0, -1.0, -1.0], dtype=np.float32)
    original_copy = original.copy()
    result = blend_audio(vocals, original, .15)
    assert result.shape == vocals.shape
    assert result.dtype == np.float32
    assert np.max(np.abs(result)) <= 1.0
    np.testing.assert_array_equal(original, original_copy)


def test_blend_crops_resampler_tail_to_original_timeline():
    vocals = np.arange(6, dtype=np.float32) / 10
    original = np.zeros(5, dtype=np.float32)
    result = blend_audio(vocals, original, .1)
    assert result.shape == original.shape
    np.testing.assert_allclose(result, vocals[:5] * .9)


def test_blend_zero_pads_short_vocal_tail_without_moving_start():
    vocals = np.array([.2, .4, .6], dtype=np.float32)
    original = np.zeros(5, dtype=np.float32)
    result = blend_audio(vocals, original, .1)
    assert result.shape == original.shape
    np.testing.assert_allclose(result, [.18, .36, .54, 0, 0])


def test_blend_rejects_material_duration_mismatch():
    with pytest.raises(ValueError, match="Samples"):
        blend_audio(np.zeros(500, dtype=np.float32), np.zeros(100, dtype=np.float32), .1)


def test_alignment_quality_is_optional_and_explicitly_reported():
    without, without_parts = transcript_score(
        comparison(.7)["comparison"], config(alignment_quality_weight=.15))
    with_quality, with_parts = transcript_score(
        comparison(.7)["comparison"], config(alignment_quality_weight=.15), .9)
    assert without_parts["alignment_quality"] is None
    assert with_parts["alignment_quality"] == .9
    assert with_quality > without


def test_untimed_lyrics_receive_no_anchor_quality_penalty():
    line = type("Line", (), {"timed_input": False, "timestamp": 0.0,
                             "source_timestamp": None})()
    assert timed_anchor_alignment_quality([line], [(0.0, 1.0)]) is None


def test_timed_anchor_quality_rewards_activity_near_line_anchors():
    lines = [type("Line", (), {"timed_input": True, "timestamp": timestamp,
                               "source_timestamp": timestamp})()
             for timestamp in (1.0, 3.0, 5.0)]
    near = timed_anchor_alignment_quality(lines, [(.98, 1.8), (2.98, 3.8), (4.98, 5.8)])
    far = timed_anchor_alignment_quality(lines, [(10.0, 11.0)])
    assert near > far


@pytest.mark.parametrize("changes", [
    {"minimum_improvement": -0.1},
    {"minimum_improvement": 1.1},
    {"blend_ratios": (0.0,)},
    {"blend_ratios": (1.0,)},
    {"coverage_weight": -1},
    {"coverage_weight": 0, "matching_weight": 0, "similarity_weight": 0},
    {"alignment_quality_weight": -1},
    {"pitch_quality_weight": .11},
])
def test_invalid_configuration_is_rejected(changes):
    with pytest.raises(ValueError):
        config(**changes).validate()
