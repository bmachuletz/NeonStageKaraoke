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


def _source_reliability(word: dict) -> float:
    source = str(word.get("timing_source", ""))
    if source.startswith("ctc-"):
        return 0.92 * max(0.35, float(word.get("ctc_confidence", 0.75)))
    if source == "stable-ts-whisper":
        return 0.72 + 0.24 * float(word.get("stable_ts_probability", 0.0))
    if source in {"qwen-forced", "targeted-deleted-fragment-qwen"}:
        return 0.82
    if source in {"mms-forced-alignment", "sofa-singing-alignment",
                  "easyaligner-global"}:
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


def _score_line(line, activity: list[tuple[float, float]], consensus: dict) -> tuple[float, dict]:
    words = line.words
    if not words:
        return 0.0, {"valid": False, "reason": "no-word-timestamps"}
    starts = [float(word["start"]) for word in words]
    ends = [float(word["end"]) for word in words]
    monotonic = all(right >= left for left, right in zip(starts, starts[1:]))
    positive = all(end > start for start, end in zip(starts, ends))
    plausible = all(0.025 <= end - start <= 6.0 for start, end in zip(starts, ends))
    geometry = (float(monotonic) + float(positive) + float(plausible)) / 3

    durations = [max(0.001, end - start) for start, end in zip(starts, ends)]
    activity_coverage = float(sum(
        duration * _activity_overlap(start, end, activity)
        for start, end, duration in zip(starts, ends, durations)
    ) / max(0.001, sum(durations)))
    onset = float(_onset_support(starts[0], activity))
    reliability = float(statistics.mean(_source_reliability(word) for word in words))

    source_timestamp = line.source_timestamp
    source_prior = (math.exp(-abs(starts[0] - float(source_timestamp)) / 0.8)
                    if source_timestamp is not None else 0.65)
    deviations = []
    if consensus["start"] is not None:
        deviations.append(abs(starts[0] - float(consensus["start"])))
    if consensus["end"] is not None:
        deviations.append(abs(ends[-1] - float(consensus["end"])))
    agreement = math.exp(-statistics.mean(deviations) / 0.22) if deviations else 0.5

    score = float(0.31 * activity_coverage + 0.18 * onset + 0.18 * reliability +
                  0.14 * agreement + 0.11 * geometry + 0.08 * source_prior)
    if not monotonic or not positive:
        score *= 0.25
    return score, {
        "valid": monotonic and positive,
        "score": round(score, 4),
        "activity_coverage": round(activity_coverage, 4),
        "onset_support": round(onset, 4),
        "model_reliability": round(reliability, 4),
        "candidate_agreement": round(agreement, 4),
        "geometry": round(geometry, 4),
        "lrc_prior": round(source_prior, 4),
        "start": round(starts[0], 3),
        "end": round(ends[-1], 3),
    }


def _transition_penalty(previous, current) -> float:
    if not previous.words or not current.words:
        return 1.0
    previous_end = float(previous.words[-1]["end"])
    current_start = float(current.words[0]["start"])
    overlap = previous_end - current_start
    if overlap <= 0.015:
        return 0.0
    # Cross-line overlap is forbidden in the emitted karaoke lane. A tiny
    # tolerance covers rounding, while larger collisions dominate local gains.
    return 0.45 + min(1.5, overlap * 2.5)


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
            row.append((score, {**detail, "independent_boundary_support": support,
                                "supporting_families": families}))
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
            disagreement_supported = (
                not high_disagreement
                or detail["independent_boundary_support"] >= 2
                or strong_acoustic_rescue
            )
            reliability_supported = (
                detail.get("model_reliability", 0.0) >=
                baseline_detail.get("model_reliability", 0.0) - 0.12
                or strong_acoustic_rescue
            )
            eligible = bool(candidate_index == baseline_index or
                            ((score >= baseline_score + minimum_line_improvement or
                              not baseline_detail.get("valid", False))
                             and disagreement_supported
                             and reliability_supported))
            adjusted.append((score if eligible else -10.0,
                             {**detail, "eligible": eligible,
                              "high_disagreement": high_disagreement,
                              "strong_acoustic_rescue": strong_acoustic_rescue,
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
        "applied": mode == "select" and changed > 0,
        "baseline": baseline_id,
        "candidates": [{"id": item.id, "description": item.description,
                        "family": item.family} for item in candidates],
        "minimum_line_improvement": minimum_line_improvement,
        "selected_nonbaseline_lines": changed,
        "review_lines": sum(item["needs_review"] for item in line_reports),
        "lines": line_reports,
    }
    return (selected_lines if mode == "select" else baseline_lines), report
