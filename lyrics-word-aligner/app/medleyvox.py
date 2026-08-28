from __future__ import annotations

import gc
import json
import math
import os
from argparse import Namespace
from pathlib import Path
from typing import Callable, Iterable

import numpy as np
import soundfile as sf

from .completeness import is_nonlexical_vocalization


MODEL_REPOSITORY = "Cyru5/MedleyVox"
MODEL_REVISION = "5c9e4e0d909e5a006c992b3422901ed416f4e57f"
MODEL_DIRECTORY = "multi_singing_librispeech"
MODEL_LICENSE = "CC-BY-4.0"
MODEL_SAMPLE_RATE = 24000


def plan_duet_windows(lines: Iterable, duration: float, *, padding: float = 2.0,
                       maximum_window: float = 12.0, maximum_windows: int = 12) -> list[tuple[float, float]]:
    """Select bounded windows where concurrent singers are plausible.

    Explicit non-lexical vocalisations (woho/lalala/...) are the strongest
    useful signal available before singer separation.  Existing secondary
    lanes are also rechecked so editor corrections can be audited.  Nearby
    windows are merged, then split to a strict GPU-memory bound.
    """
    candidates: list[tuple[float, float]] = []
    for line in lines:
        lane = max(0, int(getattr(line, "voice_lane", 0)))
        if lane == 0 and not is_nonlexical_vocalization(str(line.text)):
            continue
        words = getattr(line, "words", None) or []
        start = float(words[0]["start"]) if words else float(line.timestamp)
        end = float(words[-1].get("end", words[-1]["start"])) if words else start + 2.0
        candidates.append((max(0.0, start - padding), min(duration, end + padding)))

    merged: list[list[float]] = []
    for start, end in sorted(candidates):
        if end <= start:
            continue
        if merged and start <= merged[-1][1] + 0.4:
            merged[-1][1] = max(merged[-1][1], end)
        else:
            merged.append([start, end])

    bounded: list[tuple[float, float]] = []
    for start, end in merged:
        cursor = start
        while cursor < end and len(bounded) < maximum_windows:
            part_end = min(end, cursor + maximum_window)
            bounded.append((cursor, part_end))
            cursor = part_end
    return bounded


def analyze_multiple_singing_voices(vocal_path: str | Path, lines: list, output_dir: str | Path,
                                    *, device: str = "cuda",
                                    discover_solo_singers: bool = False) -> dict:
    """Separate plausible duet windows and produce conservative lane proposals.

    The model outputs remain anonymous. Only explicit non-lexical backing
    vocals with a unique acoustic activity component receive a proposal. The
    caller applies accepted proposals after every ordinary timing pass, so no
    single-lane cleanup can destroy concurrent singing.
    """
    enabled = _truthy("LRC_MEDLEYVOX_ENABLED", False)
    mode = os.getenv("LRC_MEDLEYVOX_MODE", "shadow").strip().lower()
    report = {
        "enabled": enabled,
        "mode": mode,
        "method": "medleyvox-targeted-overlap-add-multilane-v2",
        "model_repository": MODEL_REPOSITORY,
        "model_revision": MODEL_REVISION,
        "model_file": f"{MODEL_DIRECTORY}/vocals.pth",
        "model_license": MODEL_LICENSE,
        "mutated_lyrics": False,
    }
    if not enabled:
        report["reason"] = "feature-disabled"
        return report
    if mode not in {"off", "shadow", "promote"}:
        raise ValueError("LRC_MEDLEYVOX_MODE muss off, shadow oder promote sein.")
    if mode == "off":
        report["reason"] = "mode-off"
        return report

    audio, source_rate = sf.read(vocal_path, dtype="float32", always_2d=False)
    if audio.ndim > 1:
        audio = audio.mean(axis=1)
    duration = len(audio) / max(1, source_rate)
    solo_voice_clustering = (_cluster_solo_voice_lines(audio, source_rate, lines)
                             if discover_solo_singers else {
                                 "enabled": False,
                                 "reason": "research-shadow-only",
                                 "method": "line-timbre-f0-clustering-v1",
                             })
    report["solo_voice_clustering"] = solo_voice_clustering
    windows = plan_duet_windows(
        lines, duration,
        padding=float(os.getenv("LRC_MEDLEYVOX_WINDOW_PADDING_SECONDS", "2.0")),
        maximum_window=float(os.getenv("LRC_MEDLEYVOX_MAX_WINDOW_SECONDS", "12.0")),
        maximum_windows=int(os.getenv("LRC_MEDLEYVOX_MAX_WINDOWS", "12")))
    report["candidate_windows"] = [
        {"start": round(start, 3), "end": round(end, 3)} for start, end in windows]
    if not windows:
        report["reason"] = "no-nonlexical-or-secondary-lane-candidates"
        report["analyzed_windows"] = 0
        return report

    output_dir = Path(output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    model = None
    try:
        model, model_args = _load_model(device)
        target_device = next(model.parameters()).device
        voice_outputs = [np.zeros(len(audio), dtype=np.float32) for _ in range(2)]
        output_weights = np.zeros(len(audio), dtype=np.float32)
        diagnostics: list[dict] = []
        identity_prototypes: list[np.ndarray | None] = [None, None]
        for window_index, (start, end) in enumerate(windows):
            first = max(0, int(round(start * source_rate)))
            last = min(len(audio), int(round(end * source_rate)))
            native = np.ascontiguousarray(audio[first:last], dtype=np.float32)
            resampled = _resample(native, source_rate, int(model_args.sample_rate))
            normalized, gain = _normalize_rms(resampled)
            separated = _separate_overlap_add(
                normalized, model, target_device,
                sample_rate=int(model_args.sample_rate),
                chunk_seconds=float(os.getenv("LRC_MEDLEYVOX_CHUNK_SECONDS", "3.0")),
                overlap_seconds=float(os.getenv("LRC_MEDLEYVOX_CHUNK_OVERLAP_SECONDS", "1.0")))
            separated /= max(gain, 1e-8)
            native_sources = [_resample(item, int(model_args.sample_rate), source_rate) for item in separated]
            native_length = last - first
            native_sources = [_fit_length(item, native_length) for item in native_sources]
            native_sources, identity = _stabilize_source_identity(
                native_sources, source_rate, identity_prototypes)
            confidence = _separation_evidence(native_sources, source_rate)
            confidence.update({"window": window_index + 1, "start": round(start, 3),
                               "end": round(end, 3), "singer_identity": identity})
            diagnostics.append(confidence)
            blend = _edge_window(native_length, source_rate)
            for lane in range(2):
                voice_outputs[lane][first:last] += native_sources[lane] * blend
            output_weights[first:last] += blend

        covered = output_weights > 1e-6
        for lane in range(2):
            voice_outputs[lane][covered] /= output_weights[covered]
        stem = Path(vocal_path).stem.removesuffix(".vocals")
        names = [f"{stem}.voice-1-shadow.flac", f"{stem}.voice-2-shadow.flac"]
        for name, voice in zip(names, voice_outputs):
            sf.write(output_dir / name, voice, source_rate, format="FLAC")
        report.update({
            "reason": ("multilane-proposals-complete" if mode == "promote"
                       else "shadow-analysis-complete"),
            "analyzed_windows": len(windows),
            "sample_rate": source_rate,
            "voice_candidates": names,
            "diagnostics": diagnostics,
            "overlap_candidates": sum(bool(item["plausible_overlap"]) for item in diagnostics),
        })
        manual_anchors = _manual_lane_anchors(lines, voice_outputs, source_rate)
        report["manual_lane_anchors"] = manual_anchors
        preferred_backing_source = _preferred_source_for_lane(manual_anchors, 1)
        proposals = _line_voice_proposals(
            lines, windows, diagnostics, voice_outputs, source_rate,
            preferred_backing_source=preferred_backing_source,
            compare_distinct_sources=discover_solo_singers)
        automatic_backing_source = (_preferred_source_from_proposals(proposals)
                                    if discover_solo_singers else None)
        if (discover_solo_singers and preferred_backing_source is None
                and automatic_backing_source is not None):
            # MedleyVox sources are anonymous per window. Strong, repeated
            # non-lexical passages nevertheless establish a song-local source
            # identity. Re-score ambiguous passages with that independent
            # evidence instead of rejecting an otherwise coherent chorus.
            proposals = _line_voice_proposals(
                lines, windows, diagnostics, voice_outputs, source_rate,
                preferred_backing_source=automatic_backing_source,
                compare_distinct_sources=True)
        report["automatic_backing_source"] = (
            automatic_backing_source + 1 if automatic_backing_source is not None else None)
        report["lane_proposals"] = proposals
        # Adjacent lead phrases are intentionally measured later, after every
        # ordinary timing stage has established its final geometry. Measuring
        # them here would score against stale pre-fusion line boundaries.
        report["adjacent_lead_proposals"] = []
        report["accepted_lane_proposals"] = sum(
            bool(item.get("accepted")) for item in proposals)
        return report
    except (ImportError, OSError, RuntimeError, ValueError, KeyError) as error:
        report.update({"reason": "shadow-analysis-failed", "error": str(error)[:1200]})
        return report
    finally:
        if model is not None:
            del model
        gc.collect()
        try:
            import torch
            if torch.cuda.is_available():
                torch.cuda.empty_cache()
        except ImportError:
            pass


def planned_voice_lanes(lines: list, report: dict) -> dict[int, int]:
    """Predict the voice lanes this report will assign, without applying them.

    Lane promotion deliberately runs late, after the boundary gates.  A gate
    that has to reason about which neighbouring line may legally overlap would
    otherwise read a stale lead lane for a phrase already decided to be backing
    vocals.  This read-only projection uses the same matching rules as
    ``apply_multiple_singing_voice_proposals`` and mutates nothing.
    """
    planned: dict[int, int] = {}
    if report.get("mode") != "promote":
        return planned
    index_of = {id(line): index for index, line in enumerate(lines)}
    for proposal in report.get("lane_proposals", []):
        if not proposal.get("accepted"):
            continue
        candidates = [line for line in lines
                      if is_nonlexical_vocalization(str(line.text))
                      and str(line.text).casefold() == str(proposal["text"]).casefold()
                      and not bool(getattr(line, "manual_adjusted", False))]
        if not candidates:
            continue
        source_start = float(proposal["source_start"])

        def distance(item, reference=source_start):
            timestamp = (item.source_timestamp if item.source_timestamp is not None
                         else item.timestamp)
            return abs(float(timestamp) - reference)

        line = min(candidates, key=distance)
        if distance(line) > 0.75:
            continue
        if float(proposal["end"]) <= float(proposal["start"]) + 0.08:
            continue
        planned[index_of[id(line)]] = 1
    for proposal in report.get("solo_voice_clustering", {}).get("proposals", []):
        if not proposal.get("accepted"):
            continue
        line_index = int(proposal.get("line", 0)) - 1
        if not 0 <= line_index < len(lines):
            continue
        if bool(getattr(lines[line_index], "manual_adjusted", False)):
            continue
        planned[line_index] = 1
    return planned


def apply_multiple_singing_voice_proposals(lines: list, report: dict, *,
                                           full_vocals: np.ndarray | None = None,
                                           sample_rate: int | None = None,
                                           voice_output_dir: str | Path | None = None) -> dict:
    """Apply uniquely supported backing-vocal proposals after final alignment."""
    summary = {"enabled": report.get("mode") == "promote", "applied": 0,
               "method": "late-medleyvox-voice-lane-promotion-v2", "details": [],
               "solo_singer_lanes_applied": 0}
    if not summary["enabled"]:
        summary["reason"] = "mode-is-not-promote"
        return summary
    solo = report.get("solo_voice_clustering", {})
    if (solo.get("enabled") and full_vocals is not None and sample_rate
            and voice_output_dir is not None):
        try:
            sources = []
            for name in report.get("voice_candidates", []):
                source, source_rate = sf.read(
                    Path(voice_output_dir) / str(name), dtype="float32", always_2d=False)
                if source.ndim > 1:
                    source = source.mean(axis=1)
                if int(source_rate) != int(sample_rate):
                    source = _resample(source, int(source_rate), int(sample_rate))
                sources.append(_fit_length(source, len(full_vocals)))
            windows = [(float(item["start"]), float(item["end"]))
                       for item in report.get("candidate_windows", [])]
            if len(sources) == 2:
                report["adjacent_lead_proposals"] = _adjacent_lead_proposals(
                    lines, windows, report.get("lane_proposals", []), sources,
                    np.asarray(full_vocals, dtype=np.float32), int(sample_rate))
        except (OSError, ValueError, KeyError) as error:
            report["adjacent_lead_proposals"] = []
            summary["adjacent_lead_error"] = str(error)[:500]
    for proposal in report.get("lane_proposals", []):
        if not proposal.get("accepted"):
            continue
        candidates = [line for line in lines
                      if is_nonlexical_vocalization(str(line.text))
                      and str(line.text).casefold() == str(proposal["text"]).casefold()
                      and not bool(getattr(line, "manual_adjusted", False))]
        if not candidates:
            continue
        source_start = float(proposal["source_start"])
        line = min(candidates, key=lambda item: abs(
            float(item.source_timestamp if item.source_timestamp is not None
                  else item.timestamp) - source_start))
        if abs(float(line.source_timestamp if line.source_timestamp is not None
                     else line.timestamp) - source_start) > 0.75:
            continue
        old_start = float(line.words[0]["start"]) if line.words else float(line.timestamp)
        old_end = (float(line.words[-1].get("end", line.words[-1]["start"]))
                   if line.words else old_start + 0.5)
        new_start, new_end = float(proposal["start"]), float(proposal["end"])
        if new_end <= new_start + 0.08:
            continue
        if line.words:
            scale = (new_end - new_start) / max(0.04, old_end - old_start)
            for word in line.words:
                word["start"] = round(new_start + (float(word["start"]) - old_start) * scale, 3)
                word["end"] = round(new_start + (
                    float(word.get("end", word["start"])) - old_start) * scale, 3)
                word["timing_source"] = "medleyvox-backing-vocal-activity"
        line.timestamp = round(new_start, 3)
        line.voice_lane = 1
        line.voice_label = "Backing Vocals"
        summary["applied"] += 1
        summary["details"].append({"text": line.text, "start": round(new_start, 3),
                                   "end": round(new_end, 3),
                                   "confidence": proposal["confidence"]})
    for proposal in solo.get("proposals", []):
        if not proposal.get("accepted"):
            continue
        line_index = int(proposal.get("line", 0)) - 1
        if not 0 <= line_index < len(lines):
            continue
        line = lines[line_index]
        if bool(getattr(line, "manual_adjusted", False)):
            continue
        line.voice_lane = 1
        line.voice_label = "Backing Vocals"
        summary["applied"] += 1
        summary["solo_singer_lanes_applied"] += 1
        summary["details"].append({
            "text": line.text,
            "start": round(float(line.words[0]["start"]), 3) if line.words else line.timestamp,
            "end": round(float(line.words[-1].get("end", line.words[-1]["start"])), 3)
            if line.words else line.timestamp,
            "confidence": proposal.get("confidence_margin"),
            "source": "solo-singer-clustering",
        })
    for proposal in report.get("adjacent_lead_proposals", []):
        if not proposal.get("accepted"):
            continue
        line_index = int(proposal.get("line", 0)) - 1
        if not 0 <= line_index < len(lines):
            continue
        line = lines[line_index]
        if bool(getattr(line, "manual_adjusted", False)) or not line.words:
            continue
        old_start = float(line.words[0]["start"])
        old_end = float(line.words[-1].get("end", line.words[-1]["start"]))
        new_start = float(proposal["start"])
        new_end = float(proposal["end"])
        if new_end <= new_start + .08 or old_end <= old_start:
            continue
        scale = (new_end - new_start) / (old_end - old_start)
        for word in line.words:
            word["start"] = round(new_start + (float(word["start"]) - old_start) * scale, 3)
            word["end"] = round(new_start + (
                float(word.get("end", word["start"])) - old_start) * scale, 3)
            word["timing_source"] = "medleyvox-complementary-lead-activity"
        line.timestamp = round(new_start, 3)
        summary["applied"] += 1
        summary["details"].append({
            "text": line.text,
            "start": round(new_start, 3),
            "end": round(new_end, 3),
            "confidence": proposal.get("confidence"),
            "source": "adjacent-complementary-lead",
        })
    report["mutated_lyrics"] = summary["applied"] > 0
    return summary


def _line_voice_proposals(lines: list, windows: list[tuple[float, float]],
                          diagnostics: list[dict], sources: list[np.ndarray],
                          sample_rate: int,
                          preferred_backing_source: int | None = None,
                          compare_distinct_sources: bool = False) -> list[dict]:
    proposals: list[dict] = []
    for line in lines:
        if not is_nonlexical_vocalization(str(line.text)):
            continue
        words = getattr(line, "words", None) or []
        old_start = float(words[0]["start"]) if words else float(line.timestamp)
        old_end = (float(words[-1].get("end", words[-1]["start"]))
                   if words else old_start + 1.0)
        source_start = float(line.source_timestamp if line.source_timestamp is not None
                             else line.timestamp)
        window_index = next((index for index, (start, end) in enumerate(windows)
                             if start <= (old_start + old_end) / 2 <= end), None)
        base = {"text": line.text, "source_start": round(source_start, 3),
                "original_start": round(old_start, 3), "original_end": round(old_end, 3),
                "accepted": False}
        if window_index is None or not diagnostics[window_index].get("plausible_overlap"):
            base["reason"] = "no-plausible-overlap-window"
            proposals.append(base)
            continue
        start, end = windows[window_index]
        ranked = []
        for source_index, source in enumerate(sources):
            for region_start, region_end in _activity_regions(
                    source, sample_rate, start, end):
                overlap = max(0.0, min(old_end, region_end) - max(old_start, region_start))
                if overlap < min(0.25, max(0.08, (old_end - old_start) * 0.18)):
                    continue
                onset_error = abs(region_start - old_start)
                if onset_error > 1.5:
                    continue
                duration_error = abs((region_end - region_start) - (old_end - old_start))
                score = (0.58 * math.exp(-onset_error / 0.55)
                         + 0.27 * math.exp(-duration_error / 1.4)
                         + 0.15 * min(1.0, overlap / max(0.25, old_end - old_start)))
                if preferred_backing_source == source_index:
                    score += 0.10
                ranked.append((score, source_index, region_start, region_end))
        ranked.sort(reverse=True)
        if not ranked:
            base["reason"] = "no-nearby-voice-activity-component"
            proposals.append(base)
            continue
        # The uniqueness margin describes *which separated singer* owns the
        # phrase. Comparing the two best activity islands was incorrect when
        # both islands belonged to the same source: it made one singer look
        # ambiguous merely because they paused briefly inside the phrase.
        source_ranked = ranked
        if compare_distinct_sources:
            best_by_source: dict[int, tuple[float, int, float, float]] = {}
            for candidate in ranked:
                best_by_source.setdefault(candidate[1], candidate)
            source_ranked = sorted(best_by_source.values(), reverse=True)
        best = source_ranked[0]
        runner_up = source_ranked[1][0] if len(source_ranked) > 1 else 0.0
        margin = best[0] - runner_up
        base.update({"start": round(best[2], 3), "end": round(best[3], 3),
                     "voice_candidate": best[1] + 1,
                     "confidence": round(best[0], 4),
                     "margin": round(margin, 4)})
        # Both a strong local fit and a unique separated source are required.
        minimum_score = float(os.getenv("LRC_MEDLEYVOX_PROMOTION_MIN_SCORE", "0.42"))
        minimum_margin = float(os.getenv("LRC_MEDLEYVOX_PROMOTION_MIN_MARGIN", "0.08"))
        base["accepted"] = best[0] >= minimum_score and margin >= minimum_margin
        base["reason"] = ("unique-backing-vocal-component" if base["accepted"]
                          else "ambiguous-separated-source")
        proposals.append(base)
    return proposals


def _preferred_source_from_proposals(proposals: list[dict]) -> int | None:
    """Infer one backing source from repeated, independently clear passages."""
    votes = [int(item["voice_candidate"]) - 1 for item in proposals
             if item.get("accepted") and float(item.get("margin", 0.0)) >= .08]
    if len(votes) < 2:
        return None
    counts = {candidate: votes.count(candidate) for candidate in set(votes)}
    winner = max(counts, key=counts.get)
    return winner if counts[winner] / len(votes) >= .67 else None


def _adjacent_lead_proposals(lines: list, windows: list[tuple[float, float]],
                             backing_proposals: list[dict], sources: list[np.ndarray],
                             full_vocals: np.ndarray, sample_rate: int) -> list[dict]:
    """Measure lead phrases adjacent to an accepted backing-vocal phrase.

    A combined vocal stem can mistake the continuing backing singer for the
    next lead onset. Once MedleyVox has identified the backing source, the
    complementary source is an independent measurement for the neighbouring
    lexical line. Only a unique, nearby, whole-phrase activity island is
    proposed; word geometry is affinely mapped later in the shadow result.
    """
    results: list[dict] = []
    seen_lines: set[int] = set()
    for backing in backing_proposals:
        if not backing.get("accepted") or backing.get("voice_candidate") not in (1, 2):
            continue
        source_start = float(backing.get("source_start", backing["start"]))
        backing_index = min(
            (index for index, line in enumerate(lines)
             if is_nonlexical_vocalization(str(line.text))),
            key=lambda index: abs(float(
                lines[index].source_timestamp if lines[index].source_timestamp is not None
                else lines[index].timestamp) - source_start), default=-1)
        if backing_index < 0:
            continue
        center = (float(backing["start"]) + float(backing["end"])) / 2
        analysis_window = next(((start, end) for start, end in windows
                                if start <= center <= end), None)
        if analysis_window is None:
            continue
        lead_source = 1 - (int(backing["voice_candidate"]) - 1)
        complementary_activity = _merge_activity_regions(
            _activity_regions(sources[lead_source], sample_rate, *analysis_window),
            maximum_gap=.52)
        for line_index in (backing_index - 1, backing_index + 1):
            if (line_index in seen_lines or not 0 <= line_index < len(lines)
                    or is_nonlexical_vocalization(str(lines[line_index].text))):
                continue
            line = lines[line_index]
            words = getattr(line, "words", None) or []
            if not words:
                continue
            old_start = float(words[0]["start"])
            old_end = float(words[-1].get("end", words[-1]["start"]))
            old_duration = max(.08, old_end - old_start)
            if line_index < backing_index:
                activity = [region for region in complementary_activity
                            if region[0] >= analysis_window[0] + .08]
            else:
                search_start = min(float(backing["end"]) + .03, old_end)
                search_end = min(len(full_vocals) / sample_rate, old_end + 1.8)
                activity = _merge_activity_regions(
                    _activity_regions(full_vocals, sample_rate, search_start, search_end),
                    maximum_gap=.52)
            ranked = []
            for start, end in activity:
                if (abs(start - old_start) > 1.8 or abs(end - old_end) > 1.8
                        or end - start < .35):
                    continue
                onset_error = abs(start - old_start)
                release_error = abs(end - old_end)
                duration_error = abs((end - start) - old_duration)
                shift_coherence = abs((start - old_start) - (end - old_end))
                score = (.25 * math.exp(-onset_error / .65)
                         + .35 * math.exp(-release_error / .75)
                         + .25 * math.exp(-duration_error / 1.1)
                         + .15 * math.exp(-shift_coherence / .40))
                ranked.append((score, start, end))
            ranked.sort(reverse=True)
            base = {"line": line_index + 1, "text": line.text, "accepted": False,
                    "old_start": round(old_start, 3), "old_end": round(old_end, 3),
                    "lead_voice_candidate": lead_source + 1}
            if not ranked:
                base["reason"] = "no-nearby-complementary-lead-region"
                results.append(base)
                continue
            best = ranked[0]
            margin = best[0] - (ranked[1][0] if len(ranked) > 1 else 0.0)
            boundary_change = max(abs(best[1] - old_start), abs(best[2] - old_end))
            base.update({"start": round(best[1], 3), "end": round(best[2], 3),
                         "confidence": round(best[0], 4), "margin": round(margin, 4)})
            base["accepted"] = (best[0] >= .40 and margin >= .08
                                and .10 <= boundary_change <= 1.8)
            base["reason"] = ("unique-complementary-lead-region" if base["accepted"]
                              else "ambiguous-complementary-lead-region")
            results.append(base)
            if base["accepted"]:
                seen_lines.add(line_index)
    return results


def _merge_activity_regions(regions: list[tuple[float, float]], *, maximum_gap: float
                            ) -> list[tuple[float, float]]:
    merged: list[list[float]] = []
    for start, end in sorted(regions):
        if merged and start - merged[-1][1] <= maximum_gap:
            merged[-1][1] = max(merged[-1][1], end)
        else:
            merged.append([start, end])
    return [(start, end) for start, end in merged]


def _cluster_solo_voice_lines(audio: np.ndarray, sample_rate: int, lines: list) -> dict:
    """Conservatively discover non-overlapping singer changes.

    This is deliberately a research-shadow observation. It clusters complete
    lexical line windows using the same MFCC/spectral/F0 representation used
    to keep MedleyVox sources stable. Only a well-separated minority cluster
    containing a contiguous run is proposed as lane 2. Timing is untouched.
    """
    method = "line-timbre-f0-clustering-v1"
    observations: list[tuple[int, object, np.ndarray]] = []
    intervals = []
    for line in lines:
        words = getattr(line, "words", None) or []
        intervals.append((float(words[0]["start"]),
                          float(words[-1].get("end", words[-1]["start"])))
                         if words else None)
    for index, line in enumerate(lines):
        if is_nonlexical_vocalization(str(line.text)):
            continue
        words = getattr(line, "words", None) or []
        if not words:
            continue
        start = float(words[0]["start"])
        end = float(words[-1].get("end", words[-1]["start"]))
        if not .65 <= end - start <= 10.0:
            continue
        if any(other_index != index and other is not None
               and min(end, other[1]) - max(start, other[0]) > .08
               for other_index, other in enumerate(intervals)):
            continue
        first = max(0, int(round(start * sample_rate)))
        last = min(len(audio), int(round(end * sample_rate)))
        segment = np.ascontiguousarray(audio[first:last], dtype=np.float32)
        if len(segment) < int(.5 * sample_rate):
            continue
        rms = float(np.sqrt(np.mean(np.square(segment, dtype=np.float64))))
        if rms < 2e-4:
            continue
        observations.append((index, line, _voice_fingerprint(segment, sample_rate)))
    if len(observations) < 8:
        return {"enabled": True, "method": method, "reason": "too-few-lexical-lines",
                "analyzed_lines": len(observations), "accepted_lines": 0,
                "proposals": []}

    vectors = np.stack([item[2] for item in observations])
    similarities = vectors @ vectors.T
    first_seed, second_seed = np.unravel_index(np.argmin(similarities), similarities.shape)
    centroids = np.stack([vectors[first_seed], vectors[second_seed]])
    labels = np.zeros(len(vectors), dtype=np.int32)
    for _ in range(12):
        scores = vectors @ centroids.T
        next_labels = np.argmax(scores, axis=1).astype(np.int32)
        if np.array_equal(next_labels, labels) and _ > 0:
            break
        labels = next_labels
        if any(not np.any(labels == lane) for lane in (0, 1)):
            return {"enabled": True, "method": method, "reason": "collapsed-clusters",
                    "analyzed_lines": len(observations), "accepted_lines": 0,
                    "proposals": []}
        centroids = np.stack([
            _unit(np.mean(vectors[labels == lane], axis=0)) for lane in (0, 1)])

    sizes = [int(np.sum(labels == lane)) for lane in (0, 1)]
    primary = (0 if sizes[0] > sizes[1] else 1 if sizes[1] > sizes[0]
               else int(labels[0]))
    secondary = 1 - primary
    centroid_separation = 1.0 - _cosine(centroids[0], centroids[1])
    margins = (vectors @ centroids.T)
    assignment_margins = np.abs(margins[:, 0] - margins[:, 1])
    secondary_positions = np.flatnonzero(labels == secondary)
    secondary_runs = [group for group in np.split(
        secondary_positions, np.where(np.diff(secondary_positions) != 1)[0] + 1)
                      if len(group)]
    has_stable_run = any(len(group) >= 2 for group in secondary_runs)
    accepted_model = (min(sizes) >= 3 and centroid_separation >= .075
                      and float(np.median(assignment_margins)) >= .035
                      and has_stable_run)
    proposals = []
    accepted = 0
    for position, (line_index, line, _vector) in enumerate(observations):
        margin = float(assignment_margins[position])
        is_secondary = int(labels[position]) == secondary
        in_stable_run = any(position in group and len(group) >= 2
                            for group in secondary_runs)
        line_accepted = bool(accepted_model and is_secondary and in_stable_run
                             and margin >= .025)
        accepted += int(line_accepted)
        proposals.append({
            "line": line_index + 1,
            "text": str(line.text),
            "voice_lane": 1 if is_secondary else 0,
            "confidence_margin": round(margin, 4),
            "accepted": line_accepted,
        })
    return {
        "enabled": True,
        "method": method,
        "reason": "stable-two-singer-clusters" if accepted_model else "ambiguous-clusters",
        "analyzed_lines": len(observations),
        "cluster_sizes": sizes,
        "centroid_separation": round(float(centroid_separation), 4),
        "median_assignment_margin": round(float(np.median(assignment_margins)), 4),
        "accepted_lines": accepted,
        "proposals": proposals,
    }


def _activity_regions(audio: np.ndarray, sample_rate: int, start: float,
                      end: float) -> list[tuple[float, float]]:
    first, last = max(0, int(start * sample_rate)), min(len(audio), int(end * sample_rate))
    values = audio[first:last]
    frame = max(1, int(round(.04 * sample_rate)))
    usable = values[:len(values) // frame * frame]
    if not len(usable):
        return []
    rms = np.sqrt(np.mean(np.square(usable.reshape(-1, frame), dtype=np.float64), axis=1))
    peak = float(np.max(rms))
    threshold = max(min(float(np.quantile(rms, .35)) * 2.0, peak * .35), peak * .04, 1e-5)
    release_threshold = max(threshold * .42, peak * .018, 5e-6)
    strong = rms >= threshold
    active = strong.copy()
    # Hysteresis keeps a quiet held vowel attached to its strong lexical core
    # without admitting an unrelated low-level separator tail.
    for index in np.flatnonzero(strong):
        cursor = index - 1
        while cursor >= 0 and rms[cursor] >= release_threshold:
            active[cursor] = True
            cursor -= 1
        cursor = index + 1
        while cursor < len(active) and rms[cursor] >= release_threshold:
            active[cursor] = True
            cursor += 1
    # A consonant or breath may create a tiny dip inside one sung phrase.
    bridge = max(1, int(round(.20 / .04)))
    false_indices = np.flatnonzero(~active)
    if len(false_indices):
        for group in np.split(false_indices, np.where(np.diff(false_indices) != 1)[0] + 1):
            if (len(group) <= bridge and group[0] > 0 and group[-1] + 1 < len(active)
                    and active[group[0] - 1] and active[group[-1] + 1]):
                active[group] = True
    regions = []
    indices = np.flatnonzero(active)
    for group in np.split(indices, np.where(np.diff(indices) != 1)[0] + 1) if len(indices) else []:
        region_start = start + float(group[0]) * .04
        region_end = start + float(group[-1] + 1) * .04
        if region_end - region_start >= .28:
            regions.append((region_start, region_end))
    return regions


def _stabilize_source_identity(sources: list[np.ndarray], sample_rate: int,
                               prototypes: list[np.ndarray | None]
                               ) -> tuple[list[np.ndarray], dict]:
    """Keep anonymous separator outputs attached to one singer identity.

    Waveform correlation works only while overlap-add chunks share samples.
    MFCC distribution, spectral shape and median F0 remain comparable across
    disjoint chorus windows and therefore prevent source 1/2 from swapping.
    """
    fingerprints = [_voice_fingerprint(source, sample_rate) for source in sources]
    swapped = False
    identity_margin = 0.0
    if all(prototype is not None for prototype in prototypes):
        direct = (_cosine(fingerprints[0], prototypes[0])
                  + _cosine(fingerprints[1], prototypes[1]))
        reverse = (_cosine(fingerprints[0], prototypes[1])
                   + _cosine(fingerprints[1], prototypes[0]))
        identity_margin = abs(direct - reverse)
        minimum_margin = float(os.getenv(
            "LRC_MEDLEYVOX_IDENTITY_MIN_MARGIN", "0.035"))
        if reverse > direct + minimum_margin:
            sources = [sources[1], sources[0]]
            fingerprints = [fingerprints[1], fingerprints[0]]
            swapped = True
    for index, fingerprint in enumerate(fingerprints):
        if prototypes[index] is None:
            prototypes[index] = fingerprint
        else:
            # Ambiguous duet windows must not rapidly contaminate the running
            # singer reference. A confident assignment may adapt to a changed
            # register more quickly.
            weight = .22 if identity_margin >= .035 else .05
            blended = (1.0 - weight) * prototypes[index] + weight * fingerprint
            prototypes[index] = blended / max(float(np.linalg.norm(blended)), 1e-8)
    return sources, {"method": "medleyvox-sf-chunk-mfcc-f0-v1",
                     "swapped": swapped, "assignment_margin": round(identity_margin, 4),
                     "confident": identity_margin >= .035}


def _voice_fingerprint(audio: np.ndarray, sample_rate: int) -> np.ndarray:
    import librosa

    if not len(audio) or float(np.max(np.abs(audio))) < 1e-6:
        return np.zeros(43, dtype=np.float32)
    # Remove mostly silent frames before describing timbre. This avoids a
    # separator's noise floor becoming the strongest identity feature.
    intervals = librosa.effects.split(audio, top_db=35, frame_length=1024, hop_length=256)
    voiced = np.concatenate([audio[start:end] for start, end in intervals]) if len(intervals) else audio
    if len(voiced) < 1024:
        voiced = np.pad(voiced, (0, 1024 - len(voiced)))
    mfcc = librosa.feature.mfcc(y=voiced, sr=sample_rate, n_mfcc=20,
                               n_fft=2048, hop_length=512)
    centroid = librosa.feature.spectral_centroid(
        y=voiced, sr=sample_rate, n_fft=2048, hop_length=512)
    bandwidth = librosa.feature.spectral_bandwidth(
        y=voiced, sr=sample_rate, n_fft=2048, hop_length=512)
    rolloff = librosa.feature.spectral_rolloff(
        y=voiced, sr=sample_rate, n_fft=2048, hop_length=512, roll_percent=.85)
    try:
        f0, voiced_flag, _probability = librosa.pyin(
            voiced, fmin=65, fmax=min(1200, sample_rate / 2 - 1), sr=sample_rate,
            frame_length=2048, hop_length=512)
        pitches = np.log1p(f0[np.isfinite(f0) & voiced_flag])
        pitch = np.array([
            float(np.median(pitches)) / 7.0 if len(pitches) else 0.0,
            float(np.subtract(*np.percentile(pitches, [75, 25]))) / 2.0
            if len(pitches) > 2 else 0.0,
        ], dtype=np.float32)
    except (ValueError, FloatingPointError):
        pitch = np.zeros(2, dtype=np.float32)
    # MFCC-0 mostly represents loudness and is intentionally excluded. Each
    # feature family is normalized independently so pitch/timbre cannot be
    # drowned by the much larger raw cepstral coefficient scale.
    mfcc_mean = _unit(np.mean(mfcc[1:], axis=1)) * .58
    mfcc_spread = _unit(np.std(mfcc[1:], axis=1)) * .24
    spectral = _unit(np.array([
        float(np.mean(centroid)) / max(1.0, sample_rate),
        float(np.mean(bandwidth)) / max(1.0, sample_rate),
        float(np.mean(rolloff)) / max(1.0, sample_rate),
    ], dtype=np.float32)) * .18
    vector = np.concatenate([mfcc_mean, mfcc_spread, spectral,
                             _unit(pitch) * .34]).astype(np.float32)
    return vector / max(float(np.linalg.norm(vector)), 1e-8)


def _unit(values: np.ndarray) -> np.ndarray:
    values = np.asarray(values, dtype=np.float32)
    return values / max(float(np.linalg.norm(values)), 1e-8)


def _cosine(left: np.ndarray, right: np.ndarray | None) -> float:
    if right is None:
        return 0.0
    return float(np.dot(left, right) / max(
        float(np.linalg.norm(left)) * float(np.linalg.norm(right)), 1e-8))


def _manual_lane_anchors(lines: list, sources: list[np.ndarray],
                         sample_rate: int) -> list[dict]:
    anchors = []
    for line in lines:
        lane = max(0, int(getattr(line, "voice_lane", 0)))
        if lane == 0:
            continue
        words = getattr(line, "words", None) or []
        if not words:
            continue
        start, end = float(words[0]["start"]), float(words[-1].get("end", words[-1]["start"]))
        first, last = max(0, int(start * sample_rate)), min(len(sources[0]), int(end * sample_rate))
        if last <= first:
            continue
        energies = [float(np.sqrt(np.mean(np.square(source[first:last], dtype=np.float64))))
                    for source in sources]
        order = np.argsort(energies)[::-1]
        winner, runner = int(order[0]), int(order[1])
        ratio = energies[winner] / max(energies[runner], 1e-8)
        anchors.append({"text": line.text, "voice_lane": lane,
                        "source_candidate": winner + 1,
                        "energy_ratio": round(ratio, 4),
                        "accepted": ratio >= 1.18,
                        "manual_reference": bool(getattr(line, "manual_adjusted", False))})
    return anchors


def _preferred_source_for_lane(anchors: list[dict], lane: int) -> int | None:
    votes = [int(item["source_candidate"]) - 1 for item in anchors
             if item.get("voice_lane") == lane and item.get("accepted")]
    if not votes:
        return None
    counts = {candidate: votes.count(candidate) for candidate in set(votes)}
    winner = max(counts, key=counts.get)
    return winner if counts[winner] / len(votes) >= .67 else None


def _load_model(device: str):
    import torch
    from asteroid.models.base_models import BaseEncoderMaskerDecoder
    from asteroid.masknn import TDConvNet
    from asteroid_filterbanks import make_enc_dec
    from .model_loading import hub_file_local_first

    config_path = hub_file_local_first(
        MODEL_REPOSITORY, f"{MODEL_DIRECTORY}/vocals.json", revision=MODEL_REVISION)
    checkpoint_path = hub_file_local_first(
        MODEL_REPOSITORY, f"{MODEL_DIRECTORY}/vocals.pth", revision=MODEL_REVISION)
    config = json.loads(Path(config_path).read_text(encoding="utf-8"))["args"]
    args = Namespace(**config)
    if args.architecture != "conv_tasnet_stft" or int(args.n_src) != 2:
        raise RuntimeError(
            f"Nicht unterstützte MedleyVox-Architektur: {args.architecture}/{args.n_src}")
    encoder, decoder = make_enc_dec(
        "torch_stft", n_filters=args.nfft, kernel_size=args.nfft,
        stride=args.nhop, sample_rate=args.sample_rate)
    masker = TDConvNet(
        in_chan=encoder.n_feats_out, n_src=args.n_src, out_chan=None,
        n_blocks=args.n_blocks, n_repeats=args.n_repeats,
        bn_chan=args.bn_chan, hid_chan=args.hid_chan,
        skip_chan=args.skip_chan, mask_act=args.mask_act)
    model = BaseEncoderMaskerDecoder(
        encoder, masker, decoder, encoder_activation=args.encoder_activation)
    target = torch.device(device if device.startswith("cuda") and torch.cuda.is_available() else "cpu")
    checkpoint = torch.load(checkpoint_path, map_location=target, weights_only=True)
    prefix = "ema_model.module." if bool(args.ema) else ""
    state = {key.removeprefix(prefix): value for key, value in checkpoint.items()
             if not prefix or key.startswith(prefix)}
    missing, unexpected = model.load_state_dict(state, strict=False)
    material_missing = [item for item in missing if not item.endswith("num_batches_tracked")]
    if material_missing or unexpected:
        raise RuntimeError(
            f"MedleyVox-Checkpoint passt nicht zur Architektur: missing={material_missing[:5]}, "
            f"unexpected={unexpected[:5]}")
    return model.to(target).eval(), args


def _separate_overlap_add(audio: np.ndarray, model, device, *, sample_rate: int,
                          chunk_seconds: float, overlap_seconds: float) -> np.ndarray:
    import torch

    chunk = max(1024, int(round(chunk_seconds * sample_rate)))
    overlap = max(0, min(chunk - 1, int(round(overlap_seconds * sample_rate))))
    hop = chunk - overlap
    output = np.zeros((2, len(audio)), dtype=np.float32)
    weights = np.zeros(len(audio), dtype=np.float32)
    starts = list(range(0, max(1, len(audio)), hop))
    with torch.inference_mode():
        for start in starts:
            end = min(len(audio), start + chunk)
            part = audio[start:end]
            padded = np.pad(part, (0, chunk - len(part)))
            tensor = torch.from_numpy(padded).to(device=device, dtype=torch.float32)[None, None, :]
            estimate = model(tensor)
            residual = tensor - estimate.sum(dim=1, keepdim=True)
            estimate = estimate + residual / 2.0
            current = estimate[0, :, :len(part)].detach().cpu().numpy().astype(np.float32)
            if start > 0 and overlap > 0:
                length = min(overlap, len(part), start)
                reference = output[:, start:start + length] / np.maximum(weights[start:start + length], 1e-6)
                identity = _pair_similarity(reference, current[:, :length])
                swapped = _pair_similarity(reference, current[::-1, :length])
                if swapped > identity:
                    current = current[::-1]
            window = _edge_window(len(part), sample_rate, overlap_seconds)
            output[:, start:end] += current * window
            weights[start:end] += window
            if end >= len(audio):
                break
    output /= np.maximum(weights, 1e-6)[None, :]
    return output


def _separation_evidence(sources: list[np.ndarray], sample_rate: int) -> dict:
    frame = max(1, int(round(0.04 * sample_rate)))
    rms_tracks = []
    total = []
    for source in sources:
        usable = source[:len(source) // frame * frame]
        track = (np.sqrt(np.mean(np.square(usable.reshape(-1, frame), dtype=np.float64), axis=1))
                 if len(usable) else np.zeros(0))
        rms_tracks.append(track)
        total.append(float(np.sqrt(np.mean(np.square(source, dtype=np.float64)))) if len(source) else 0.0)
    length = min(map(len, rms_tracks), default=0)
    if length == 0:
        overlap_fraction = 0.0
    else:
        thresholds = []
        for track in rms_tracks:
            values = track[:length]
            peak = float(np.max(values)) if len(values) else 0.0
            # A continuously held harmony has no quiet local noise floor.  Do
            # not let a high quantile raise the gate above that steady voice.
            adaptive = min(float(np.quantile(values, .35)) * 2.0, peak * .35)
            thresholds.append(max(adaptive, peak * .04, 1e-5))
        active = [(track[:length] >= threshold) for track, threshold in zip(rms_tracks, thresholds)]
        overlap_fraction = float(np.mean(active[0] & active[1]))
    balance = min(total) / max(max(total), 1e-9)
    plausible = overlap_fraction >= .08 and balance >= .06
    return {
        "source_rms": [round(value, 8) for value in total],
        "source_balance": round(balance, 5),
        "simultaneous_activity_fraction": round(overlap_fraction, 5),
        "plausible_overlap": plausible,
    }


def _normalize_rms(audio: np.ndarray, target_rms: float = 0.0631) -> tuple[np.ndarray, float]:
    rms = float(np.sqrt(np.mean(np.square(audio, dtype=np.float64)))) if len(audio) else 0.0
    gain = min(20.0, target_rms / max(rms, 1e-8))
    peak = float(np.max(np.abs(audio))) if len(audio) else 0.0
    if peak * gain > .98:
        gain = .98 / peak
    return np.ascontiguousarray(audio * gain, dtype=np.float32), gain


def _resample(audio: np.ndarray, source_rate: int, target_rate: int) -> np.ndarray:
    if source_rate == target_rate:
        return np.ascontiguousarray(audio, dtype=np.float32)
    import torch
    import torchaudio.functional as functional
    tensor = torch.from_numpy(np.ascontiguousarray(audio, dtype=np.float32))
    return functional.resample(tensor, source_rate, target_rate).numpy().astype(np.float32)


def _fit_length(audio: np.ndarray, length: int) -> np.ndarray:
    if len(audio) >= length:
        return np.ascontiguousarray(audio[:length], dtype=np.float32)
    return np.pad(audio, (0, length - len(audio))).astype(np.float32)


def _edge_window(length: int, sample_rate: int, edge_seconds: float = .08) -> np.ndarray:
    window = np.ones(length, dtype=np.float32)
    edge = min(length // 2, max(0, int(round(edge_seconds * sample_rate))))
    if edge:
        ramp = np.linspace(.02, 1.0, edge, dtype=np.float32)
        window[:edge] = ramp
        window[-edge:] = ramp[::-1]
    return window


def _pair_similarity(left: np.ndarray, right: np.ndarray) -> float:
    score = 0.0
    for first, second in zip(left, right):
        denominator = math.sqrt(float(np.dot(first, first)) * float(np.dot(second, second)))
        score += float(np.dot(first, second)) / max(denominator, 1e-9)
    return score


def _truthy(name: str, default: bool) -> bool:
    raw = os.getenv(name)
    if raw is None:
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}
