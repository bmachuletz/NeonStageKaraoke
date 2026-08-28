from __future__ import annotations

import math
import statistics
from copy import deepcopy
from dataclasses import dataclass

from .transcript_match import normalize_words


@dataclass(slots=True)
class AlignmentCandidate:
    """An immutable-by-convention snapshot produced by one alignment path."""

    id: str
    description: str
    lines: list
    family: str


def capture_candidate(candidate_id: str, description: str, lines: list,
                      family: str) -> AlignmentCandidate:
    return AlignmentCandidate(candidate_id, description, deepcopy(lines), family)


def _activity_overlap(start: float, end: float,
                      activity: list[tuple[float, float]]) -> float:
    duration = max(0.001, end - start)
    overlap = sum(max(0.0, min(end, stop) - max(start, begin))
                  for begin, stop in activity if stop > start and begin < end)
    return float(min(1.0, overlap / duration))


def _onset_support(start: float, activity: list[tuple[float, float]]) -> float:
    if not activity:
        return 0.0
    beginnings = [begin for begin, _stop in activity]
    distance = min(abs(start - begin) for begin in beginnings)
    if any(begin - 0.04 <= start <= stop for begin, stop in activity):
        return max(0.72, math.exp(-distance / 0.18))
    return math.exp(-distance / 0.12)


def _release_support(end: float, activity: list[tuple[float, float]]) -> float:
    """Score whether a line ends at its own connected vocal release."""
    if not activity:
        return 0.0
    containing = [(begin, stop) for begin, stop in activity
                  if begin - 0.04 <= end <= stop + 0.04]
    if containing:
        distance = min(abs(end - stop) for _begin, stop in containing)
    else:
        distance = min(abs(end - stop) for _begin, stop in activity)
    return math.exp(-distance / 0.24)


def _source_reliability(word: dict) -> float:
    source = str(word.get("timing_source", ""))
    if source == "xlsr-espeak-global-primary":
        # Known canonical text, one monotone full-track CTC path and no LRC/ASR
        # timing prior. Per-word posterior confidence still controls weak sung
        # or separator-damaged regions.
        return 0.96 * max(0.30, float(word.get("ctc_confidence", 0.70)))
    if source.startswith("ctc-"):
        return 0.92 * max(0.35, float(word.get("ctc_confidence", 0.75)))
    if source == "stable-ts-whisper":
        return 0.72 + 0.24 * float(word.get("stable_ts_probability", 0.0))
    if source in {"qwen-forced", "targeted-deleted-fragment-qwen"}:
        return 0.82
    if source == "input-enhanced-lrc":
        # A human/editor timing is a real competing hypothesis. It is not
        # blindly trusted, but strong agreement with the Stage vocal activity
        # must be allowed to beat a weaker automatic re-alignment.
        return 0.80
    if source == "editor-guided-calibration":
        # This timing is inferred from exact human residuals, but it still has
        # to win against the final vocal activity before being emitted.
        return 0.88 * max(0.45, float(word.get(
            "editor_guidance_confidence", 0.70)))
    if source == "sofa-singing-alignment":
        # Section-level SOFA confidence belongs to every emitted word.  A
        # weak section must not receive the same reliability as a confident
        # forced alignment merely because all dictionary tokens were emitted.
        return 0.86 * max(0.20, min(1.0, float(word.get("sofa_confidence", 0.50))))
    if source in {"mms-forced-alignment", "easyaligner-global"}:
        return 0.86
    if source in {"asr-repetition-anchor", "asr-repetition-activity"}:
        return 0.78
    if source in {"vocal-activity-repair", "anchor-context-vocal-activity",
                  "anchor-tail-vocal-activity"}:
        return 0.48
    if source in {"geometric-repair", "stable-ts-interpolated",
                  "overlap-display-lane-fallback"}:
        return 0.25
    return 0.55


def _compatible(candidates: list[AlignmentCandidate]) -> None:
    if not candidates:
        raise ValueError("Lyrics Engine v2 benötigt mindestens einen Kandidaten.")
    reference = [normalize_words(line.text) for line in candidates[0].lines]
    for candidate in candidates[1:]:
        current = [normalize_words(line.text) for line in candidate.lines]
        if current != reference:
            raise ValueError(
                f"Alignment-Kandidat '{candidate.id}' verwendet einen anderen kanonischen Text.")


def _consensus_boundaries(candidates: list[AlignmentCandidate]) -> list[dict]:
    result: list[dict] = []
    for line_index in range(len(candidates[0].lines)):
        starts = []
        ends = []
        for candidate in candidates:
            words = candidate.lines[line_index].words
            if words:
                starts.append(float(words[0]["start"]))
                ends.append(float(words[-1]["end"]))
        result.append({
            "start": statistics.median(starts) if starts else None,
            "end": statistics.median(ends) if ends else None,
            "start_spread_ms": (round((max(starts) - min(starts)) * 1000, 1)
                                if len(starts) > 1 else 0.0),
            "end_spread_ms": (round((max(ends) - min(ends)) * 1000, 1)
                              if len(ends) > 1 else 0.0),
        })
    return result


def _independent_boundary_support(candidates: list[AlignmentCandidate], line_index: int,
                                  candidate_index: int, *, start_tolerance: float = 0.18,
                                  end_tolerance: float = 0.24) -> tuple[int, list[str]]:
    target = candidates[candidate_index].lines[line_index]
    if not target.words:
        return 0, []
    target_start = float(target.words[0]["start"])
    target_end = float(target.words[-1]["end"])
    families = {
        candidate.family
        for candidate in candidates
        if candidate.lines[line_index].words
        and abs(float(candidate.lines[line_index].words[0]["start"]) - target_start)
        <= start_tolerance
        and abs(float(candidate.lines[line_index].words[-1]["end"]) - target_end)
        <= end_tolerance
    }
    return len(families), sorted(families)


def _boundary_candidate_votes(candidates: list[AlignmentCandidate], line_index: int,
                              candidate_index: int, *, start_tolerance: float = 0.12,
                              end_tolerance: float = 0.24) -> int:
    """Count agreeing candidate scopes without calling them independent models.

    Long-context and short-window Stable-TS share a model family, but agreement
    between their independently decoded scopes is still useful for rejecting a
    known window-edge fallback.  The stricter family count remains authoritative
    for every ordinary high-disagreement decision.
    """
    target = candidates[candidate_index].lines[line_index]
    if not target.words:
        return 0
    target_start = float(target.words[0]["start"])
    target_end = float(target.words[-1]["end"])
    return sum(
        bool(candidate.lines[line_index].words)
        and abs(float(candidate.lines[line_index].words[0]["start"]) - target_start)
        <= start_tolerance
        and abs(float(candidate.lines[line_index].words[-1]["end"]) - target_end)
        <= end_tolerance
        for candidate in candidates
    )


def _score_line(line, activity: list[tuple[float, float]], consensus: dict) -> tuple[float, dict]:
    words = line.words
    if not words:
        return 0.0, {"valid": False, "reason": "no-word-timestamps"}
    starts = [float(word["start"]) for word in words]
    ends = [float(word["end"]) for word in words]
    ordered_starts = all(right >= left for left, right in zip(starts, starts[1:]))
    nonoverlapping = all(left_end <= right_start + 0.001
                         for left_end, right_start in zip(ends, starts[1:]))
    monotonic = ordered_starts and nonoverlapping
    positive = all(end > start for start, end in zip(starts, ends))
    plausible = all(0.025 <= end - start <= 6.0 for start, end in zip(starts, ends))
    geometry = (float(monotonic) + float(positive) + float(plausible)) / 3

    durations = [max(0.001, end - start) for start, end in zip(starts, ends)]
    activity_coverage = float(sum(
        duration * _activity_overlap(start, end, activity)
        for start, end, duration in zip(starts, ends, durations)
    ) / max(0.001, sum(durations)))
    onset = float(_onset_support(starts[0], activity))
    release = float(_release_support(ends[-1], activity))
    reliability = float(statistics.mean(_source_reliability(word) for word in words))
    leading_window_edge_fallback = bool(words[0].get("window_edge_fallback"))

    source_timestamp = line.source_timestamp
    source_prior = (math.exp(-abs(starts[0] - float(source_timestamp)) / 0.8)
                    if source_timestamp is not None else 0.65)
    deviations = []
    if consensus["start"] is not None:
        deviations.append(abs(starts[0] - float(consensus["start"])))
    if consensus["end"] is not None:
        deviations.append(abs(ends[-1] - float(consensus["end"])))
    agreement = math.exp(-statistics.mean(deviations) / 0.22) if deviations else 0.5

    score = float(0.27 * activity_coverage + 0.16 * onset + 0.16 * reliability +
                  0.13 * agreement + 0.10 * geometry + 0.07 * source_prior +
                  0.11 * release)
    if leading_window_edge_fallback:
        # The timestamp is the search-window boundary, not a measured phoneme
        # or vocal onset.  A sizeable penalty lets corroborated acoustic
        # candidates win while the baseline remains available as a fallback.
        score -= 0.18
    if not monotonic or not positive:
        score *= 0.25
    return score, {
        "valid": monotonic and positive,
        "score": round(score, 4),
        "activity_coverage": round(activity_coverage, 4),
        "onset_support": round(onset, 4),
        "release_support": round(release, 4),
        "model_reliability": round(reliability, 4),
        "candidate_agreement": round(agreement, 4),
        "geometry": round(geometry, 4),
        "nonoverlapping_words": nonoverlapping,
        "lrc_prior": round(source_prior, 4),
        "start": round(starts[0], 3),
        "end": round(ends[-1], 3),
        "leading_window_edge_fallback": leading_window_edge_fallback,
    }


def _transition_penalty(previous, current) -> float:
    if not previous.words or not current.words:
        return 1.0
    previous_end = float(previous.words[-1]["end"])
    current_start = float(current.words[0]["start"])
    overlap = previous_end - current_start
    if overlap <= 0.015:
        return 0.0
    # Cross-line overlap is forbidden in the emitted karaoke lane. Make an
    # overlapping path effectively impossible whenever a clean candidate path
    # exists; the final single-lane gate remains the fallback when every model
    # reports concurrent lead/backing vocals.
    return 25.0 + min(25.0, overlap * 10.0)


def fuse_alignment_candidates(candidates: list[AlignmentCandidate],
                              vocal_activity: list[tuple[float, float]], *,
                              baseline_id: str,
                              mode: str = "shadow",
                              minimum_line_improvement: float = 0.035
                              ) -> tuple[list, dict]:
    """Select a globally monotonic path without mutating any input candidate.

    ``shadow`` evaluates and reports the path while returning the historical
    baseline. ``select`` emits the measured winner, but each non-baseline line
    must beat the baseline by a configured margin.
    """
    if mode not in {"off", "shadow", "select"}:
        raise ValueError("LRC_ENGINE_V2_MODE muss off, shadow oder select sein.")
    _compatible(candidates)
    baseline_index = next((index for index, item in enumerate(candidates)
                           if item.id == baseline_id), None)
    if baseline_index is None:
        raise ValueError("Der Engine-v2-Basiskandidat fehlt.")
    if mode == "off" or len(candidates) == 1:
        return deepcopy(candidates[baseline_index].lines), {
            "version": 2, "mode": mode, "applied": False,
            "reason": "disabled-or-single-candidate", "candidates": [item.id for item in candidates],
        }

    consensus = _consensus_boundaries(candidates)
    scores: list[list[tuple[float, dict]]] = []
    for line_index in range(len(candidates[0].lines)):
        row = []
        for candidate_index, candidate in enumerate(candidates):
            score, detail = _score_line(
                candidate.lines[line_index], vocal_activity, consensus[line_index])
            support, families = _independent_boundary_support(
                candidates, line_index, candidate_index)
            boundary_votes = _boundary_candidate_votes(
                candidates, line_index, candidate_index)
            corroborated_scope_edge = (
                detail.get("leading_window_edge_fallback", False)
                and candidate.family == "full-transcript-stable-ts"
                and boundary_votes >= 2
            )
            if corroborated_scope_edge:
                # The forced aligner still emitted timestamp zero relative to
                # its local window, but two independently decoded transcript
                # scopes placed that window at the same song boundary. Restore
                # the penalty only for this explicitly corroborated scaffold.
                score += 0.18
                detail = {**detail, "score": round(score, 4)}
            row.append((score, {**detail, "independent_boundary_support": support,
                                "supporting_families": families,
                                "boundary_candidate_votes": boundary_votes,
                                "corroborated_scope_edge": corroborated_scope_edge}))
        baseline_score = row[baseline_index][0]
        baseline_detail = row[baseline_index][1]
        high_disagreement = (consensus[line_index]["start_spread_ms"] > 180 or
                             consensus[line_index]["end_spread_ms"] > 240)
        adjusted = []
        for candidate_index, (score, detail) in enumerate(row):
            strong_acoustic_rescue = (
                detail.get("activity_coverage", 0.0) >=
                baseline_detail.get("activity_coverage", 0.0) + 0.45
                and detail.get("onset_support", 0.0) >=
                baseline_detail.get("onset_support", 0.0) + 0.20
            )
            edge_fallback_rescue = (
                high_disagreement
                and baseline_detail.get("leading_window_edge_fallback", False)
                and (not detail.get("leading_window_edge_fallback", False)
                     or detail.get("corroborated_scope_edge", False))
                and detail.get("boundary_candidate_votes", 0) >= 2
                and detail.get("onset_support", 0.0) >= 0.80
                and detail.get("onset_support", 0.0) >=
                baseline_detail.get("onset_support", 0.0) + 0.10
            )
            disagreement_supported = (
                not high_disagreement
                or detail["independent_boundary_support"] >= 2
                or strong_acoustic_rescue
                or edge_fallback_rescue
            )
            reliability_supported = (
                detail.get("model_reliability", 0.0) >=
                baseline_detail.get("model_reliability", 0.0) - 0.12
                or strong_acoustic_rescue
                or edge_fallback_rescue
            )
            eligible = bool(candidate_index == baseline_index or
                            ((score >= baseline_score + minimum_line_improvement or
                              edge_fallback_rescue or
                              not baseline_detail.get("valid", False))
                             and disagreement_supported
                             and reliability_supported))
            # Ineligible candidates must remain impossible even when the
            # baseline has a large cross-line transition penalty. A small
            # sentinel allowed Viterbi to prefer an unsupported clean seam.
            adjusted.append((score if eligible else -1_000_000.0,
                             {**detail, "eligible": eligible,
                              "high_disagreement": high_disagreement,
                              "strong_acoustic_rescue": strong_acoustic_rescue,
                              "edge_fallback_rescue": edge_fallback_rescue,
                              "disagreement_supported": disagreement_supported,
                              "reliability_supported": reliability_supported}))
        scores.append(adjusted)

    # Viterbi-style path selection prevents independently good lines from
    # producing an impossible cross-line overlap at their seam.
    paths: list[list[tuple[float, int | None]]] = []
    for line_index, row in enumerate(scores):
        current: list[tuple[float, int | None]] = []
        for candidate_index, (local_score, _detail) in enumerate(row):
            if line_index == 0:
                current.append((local_score, None))
                continue
            choices = []
            for previous_index, (previous_total, _parent) in enumerate(paths[-1]):
                penalty = _transition_penalty(
                    candidates[previous_index].lines[line_index - 1],
                    candidates[candidate_index].lines[line_index])
                choices.append((previous_total + local_score - penalty, previous_index))
            current.append(max(choices, key=lambda item: item[0]))
        paths.append(current)

    selected = [baseline_index] * len(scores)
    selected[-1] = max(range(len(candidates)), key=lambda index: paths[-1][index][0])
    for line_index in range(len(selected) - 1, 0, -1):
        parent = paths[line_index][selected[line_index]][1]
        selected[line_index - 1] = baseline_index if parent is None else parent

    selected_lines = [deepcopy(candidates[candidate_index].lines[line_index])
                      for line_index, candidate_index in enumerate(selected)]
    leading_boundary_rescues: dict[int, dict] = {}
    for line_index, candidate_index in enumerate(selected):
        detail = scores[line_index][candidate_index][1]
        if detail.get("edge_fallback_rescue") and selected_lines[line_index].words:
            first_word = selected_lines[line_index].words[0]
            first_word.pop("window_edge_fallback", None)
            first_word["window_edge_fallback_resolved"] = True
            first_word["window_edge_rescue"] = "corroborated-transcript-scopes"
            continue
        # A globally selected baseline may retain a false leading edge because
        # replacing its whole line would currently overlap the next provisional
        # candidate.  When a competing line has overwhelming acoustic onset
        # support, move only the first word's start. Its end and every later
        # boundary stay untouched, so this cannot create a new seam collision.
        if (candidate_index != baseline_index or not selected_lines[line_index].words
                or not detail.get("leading_window_edge_fallback", False)):
            continue
        first_word = selected_lines[line_index].words[0]
        current_start = float(first_word["start"])
        current_end = float(first_word["end"])
        previous_end = (float(selected_lines[line_index - 1].words[-1]["end"])
                        if line_index > 0 and selected_lines[line_index - 1].words
                        else 0.0)
        alternatives = []
        for alternative_index, candidate in enumerate(candidates):
            if alternative_index == baseline_index or not candidate.lines[line_index].words:
                continue
            alternative_detail = scores[line_index][alternative_index][1]
            alternative_start = float(candidate.lines[line_index].words[0]["start"])
            if (not alternative_detail.get("strong_acoustic_rescue", False)
                    or alternative_start < current_start + 0.18
                    or alternative_start >= current_end - 0.04
                    or alternative_start < previous_end - 0.001):
                continue
            alternatives.append((
                alternative_detail.get("onset_support", 0.0),
                alternative_detail.get("score", 0.0),
                alternative_start, alternative_index,
            ))
        if not alternatives:
            continue
        _onset, _score, rescued_start, rescue_index = max(alternatives)
        first_word["start"] = round(rescued_start, 3)
        first_word.pop("window_edge_fallback", None)
        first_word["window_edge_fallback_resolved"] = True
        first_word["window_edge_rescue"] = "boundary-only-acoustic-onset"
        selected_lines[line_index].timestamp = round(rescued_start, 3)
        leading_boundary_rescues[line_index] = {
            "candidate": candidates[rescue_index].id,
            "from": round(current_start, 3),
            "to": round(rescued_start, 3),
            "preserved_word_end": round(current_end, 3),
            "method": "boundary-only-acoustic-onset",
        }
    baseline_lines = deepcopy(candidates[baseline_index].lines)
    line_reports = []
    for line_index, candidate_index in enumerate(selected):
        options = {
            candidate.id: scores[line_index][index][1]
            for index, candidate in enumerate(candidates)
        }
        line_reports.append({
            "line": line_index + 1,
            "text": candidates[0].lines[line_index].text,
            "selected": candidates[candidate_index].id,
            "baseline": baseline_id,
            "changed": candidate_index != baseline_index,
            "leading_boundary_rescue": leading_boundary_rescues.get(line_index),
            "start_spread_ms": consensus[line_index]["start_spread_ms"],
            "end_spread_ms": consensus[line_index]["end_spread_ms"],
            "needs_review": (consensus[line_index]["start_spread_ms"] > 180 or
                             consensus[line_index]["end_spread_ms"] > 240),
            "options": options,
        })
    changed = sum(item["changed"] for item in line_reports)
    report = {
        "version": 2,
        "method": "immutable-multi-candidate-viterbi-v1",
        "mode": mode,
        "applied": mode == "select" and (changed > 0 or bool(leading_boundary_rescues)),
        "baseline": baseline_id,
        "candidates": [{"id": item.id, "description": item.description,
                        "family": item.family} for item in candidates],
        "minimum_line_improvement": minimum_line_improvement,
        "selected_nonbaseline_lines": changed,
        "leading_boundary_rescues": len(leading_boundary_rescues),
        "review_lines": sum(item["needs_review"] for item in line_reports),
        "lines": line_reports,
    }
    return (selected_lines if mode == "select" else baseline_lines), report


def preserve_better_enhanced_input(generated: list, source: AlignmentCandidate | None,
                                   vocal_activity: list[tuple[float, float]], *,
                                   minimum_improvement: float = 0.06) -> tuple[list, dict]:
    """Preserve editor timings where they fit Stage vocals measurably better.

    Adjacent lyrics are also compared as a block.  This matters for repeated
    choruses: an automatic candidate can let the end of one line invade the
    following line.  Comparing those lines independently makes the good editor
    boundary ineligible merely because its neighbour is already wrong.
    """
    if source is None or len(source.lines) != len(generated):
        return generated, {"enabled": False, "reason": "no-compatible-enhanced-input",
                           "preserved_lines": 0, "lines": []}
    probe = [capture_candidate("generated", "generated", generated, "automatic"), source]
    try:
        _compatible(probe)
    except ValueError:
        return generated, {"enabled": False, "reason": "canonical-text-changed",
                           "preserved_lines": 0, "lines": []}

    result = deepcopy(generated)
    consensus = _consensus_boundaries(probe)
    preserved = []
    preserved_indices: set[int] = set()
    preserved_blocks = []

    # First repair coherent blocks around cross-line collisions.  Expand a
    # block when the source boundary would otherwise collide with an unchanged
    # neighbour.  Keeping this bounded prevents a single disagreement from
    # replacing a complete song.
    block_candidates = []
    for index in range(len(result) - 1):
        if not result[index].words or not result[index + 1].words:
            continue
        generated_seam_overlap = (
            float(result[index].words[-1]["end"]) -
            float(result[index + 1].words[0]["start"]))
        if generated_seam_overlap <= 0.015:
            continue
        first, last = index, index + 1
        while first > 0 and source.lines[first].words and result[first - 1].words and (
                float(source.lines[first].words[0]["start"]) <
                float(result[first - 1].words[-1]["end"]) - 0.001):
            first -= 1
        while last + 1 < len(result) and source.lines[last].words and result[last + 1].words and (
                float(source.lines[last].words[-1]["end"]) >
                float(result[last + 1].words[0]["start"]) + 0.001):
            last += 1
        if last - first + 1 > 4:
            continue
        source_details = []
        generated_details = []
        source_scores = []
        generated_scores = []
        valid_source_block = True
        for line_index in range(first, last + 1):
            generated_score, generated_detail = _score_line(
                result[line_index], vocal_activity, consensus[line_index])
            source_score, source_detail = _score_line(
                source.lines[line_index], vocal_activity, consensus[line_index])
            generated_scores.append(generated_score)
            source_scores.append(source_score)
            generated_details.append(generated_detail)
            source_details.append(source_detail)
            valid_source_block = valid_source_block and bool(source_detail["valid"])
        valid_source_block = valid_source_block and all(
            float(source.lines[line_index].words[-1]["end"]) <=
            float(source.lines[line_index + 1].words[0]["start"]) + 0.001
            for line_index in range(first, last)
            if source.lines[line_index].words and source.lines[line_index + 1].words)
        average_gain = (statistics.mean(source_scores) - statistics.mean(generated_scores))
        if not valid_source_block or average_gain < minimum_improvement / 2:
            continue
        block_candidates.append((average_gain, first, last, generated_seam_overlap,
                                 generated_details, source_details))

    for (average_gain, first, last, seam_overlap,
         generated_details, source_details) in sorted(block_candidates, reverse=True):
        indices = set(range(first, last + 1))
        if indices & preserved_indices:
            continue
        for line_index in indices:
            result[line_index] = deepcopy(source.lines[line_index])
        preserved_indices.update(indices)
        preserved_blocks.append({
            "first_line": first + 1,
            "last_line": last + 1,
            "average_score_improvement": round(average_gain, 4),
            "repaired_seam_overlap_ms": round(seam_overlap * 1000, 1),
            "generated": generated_details,
            "input": source_details,
        })
        for offset, line_index in enumerate(range(first, last + 1)):
            preserved.append({
                "line": line_index + 1,
                "reason": "coherent-block",
                "generated": generated_details[offset],
                "input": source_details[offset],
            })

    for index, source_line in enumerate(source.lines):
        if index in preserved_indices:
            continue
        generated_score, generated_detail = _score_line(
            result[index], vocal_activity, consensus[index])
        source_score, source_detail = _score_line(
            source_line, vocal_activity, consensus[index])
        stronger_activity = (
            source_detail["activity_coverage"] >=
            generated_detail["activity_coverage"] + 0.08
            or source_detail["onset_support"] >=
            generated_detail["onset_support"] + 0.18
            or source_detail["release_support"] >=
            generated_detail["release_support"] + 0.18
        )
        if (not source_detail["valid"]
                or (generated_detail["valid"] and not stronger_activity)
                or source_score < generated_score + minimum_improvement):
            continue
        source_start = float(source_line.words[0]["start"])
        source_end = float(source_line.words[-1]["end"])
        previous_end = (float(result[index - 1].words[-1]["end"])
                        if index > 0 and result[index - 1].words else None)
        next_start = (float(result[index + 1].words[0]["start"])
                      if index + 1 < len(result) and result[index + 1].words else None)
        if ((previous_end is not None and source_start < previous_end - 0.001)
                or (next_start is not None and source_end > next_start + 0.001)):
            continue
        result[index] = deepcopy(source_line)
        preserved.append({
            "line": index + 1,
            "generated_score": round(generated_score, 4),
            "input_score": round(source_score, 4),
            "generated": generated_detail,
            "input": source_detail,
        })
    return result, {
        "enabled": True,
        "method": "stage-vocal-coherent-input-preservation-v2",
        "preserved_lines": len(preserved),
        "preserved_blocks": preserved_blocks,
        "lines": preserved,
    }


PRECISE_INTERNAL_BOUNDARY_SOURCES = {
    "ctc-phoneme-alignment",
    "ctc-context-alignment",
    "ctc-section-alignment",
    "mms-forced-alignment",
    "sofa-singing-alignment",
    "easyaligner-global",
    "ipa-collapsed-run-repair",
    "ipa-vocal-hole-repair",
    "ipa-delayed-phrase-repair",
    "ipa-reduced-connector-repair",
    "ipa-clipped-final-phrase-repair",
    "verified-local-ipa-interval",
}


def preserve_uncorroborated_internal_editor_boundaries(
        generated: list, source: AlignmentCandidate | None, *,
        boundary_disagreement: float = 0.14,
        maximum_outer_disagreement: float = 0.28) -> tuple[list, dict]:
    """Prevent a coarse recogniser from degrading an editor word layout.

    Variant 1.2 starts with a complete, word-timed editor document.  A new
    acoustic candidate may improve it, but line-level activity coverage alone
    cannot decide whether an internal pause belongs to the left word, the
    right word, or neither.  Preserve the editor layout when outer line edges
    still agree but an internal edge moves materially without a precise,
    independently constrained word aligner supporting both sides.

    A large onset shift is intentionally left to candidate selection because
    it can represent a genuinely misplaced editor line. A large final release
    is different: sustain analysis may change only that edge and must not make
    unrelated earlier word boundaries lose their protection. If the release
    is independently precise, retain it on top of the restored editor prefix;
    otherwise restore the complete editor line.
    """
    report = {
        "enabled": source is not None,
        "method": "corroborated-internal-editor-boundary-guard-v1",
        "preserved_lines": 0,
        "boundary_disagreement_ms": round(boundary_disagreement * 1000),
        "maximum_outer_disagreement_ms": round(maximum_outer_disagreement * 1000),
        "lines": [],
    }
    if source is None or len(source.lines) != len(generated):
        report["enabled"] = False
        report["reason"] = "no-compatible-enhanced-input"
        return generated, report
    try:
        _compatible([capture_candidate("generated", "generated", generated, "automatic"),
                     source])
    except ValueError:
        report["enabled"] = False
        report["reason"] = "canonical-text-changed"
        return generated, report

    result = deepcopy(generated)
    for line_index, (current, editor) in enumerate(zip(generated, source.lines)):
        if len(current.words) < 2 or len(current.words) != len(editor.words):
            continue
        if [normalize_words(word.get("word", "")) for word in current.words] != [
                normalize_words(word.get("word", "")) for word in editor.words]:
            continue
        current_start, current_end = (float(current.words[0]["start"]),
                                      float(current.words[-1]["end"]))
        editor_start, editor_end = (float(editor.words[0]["start"]),
                                    float(editor.words[-1]["end"]))
        if abs(current_start - editor_start) > maximum_outer_disagreement:
            continue
        outer_end_disagrees = abs(current_end - editor_end) > maximum_outer_disagreement

        changed_edges = []
        corroborated = True
        for edge in range(len(current.words) - 1):
            left, right = current.words[edge], current.words[edge + 1]
            editor_left, editor_right = editor.words[edge], editor.words[edge + 1]
            end_delta = abs(float(left["end"]) - float(editor_left["end"]))
            start_delta = abs(float(right["start"]) - float(editor_right["start"]))
            if max(end_delta, start_delta) < boundary_disagreement:
                continue
            left_precise = _has_precise_internal_boundary(left, edge="end")
            right_precise = _has_precise_internal_boundary(right, edge="start")
            # Only the side which actually moved needs independent proof.  A
            # precise measured release used to be discarded merely because
            # the unchanged following onset had no redundant model vote.
            edge_corroborated = (
                (end_delta < boundary_disagreement or left_precise)
                and (start_delta < boundary_disagreement or right_precise)
            )
            corroborated = corroborated and edge_corroborated
            changed_edges.append({
                "after_word": edge + 1,
                "left": left.get("word", ""),
                "right": right.get("word", ""),
                "end_delta_ms": round(end_delta * 1000, 1),
                "start_delta_ms": round(start_delta * 1000, 1),
                "left_precise": left_precise,
                "right_precise": right_precise,
            })
        if not changed_edges or corroborated:
            continue
        restored = deepcopy(editor)
        preserved_precise_release = False
        if outer_end_disagrees and _has_precise_internal_boundary(
                current.words[-1], edge="end"):
            restored.words[-1]["end"] = current_end
            for key, value in current.words[-1].items():
                if key.startswith(("sustain_", "phoneme_end_", "pyin_release_")):
                    restored.words[-1][key] = deepcopy(value)
            preserved_precise_release = True
        restored.timestamp = float(restored.words[0]["start"])
        result[line_index] = restored
        report["preserved_lines"] += 1
        report["lines"].append({
            "line": line_index + 1,
            "text": current.text,
            "reason": "internal-boundary-change-without-independent-word-proof",
            "outer_end_disagreement_ms": round(abs(current_end - editor_end) * 1000, 1),
            "preserved_precise_generated_release": preserved_precise_release,
            "changed_edges": changed_edges,
        })
    return result, report


def _has_precise_internal_boundary(word: dict, *, edge: str) -> bool:
    source = str(word.get("timing_source", ""))
    if source in PRECISE_INTERNAL_BOUNDARY_SOURCES:
        return True
    if source == "stable-repetition-acoustic-onset":
        return bool(word.get("repeated_phrase_periodicity_verified"))
    if edge == "start":
        evidence = word.get("phoneme_start_evidence")
        return bool(isinstance(evidence, dict)
                    and evidence.get("supported")
                    and float(evidence.get("score", 0.0)) >= 0.68
                    and float(word.get("phoneme_confidence", 0.0)) >= 0.55)
    # Pitch alone is not an independent word-identity vote: separator residue
    # can remain tonal.  A precise end needs a word/phone model as well.
    phoneme_proof = bool(word.get("phoneme_end_evidence", {}).get("supported")
                         and float(word.get("phoneme_confidence", 0.0)) >= 0.55)
    tonal_release = word.get("sustain_tonal_release")
    measured_release = bool(
        tonal_release is not None
        and float(word.get("sustain_release_confidence", 0.0)) >= 0.80
        and abs(float(word.get("end", 0.0)) - float(tonal_release)) <= 0.045
    )
    return phoneme_proof or measured_release
