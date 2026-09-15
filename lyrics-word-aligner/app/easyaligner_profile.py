from __future__ import annotations

import json
import os
import re
import statistics
import tempfile
from pathlib import Path
from typing import Callable

from .audio import ffmpeg_to_flac, ffmpeg_to_mono16k, load_audio
from .consensus import extend_final_word_sustains
from .easy_aligner import MODEL_IDS, EasyGlobalAligner
from .lrc import parse_lrc, render_enhanced_lrc
from .models import AlignmentConfig, LrcLine
from .phoneme_ctc_aligner import (PhonemeCtcAligner,
                                  _acoustic_boundary_evidence)
from .canonical_mapping import assign_canonical_timings
from .separator import KARAOKE_MODEL, StemPaths, separate_stems
from .stem_roles import DEFAULT_ANALYSIS_SEPARATOR_MODELS
from .stem_contrast import refine_final_releases_with_stem_contrast
from .syllables import enrich_lines_with_syllables
from .transcriber import language_code
from .validator import validate


ProgressCallback = Callable[[int, str], None]
TIME_STEP_SECONDS = 0.02
TRAILING_BACKING_RE = re.compile(r"^(?P<lead>.+?)\s+(?P<backing>\([^()]+\))\s*$")
SHORT_FUNCTION_WORDS = frozenset({
    "a", "an", "and", "of", "the", "to",
    "am", "das", "der", "die", "ein", "eine", "im", "und", "von", "zu",
})


def _enabled(value: str | None, default: bool = True) -> bool:
    if value is None:
        return default
    return value.strip().lower() in {"1", "true", "yes", "on"}


def _split_trailing_backing_lines(lines: list[LrcLine]) -> dict:
    """Move an explicitly parenthesised backing phrase to its own voice lane.

    A global monotone CTC path cannot represent two singers at once.  Lyrics
    providers commonly append the simultaneous answer in parentheses, which
    otherwise consumes later repetitions from the lead path.  Keep every
    display token, but give the answer an independently alignable lane.
    """
    inserted: list[LrcLine] = []
    split = 0
    for line in list(lines):
        if line.voice_lane != 0 or line.source_timestamp is None:
            continue
        match = TRAILING_BACKING_RE.fullmatch(line.text.strip())
        if match is None:
            continue
        lead = match.group("lead").strip()
        backing = match.group("backing").strip()
        if not lead or len(backing.strip("() ").split()) < 2:
            continue
        lead_tokens = re.findall(r"[^\W_]+", lead.casefold(), flags=re.UNICODE)
        backing_tokens = re.findall(
            r"[^\W_]+", backing.strip("() ").casefold(), flags=re.UNICODE)
        repeated_suffix = (len(lead_tokens) > len(backing_tokens)
                           and lead_tokens[-len(backing_tokens):] == backing_tokens)
        line.text = lead
        inserted.append(LrcLine(
            timestamp=line.timestamp,
            text=backing,
            original=line.original,
            timed_input=line.timed_input,
            source_timestamp=line.source_timestamp,
            source_end_boundary=line.source_end_boundary,
            voice_lane=1,
            voice_label="Backing Echo" if repeated_suffix else "Backing Vocals",
        ))
        split += 1
    lines.extend(inserted)
    return {"method": "parenthesized-backing-independent-lane-v1",
            "split_lines": split, "inserted_lines": len(inserted)}


def _source_anchor_calibration(lines: list[LrcLine]) -> dict:
    """Decide whether provider line times are coherent enough for guardrails."""
    deltas = [float(line.words[0]["start"]) - float(line.source_timestamp)
              for line in lines if line.words and line.source_timestamp is not None]
    if len(deltas) < 6:
        return {"trusted": False, "reason": "too-few-source-anchors",
                "anchors": len(deltas), "offset_seconds": 0.0}
    offset = statistics.median(deltas)
    deviations = [abs(value - offset) for value in deltas]
    mad = statistics.median(deviations)
    # A minority of global CTC collapses is precisely what this calibration is
    # intended to catch.  Median statistics keep those failures from poisoning
    # an otherwise coherent provider timeline.
    trusted = mad <= 0.75 and abs(offset) <= 4.0
    return {
        "trusted": trusted,
        "reason": "coherent-provider-timeline" if trusted else "incoherent-provider-timeline",
        "anchors": len(deltas),
        "offset_seconds": round(offset, 3),
        "median_absolute_deviation_seconds": round(mad, 3),
    }


def _next_source_start(lines: list[LrcLine], index: int, offset: float) -> float | None:
    lane = lines[index].voice_lane
    for following in lines[index + 1:]:
        if following.voice_lane == lane and following.source_timestamp is not None:
            return float(following.source_timestamp) + offset
    return None


def _offset_aligned_words(words: list[dict], offset: float) -> list[dict]:
    result = []
    for word in words:
        item = dict(word)
        item["start"] = round(float(item["start"]) + offset, 3)
        item["end"] = round(float(item["end"]) + offset, 3)
        result.append(item)
    return result


def _maximum_word_gap(words: list[dict]) -> float:
    return max((float(right["start"]) - float(left["end"])
                for left, right in zip(words, words[1:])), default=0.0)


def _normalized_display_word(word: dict) -> str:
    return str(word.get("word", "")).strip(".,!?;:'\"()[]{}").casefold()


def _has_overlong_function_word(words: list[dict], *, maximum: float = 0.65) -> bool:
    return any(_normalized_display_word(word) in SHORT_FUNCTION_WORDS
               and float(word["end"]) - float(word["start"]) > maximum
               for word in words)


def apply_source_timing_guardrails(lines: list[LrcLine], audio,
                                   aligner: EasyGlobalAligner, mapping: dict,
                                   *, start_tolerance: float = 1.5) -> dict:
    """Locally re-align only global lines that contradict a coherent LRC clock.

    Source timestamps never become word boundaries.  They merely bound a new
    exact-text CTC measurement, preventing one missing ad-lib or instrumental
    break from shifting every later repetition.
    """
    calibration = _source_anchor_calibration(lines)
    diagnostics = []
    if not calibration["trusted"]:
        return {**calibration, "method": "calibrated-source-guardrails-v1",
                "attempted_lines": 0, "repaired_lines": 0,
                "diagnostics": diagnostics}
    duration = len(audio) / 16000
    offset = float(calibration["offset_seconds"])
    attempted = repaired = 0
    for index, line in enumerate(lines):
        if line.voice_lane != 0 or not line.words or line.source_timestamp is None:
            continue
        expected = float(line.source_timestamp) + offset
        following = _next_source_start(lines, index, offset)
        boundary = (float(line.source_end_boundary) + offset
                    if line.source_end_boundary is not None else None)
        actual_start = float(line.words[0]["start"])
        actual_end = float(line.words[-1]["end"])
        maximum_internal_gap = _maximum_word_gap(line.words)
        reasons = []
        if abs(actual_start - expected) > start_tolerance:
            reasons.append("line-start-outside-guardrail")
        if following is not None and actual_end > following + 0.75:
            reasons.append("line-spills-into-next-source-line")
        if boundary is not None and actual_end > boundary + 0.50:
            reasons.append("line-crosses-explicit-section-end")
        if maximum_internal_gap > 1.20:
            reasons.append("implausible-internal-word-gap")
        if _has_overlong_function_word(line.words):
            reasons.append("overlong-short-function-word")
        if not reasons:
            continue
        attempted += 1
        window_start = max(0.0, expected - 0.75)
        candidates = [duration]
        if following is not None:
            source_gap = max(0.0, following - expected)
            trailing_space = max(0.35, min(1.20, source_gap * 0.22))
            candidates.append(following - trailing_space)
        if boundary is not None:
            candidates.append(boundary + 0.30)
        window_end = min(candidates)
        if window_end - window_start < 0.8:
            diagnostics.append({"line": index + 1, "text": line.text,
                                "status": "invalid-source-window", "reasons": reasons})
            continue
        try:
            local = _offset_aligned_words(aligner.align(
                audio[int(window_start * 16000):int(window_end * 16000)], line.text),
                window_start)
            # Repeated refrains sometimes let the final one or two tokens jump
            # to the next occurrence even inside a source-bounded window.  A
            # second, earlier closing edge tests the same exact text against
            # the current occurrence. Prefer it only when it removes the
            # conspicuous lexical hole.
            if (_maximum_word_gap(local) > 1.20
                    or _has_overlong_function_word(local)) and window_end - window_start > 2.0:
                retry_end = window_end - min(0.80, (window_end - window_start) * 0.22)
                retry = _offset_aligned_words(aligner.align(
                    audio[int(window_start * 16000):int(retry_end * 16000)], line.text),
                    window_start)
                local_badness = (_maximum_word_gap(local)
                                 + (0.8 if _has_overlong_function_word(local) else 0.0))
                retry_badness = (_maximum_word_gap(retry)
                                 + (0.8 if _has_overlong_function_word(retry) else 0.0))
                if retry_badness + 0.25 < local_badness:
                    local = retry
                    window_end = retry_end
        except Exception as exception:
            diagnostics.append({"line": index + 1, "text": line.text,
                                "status": "local-alignment-failed", "reasons": reasons,
                                "error": str(exception)[:300]})
            continue
        local_start = float(local[0]["start"])
        local_end = float(local[-1]["end"])
        if (abs(local_start - expected) > 1.0
                or (following is not None and local_end > following + 0.35)
                or (boundary is not None and local_end > boundary + 0.35)):
            diagnostics.append({"line": index + 1, "text": line.text,
                                "status": "local-path-rejected", "reasons": reasons,
                                "candidate_start": local_start,
                                "candidate_end": local_end})
            continue
        assign_easyaligner_timings([line], local)
        for word in line.words:
            word["timing_source"] = "easyaligner-source-guided-local"
            word["source_guardrail_repair"] = True
        repaired += 1
        diagnostics.append({"line": index + 1, "text": line.text,
                            "status": "repaired", "reasons": reasons,
                            "original_start": round(actual_start, 3),
                            "candidate_start": round(float(line.words[0]["start"]), 3),
                            "window": [round(window_start, 3), round(window_end, 3)]})
    return {**calibration, "method": "calibrated-source-guardrails-v1",
            "attempted_lines": attempted, "repaired_lines": repaired,
            "diagnostics": diagnostics}


def align_independent_backing_lines(lines: list[LrcLine], audio,
                                    aligner: EasyGlobalAligner,
                                    calibration: dict) -> dict:
    """Align provider-marked backing answers without serialising them as lead."""
    backing = [line for line in lines if line.voice_lane > 0 and line.source_timestamp is not None]
    offset = float(calibration.get("offset_seconds", 0.0))
    duration = len(audio) / 16000
    aligned = 0
    diagnostics = []
    lead_starts = sorted(float(line.source_timestamp) + offset for line in lines
                         if line.voice_lane == 0 and line.source_timestamp is not None)
    for line in backing:
        expected = float(line.source_timestamp) + offset
        following = next((value for value in lead_starts if value > expected + 0.05), duration)
        window_start = max(0.0, expected - 0.75)
        if line.voice_label == "Backing Echo":
            owner = next((candidate for candidate in lines
                          if candidate.voice_lane == 0 and candidate.words
                          and candidate.source_timestamp == line.source_timestamp), None)
            if owner is not None:
                window_start = max(window_start,
                                   float(owner.words[-1]["end"]) - 0.15)
        source_gap = max(0.0, following - expected)
        trailing_space = max(0.35, min(1.20, source_gap * 0.22))
        window_end = min(duration, following - trailing_space)
        if window_end - window_start < 0.8:
            diagnostics.append({"text": line.text, "status": "invalid-source-window"})
            continue
        try:
            local = _offset_aligned_words(aligner.align(
                audio[int(window_start * 16000):int(window_end * 16000)], line.text),
                window_start)
            assign_easyaligner_timings([line], local)
            for word in line.words:
                word["timing_source"] = "easyaligner-independent-backing-local"
            aligned += 1
            diagnostics.append({"text": line.text, "status": "aligned",
                                "window": [round(window_start, 3), round(window_end, 3)]})
        except Exception as exception:
            diagnostics.append({"text": line.text, "status": "alignment-failed",
                                "error": str(exception)[:300]})
    return {"method": "source-window-independent-backing-v1",
            "attempted_lines": len(backing), "aligned_lines": aligned,
            "diagnostics": diagnostics}


def _line_alignment_score(line: LrcLine) -> float:
    return statistics.mean(
        float(word.get("easyaligner_score", word.get("confidence", 0.0)))
        for word in line.words
    ) if line.words else 0.0


def _primary_vocal_collapse(line: LrcLine) -> bool:
    if line.voice_lane != 0 or line.source_timestamp is None or not line.words:
        return False
    score = _line_alignment_score(line)
    start_error = abs(float(line.words[0]["start"]) - float(line.source_timestamp))
    span_per_word = (float(line.words[-1]["end"]) - float(line.words[0]["start"])) / len(line.words)
    return ((start_error > 3.0 and score < 0.40)
            or (score < 0.10 and start_error > 1.10)
            or (score < 0.025 and span_per_word < 0.30))


def backing_recovery_candidate_indices(lines: list[LrcLine]) -> list[int]:
    """Find source-anchored lines that the primary vocal stem barely supports.

    A missing group vocal typically appears as a compressed, near-zero-score
    CTC run several seconds away from the provider's line anchor.  Neighbouring
    lines are included so a simultaneous lead response can be measured on the
    same all-vocals stem and projected to a separate editor lane when needed.
    """
    suspicious: set[int] = set()
    for index, line in enumerate(lines):
        if _primary_vocal_collapse(line):
            suspicious.add(index)
    expanded = set(suspicious)
    for index in suspicious:
        for neighbour in range(max(0, index - 2), min(len(lines), index + 3)):
            expanded.add(neighbour)
    return sorted(expanded)


def recover_missing_backing_vocals(lines: list[LrcLine], all_vocals_audio,
                                    aligner: EasyGlobalAligner,
                                    candidate_indices: list[int]) -> dict:
    """Re-measure collapsed lines on an independent all-vocals separation."""
    duration = len(all_vocals_audio) / 16000
    diagnostics: list[dict] = []
    accepted: dict[int, dict] = {}
    for index in candidate_indices:
        line = lines[index]
        if line.voice_lane != 0 or line.source_timestamp is None or not line.words:
            continue
        source_start = float(line.source_timestamp)
        following = next((float(candidate.source_timestamp)
                          for candidate in lines[index + 1:]
                          if candidate.voice_lane == 0
                          and candidate.source_timestamp is not None), None)
        boundary = (float(line.source_end_boundary)
                    if line.source_end_boundary is not None else None)
        window_start = max(0.0, source_start - .90)
        window_end = min(duration, min(
            value for value in (
                (following + 1.25) if following is not None else duration,
                (boundary + .75) if boundary is not None else duration,
                source_start + 12.0,
            )))
        if window_end - window_start < .8:
            continue
        primary_score = _line_alignment_score(line)
        primary_start = float(line.words[0]["start"])
        try:
            local = _offset_aligned_words(aligner.align(
                all_vocals_audio[int(window_start * 16000):int(window_end * 16000)],
                line.text), window_start)
            candidate_score = statistics.mean(float(word.get("score", 0.0)) for word in local)
            candidate_start = float(local[0]["start"])
            primary_error = abs(primary_start - source_start)
            candidate_error = abs(candidate_start - source_start)
            stronger = (_primary_vocal_collapse(line)
                        and candidate_score >= max(.035, primary_score * 1.35))
            anchor_rescue = (candidate_score >= .035
                             and candidate_error + .50 < primary_error)
            if not (stronger or anchor_rescue):
                diagnostics.append({
                    "line": index + 1, "text": line.text, "status": "not-better",
                    "primary_score": round(primary_score, 4),
                    "candidate_score": round(candidate_score, 4),
                    "primary_start": round(primary_start, 3),
                    "candidate_start": round(candidate_start, 3),
                })
                continue
            assign_easyaligner_timings([line], local)
            for word in line.words:
                word["timing_source"] = "easyaligner-all-vocals-recovery"
                word["backing_vocal_recovery"] = True
            accepted[index] = {
                "primary_score": primary_score,
                "candidate_score": candidate_score,
            }
            diagnostics.append({
                "line": index + 1, "text": line.text, "status": "recovered",
                "primary_score": round(primary_score, 4),
                "candidate_score": round(candidate_score, 4),
                "primary_start": round(primary_start, 3),
                "candidate_start": round(float(line.words[0]["start"]), 3),
                "window": [round(window_start, 3), round(window_end, 3)],
            })
        except Exception as exception:
            diagnostics.append({"line": index + 1, "text": line.text,
                                "status": "alignment-failed",
                                "error": str(exception)[:300]})

    projected = 0
    normalized_lines = [" ".join(re.findall(
        r"[^\W_]+", line.text.casefold(), flags=re.UNICODE)) for line in lines]
    repetition_counts = {
        text: normalized_lines.count(text) for text in set(normalized_lines) if text
    }
    timed = sorted(((float(line.words[0]["start"]), index, line)
                    for index, line in enumerate(lines) if line.words),
                   key=lambda item: (item[0], item[1]))
    for position, (_, left_index, left) in enumerate(timed):
        if left.voice_lane != 0:
            continue
        left_end = float(left.words[-1]["end"])
        for _, right_index, right in timed[position + 1:]:
            if float(right.words[0]["start"]) >= left_end - .12:
                break
            if right.voice_lane != 0 or not ({left_index, right_index} & accepted.keys()):
                continue
            if max(repetition_counts.get(normalized_lines[left_index], 0),
                   repetition_counts.get(normalized_lines[right_index], 0)) <= 1:
                continue
            left_score = accepted.get(left_index, {}).get(
                "primary_score", _line_alignment_score(left))
            right_score = accepted.get(right_index, {}).get(
                "primary_score", _line_alignment_score(right))
            move = left if left_score < right_score else right
            move.voice_lane = 1
            move.voice_label = "Recovered Backing Vocals"
            projected += 1
            if move is left:
                break
    return {
        "enabled": True,
        "method": "adaptive-all-vocals-local-recovery-v1",
        "candidate_lines": len(candidate_indices),
        "recovered_lines": len(accepted),
        "projected_backing_lines": projected,
        "diagnostics": diagnostics,
    }


def reconcile_guardrail_lane_overlaps(lines: list[LrcLine]) -> dict:
    """Keep a provider-only token from stealing time from the next phrase.

    If even independent local CTC windows place a suffix beyond the measured
    onset of the following line, the source contains a token that is absent,
    simultaneous, or indistinguishable in this vocal stem.  Preserve its human
    display spelling, but give it no karaoke sweep instead of fabricating a
    rhythmically disruptive duration.
    """
    clipped = omitted = 0
    diagnostics = []
    lanes: dict[int, list[LrcLine]] = {}
    for line in lines:
        if line.words:
            lanes.setdefault(line.voice_lane, []).append(line)
    for lane, lane_lines in lanes.items():
        lane_lines.sort(key=lambda line: float(line.words[0]["start"]))
        for previous, following in zip(lane_lines, lane_lines[1:]):
            boundary = float(following.words[0]["start"])
            if float(previous.words[-1]["end"]) <= boundary + 0.001:
                continue
            affected = []
            for word in previous.words:
                start = float(word["start"])
                end = float(word["end"])
                if start >= boundary - 0.001:
                    word["start"] = boundary
                    word["end"] = boundary
                    word["frame_start"] = round(boundary / TIME_STEP_SECONDS)
                    word["frame_end"] = word["frame_start"]
                    word["technical_text"] = ""
                    word["technical_omitted"] = True
                    word["timing_source"] = "source-text-unverified-display-only"
                    omitted += 1
                    affected.append(word.get("word", ""))
                elif end > boundary:
                    word["end"] = boundary
                    word["frame_end"] = round(boundary / TIME_STEP_SECONDS)
                    word["guardrail_end_clipped"] = True
                    clipped += 1
                    affected.append(word.get("word", ""))
            diagnostics.append({"lane": lane, "previous": previous.text,
                                "following": following.text,
                                "boundary": round(boundary, 3),
                                "affected_words": affected})
    return {"method": "unverified-source-suffix-display-only-v1",
            "clipped_word_ends": clipped, "display_only_words": omitted,
            "diagnostics": diagnostics}


def _apply_phoneme_onset_candidates(
        lines, phoneme_result: dict, audio, *,
        maximum_easyaligner_confidence: float = 0.55,
        minimum_phoneme_confidence: float = 0.50,
        minimum_acoustic_evidence: float = 0.60,
        minimum_shift: float = 0.04,
        maximum_shift: float = 0.14) -> dict:
    """Promote only weak EasyAligner onsets with two independent votes.

    The global grapheme path remains authoritative.  A global IPA path may
    challenge a weak onset only when it stays close, preserves monotonic word
    geometry and a local energy/spectral measurement supports the same frame.
    Word ends remain owned by EasyAligner and the vocal sustain refiners.
    """
    words = phoneme_result.get("words", [])
    ranges = phoneme_result.get("line_ranges", [])
    diagnostics = []
    adjusted = 0
    attempted = 0
    for line_index, line in enumerate(lines):
        if line_index >= len(ranges) or not line.words:
            continue
        first, final = ranges[line_index]
        candidates = words[first:final]
        if len(candidates) != len(line.words):
            diagnostics.append({"line": line_index + 1,
                                "status": "word-count-mismatch"})
            continue
        for word_index, (word, candidate) in enumerate(zip(line.words, candidates)):
            easy_confidence = float(word.get("easyaligner_score", 1.0))
            if easy_confidence > maximum_easyaligner_confidence:
                continue
            attempted += 1
            phoneme_confidence = float(candidate.get("confidence", 0.0))
            old_start = float(word["start"])
            proposed_start = float(candidate["start"])
            shift = proposed_start - old_start
            diagnostic = {
                "line": line_index + 1,
                "word": word_index + 1,
                "text": word.get("word", ""),
                "easyaligner_confidence": round(easy_confidence, 4),
                "phoneme_confidence": round(phoneme_confidence, 4),
                "delta_ms": round(shift * 1000, 1),
            }
            if phoneme_confidence < minimum_phoneme_confidence:
                diagnostics.append({**diagnostic, "status": "weak-phoneme-vote"})
                continue
            if not minimum_shift <= abs(shift) <= maximum_shift:
                diagnostics.append({**diagnostic, "status": "outside-safe-shift"})
                continue
            previous_end = (float(line.words[word_index - 1]["end"])
                            if word_index else 0.0)
            if proposed_start < previous_end + (0.001 if word_index else 0.0):
                diagnostics.append({**diagnostic, "status": "nonmonotonic"})
                continue
            if float(word["end"]) - proposed_start < 0.04:
                diagnostics.append({**diagnostic, "status": "word-too-short"})
                continue
            previous_phone_end = (float(candidates[word_index - 1]["end"])
                                  if word_index else None)
            kind = ("onset" if previous_phone_end is None
                    or proposed_start - previous_phone_end >= 0.055
                    else "transition")
            evidence = _acoustic_boundary_evidence(audio, proposed_start, kind)
            if (not evidence.get("supported")
                    or float(evidence.get("score", 0.0)) < minimum_acoustic_evidence):
                diagnostics.append({**diagnostic, "status": "no-acoustic-vote",
                                    "evidence": evidence})
                continue
            word["start"] = round(proposed_start, 3)
            word["phoneme_start_original"] = round(old_start, 3)
            word["phoneme_start_candidate"] = round(proposed_start, 3)
            word["phoneme_start_refinement_ms"] = round(shift * 1000, 1)
            word["phoneme_confidence"] = round(phoneme_confidence, 4)
            word["phoneme_source"] = "xlsr-espeak-local-shadow"
            word["phoneme_start_evidence"] = evidence
            word["phonemes"] = candidate.get("phonemes", [])
            adjusted += 1
            diagnostics.append({**diagnostic, "status": "accepted",
                                "evidence": evidence})
        line.timestamp = float(line.words[0]["start"])
    return {
        "enabled": True,
        "method": "easyaligner-weak-onset-ipa-shadow-v1",
        "attempted_words": attempted,
        "adjusted_words": adjusted,
        "maximum_easyaligner_confidence": maximum_easyaligner_confidence,
        "minimum_phoneme_confidence": minimum_phoneme_confidence,
        "minimum_acoustic_evidence": minimum_acoustic_evidence,
        "minimum_shift_ms": round(minimum_shift * 1000),
        "maximum_shift_ms": round(maximum_shift * 1000),
        "diagnostics": diagnostics,
    }


def _local_phoneme_candidates(aligner: PhonemeCtcAligner, lines, audio,
                              *, padding: float = 0.45) -> dict:
    """Align each canonical line inside a narrow EasyAligner-anchored window."""
    duration = len(audio) / 16000
    words = []
    line_ranges = []
    failures = []
    for line_index, line in enumerate(lines):
        first = len(words)
        if not line.words:
            line_ranges.append((first, first))
            continue
        start = max(0.0, float(line.words[0]["start"]) - padding)
        end = min(duration, float(line.words[-1]["end"]) + padding)
        try:
            aligned = aligner.align(
                audio[int(start * 16000):int(end * 16000)], line.text, start)
        except Exception as exception:
            aligned = []
            failures.append({"line": line_index + 1, "error": str(exception)})
        if len(aligned) != len(line.words):
            failures.append({"line": line_index + 1,
                             "error": "word-count-or-oov-mismatch"})
            line_ranges.append((first, first))
            continue
        words.extend(aligned)
        line_ranges.append((first, len(words)))
    return {"words": words, "line_ranges": line_ranges,
            "windows": len(lines), "failures": failures}


def refine_easyaligner_phoneme_onsets(lines, audio, language: str,
                                      device: str) -> dict:
    """Run optional local IPA shadow windows and gate every mutation."""
    if not _enabled(os.getenv("LRC_EASYALIGNER_PHONEME_VERIFY"), True):
        return {"enabled": False, "reason": "disabled-by-configuration",
                "method": "easyaligner-weak-onset-ipa-shadow-v1"}
    try:
        aligner = PhonemeCtcAligner(language, device)
        try:
            candidates = _local_phoneme_candidates(aligner, lines, audio)
            model_id = aligner.model_id
        finally:
            aligner.close()
        summary = _apply_phoneme_onset_candidates(
            lines, candidates, audio,
            maximum_easyaligner_confidence=float(os.getenv(
                "LRC_EASYALIGNER_PHONEME_MAX_CTC_CONFIDENCE", "0.55")),
            minimum_phoneme_confidence=float(os.getenv(
                "LRC_PHONEME_PROMOTION_MIN_CONFIDENCE", "0.50")),
            minimum_acoustic_evidence=float(os.getenv(
                "LRC_PHONEME_PROMOTION_MIN_EVIDENCE", "0.60")),
            maximum_shift=float(os.getenv(
                "LRC_PHONEME_PROMOTION_MAX_SHIFT", "0.14")))
        return {
            **summary,
            "model": model_id,
            "phoneme_windows": candidates.get("windows", 0),
            "phoneme_failures": candidates.get("failures", []),
        }
    except Exception as exception:
        # This verifier is deliberately supplementary. An unsupported eSpeak
        # word or unavailable shadow model must not discard a complete global
        # EasyAligner result.
        return {"enabled": True, "applied": False,
                "method": "easyaligner-weak-onset-ipa-shadow-v1",
                "error": str(exception), "attempted_words": 0,
                "adjusted_words": 0, "diagnostics": []}


def refine_easyaligner_sustain_releases(lines, audio) -> dict:
    """Extend only acoustically connected sung tails after the CTC core.

    EasyAligner's global CTC path is authoritative for every word onset and
    lexical core. Singing can hold the final vowel long after that core has
    been consumed, though. Measure that decay on the same isolated vocal stem
    and preserve the original CTC frame as provenance.
    """
    original_ends = {
        id(word): (float(word["end"]), int(word["frame_end"]))
        for line in lines for word in line.words
    }
    # A CTC character edge is already a good rhythmic release estimate. The
    # generic singing detector is intentionally generous for pipelines whose
    # ASR words end early, but that made short tonal residue feel late here.
    # Retain only clearly longer, continuously voiced holds and compensate for
    # the detector window's perceptually inaudible tail.
    summary = extend_final_word_sustains(
        lines, [], audio=audio,
        release_padding=-0.06,
        minimum_sung_extension=0.18,
        final_bridge_gap=0.20)
    protected_extensions = 0
    for line in lines:
        for word_index, word in enumerate(line.words):
            old_end, old_frame_end = original_ends[id(word)]
            if float(word["end"]) <= old_end + .001:
                continue
            # The lexical CTC core of an internal connector is rhythmically
            # more useful than a generic tonal tail. Separator residue or the
            # following word's vowel must not turn "to"/"und" into a held
            # karaoke word. Final words may still be genuinely sustained.
            if (word_index < len(line.words) - 1
                    and _normalized_display_word(word) in SHORT_FUNCTION_WORDS):
                word["end"] = old_end
                word["frame_end"] = old_frame_end
                for key in ("acoustic_end", "sustain_activity_end",
                            "sustain_tonal_release", "sustain_release_confidence",
                            "sustain_extension_ms"):
                    word.pop(key, None)
                protected_extensions += 1
                continue
            word["ctc_frame_end"] = old_frame_end
            word["sung_release_frame_end"] = round(
                float(word["end"]) / TIME_STEP_SECONDS)
            word["release_timing_source"] = "isolated-vocal-tonal-decay"
    if protected_extensions:
        summary["adjusted_words"] = max(
            0, int(summary.get("adjusted_words", 0)) - protected_extensions)
    summary["protected_internal_function_words"] = protected_extensions
    return summary


def assign_easyaligner_timings(lines, aligned_words: list[dict]) -> dict:
    """Map one immutable global CTC path back to the canonical display text."""
    prepared = []
    for word in aligned_words:
        item = dict(word)
        item["probability"] = float(item.get("score", item.get("probability", 0.0)))
        prepared.append(item)
    summary = assign_canonical_timings(lines, prepared)
    mapped_words = 0
    for line in lines:
        for word in line.words:
            mapped_words += 1
            confidence = float(word.get("probability", 0.0))
            word["timing_source"] = "easyaligner-global-direct"
            word["confidence"] = round(max(0.0, min(1.0, confidence)), 4)
            word["easyaligner_score"] = word["confidence"]
            word["frame_start"] = round(float(word["start"]) / TIME_STEP_SECONDS)
            word["frame_end"] = round(float(word["end"]) / TIME_STEP_SECONDS)
            word["alignment_grid_ms"] = 20
            if float(word["end"]) <= float(word["start"]):
                # A measured one-frame CTC span can collapse when both raw
                # edges round to the same 20 ms presentation-grid point (most
                # visibly for the English article "a"). Keep that CTC frame.
                word["frame_end"] = int(word["frame_start"]) + 1
                word["end"] = round(
                    float(word["start"]) + TIME_STEP_SECONDS, 3)
    if mapped_words != int(summary["display_words"]):
        raise ValueError(
            "EasyAligner-Zuordnung enthält nicht alle kanonischen Anzeigewörter.")
    return {
        **summary,
        "method": "easyaligner-global-viterbi-direct-v1",
        "timing_source": "easyaligner-global-direct",
    }


def run(audio_path: Path, lrc_path: Path, output_dir: Path, *, language: str,
        separator: bool, device: str, progress: ProgressCallback | None = None,
        provided_vocals: Path | None = None,
        provided_instrumental: Path | None = None, **_) -> dict:
    """Run the benchmark-winning EasyAligner path without downstream mutation."""
    if (provided_vocals is None) != (provided_instrumental is None):
        raise ValueError(
            "Vorhandene Vocal- und Instrumental-Stems müssen gemeinsam angegeben werden.")
    notify = progress or (lambda _percent, _message: None)
    output_dir.mkdir(parents=True, exist_ok=True)
    headers, lines = parse_lrc(lrc_path)
    if not lines:
        raise ValueError("Die Lyrics enthalten keine alignierbaren Zeilen.")
    backing_split = _split_trailing_backing_lines(lines)
    # Enhanced input may be selected as a convenient text source in the editor,
    # but its word boundaries are never authority for this independent path.
    for line in lines:
        line.words = []
        line.timed_input = False
        line.status = "pending"
        line.reason = None
    primary_lines = [line for line in lines if line.voice_lane == 0]
    canonical_text = "\n".join(line.text for line in primary_lines)
    resolved_language = language_code(
        None if language.strip().lower() == "auto" else language, canonical_text)
    if resolved_language not in MODEL_IDS:
        raise ValueError(
            "Das direkte EasyAligner-Profil unterstützt derzeit Deutsch und Englisch.")

    stem = audio_path.stem
    stem_outputs: dict[str, str] = {}
    stems_manifest = None
    notify(5, "EasyAligner Direct: kanonischer Text wird ohne Wortzeiten vorbereitet")
    with tempfile.TemporaryDirectory(prefix="easyaligner-direct-") as temporary:
        temp = Path(temporary)
        if provided_vocals is not None:
            stems = StemPaths(provided_vocals, provided_instrumental)
            audio_source = "provided-library-vocal-stem"
            notify(15, "Gespeicherter Vocal-Stem wird als einzige Alignmentquelle geladen")
        elif separator:
            notify(10, "Vocal-Stem für EasyAligner Direct wird erzeugt")
            stems = separate_stems(audio_path, temp / "separated")
            audio_source = KARAOKE_MODEL
        else:
            stems = None
            audio_source = "original-mix-fallback"
        alignment_source = stems.vocals if stems is not None else audio_path
        vocal_wav = ffmpeg_to_mono16k(alignment_source, temp / "alignment-16k.wav")
        audio = load_audio(vocal_wav)
        detail_audio = audio

        language_label = "deutscher" if resolved_language == "de" else "englischer"
        notify(35, f"Globaler {language_label} CTC/Viterbi-Pfad wird berechnet")
        aligner = EasyGlobalAligner(resolved_language, device)
        try:
            aligned_words = aligner.align(audio, canonical_text)
            model_id = aligner.model_id
            ctc_inference = aligner.last_inference
            notify(80, "EasyAligner-Wörter werden auf den kanonischen Text abgebildet")
            mapping = assign_easyaligner_timings(primary_lines, aligned_words)
            notify(81, "Plausible LRC-Zeiten prüfen den globalen Pfad auf Abschnittssprünge")
            source_guardrails = apply_source_timing_guardrails(
                lines, audio, aligner, mapping)
            backing_alignment = align_independent_backing_lines(
                lines, audio, aligner, source_guardrails)
        finally:
            aligner.close()
        recovery_candidates = backing_recovery_candidate_indices(lines)
        backing_recovery = {
            "enabled": _enabled(os.getenv("LRC_EASYALIGNER_BACKING_RECOVERY"), True),
            "method": "adaptive-all-vocals-local-recovery-v1",
            "candidate_lines": len(recovery_candidates),
            "recovered_lines": 0,
            "projected_backing_lines": 0,
            "reason": "no-primary-vocal-collapse" if not recovery_candidates else None,
            "diagnostics": [],
        }
        if backing_recovery["enabled"] and recovery_candidates:
            recovery_model = os.getenv(
                "LRC_EASYALIGNER_BACKING_RECOVERY_MODEL",
                DEFAULT_ANALYSIS_SEPARATOR_MODELS[0]).strip()
            notify(82, "Schwache Chorus-Zeilen werden auf einem All-Vocals-Stem nachgemessen")
            try:
                recovery_stems = separate_stems(
                    audio_path, temp / "backing-recovery", model_name=recovery_model)
                recovery_wav = ffmpeg_to_mono16k(
                    recovery_stems.vocals, temp / "backing-recovery-16k.wav")
                recovered_audio = load_audio(recovery_wav)
                recovery_aligner = EasyGlobalAligner(resolved_language, device)
                try:
                    backing_recovery = recover_missing_backing_vocals(
                        lines, recovered_audio, recovery_aligner, recovery_candidates)
                finally:
                    recovery_aligner.close()
                backing_recovery["model"] = recovery_model
                if backing_recovery["recovered_lines"] > 0:
                    detail_audio = recovered_audio
            except Exception as exception:
                backing_recovery = {
                    **backing_recovery,
                    "reason": "recovery-separation-failed",
                    "model": recovery_model,
                    "error": str(exception)[:500],
                }
        lines.sort(key=lambda line: (line.timestamp, line.voice_lane))
        notify(83, "Unsichere Wortanfänge werden durch einen IPA-Schattenpfad geprüft")
        phoneme_verification = refine_easyaligner_phoneme_onsets(
            lines, detail_audio, resolved_language, device)
        notify(86, "Lang gehaltene Wortenden werden im Vocal-Stem ausgemessen")
        sustain_releases = refine_easyaligner_sustain_releases(lines, detail_audio)
        if stems is not None:
            instrumental_wav = ffmpeg_to_mono16k(
                stems.instrumental, temp / "instrumental-16k.wav")
            instrumental_audio = load_audio(instrumental_wav)
            stem_contrast_releases = refine_final_releases_with_stem_contrast(
                lines, detail_audio, instrumental_audio, mode="select",
                include_internal_phrase_ends=True)
        else:
            stem_contrast_releases = {
                "enabled": False,
                "reason": "paired-stems-unavailable",
                "method": "paired-stem-vocal-dominance-release-v1",
                "mode": "select",
            }
        overlap_reconciliation = reconcile_guardrail_lane_overlaps(lines)
        notify(89, "Silben werden innerhalb der fertigen EasyAligner-Wortfenster erkannt")
        syllable_summary = enrich_lines_with_syllables(
            lines, resolved_language, audio=detail_audio,
            enforce_minimum_geometry=True)

        if stems is not None:
            vocals_out = output_dir / f"{stem}.vocals.flac"
            instrumental_out = output_dir / f"{stem}.instrumental.flac"
            ffmpeg_to_flac(stems.vocals, vocals_out)
            ffmpeg_to_flac(stems.instrumental, instrumental_out)
            stem_outputs = {
                "vocals": vocals_out.name,
                "instrumental": instrumental_out.name,
            }
            stems_out = output_dir / f"{stem}.stems.json"
            stems_out.write_text(json.dumps({
                "version": 2,
                "source": audio_path.name,
                "separator_model": audio_source,
                "format": "flac",
                "sample_aligned": True,
                **stem_outputs,
            }, ensure_ascii=False, indent=2), encoding="utf-8")
            stems_manifest = stems_out.name

    notify(90, "EasyAligner Direct wird auf Vollständigkeit geprüft")
    validation = validate(lines, AlignmentConfig(language=resolved_language))
    lrc_out = output_dir / f"{stem}.word-synced.lrc"
    report_out = output_dir / f"{stem}.alignment.json"
    lrc_out.write_text(render_enhanced_lrc(headers, lines), encoding="utf-8")
    report = {
        **validation,
        "language": resolved_language,
        "alignment_profile": "easyaligner-global",
        "alignment_mode": "easyaligner-global-viterbi-direct",
        "input_timing": "calibrated-guardrails-only",
        "canonical_text_immutable": True,
        # Every mutation remains an acoustic CTC measurement. Provider times
        # only select the small window in which a contradictory line is rerun.
        "post_alignment_repairs": (
            sustain_releases["adjusted_words"] > 0
            or source_guardrails["repaired_lines"] > 0
            or backing_alignment["aligned_lines"] > 0
            or overlap_reconciliation["display_only_words"] > 0),
        "sustain_release_refinement": sustain_releases,
        "stem_contrast_release_refinement": stem_contrast_releases,
        "ctc_inference": ctc_inference,
        "phoneme_onset_verification": phoneme_verification,
        "model": model_id,
        "audio_source": audio_source,
        "canonical_mapping": mapping,
        "source_timing_guardrails": source_guardrails,
        "source_text_overlap_reconciliation": overlap_reconciliation,
        "syllable_alignment": syllable_summary,
        "backing_vocal_projection": {**backing_split,
                                     "alignment": backing_alignment},
        "backing_vocal_recovery": backing_recovery,
        "output_lrc": lrc_out.name,
        "stems": stem_outputs,
        "details": [{
            "timestamp": line.timestamp,
            "text": line.text,
            "technical_text": " ".join(
                str(word.get("technical_text", word["word"]))
                for word in line.words
                if str(word.get("technical_text", word["word"])).strip()),
            "status": line.status,
            "reason": line.reason,
            "voice_lane": line.voice_lane,
            "voice_label": line.voice_label,
            "words": line.words,
        } for line in lines],
        "technical_lyrics": {
            "representation": "ctc-normalized-graphemes",
            "display_text_authoritative": True,
            "punctuation_removed_for_alignment": True,
            "phonetic_alphabet": False,
            "parallel_parenthetical_vocals_use_independent_lane": True,
        },
    }
    if stems_manifest is not None:
        report["stems_manifest"] = stems_manifest
    report_out.write_text(
        json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    report["output_report"] = report_out.name
    notify(100, "EasyAligner Direct ist als unabhängiger Review-Stand fertig")
    return report
