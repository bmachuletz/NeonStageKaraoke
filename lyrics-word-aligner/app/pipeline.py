from __future__ import annotations
import json
import os
import re
import tempfile
import gc
import soundfile as sf
from collections import Counter
from copy import deepcopy
from dataclasses import replace
from pathlib import Path
from typing import Callable
from .aligner import QwenWordAligner
from .alignment_profiles import ALIGNMENT_PROFILES
from .audio import (ffmpeg_to_flac, ffmpeg_to_mono16k, load_audio,
                    load_native_audio)
from .asr_prompt import build_asr_prompt
from .basic_pitch_evidence import (analyze_and_refine_line_onsets,
                                   candidate_pitch_quality,
                                   separation_quality_report)
from .analysis_stems import build_analysis_candidates
from .candidate_selection import (AudioAlignmentCandidate, CandidateSelectionConfig,
                                  select_stage_stem_candidate,
                                  select_alignment_candidate,
                                  timed_anchor_alignment_quality)
from .canonical_lyrics import transfer_canonical_lines
from .ctc_aligner import realign_heuristic_lines, realign_overlapping_line_pairs
from .easy_aligner import realign_with_easyaligner
from .consensus import (eliminate_remaining_line_overlaps, extend_final_word_sustains,
                        reassign_overlong_connector_sustains, reconcile_acoustic_boundaries,
                        stabilize_acoustic_display_durations)
from .fragment_recovery import recover_deleted_fragments
from .lrc import parse_lrc, render_enhanced_lrc
from .lyrics_engine_v2 import (capture_candidate, fuse_alignment_candidates,
                               preserve_better_enhanced_input,
                               preserve_uncorroborated_internal_editor_boundaries)
from .mms_aligner import realign_remaining_lines
from .models import AlignmentConfig
from .onset_validation import apply_supported_onset_refinements, validate_line_onsets
from .phoneme_ctc_aligner import (align_global_phoneme_path,
                                  annotate_phoneme_boundaries,
                                  audit_final_word_boundaries)
from .micro_boundaries import (analyze_voicing, compare_timing_reference,
                               serialize_voicing_evidence,
                               sustain_voicing_intervals,
                               refine_sustain_releases_with_voicing)
from .line_transitions import analyze_line_transitions
from .vocal_boundaries import constrain_to_stage_vocals
from .collapsed_lines import repair_collapsed_lines, repair_compressed_word_runs
from .boundary_evidence import leakage_aware_evidence
from .transcript_quality import (detect_hallucinated_runs,
                                 measure_transcript_coverage, score_transcript,
                                 selection_rank)
from .separator import KARAOKE_MODEL, StemPaths, separate_stems
from .stem_roles import SeparationConfig, separator_family
from .sofa_aligner import realign_english_singing
from .stable_transcriber import realign_with_stable_words, transcribe_stable_variants
from .stem_leakage import recover_complementary_stem_lines
from .repair import (constrain_final_words_to_source_boundaries,
                     repair_collapsed_timings)
from .repetition_anchors import (apply_local_timestamp, apply_repetition_anchors,
                                 apply_transition_words, local_repetition_requests,
                                 refine_stretched_repetition_anchors,
                                 timestamp_repetition_pairs, transition_requests)
from .repeated_phrase_refinement import refine_repeated_phrase_words
from .syllables import enrich_lines_with_syllables
from .transcriber import (QwenTranscriber, language_code,
                          merge_transcript_chunks, reconcile_detected_language)
from .transcript_match import compare_transcripts, normalize_words
from .validator import validate
from .chorus_anchors import apply_trusted_chorus_anchors, recover_missing_initial_chorus
from .vocal_activity import (detect_vocal_activity, repair_anchor_context,
                             repair_anchor_line_tails, repair_with_vocal_activity)
from .editor_guidance import build_editor_guided_candidate
from .evidence_fusion import fuse_syllable_evidence
from .note_alignment import align_notes_to_syllables
from .completeness import (apply_completeness_gate, apply_targeted_gap_results,
                           assess_lyric_completeness,
                           constrain_lyrics_before_nonlexical_vocalizations)
from .gap_recovery import recover_vocal_gap_lines
from .full_transcription import (fallback_transcription_windows,
                                 plan_transcription_windows)
from .timestamp_calibration import calibrate_source_timestamps
from .stem_contrast import refine_final_releases_with_stem_contrast
from .medleyvox import (analyze_multiple_singing_voices,
                        apply_multiple_singing_voice_proposals,
                        planned_voice_lanes)

ProgressCallback = Callable[[int, str], None]

# Architectural contract: broad/lexical models establish hypotheses first;
# increasingly local acoustic stages follow; syllables are derived only after
# the final immutable word geometry.  The runtime trace is persisted in every
# report and fails fast if a future edit enters phases out of order.
PIPELINE_PHASE_ORDER = (
    "input-and-audio-preparation",
    "transcript-evidence",
    "primary-word-alignment",
    "independent-forced-refinement",
    "stage-stem-finalization",
    "timing-candidate-fusion",
    "macro-boundary-refinement",
    "missing-lyrics-recovery",
    "late-word-geometry",
    "editor-boundary-protection",
    "hard-boundary-precheck",
    "sentence-word-verification",
    "final-hard-constraints",
    "syllable-derivation",
    "validation-and-export",
)

def _noop(_percent: int, _message: str) -> None:
    pass


_MANUAL_RANGE_RE = re.compile(
    r"^\[neon-manual:(?P<start>\d+(?:\.\d+)?),(?P<end>\d+(?:\.\d+)?)\]$")


def _manual_editor_ranges(headers: list[str]) -> list[tuple[float, float]]:
    ranges = []
    for header in headers:
        match = _MANUAL_RANGE_RE.fullmatch(header.strip())
        if match is None:
            continue
        start, end = float(match.group("start")), float(match.group("end"))
        if end > start:
            ranges.append((start, end))
    return ranges


def _restore_manual_editor_lines(lines: list, enhanced_candidate,
                                 ranges: list[tuple[float, float]]) -> dict:
    summary = {"enabled": bool(ranges), "preserved_lines": 0,
               "method": "immutable-manual-editor-lines-v1", "line_indices": []}
    if enhanced_candidate is None or not ranges or len(enhanced_candidate.lines) != len(lines):
        return summary
    for index, (current, source) in enumerate(zip(lines, enhanced_candidate.lines)):
        if normalize_words(current.text) != normalize_words(source.text) or not source.words:
            continue
        # The editor line may deliberately start shortly before its first word
        # (for display preparation/lead-in). The marker describes the line,
        # not the first word, so compare it with the source timestamp.
        start = float(source.timestamp)
        end = float(source.words[-1]["end"])
        # The marker describes this exact source line.  Merely intersecting a
        # range could also freeze an adjacent line after an earlier alignment
        # accidentally created an overlap.
        matched_range = next((
            (range_start, range_end)
            for range_start, range_end in ranges
            if abs(start - range_start) <= 0.002 and end <= range_end + 0.002
        ), None)
        if matched_range is None:
            continue
        range_start, range_end = matched_range
        restored = deepcopy(source)
        restored.manual_adjusted = True
        restored.manual_editor_start = range_start
        restored.manual_editor_end = range_end
        for word in restored.words:
            if word.get("editor_word_manual_adjusted"):
                word["manual_adjusted"] = True
        lines[index] = restored
        summary["preserved_lines"] += 1
        summary["line_indices"].append(index + 1)
    return summary


def _restore_manual_editor_syllables(lines: list, enhanced_candidate) -> dict:
    """Restore edited syllable structure after automatic syllable derivation.

    This deliberately runs after ``enrich_lines_with_syllables``. Automatic
    alignment may improve untouched words, while explicitly edited syllables
    (including inserted or removed syllables) remain the human authority.
    """
    summary = {"enabled": enhanced_candidate is not None, "preserved_words": 0,
               "preserved_syllables": 0,
               "method": "immutable-manual-editor-syllables-v1"}
    if enhanced_candidate is None or len(enhanced_candidate.lines) != len(lines):
        return summary
    for current_line, source_line in zip(lines, enhanced_candidate.lines):
        if normalize_words(current_line.text) != normalize_words(source_line.text):
            continue
        if len(current_line.words) != len(source_line.words):
            continue
        for current_word, source_word in zip(current_line.words, source_line.words):
            edited = source_word.get("editor_syllables")
            if normalize_words(str(current_word.get("word", ""))) != normalize_words(
                    str(source_word.get("word", ""))):
                continue
            if (abs(float(current_word["start"]) - float(source_word["start"])) > 0.002
                    or abs(float(current_word.get("end", current_word["start"]))
                           - float(source_word.get("end", source_word["start"]))) > 0.002):
                continue
            if source_word.get("editor_word_manual_adjusted"):
                current_word["start"] = float(source_word.get(
                    "editor_word_start", source_word["start"]))
                current_word["end"] = float(source_word.get(
                    "editor_word_end", source_word.get("end", source_word["start"])))
                current_word["manual_adjusted"] = True
            if not isinstance(edited, list) or not edited:
                continue
            current_word["syllables"] = deepcopy(edited)
            current_word["syllable_confidence"] = min(
                (float(item.get("confidence", 1.0)) for item in edited), default=1.0)
            current_word["syllable_method"] = "manual-editor"
            summary["preserved_words"] += 1
            summary["preserved_syllables"] += len(edited)
    return summary


def _constrain_following_lines_after_manual_editor_holds(lines: list) -> dict:
    """Keep an automatic neighbour outside a manually authored line window.

    Editor lines may intentionally keep a short display tail after their final
    word. The ordinary overlap pass only sees sung word intervals, so an
    automatically realigned following line could otherwise move into that
    protected display interval and make the reconstructed editor hierarchy
    invalid. Shift only the automatic neighbour; two manual neighbours are
    already authoritative and must not be rewritten here.
    """
    adjustments = []
    for index in range(len(lines) - 1):
        previous, following = lines[index], lines[index + 1]
        boundary = previous.manual_editor_end
        if boundary is None or following.manual_adjusted:
            continue
        delta = float(boundary) - float(following.timestamp)
        if delta <= 0.000001:
            continue
        old_start = float(following.timestamp)
        following.timestamp = float(boundary)
        for word in following.words:
            word["start"] = float(word["start"]) + delta
            word["end"] = float(word.get("end", word["start"])) + delta
        adjustments.append({
            "preceding_line": index + 1,
            "following_line": index + 2,
            "old_start": round(old_start, 6),
            "new_start": round(float(boundary), 6),
            "shift_ms": round(delta * 1000.0, 3),
        })
    return {
        "method": "manual-editor-display-boundary-v1",
        "adjusted_lines": len(adjustments),
        "adjustments": adjustments,
    }


def _constrain_preceding_lines_before_manual_editor_starts(
        lines: list, *, minimum_word_duration: float = 0.04) -> dict:
    """Keep generated sung tails out of a protected editor display window.

    A manually authored line may intentionally be displayed shortly before its
    first sung word. The ordinary overlap resolver compares word to word and
    therefore cannot see a generated predecessor extending into this lead-in.
    Preserve the manual line and compress only its automatic predecessor tail.
    """
    adjustments = []
    unresolved = []
    for index in range(1, len(lines)):
        previous, current = lines[index - 1], lines[index]
        if not current.manual_adjusted or previous.manual_adjusted or not previous.words:
            continue
        boundary = current.manual_editor_start
        if boundary is None:
            boundary = float(current.timestamp)
        boundary = float(boundary)
        previous_end = float(previous.words[-1].get(
            "end", previous.words[-1]["start"]))
        if previous_end <= boundary + 0.000001:
            continue

        first = next((word_index for word_index, word in enumerate(previous.words)
                      if float(word.get("end", word["start"])) > boundary),
                     len(previous.words) - 1)
        while first > 0 and boundary - float(previous.words[first]["start"]) < (
                len(previous.words) - first) * minimum_word_duration:
            first -= 1
        old_start = float(previous.words[first]["start"])
        available = boundary - old_start
        required = (len(previous.words) - first) * minimum_word_duration
        if available < required or previous_end <= old_start:
            unresolved.append({
                "preceding_line": index,
                "manual_line": index + 1,
                "overlap_ms": round((previous_end - boundary) * 1000.0, 3),
                "reason": "insufficient-safe-tail-space",
            })
            continue

        tail = previous.words[first:]
        originals = [(float(word["start"]),
                      float(word.get("end", word["start"]))) for word in tail]
        scale = available / max(0.001, previous_end - old_start)
        cursor = old_start
        for offset, (word, (original_start, original_end)) in enumerate(
                zip(tail, originals)):
            remaining = len(tail) - offset - 1
            latest_end = boundary - remaining * minimum_word_duration
            mapped_start = old_start + (original_start - old_start) * scale
            mapped_end = old_start + (original_end - old_start) * scale
            word_start = min(max(cursor, mapped_start),
                             latest_end - minimum_word_duration)
            word_end = min(latest_end,
                           max(word_start + minimum_word_duration, mapped_end))
            word["start"] = round(word_start, 3)
            word["end"] = round(word_end, 3)
            word["timing_source"] = "manual-successor-display-boundary"
            for syllable in word.get("syllables", []):
                syllable_start = old_start + (
                    float(syllable["start"]) - old_start) * scale
                syllable_end = old_start + (
                    float(syllable["end"]) - old_start) * scale
                syllable["start"] = round(
                    max(float(word["start"]), syllable_start), 3)
                syllable["end"] = round(
                    min(float(word["end"]),
                        max(float(syllable["start"]), syllable_end)), 3)
            cursor = float(word["end"])
        previous.words[-1]["end"] = round(boundary, 3)
        if previous.words[-1].get("syllables"):
            previous.words[-1]["syllables"][-1]["end"] = round(boundary, 3)
        adjustments.append({
            "preceding_line": index,
            "manual_line": index + 1,
            "overlap_ms": round((previous_end - boundary) * 1000.0, 3),
            "tail_words": len(tail),
            "boundary": round(boundary, 3),
        })
    return {
        "method": "manual-editor-preceding-display-boundary-v1",
        "adjusted_lines": len(adjustments),
        "adjustments": adjustments,
        "unresolved": unresolved,
    }


def _restore_editor_tail_after_delayed_first_word(lines: list,
                                                   enhanced_candidate) -> dict:
    """Restore only explicitly edited neighbours after an onset repair.

    An enhanced LRC is also used as an automatically generated alignment
    basis. Treating every boundary in it as human-authored used to discard a
    substantially better acoustic path for all words after a repaired first
    onset. Only boundaries carrying the editor's explicit manual marker are
    authoritative here.
    """
    summary = {"enabled": enhanced_candidate is not None, "restored_lines": 0,
               "restored_word_boundaries": 0,
               "method": "delayed-first-word-editor-tail-guard-v2", "lines": []}
    if enhanced_candidate is None or len(enhanced_candidate.lines) != len(lines):
        return summary
    for line_index, (current, source) in enumerate(
            zip(lines, enhanced_candidate.lines)):
        if (not current.words or not source.words
                or current.words[0].get("timing_source")
                != "ipa-delayed-first-word-onset"
                or len(current.words) != len(source.words)
                or normalize_words(current.text) != normalize_words(source.text)):
            continue
        if [normalize_words(str(word.get("word", ""))) for word in current.words] != [
                normalize_words(str(word.get("word", ""))) for word in source.words]:
            continue
        repaired_start = float(current.words[0]["start"])
        source_first_end = float(source.words[0]["end"])
        restored = 0
        if (source.words[0].get("editor_word_manual_adjusted")
                and source_first_end - repaired_start >= 0.04):
            current.words[0]["end"] = source_first_end
            restored += 1
        for current_word, source_word in zip(current.words[1:], source.words[1:]):
            if not source_word.get("editor_word_manual_adjusted"):
                continue
            current_word["start"] = float(source_word["start"])
            current_word["end"] = float(source_word["end"])
            current_word["timing_source"] = "editor-tail-after-delayed-first-word"
            restored += 2
        if not restored:
            continue
        current.timestamp = repaired_start
        summary["restored_lines"] += 1
        summary["restored_word_boundaries"] += restored
        summary["lines"].append({
            "line": line_index + 1,
            "text": current.text,
            "repaired_first_start": round(repaired_start, 6),
            "restored_first_end": round(source_first_end, 6),
            "restored_boundaries": restored,
        })
    return summary


def _restore_editor_syllable_tail_after_delayed_first_word(
        lines: list, enhanced_candidate) -> dict:
    """Restore only the unchanged following syllables after the onset repair.

    Syllables are derived after the final word-boundary pass, so the matching
    word guard above cannot protect them yet.  The first word deliberately
    remains acoustic: shortening its false prefix changes its usable syllable
    window. Only following words explicitly adjusted by a person retain their
    editor syllables; generated references must not override acoustic output.
    """
    summary = {"enabled": enhanced_candidate is not None, "restored_words": 0,
               "restored_syllables": 0,
               "method": "delayed-first-word-editor-syllable-tail-guard-v2"}
    if enhanced_candidate is None or len(enhanced_candidate.lines) != len(lines):
        return summary
    for current, source in zip(lines, enhanced_candidate.lines):
        if (not current.words or not source.words
                or current.words[0].get("timing_source")
                != "ipa-delayed-first-word-onset"
                or len(current.words) != len(source.words)
                or normalize_words(current.text) != normalize_words(source.text)):
            continue
        for current_word, source_word in zip(current.words[1:], source.words[1:]):
            if not source_word.get("editor_word_manual_adjusted"):
                continue
            if normalize_words(str(current_word.get("word", ""))) != normalize_words(
                    str(source_word.get("word", ""))):
                continue
            if (abs(float(current_word["start"]) - float(source_word["start"])) > 0.002
                    or abs(float(current_word.get("end", current_word["start"]))
                           - float(source_word.get("end", source_word["start"]))) > 0.002):
                continue
            source_syllables = source_word.get("editor_reference_syllables")
            if not isinstance(source_syllables, list) or not source_syllables:
                source_syllables = source_word.get("editor_syllables")
            if not isinstance(source_syllables, list) or not source_syllables:
                continue
            current_word["syllables"] = deepcopy(source_syllables)
            current_word["syllable_confidence"] = min(
                (float(item.get("confidence", 1.0)) for item in source_syllables),
                default=1.0)
            current_word["syllable_method"] = "editor-tail-after-delayed-first-word"
            summary["restored_words"] += 1
            summary["restored_syllables"] += len(source_syllables)
    return summary


def _prepare_research_shadow_input(headers: list[str], lines: list) -> tuple[list[str], dict]:
    """Remove every editor-derived timing authority from a shadow run.

    The immutable pre-align file supplies text, line order and optional rough
    line cues only. Enhanced word timestamps, manual ranges, editor syllables,
    holds, and effects must never leak into the clean-room comparison. This is
    repeated inside the worker even though the server already selects the
    pre-align source, making the guarantee independent of caller behaviour.
    """
    clean_headers = [header for header in headers if not header.strip().lower().startswith(
        ("[neon-manual:", "[neon-editor-syllables:", "[neon-voice:"))]
    removed_words = 0
    cleared_manual_lines = 0
    for line in lines:
        removed_words += len(line.words)
        cleared_manual_lines += int(bool(
            line.manual_adjusted or line.manual_editor_start is not None
            or line.manual_editor_end is not None))
        line.words = []
        line.manual_adjusted = False
        line.manual_editor_start = None
        line.manual_editor_end = None
        line.voice_lane = 0
        line.voice_label = None
        line.status = "pending"
        line.reason = None
    return clean_headers, {
        "enabled": True,
        "method": "immutable-pre-align-clean-room-v1",
        "removed_editor_headers": len(headers) - len(clean_headers),
        "removed_word_timing_records": removed_words,
        "cleared_manual_lines": cleared_manual_lines,
        "retained_line_cues": len(lines),
        "manual_timing_authority": False,
    }


def _release_stage_gpu_memory() -> None:
    """Release allocator caches after a model stage has returned."""
    gc.collect()
    try:
        import torch
        if torch.cuda.is_available():
            torch.cuda.synchronize()
            torch.cuda.empty_cache()
            try:
                torch.cuda.ipc_collect()
            except (RuntimeError, AttributeError):
                pass
    except ImportError:
        pass


def _transcribe_for_verification(session: QwenTranscriber, audio, language: str,
                                 prompt: str | None) -> dict:
    """Run ordinary songs whole, retry empty results, and bound long-song ASR memory."""
    threshold = float(os.getenv("LRC_TRANSCRIPTION_CHUNK_THRESHOLD_SECONDS", "300"))
    chunk_seconds = float(os.getenv("LRC_TRANSCRIPTION_CHUNK_SECONDS", "20"))
    overlap_seconds = float(os.getenv("LRC_TRANSCRIPTION_CHUNK_OVERLAP_SECONDS", "2"))
    windows, chunked = plan_transcription_windows(
        len(audio), threshold_seconds=threshold, chunk_seconds=chunk_seconds,
        overlap_seconds=overlap_seconds)
    if not windows:
        raise ValueError("Die ASR-Prüfung erhielt eine leere Audiospur.")

    def transcribe_windows(source_windows, *, token_limit: int) -> list[dict]:
        recognized: list[dict] = []
        for sample_start, sample_end, _keep_start, _keep_end in source_windows:
            chunk = audio[sample_start:sample_end]
            try:
                result = session.transcribe(
                    chunk, language, prompt=prompt, max_new_tokens=token_limit)
                if str(result.get("text", "")).strip():
                    recognized.append(result)
            finally:
                del chunk
                _release_stage_gpu_memory()
        return recognized

    token_limit = int(os.getenv(
        "LRC_TRANSCRIPTION_MAX_NEW_TOKENS" if chunked else "LRC_ASR_MAX_NEW_TOKENS",
        "256" if chunked else "1024"))
    results = transcribe_windows(windows, token_limit=token_limit)
    retry_windows = fallback_transcription_windows(
        len(audio), initial_was_chunked=chunked, chunk_seconds=chunk_seconds,
        overlap_seconds=overlap_seconds)
    if not results and retry_windows:
        windows = retry_windows
        chunked = True
        token_limit = int(os.getenv("LRC_TRANSCRIPTION_MAX_NEW_TOKENS", "256"))
        results = transcribe_windows(windows, token_limit=token_limit)
    if not results:
        raise ValueError("Qwen-ASR konnte in der Vocalspur keinen Text erkennen.")
    languages = [str(result.get("language", "")).strip()
                 for result in results if str(result.get("language", "")).strip()]
    return {
        "model": results[0].get("model"),
        "language": Counter(languages).most_common(1)[0][0] if languages else None,
        "text": merge_transcript_chunks([str(result["text"]) for result in results]),
        "chunked": chunked,
        "chunks": len(windows),
        "nonempty_chunks": len(results),
        "chunk_threshold_seconds": threshold,
        "chunk_seconds": chunk_seconds if chunked else len(audio) / 16000,
        "chunk_overlap_seconds": overlap_seconds if chunked else 0,
        "max_new_tokens_per_chunk": token_limit,
    }


def run(audio_path: Path, lrc_path: Path, output_dir: Path, *, language: str, separator: bool, device: str,
        progress: ProgressCallback | None = None,
        provided_vocals: Path | None = None,
        provided_instrumental: Path | None = None,
        alignment_profile: str = "standard") -> dict:
    if alignment_profile not in ALIGNMENT_PROFILES:
        raise ValueError(
            "unbekanntes alignment_profile")
    if (provided_vocals is None) != (provided_instrumental is None):
        raise ValueError("Vorhandene Vocal- und Instrumentalspur müssen gemeinsam angegeben werden.")
    provided_stage_stems = provided_vocals is not None
    if provided_stage_stems and not separator:
        raise ValueError("Vorhandene Stems erfordern aktivierte Separation/Stem-Verarbeitung.")
    notify = progress or _noop
    runtime_device = {
        "requested": device,
        "cuda_available": False,
        "warning": None,
    }
    if device == "cuda":
        try:
            import torch
            runtime_device["cuda_available"] = torch.cuda.is_available()
        except (ImportError, RuntimeError):
            runtime_device["cuda_available"] = False
        if not runtime_device["cuda_available"]:
            runtime_device["warning"] = (
                "CUDA wurde angefordert, ist aber nicht verfügbar. "
                "Separation und Modelle laufen im langsamen CPU-Fallback."
            )
            notify(3, runtime_device["warning"])
    phase_trace: list[str] = []

    def enter_phase(name: str) -> None:
        expected = PIPELINE_PHASE_ORDER[len(phase_trace)]
        if name != expected:
            raise RuntimeError(
                f"Ungültige Alignment-Reihenfolge: erwartet '{expected}', erhielt '{name}'.")
        phase_trace.append(name)

    enter_phase("input-and-audio-preparation")
    output_dir.mkdir(parents=True, exist_ok=True)
    notify(5, "LRC-Datei wird eingelesen")
    cfg = AlignmentConfig(
        language=language,
        pre_roll=float(os.getenv("LRC_PRE_ROLL", "0.45")),
        post_roll=float(os.getenv("LRC_POST_ROLL", "0.35")),
        full_song_max_seconds=float(os.getenv("LRC_FULL_SONG_MAX_SECONDS", "300")),
        section_pause_gap=float(os.getenv("LRC_SECTION_PAUSE_GAP", "7")),
        section_max_duration=float(os.getenv("LRC_SECTION_MAX_DURATION", "45")),
    )
    headers, lines = parse_lrc(lrc_path)
    research_shadow_input = {"enabled": False, "method": "standard-editor-aware-input"}
    if alignment_profile == "research-shadow":
        headers, research_shadow_input = _prepare_research_shadow_input(headers, lines)
    manual_editor_ranges = (_manual_editor_ranges(headers)
                            if alignment_profile in {"standard", "editor-guided", "basic-pitch-ab"} else [])
    # Experimental recovery of lines placed in measured silence. It moves whole
    # lines and is therefore restricted to the clean-room profile until it has
    # been evaluated; variant 1.2 and every editor-aware run keep the previous
    # behaviour. The variable stays available for a diagnostic opt-out.
    shadow_silent_prefix_recovery = (
        alignment_profile == "research-shadow"
        and os.getenv("LRC_SHADOW_SILENT_PREFIX_RECOVERY", "true").strip().lower()
        in {"1", "true", "yes", "on"})
    # Same staging for the language cross-check: it changes syllabification and
    # model selection for the whole run, so variant 1.2 keeps the ASR verdict
    # until this has been evaluated in the clean room.
    shadow_language_reconciliation = (
        alignment_profile == "research-shadow"
        and os.getenv("LRC_SHADOW_LANGUAGE_RECONCILIATION", "true").strip().lower()
        in {"1", "true", "yes", "on"})
    language_reconciliation = {"enabled": shadow_language_reconciliation,
                               "applied": False, "reason": "not-evaluated"}
    # Coverage and hallucination are measured for every profile because they
    # are read-only diagnostics. Only the clean room lets them decide which
    # transcript wins, since that changes every downstream stage.
    shadow_transcript_selection = (
        alignment_profile == "research-shadow"
        and os.getenv("LRC_SHADOW_TRANSCRIPT_SELECTION", "true").strip().lower()
        in {"1", "true", "yes", "on"})
    # A song whose lyrics arrive without any timing gets no line anchors at
    # all, so the whole-song forced alignment has to place every word from text
    # alone. The transcript scaffolds can supply those anchors.
    shadow_untimed_scaffolds = (
        alignment_profile == "research-shadow"
        and os.getenv("LRC_SHADOW_UNTIMED_SCAFFOLDS", "true").strip().lower()
        in {"1", "true", "yes", "on"})
    untimed_input = all(not line.timed_input for line in lines)
    if not lines:
        raise ValueError("Die LRC-Datei enthält keine synchronisierten Textzeilen.")
    # Immutable canonical input for the anchor-free primary path. It must not
    # inherit geometry later produced by Qwen, Stable-TS or the input LRC.
    global_phoneme_input = deepcopy(lines)
    global_phoneme_mode = os.getenv(
        "LRC_GLOBAL_PHONEME_MODE", "primary").strip().lower()
    if global_phoneme_mode not in {"off", "shadow", "primary"}:
        raise ValueError("LRC_GLOBAL_PHONEME_MODE muss off, shadow oder primary sein.")
    # Architecture changes remain clean-room-only until their regression set
    # has been measured. Variant 1.2 and manual editor timing stay untouched.
    if alignment_profile != "research-shadow":
        global_phoneme_mode = "off"
    engine_v2_mode = os.getenv("LRC_ENGINE_V2_MODE", "select").strip().lower()
    if engine_v2_mode not in {"off", "shadow", "select"}:
        raise ValueError("LRC_ENGINE_V2_MODE muss off, shadow oder select sein.")
    engine_v2_candidates = []
    engine_v2_candidate_errors: list[dict] = []
    editor_guidance_summary = {
        "enabled": alignment_profile == "editor-guided",
        "reason": "awaiting-final-stage-stem" if alignment_profile == "editor-guided"
                  else "profile-disabled",
    }
    enhanced_input_candidate = (
        capture_candidate("enhanced-input", "Gespeicherter Editor-Stand",
                          lines, "human-editor")
        if all(line.words for line in lines) else None
    )

    stem = audio_path.stem
    qwen_prompt, qwen_prompt_summary = build_asr_prompt(
        (line.text for line in lines), stem, language,
        max_chars=int(os.getenv("LRC_ASR_PROMPT_MAX_CHARS", "1600")),
    )
    stable_prompt, stable_prompt_summary = build_asr_prompt(
        (line.text for line in lines), stem, language,
        max_chars=int(os.getenv("LRC_STABLE_TS_PROMPT_MAX_CHARS", "700")),
    )
    asr_prompt_summary = {
        "qwen": qwen_prompt_summary,
        "stable_ts": stable_prompt_summary,
    }
    stem_outputs: dict[str, str] = {}
    stage_stem_selection: dict = {"enabled": False, "reason": "separator-disabled"}
    stage_stem_quality: dict | None = None
    analysis_stem_selection: dict = {"enabled": False, "reason": "separator-disabled"}
    line_transition_analysis: dict = {
        "method": "joint-line-transition-v1",
        "version": 1,
        "enabled": False,
        "reason": "pipeline-terminated-before-hard-boundary-precheck",
    }
    stage_separator_model: str | None = None
    separation_config = SeparationConfig.from_environment()
    with tempfile.TemporaryDirectory(prefix="lyrics-align-") as temp:
        temp_dir = Path(temp)
        analysis_accompaniment_audio = None
        stage_instrumental_audio = None
        stage_vocal_audio = None
        if separator:
            if provided_stage_stems:
                notify(12, "Gespeicherte Bibliotheksspuren werden als feste Audioreferenz geladen")
                stage_stems = StemPaths(Path(provided_vocals), Path(provided_instrumental))
                stage_separator_model = "provided-library-stems"
                stage_vocal_audio = load_audio(ffmpeg_to_mono16k(
                    stage_stems.vocals, temp_dir / "provided-stage-vocals-16k.wav"))
            else:
                notify(12, "Stage-Vocals und Instrumental werden getrennt")
                stage_stems = separate_stems(
                    audio_path, temp_dir / "stage-separated",
                    model_name=separation_config.stage_model)
                stage_separator_model = separation_config.stage_model
                stage_vocal_audio = load_audio(ffmpeg_to_mono16k(
                    stage_stems.vocals, temp_dir / "stage-baseline-vocals-16k.wav"))
                stage_instrumental_audio = load_audio(ffmpeg_to_mono16k(
                    stage_stems.instrumental,
                    temp_dir / "stage-baseline-instrumental-16k.wav"))
            vocals_out = output_dir / f"{stem}.vocals.flac"
            instrumental_out = output_dir / f"{stem}.instrumental.flac"
            stem_outputs = {
                "vocals": vocals_out.name,
                "instrumental": instrumental_out.name,
            }
            stage_stem_selection = {
                "enabled": True,
                "selected_candidate": "provided-stage-stems" if provided_stage_stems
                                      else "configured-stage-separator",
                "reason": ("provided-library-stems-locked" if provided_stage_stems
                           else "awaiting-separator-candidate-evaluation"),
                "purpose": "stage",
                "separator": ("provided" if provided_stage_stems
                              else separator_family(stage_separator_model)),
                "separator_model": stage_separator_model,
                "candidate_score": None,
            }
            notify(48, ("Gespeicherte Bibliotheksspuren geladen"
                        if provided_stage_stems else "Vocal-Separation abgeschlossen"))
        else:
            stage_stems = None
            notify(25, "Vocal-Separation wurde übersprungen")

        notify(49, "All-Vocals-Kandidaten für die Analyse werden erzeugt")
        effective_separation_config = (separation_config if separator else
                                       replace(separation_config, analysis_enabled=False))
        analysis_bundle = build_analysis_candidates(
            audio_path, temp_dir / "analysis", stage_stems, effective_separation_config,
            minimum_rms_ratio=float(os.getenv("LRC_MIN_VOCAL_MIX_RMS_RATIO", "0.05")),
        )
        candidates = analysis_bundle.candidates
        if separator and not provided_stage_stems:
            candidates.insert(0, AudioAlignmentCandidate(
                "configured-stage-separator",
                f"Stage-Baseline · {separator_family(separation_config.stage_model)}",
                stage_vocal_audio,
                "stage-vocal-separator",
                legacy=True,
                metadata={
                    "purpose": "stage",
                    "model": separation_config.stage_model,
                    "separator": separator_family(separation_config.stage_model),
                },
            ))
        elif provided_stage_stems:
            candidates.insert(0, AudioAlignmentCandidate(
                "provided-stage-stems",
                "Bibliotheks-Stems",
                stage_vocal_audio,
                "provided-stage-vocals",
                legacy=True,
                metadata={
                    "purpose": "stage",
                    "model": "provided-library-stems",
                    "separator": "provided",
                },
            ))
        mix_audio = analysis_bundle.mix_audio
        audio = candidates[0].audio
        alignment_audio = {
            "source": candidates[0].type,
            "fallback_used": analysis_bundle.legacy_fallback,
            "method": "role-separated-analysis-audio-v1",
        }
        candidate_config = CandidateSelectionConfig.from_environment()
        selected_candidate_transcript = None
        selected_candidate = candidates[0]
        expected_text = "\n".join(line.text for line in lines)
        if candidate_config.enabled:
            notify(54, f"{len(candidates)} Audio-Kandidaten werden anhand der Lyrics bewertet")

            with QwenTranscriber(device) as candidate_transcriber:
                def evaluate_candidate(candidate: AudioAlignmentCandidate) -> dict:
                    candidate_asr = _transcribe_for_verification(
                        candidate_transcriber, candidate.audio, language, qwen_prompt)
                    result = {**candidate_asr,
                              "comparison": compare_transcripts(
                                  expected_text, candidate_asr["text"], min_repetitions=2)}
                    result["alignment_quality"] = timed_anchor_alignment_quality(
                        lines, detect_vocal_activity(candidate.audio))
                    if os.getenv(
                            "LRC_BASIC_PITCH_USE_FOR_SEPARATOR_SCORE", "true"
                            ).strip().lower() in {"1", "true", "yes", "on"}:
                        pitch = candidate_pitch_quality(candidate.audio)
                        result["pitch_quality_diagnostics"] = pitch
                        if pitch.get("eligible"):
                            result["pitch_quality"] = pitch["quality"]
                    accompaniment = (
                        load_audio(ffmpeg_to_mono16k(
                            stage_stems.instrumental,
                            temp_dir / "provided-stage-instrumental-16k.wav"))
                        if candidate.id == "provided-stage-stems"
                        else stage_instrumental_audio
                        if candidate.id == "configured-stage-separator"
                        else analysis_bundle.accompaniment_audio.get(candidate.id)
                    )
                    if accompaniment is not None:
                        result["separation_quality"] = separation_quality_report(
                            candidate.audio, accompaniment, lines,
                            pitch_diagnostics=result.get("pitch_quality_diagnostics"))
                    return result

                selected_candidate, selected_candidate_transcript, candidate_selection = (
                    select_alignment_candidate(candidates, evaluate_candidate, candidate_config)
                )
            _release_stage_gpu_memory()
            audio = selected_candidate.audio
            candidate_selection["generation_errors"] = analysis_bundle.errors
            alignment_audio = {**alignment_audio, "selected_candidate": selected_candidate.id,
                               "selected_candidate_type": selected_candidate.type}
            if candidate_config.keep_candidate_files:
                for candidate in candidates:
                    sf.write(output_dir / f"{stem}.alignment-candidate-{candidate.id}.wav",
                             candidate.audio, 16000, subtype="PCM_16")
        else:
            candidate_selection = {"enabled": False, "selected_candidate": selected_candidate.id,
                                   "reason": "feature-disabled", "candidates": [
                                       {"id": candidate.id, "type": candidate.type,
                                        "status": "not-evaluated",
                                        "metadata": candidate.metadata}
                                       for candidate in candidates
                                   ]}
        selected_score = candidate_selection.get("selected_candidate_score")
        candidates_by_id = {candidate.id: candidate for candidate in candidates}
        analysis_stem_selection = {
            "enabled": effective_separation_config.analysis_enabled,
            "selected_candidate": selected_candidate.id,
            "selected_candidate_type": selected_candidate.type,
            "candidate_score": selected_score,
            "reason": candidate_selection.get("reason"),
            "purpose": "analysis",
            "model": selected_candidate.metadata.get("model"),
            "separator": selected_candidate.metadata.get("separator"),
            "legacy_fallback": analysis_bundle.legacy_fallback,
            "candidate_count": len(candidates),
            "generation_errors": analysis_bundle.errors,
        }
        if separation_config.a_b_models:
            analysis_stem_selection["a_b_models"] = list(separation_config.a_b_models)
            analysis_stem_selection["a_b_summary"] = {
                "enabled": True,
                "models": list(separation_config.a_b_models),
                "ranking_method": "asr-plus-separation-quality-v1",
                "successful_models": [
                    item.get("metadata", {}).get("model")
                    for item in candidate_selection.get("candidates", [])
                    if item.get("status") == "success"
                    and item.get("metadata", {}).get("model")
                    in separation_config.a_b_models
                ],
                "failed_models": [item.get("metadata", {}).get("model")
                                  for item in candidate_selection.get("candidates", [])
                                  if item.get("status") == "failed"
                                  and item.get("metadata", {}).get("model")
                                  in separation_config.a_b_models],
                "evaluation": [
                    {
                        "model": item.get("metadata", {}).get("model"),
                        "id": item.get("id"),
                        "score": item.get("score"),
                        "vocal_recall": (
                            item.get("separation_quality", {}).get("vocal_recall")),
                        "pause_leak": (
                            item.get("separation_quality", {})
                            .get("instrumental_leak_in_vocal_pauses")),
                        "singer_ambiguity": (
                            item.get("pitch_quality_diagnostics", {})
                            .get("singer_context", {}).get("singer_ambiguity")),
                    }
                    for item in candidate_selection.get("candidates", [])
                    if item.get("metadata", {}).get("model") in separation_config.a_b_models
                ],
            }
        analysis_accompaniment_audio = (
            load_audio(ffmpeg_to_mono16k(
                stage_stems.instrumental,
                temp_dir / "provided-stage-instrumental-16k.wav"))
            if selected_candidate.id == "provided-stage-stems"
            else stage_instrumental_audio
            if selected_candidate.id == "configured-stage-separator"
            else analysis_bundle.accompaniment_audio.get(selected_candidate.id)
        )
        if analysis_accompaniment_audio is None:
            analysis_stem_selection["accompaniment_reference"] = "unavailable-for-mix-candidate"
        else:
            analysis_stem_selection["accompaniment_reference"] = "matching-analysis-pair"

        if (separator and not provided_stage_stems
                and effective_separation_config.analysis_enabled):
            stage_candidate_ids = {"configured-stage-separator", *analysis_bundle.pairs.keys()}
            successful_candidates = {
                item["id"] for item in candidate_selection.get("candidates", [])
                if item.get("status") == "success"
            }
            successful_stage_candidates = stage_candidate_ids & successful_candidates
            if len(successful_stage_candidates) >= 2:
                notify(53, "Stage-Stem wird aus den erfolgreichen Separator-Kandidaten gewählt")
                stage_winner, stage_stem_selection = select_stage_stem_candidate(
                    stage_candidate_ids,
                    candidate_selection,
                    baseline_id="configured-stage-separator",
                    minimum_improvement=float(os.getenv(
                        "LRC_STAGE_STEM_MIN_IMPROVEMENT", "0.05")))
                stage_stem_selection.update({
                    "purpose": "stage",
                    "baseline_model": separation_config.stage_model,
                })
                stage_stem_selection["candidate_score"] = next(
                    float(item["score"])
                    for item in candidate_selection.get("candidates", [])
                    if item.get("id") == stage_winner and item.get("status") == "success"
                )
                if stage_winner != "configured-stage-separator":
                    stage_stems = analysis_bundle.pairs[stage_winner]
                    winner_candidate = candidates_by_id[stage_winner]
                    stage_stem_selection["model"] = winner_candidate.metadata.get("model")
                    stage_stem_selection["separator"] = winner_candidate.metadata.get("separator")
                    stage_stem_selection["candidate_id"] = stage_winner
                stage_quality_sources = {
                    "configured-stage-separator": (
                        stage_vocal_audio, stage_instrumental_audio),
                    **{candidate_id: (
                        candidates_by_id[candidate_id].audio,
                        analysis_bundle.accompaniment_audio[candidate_id])
                       for candidate_id in analysis_bundle.pairs},
                }
                stage_quality = {
                    candidate_id: separation_quality_report(
                        vocals, instrumental, lines,
                        pitch_diagnostics=next((
                            item.get("pitch_quality_diagnostics")
                            for item in candidate_selection.get("candidates", [])
                            if item.get("id") == candidate_id), None))
                    for candidate_id, (vocals, instrumental)
                    in stage_quality_sources.items()
                    if candidate_id in successful_stage_candidates
                }
                stage_stem_selection["quality"] = stage_quality
                stage_stem_quality = stage_quality
                if (os.getenv("LRC_STEM_HYBRID_DIAGNOSTICS", "true").strip().lower()
                        in {"1", "true", "yes", "on"}):
                    asr_ranked = sorted(
                        (item for item in candidate_selection.get("candidates", [])
                         if item.get("id") in successful_stage_candidates
                         and item.get("status") == "success"),
                        key=lambda item: float(item["score"]), reverse=True)
                    ranked = sorted(
                        stage_quality.items(),
                        key=lambda item: (
                            float(item[1].get("vocal_recall")
                                  or item[1].get("lyric_window_coverage") or 0),
                            -float(item[1].get("instrumental_leak_in_vocal_pauses")
                                   or 0),
                            float(item[1].get("basic_pitch", {}).get("quality") or 0),
                            -float(item[1].get("leakage_suspicion") or 0)),
                        reverse=True)
                    recommended = ranked[0][0]
                    stage_stem_selection["hybrid_diagnostics"] = {
                        "enabled": True,
                        "method": "candidate-consistency-v1-diagnostic-only",
                        "recommended_local_source": recommended,
                        "ranked_candidates": [name for name, _ in ranked],
                        "asr_recommendation": {
                            "candidate": asr_ranked[0]["id"],
                            "score": asr_ranked[0]["score"],
                        },
                        "basic_pitch_recommendation": recommended,
                        "different_winners": asr_ranked[0]["id"] != recommended,
                        "singer_context": {
                            candidate_id: (
                                item.get("singer_context") or {})
                            for candidate_id, item in stage_quality.items()
                            if item.get("basic_pitch", {}).get("singer_context")
                        },
                        "intervals": [],
                    }
            else:
                stage_stem_selection["selection_reason_detail"] = (
                    "insufficient-successful-separator-candidates")

        if not separator:
            stage_vocal_audio = audio
            stage_instrumental_audio = None
        if stage_vocal_audio is None:
            raise RuntimeError("Stage-Vocals konnten nicht geladen werden")
        vocal_activity = detect_vocal_activity(audio)
        stage_vocal_activity = detect_vocal_activity(stage_vocal_audio)
        enable_anchor_context = os.getenv("LRC_EXPERIMENTAL_ANCHOR_CONTEXT", "false").strip().lower() in {
            "1", "true", "yes", "on"
        }
        enter_phase("transcript-evidence")
        transcript_verification: dict = {"enabled": False}
        stable_ts_summary: dict = {"enabled": False, "reason": "Qwen-ASR ausreichend"}
        stable_ts_short_summary: dict = {
            "enabled": False, "reason": "Lyrics Engine v2 deaktiviert"
        }
        auto_language = language.strip().lower() == "auto"
        if (selected_candidate_transcript is not None or auto_language or
                os.getenv("LRC_ASR_VERIFY", "true").strip().lower() in {"1", "true", "yes", "on"}):
            notify(55, "Vocalspur wird unabhängig durch Qwen ASR transkribiert")
            if selected_candidate_transcript is not None:
                asr = selected_candidate_transcript
            else:
                with QwenTranscriber(device) as verification_transcriber:
                    asr = _transcribe_for_verification(
                        verification_transcriber, audio, language, qwen_prompt)
                _release_stage_gpu_memory()
            if auto_language:
                language = language_code(asr.get("language"), expected_text)
                # ASR on shouted, distorted singing reports a confident but
                # wrong language often enough to damage syllabification and
                # model choice. The canonical lyrics are exact evidence and may
                # overrule it when they are unambiguous.
                if shadow_language_reconciliation:
                    language, language_evidence = reconcile_detected_language(
                        language, expected_text)
                    language_reconciliation = {"enabled": True, **language_evidence}
                cfg = replace(cfg, language=language)
                notify(56, f"Gesangssprache automatisch erkannt: {language}")
            transcript_verification = {
                "enabled": True,
                **asr,
                "comparison": compare_transcripts(expected_text, asr["text"]),
            }
            comparison = transcript_verification["comparison"]
            recognized_ratio = comparison["recognized_words"] / max(1, comparison["expected_words"])
            stable_min_ratio = float(os.getenv("LRC_STABLE_TS_MIN_RECOGNIZED_RATIO", "0.85"))
            stable_min_similarity = float(os.getenv("LRC_STABLE_TS_MIN_SIMILARITY", "0.80"))
            if (os.getenv("LRC_STABLE_TS_VERIFY", "true").strip().lower()
                    in {"1", "true", "yes", "on"}
                    and (engine_v2_mode != "off"
                         or untimed_input
                         or recognized_ratio < stable_min_ratio
                         or comparison["similarity"] < stable_min_similarity)):
                notify(56, ("Plain Lyrics benötigen einen zweiten Zeitgeber; Stable-ts prüft die Vocalspur"
                            if untimed_input else
                            "Qwen-ASR unvollständig; Stable-ts prüft die Vocalspur"))
                try:
                    long_seconds = float(os.getenv("LRC_STABLE_TS_CHUNK_SECONDS", "30"))
                    long_overlap = float(os.getenv(
                        "LRC_STABLE_TS_CHUNK_OVERLAP_SECONDS", "3"))
                    variants = [("long", long_seconds, long_overlap)]
                    if engine_v2_mode != "off":
                        variants.append((
                            "short",
                            float(os.getenv("LRC_ENGINE_V2_SHORT_WINDOW_SECONDS", "12")),
                            float(os.getenv(
                                "LRC_ENGINE_V2_SHORT_WINDOW_OVERLAP_SECONDS", "3"))))
                        notify(57, "Lange und kurze Whisper-Fenster werden mit einem Modell verglichen")
                    stable_variants = transcribe_stable_variants(
                        audio, language, device, variants, initial_prompt=stable_prompt)
                    stable = stable_variants["long"]
                    # Alignment indices must refer to the timestamped word list.
                    # Stable-ts' regrouped segment text can contain a different
                    # token count and would shift every later word timestamp.
                    stable_word_text = " ".join(
                        str(word.get("word", "")) for word in stable.get("words", [])
                    )
                    stable_comparison = compare_transcripts(expected_text, stable_word_text)

                    def judge(words, candidate_comparison):
                        """Covered, non-invented matches instead of raw matches."""
                        coverage = measure_transcript_coverage(
                            words or [], vocal_activity)
                        hallucination = detect_hallucinated_runs(
                            words or [], expected_text)
                        return coverage, hallucination, score_transcript(
                            candidate_comparison, coverage, hallucination)

                    stable_coverage, stable_hallucination, stable_score = judge(
                        stable.get("words"), stable_comparison)
                    stable_ts_summary = {"enabled": True, **stable,
                                         "comparison": stable_comparison,
                                         "coverage": stable_coverage,
                                         "hallucination": stable_hallucination,
                                         "selection_score": stable_score}
                    # Qwen has no word timeline here, so it cannot be measured
                    # for coverage; it keeps its raw match count.
                    qwen_rank = (float(comparison["matching_words"]),
                                 comparison["similarity"])
                    transcript_candidates = [("stable-ts", stable_score, stable,
                                   stable_comparison)]
                    if "short" in stable_variants:
                        short_stable = stable_variants["short"]
                        short_text = " ".join(
                            str(word.get("word", ""))
                            for word in short_stable.get("words", []))
                        short_comparison = compare_transcripts(expected_text, short_text)
                        short_coverage, short_hallucination, short_score = judge(
                            short_stable.get("words"), short_comparison)
                        stable_ts_short_summary = {
                            "enabled": True, **short_stable,
                            "comparison": short_comparison,
                            "coverage": short_coverage,
                            "hallucination": short_hallucination,
                            "selection_score": short_score,
                        }
                        transcript_candidates.append(("stable-ts-short", short_score,
                                           short_stable, short_comparison))
                    if not shadow_transcript_selection:
                        # Variant 1.2 keeps the historical rule: the long
                        # window competes on raw matches only. Coverage and
                        # hallucination are still measured for the report.
                        transcript_candidates = transcript_candidates[:1]
                        qwen_rank = (float(comparison["matching_words"]),
                                     comparison["similarity"])
                        stable_score = {**stable_score,
                                        "effective_matches": float(
                                            stable_comparison["matching_words"]),
                                        "similarity": stable_comparison["similarity"]}
                        transcript_candidates[0] = ("stable-ts", stable_score, stable,
                                         stable_comparison)
                    best_name, best_score, best, best_comparison = max(
                        transcript_candidates, key=lambda item: selection_rank(item[1]))
                    if selection_rank(best_score) > qwen_rank:
                        transcript_verification = {
                            "enabled": True, "model": best["model"],
                            "language": best["language"], "text": best["text"],
                            "comparison": best_comparison,
                            "selected_from": best_name,
                            "selection_score": best_score,
                        }
                except (RuntimeError, ValueError) as stable_error:
                    stable_ts_summary = {"enabled": True, "error": str(stable_error)}
        # Stable-ts and Qwen ASR use different CUDA models.  Cleanup inside a
        # callee can happen while its return frame still owns tensors, so make
        # the stage boundary explicit before loading the forced aligner.
        _release_stage_gpu_memory()
        timestamp_calibration = {"applied": False, "reason": "keine Stable-ts-Wortzeiten",
                                 "method": "stable-ts-global-offset-v1"}
        if not untimed_input and stable_ts_summary.get("words"):
            timestamp_calibration = calibrate_source_timestamps(
                lines, stable_ts_summary["words"], stable_ts_summary["comparison"]
            )
            if timestamp_calibration.get("applied"):
                notify(57, f"Audiofassung kalibriert: {timestamp_calibration['offset']:+.3f} s")
        # Bounded line windows are the primary timing path. Whole-song ASR is
        # retained above as an independent transcript/order diagnostic, but no
        # longer forces an entire song plus all repetitions through one context.
        alignment_mode = os.getenv("LRC_ALIGNMENT_MODE", "line-windows").strip().lower()
        use_full_song = untimed_input or alignment_mode != "line-windows"
        alignment_selected = "full-song" if use_full_song else "line-windows"
        notify(58, f"{len(lines)} Lyrics-Zeilen werden wortgenau ausgerichtet" +
               (" (globale Vollspur)" if use_full_song else " (LRC-Zeilenfenster)"))
        enter_phase("primary-word-alignment")
        aligner = QwenWordAligner(device=device)
        alignment_attempts: list[dict] = []
        timed_repetition_pairs: list[dict] = []
        repetition_anchor_summary = {"blocks": 0, "words": 0, "rejected_blocks": 0,
                                     "method": "asr-structural-repetition-v1"}
        activity_repaired_timings = 0
        anchor_context_repairs = 0
        anchor_tail_repairs = 0
        fragment_recovery = {"enabled": False, "reason": "keine Stable-ts-Wortzeiten",
                             "recovered_fragments": 0, "recovered_words": 0,
                             "diagnostics": []}
        try:
            if transcript_verification.get("enabled") and transcript_verification.get("text"):
                notify(57, "ASR-Wiederholungsblöcke erhalten akustische Zeitanker")
                requests = local_repetition_requests(
                    lines, transcript_verification["comparison"], len(audio) / 16000
                )
                stable_repetition_pairs = timestamp_repetition_pairs(
                    transcript_verification["comparison"], stable_ts_summary.get("words", []))
                stable_pair_ids = {id(pair) for pair in stable_repetition_pairs}
                for request in requests:
                    if id(request["pair"]) in stable_pair_ids:
                        timed_repetition_pairs.append(request["pair"])
                        continue
                    chunk = audio[int(request["start"] * 16000):int(request["end"] * 16000)]
                    recognized_words = aligner.align_text(chunk, request["transcript"], language)
                    if apply_local_timestamp(request["pair"], recognized_words, request["start"]):
                        timed_repetition_pairs.append(request["pair"])
            if use_full_song:
                try:
                    aligner.align_full_song(audio, lines, cfg)
                    repetition_anchor_summary = apply_repetition_anchors(lines, timed_repetition_pairs)
                    anchor_tail_repairs = repair_anchor_line_tails(lines, vocal_activity)
                    anchor_context_repairs = (repair_anchor_context(lines, cfg, vocal_activity)
                                              if enable_anchor_context else 0)
                    activity_repaired_timings = repair_with_vocal_activity(lines, cfg, vocal_activity)
                    repaired_timings = repair_collapsed_timings(lines, cfg)
                    full_quality = validate(lines, cfg, repaired_lines=repaired_timings)
                    alignment_attempts.append({
                        "mode": "full-song",
                        "vocal_activity_repaired_lines": activity_repaired_timings,
                        "anchor_context_repaired_runs": anchor_context_repairs,
                        "anchor_tail_repaired_lines": anchor_tail_repairs,
                        **full_quality["quality"],
                    })
                    if not untimed_input and not full_quality["quality"]["publishable"]:
                        notify(68, "Vollspur unter Qualitätsschwelle; lokale Gesangsabsätze werden geprüft")
                        candidates = [(full_quality["quality"]["score"], "full-song", lines,
                                       repaired_timings, repetition_anchor_summary,
                                       activity_repaired_timings, anchor_context_repairs,
                                       anchor_tail_repairs)]
                        durations = sorted({
                            float(value.strip()) for value in
                            os.getenv("LRC_SECTION_CANDIDATE_SECONDS", "18,30,45").split(",")
                            if value.strip()
                        })
                        for duration in durations:
                            section_lines = deepcopy(parse_lrc(lrc_path)[1])
                            section_cfg = replace(cfg, section_max_duration=duration)
                            try:
                                aligner.align_sections(audio, section_lines, section_cfg)
                                section_anchor_summary = apply_repetition_anchors(
                                    section_lines, timed_repetition_pairs
                                )
                                section_tail_repairs = repair_anchor_line_tails(
                                    section_lines, vocal_activity
                                )
                                section_context_repairs = (repair_anchor_context(
                                    section_lines, section_cfg, vocal_activity
                                ) if enable_anchor_context else 0)
                                section_activity_repairs = repair_with_vocal_activity(
                                    section_lines, section_cfg, vocal_activity
                                )
                                section_repairs = repair_collapsed_timings(section_lines, section_cfg)
                                section_quality = validate(
                                    section_lines, section_cfg, repaired_lines=section_repairs
                                )
                                section_mode = f"sections-{duration:g}s"
                                alignment_attempts.append({
                                    "mode": section_mode,
                                    "vocal_activity_repaired_lines": section_activity_repairs,
                                    "anchor_context_repaired_runs": section_context_repairs,
                                    "anchor_tail_repaired_lines": section_tail_repairs,
                                    **section_quality["quality"],
                                })
                                candidates.append((section_quality["quality"]["score"], section_mode,
                                                   section_lines, section_repairs,
                                                   section_anchor_summary, section_activity_repairs,
                                                   section_context_repairs, section_tail_repairs))
                            except (RuntimeError, ValueError) as section_error:
                                alignment_attempts.append({
                                    "mode": f"sections-{duration:g}s",
                                    "error": str(section_error),
                                })
                        notify(74, "Gesangsabsätze geprüft; einzelne LRC-Zeilen werden gegengeprüft")
                        window_lines = deepcopy(parse_lrc(lrc_path)[1])
                        aligner.align(audio, window_lines, cfg)
                        window_anchor_summary = apply_repetition_anchors(window_lines, timed_repetition_pairs)
                        window_tail_repairs = repair_anchor_line_tails(window_lines, vocal_activity)
                        window_context_repairs = (repair_anchor_context(window_lines, cfg, vocal_activity)
                                                  if enable_anchor_context else 0)
                        window_activity_repairs = repair_with_vocal_activity(
                            window_lines, cfg, vocal_activity
                        )
                        window_repairs = repair_collapsed_timings(window_lines, cfg)
                        window_quality = validate(window_lines, cfg, repaired_lines=window_repairs)
                        alignment_attempts.append({
                            "mode": "line-windows",
                            "vocal_activity_repaired_lines": window_activity_repairs,
                            "anchor_context_repaired_runs": window_context_repairs,
                            "anchor_tail_repaired_lines": window_tail_repairs,
                            **window_quality["quality"],
                        })
                        candidates.append((window_quality["quality"]["score"], "line-windows",
                                           window_lines, window_repairs, window_anchor_summary,
                                           window_activity_repairs, window_context_repairs,
                                           window_tail_repairs))
                        (_, alignment_selected, lines, repaired_timings, repetition_anchor_summary,
                         activity_repaired_timings, anchor_context_repairs, anchor_tail_repairs) = max(
                            candidates, key=lambda candidate: candidate[0]
                        )
                        use_full_song = alignment_selected == "full-song"
                except (RuntimeError, ValueError) as error:
                    if untimed_input and global_phoneme_mode != "primary":
                        raise
                    if untimed_input:
                        # The canonical full-track phoneme path below does not
                        # need this failed ASR geometry. Continue with a clean
                        # text-only copy instead of making Qwen a prerequisite.
                        lines = deepcopy(global_phoneme_input)
                        repaired_timings = 0
                        alignment_selected = "global-phoneme-pending"
                        alignment_attempts.append({
                            "mode": "full-song",
                            "error": str(error)[:500],
                            "fallback": "global-phoneme-primary",
                        })
                        use_full_song = False
                        continue_after_qwen_failure = True
                    else:
                        continue_after_qwen_failure = False
                    # Timed LRC remains a safe fallback for unusually long songs
                    # or GPUs that cannot hold a complete track in one pass.
                    if not continue_after_qwen_failure:
                        notify(62, f"Vollspur nicht möglich ({error}); verwende LRC-Zeilenfenster")
                        aligner.align(audio, lines, cfg)
                        repetition_anchor_summary = apply_repetition_anchors(lines, timed_repetition_pairs)
                        anchor_tail_repairs = repair_anchor_line_tails(lines, vocal_activity)
                        anchor_context_repairs = (repair_anchor_context(lines, cfg, vocal_activity)
                                                  if enable_anchor_context else 0)
                        activity_repaired_timings = repair_with_vocal_activity(lines, cfg, vocal_activity)
                        use_full_song = False
                        alignment_selected = "line-windows"
                        repaired_timings = repair_collapsed_timings(lines, cfg)
            else:
                aligner.align(audio, lines, cfg)
                repetition_anchor_summary = apply_repetition_anchors(lines, timed_repetition_pairs)
                anchor_tail_repairs = repair_anchor_line_tails(lines, vocal_activity)
                anchor_context_repairs = (repair_anchor_context(lines, cfg, vocal_activity)
                                          if enable_anchor_context else 0)
                activity_repaired_timings = repair_with_vocal_activity(lines, cfg, vocal_activity)
                repaired_timings = repair_collapsed_timings(lines, cfg)
                alignment_selected = "line-windows"

            if timed_repetition_pairs and not untimed_input:
                notify(81, "Verankerte Übergangsblöcke werden als eigener Kandidat ausgerichtet")
                transition_lines = deepcopy(lines)
                transition_changed = 0
                try:
                    for request in transition_requests(
                            transition_lines, timed_repetition_pairs, len(audio) / 16000):
                        chunk = audio[int(request["start"] * 16000):int(request["end"] * 16000)]
                        aligned = aligner.align_text(chunk, request["transcript"], language)
                        for word in aligned:
                            word["start"] = round(float(word["start"]) + request["start"], 3)
                            word["end"] = round(float(word["end"]) + request["start"], 3)
                        transition_changed += apply_transition_words(
                            transition_lines, request, aligned
                        )
                    transition_activity_repairs = repair_with_vocal_activity(
                        transition_lines, cfg, vocal_activity
                    )
                    transition_repairs = repair_collapsed_timings(transition_lines, cfg)
                    transition_quality = validate(
                        transition_lines, cfg, repaired_lines=transition_repairs
                    )
                    transition_mode = f"{alignment_selected}+transitions"
                    alignment_attempts.append({
                        "mode": transition_mode,
                        "transition_replaced_words": transition_changed,
                        "vocal_activity_repaired_lines": transition_activity_repairs,
                        **transition_quality["quality"],
                    })
                    current_quality = validate(lines, cfg, repaired_lines=repaired_timings)
                    if transition_quality["quality"]["score"] > current_quality["quality"]["score"]:
                        lines = transition_lines
                        repaired_timings = transition_repairs
                        activity_repaired_timings = transition_activity_repairs
                        alignment_selected = transition_mode
                except (RuntimeError, ValueError) as transition_error:
                    alignment_attempts.append({
                        "mode": f"{alignment_selected}+transitions",
                        "error": str(transition_error),
                    })
            if stable_ts_summary.get("words"):
                notify(82, "Von ASR ausgelassene Gesangsfragmente werden lokal neu ausgerichtet")
                fragment_recovery = recover_deleted_fragments(
                    audio, lines, stable_ts_summary["words"],
                    stable_ts_summary["comparison"], aligner, language)
            if engine_v2_mode != "off":
                engine_v2_candidates.append(capture_candidate(
                    "lrclib-window-alignment",
                    "LRCLIB-Zeilenanker mit lokalen Forced-Alignment-Fenstern",
                    lines, "canonical-lrclib"))
                stable_scaffolds = [
                    ("full-transcript-scaffold", "long-context",
                     stable_ts_summary),
                    ("short-window-transcript-scaffold", "short-overlapping-windows",
                     stable_ts_short_summary),
                ]
                for candidate_id, window_family, stable_source in stable_scaffolds:
                    if not stable_source.get("words"):
                        continue
                    # These two candidates take their timing from the
                    # transcript, not from the input file, so plain lyrics do
                    # not disqualify them - they are in fact the only candidates
                    # able to produce anchors for a song that has none. The
                    # clean room evaluates that first; variant 1.2 keeps the
                    # previous behaviour until it is proven.
                    if untimed_input and not shadow_untimed_scaffolds:
                        continue
                    try:
                        _candidate_headers, transcript_scaffold = parse_lrc(lrc_path)
                        transfer_canonical_lines(
                            _candidate_headers, transcript_scaffold,
                            stable_source["words"],
                            minimum_coverage=float(os.getenv(
                                "LRC_CANONICAL_TRANSFER_MIN_COVERAGE", "0.55")))
                        aligner.align(audio, transcript_scaffold, cfg)
                        repair_with_vocal_activity(
                            transcript_scaffold, cfg, vocal_activity)
                        repair_collapsed_timings(transcript_scaffold, cfg)
                        engine_v2_candidates.append(capture_candidate(
                            candidate_id,
                            ("Kanonischer LRCLIB-Text auf dem unabhängigen "
                             f"Volltranskript-Zeitgerüst ({window_family})"),
                            transcript_scaffold, "full-transcript-stable-ts"))
                    except (RuntimeError, ValueError) as candidate_error:
                        engine_v2_candidate_errors.append({
                            "id": candidate_id,
                            "error": str(candidate_error)[:500],
                        })
        finally:
            aligner.close()
        enter_phase("independent-forced-refinement")
        repetition_activity_summary = refine_stretched_repetition_anchors(
            lines, vocal_activity
        )
        repetition_anchor_summary["activity_refinement"] = repetition_activity_summary
        stable_alignment = {"enabled": False, "reason": "keine Stable-ts-Wortzeiten"}
        ctc_summary = {"enabled": False, "reason": "deaktiviert"}
        if os.getenv("LRC_CTC_VERIFY", "true").strip().lower() in {"1", "true", "yes", "on"}:
            notify(84, "Heuristische Phrasen werden unabhängig per CTC/Phonemen geprüft")
            try:
                ctc_summary = realign_heuristic_lines(audio, lines, language, device)
            except (RuntimeError, ValueError) as ctc_error:
                ctc_summary = {"enabled": True, "error": str(ctc_error),
                               "attempted_lines": 0, "accepted_lines": 0, "accepted_words": 0}
        mms_summary = {"enabled": False, "reason": "deaktiviert"}
        if os.getenv("LRC_MMS_VERIFY", "true").strip().lower() in {"1", "true", "yes", "on"}:
            notify(85, "Verbleibende Phrasen werden mit multilingualem MMS gegengeprüft")
            try:
                mms_summary = realign_remaining_lines(audio, lines, device)
            except (RuntimeError, ValueError) as mms_error:
                mms_summary = {"enabled": True, "error": str(mms_error),
                               "attempted_lines": 0, "accepted_lines": 0, "accepted_words": 0}
        easyaligner_summary = {"enabled": False, "reason": "deaktiviert"}
        if os.getenv("LRC_EASYALIGNER_VERIFY", "true").strip().lower() in {"1", "true", "yes", "on"}:
            notify(85, "Verbleibende Phrasen werden global mit EasyAligner geprüft")
            try:
                easyaligner_summary = realign_with_easyaligner(audio, lines, language, device)
            except (RuntimeError, ValueError, KeyError) as easyaligner_error:
                easyaligner_summary = {"enabled": True, "error": str(easyaligner_error),
                                       "attempted_lines": 0, "accepted_lines": 0,
                                       "accepted_words": 0}
        sofa_summary = {"enabled": False, "reason": "nur für Englisch"}
        if (language.lower().split("-")[0] == "en"
                and os.getenv("LRC_SOFA_VERIFY", "true").strip().lower() in {"1", "true", "yes", "on"}):
            notify(85, "Englische Restabschnitte werden mit dem Gesangsmodell SOFA geprüft")
            sofa_summary = realign_english_singing(audio, lines)
        if engine_v2_mode != "off":
            engine_v2_candidates.append(capture_candidate(
                "forced-refinement-cascade",
                "LRCLIB-Pfad nach unabhängiger CTC/MMS/SOFA-Prüfung",
                lines, "forced-alignment-consensus"))
        # Stable-ts is the last acoustic candidate.  It may fill only complete
        # lines that are still heuristic after CTC/MMS/EasyAligner/SOFA, so no
        # later section-level pass can overwrite its measured word timestamps.
        if stable_ts_summary.get("words"):
            stable_post_alignment = realign_with_stable_words(
                lines, stable_ts_summary["words"], stable_ts_summary["comparison"],
                max_start_deviation=(float("inf") if untimed_input else 2.0),
                allow_unanchored=untimed_input,
            )
            stable_alignment = {
                "enabled": True,
                **stable_post_alignment,
            }
        enter_phase("stage-stem-finalization")
        # Stage stems are already final and are never replaced by an analysis
        # winner. Candidate fusion is scored against the selected all-vocals
        # analysis activity map.
        engine_v2_summary = {
            "version": 2, "mode": engine_v2_mode, "applied": False,
            "reason": "feature-disabled", "candidate_errors": engine_v2_candidate_errors,
        }
        enhanced_input_preservation = {
            "enabled": enhanced_input_candidate is not None,
            "preserved_lines": 0,
            "reason": "awaiting-final-stage-stem",
        }
        all_vocals_selected = selected_candidate.type == "analysis-vocal-separator"
        if all_vocals_selected:
            stem_leakage_summary = {
                "enabled": False,
                "reason": "all-vocals-alignment-source-selected",
                "candidate_lines": 0,
                "recovered_lines": 0,
            }
        else:
            notify(87, "Im Instrumental gelandete Gesangszeilen werden lokal geprüft")
            leakage_recognition_candidates = [("original-mix", mix_audio)]
            stem_leakage_summary = recover_complementary_stem_lines(
                stage_vocal_audio, stage_instrumental_audio, lines, language, device,
                recognition_audio=mix_audio,
                recognition_audio_source="original-mix",
                recognition_candidates=leakage_recognition_candidates)
        stem_hybrid_summary = {
            "enabled": False, "reason": "stage-analysis-role-separation",
            "intervals": [],
        }
        # ASR/CTC keep their required 16 kHz signal.  Final word releases are
        # measured on the exact native-rate stem that is exported below; a
        # quiet decay can disappear early in the model resample even though it
        # remains audible and visible in the editor waveform.
        if separator:
            stage_release_audio, stage_release_sample_rate = load_native_audio(
                stage_stems.vocals)
            notify(87, "Mögliche parallele Gesangsstimmen werden im Schattenlauf geprüft")
            multi_voice_analysis = analyze_multiple_singing_voices(
                stage_stems.vocals, lines, output_dir, device=device,
                discover_solo_singers=alignment_profile == "research-shadow")
        else:
            stage_release_audio, stage_release_sample_rate = stage_vocal_audio, 16000
            multi_voice_analysis = {
                "enabled": False, "reason": "vocal-separation-disabled",
                "method": "medleyvox-targeted-overlap-add-shadow-v1",
            }
        global_phoneme_summary = {
            "enabled": False,
            "mode": global_phoneme_mode,
            "reason": ("standard-profile-protected" if alignment_profile != "research-shadow"
                       else "feature-disabled"),
        }
        global_phoneme_candidate_id: str | None = None
        if global_phoneme_mode != "off":
            notify(87, "Globaler IPA-Phonempfad platziert den vollständigen Liedtext")
            global_lines = deepcopy(global_phoneme_input)
            try:
                global_phoneme_summary = {
                    "mode": global_phoneme_mode,
                    **align_global_phoneme_path(
                        audio, global_lines, language, device,
                        chunk_seconds=float(os.getenv(
                            "LRC_GLOBAL_PHONEME_CHUNK_SECONDS", "24")),
                        context_seconds=float(os.getenv(
                            "LRC_GLOBAL_PHONEME_CONTEXT_SECONDS", "1.2"))),
                }
                if global_phoneme_mode == "primary":
                    global_phoneme_candidate_id = "global-phoneme-ctc"
                    engine_v2_candidates.append(capture_candidate(
                        global_phoneme_candidate_id,
                        ("Kanonischer Volltext auf einem globalen, ankerfreien "
                         "XLSR/eSpeak-Phonempfad"),
                        global_lines, "global-phoneme-forced"))
                else:
                    global_phoneme_summary["candidate_state"] = "diagnostic-only"
            except (RuntimeError, ValueError, OSError) as global_phoneme_error:
                global_phoneme_summary = {
                    "enabled": True,
                    "mode": global_phoneme_mode,
                    "error": str(global_phoneme_error)[:1000],
                    "placed_lines": 0,
                    "placed_words": 0,
                }
                engine_v2_candidate_errors.append({
                    "id": "global-phoneme-ctc",
                    "error": str(global_phoneme_error)[:500],
                })
        enter_phase("timing-candidate-fusion")
        if engine_v2_mode != "off":
            legacy_baseline_id = "legacy-cascade"
            engine_v2_candidates.append(capture_candidate(
                legacy_baseline_id,
                "Bisherige sequentielle Pipeline einschließlich Stable-TS",
                lines, "legacy-cascade"))
            if (alignment_profile == "editor-guided"
                    and enhanced_input_candidate is not None):
                guided_lines, editor_guidance_summary = build_editor_guided_candidate(
                    lines, enhanced_input_candidate.lines, manual_editor_ranges,
                    maximum_influence_seconds=float(os.getenv(
                        "LRC_EDITOR_GUIDANCE_MAX_DISTANCE", "45")),
                    maximum_shift_seconds=float(os.getenv(
                        "LRC_EDITOR_GUIDANCE_MAX_SHIFT", "1.5")))
                if editor_guidance_summary.get("guided_lines", 0) > 0:
                    engine_v2_candidates.append(capture_candidate(
                        "editor-guided-calibration",
                        ("Lokale Timing-Kalibrierung aus unveränderlichen, "
                         "manuell ausgerichteten Nachbarzeilen"),
                        guided_lines, "human-guided-acoustic-calibration"))
            # ``primary`` means that the anchor-free global CTC path is a real
            # placement hypothesis, not merely a read-only verifier.  It must
            # nevertheless not become the Engine-v2 safety baseline: a single
            # monotone CTC path can choose the wrong occurrence of repeated
            # choruses while still producing valid-looking geometry.  Making
            # that path the baseline grants every such line unconditional
            # eligibility and can force the Viterbi path through a bad late
            # occurrence.  The established cascade remains the immutable
            # safety path; global CTC wins individual lines only when acoustic
            # evidence or an independent candidate family corroborates it.
            baseline_id = legacy_baseline_id
            if global_phoneme_candidate_id is not None:
                global_phoneme_summary["candidate_state"] = (
                    "primary-hypothesis-with-independent-safety-baseline")
                global_phoneme_summary["safety_baseline"] = legacy_baseline_id
            if (enhanced_input_candidate is not None
                    and len(enhanced_input_candidate.lines) == len(lines)
                    and all(source.text == current.text
                            for source, current in zip(enhanced_input_candidate.lines, lines))):
                engine_v2_candidates.append(enhanced_input_candidate)
            notify(88, "Zeitkandidaten werden gegen den finalen Stage-Stem bewertet")
            lines, engine_v2_summary = fuse_alignment_candidates(
                engine_v2_candidates, vocal_activity, baseline_id=baseline_id,
                mode=engine_v2_mode,
                minimum_line_improvement=float(os.getenv(
                    "LRC_ENGINE_V2_MIN_LINE_IMPROVEMENT", "0.035")))
            engine_v2_summary["candidate_errors"] = engine_v2_candidate_errors
            selected_global_lines = sum(
                1 for decision in engine_v2_summary.get("lines", [])
                if decision.get("selected") == global_phoneme_candidate_id)
            global_phoneme_summary["selected_lines"] = selected_global_lines
            if selected_global_lines:
                alignment_selected = (
                    f"{alignment_selected}+global-phoneme-fusion")
            elif global_phoneme_candidate_id is not None:
                global_phoneme_summary["candidate_state"] = (
                    "evaluated-but-rejected-by-independent-fusion")
                if engine_v2_summary.get("applied"):
                    alignment_selected = f"{alignment_selected}+lyrics-engine-v2"
            elif engine_v2_summary.get("applied"):
                alignment_selected = f"{alignment_selected}+lyrics-engine-v2"
        elif global_phoneme_candidate_id is not None:
            # A raw full-track CTC path is unsafe around repeated phrases when
            # no independent candidate fusion is available. Keep it in the
            # report, but never publish it unchecked as song geometry.
            global_phoneme_summary["candidate_state"] = (
                "diagnostic-only-fusion-disabled")
            global_phoneme_summary["selected_lines"] = 0
            engine_v2_summary = {
                "version": 2,
                "mode": "off",
                "applied": False,
                "baseline": "legacy-cascade",
                "reason": "global-primary-requires-independent-candidate-fusion",
            }
        lines, enhanced_input_preservation = preserve_better_enhanced_input(
            lines, enhanced_input_candidate, vocal_activity,
            minimum_improvement=float(os.getenv(
                "LRC_ENHANCED_INPUT_MIN_IMPROVEMENT", "0.06")))
        enter_phase("macro-boundary-refinement")
        sustain_summary = extend_final_word_sustains(
            lines, vocal_activity, audio=audio, sample_rate=16000)
        preliminary_onsets = validate_line_onsets(audio, lines)
        onset_refinements = apply_supported_onset_refinements(lines, preliminary_onsets)
        display_duration_summary = stabilize_acoustic_display_durations(lines)
        consensus_summary = reconcile_acoustic_boundaries(lines)
        missing_chorus_summary = (
            {"enabled": True, "recovered_lines": 0,
             "reason": "all-vocals-alignment-source-selected"}
            if all_vocals_selected else
            recover_missing_initial_chorus(
                audio, analysis_accompaniment_audio, lines, language, device,
                initial_prompt=stable_prompt)
        )
        # This is the last *macro anchor* transformation. Later stages may
        # still refine local word geometry, but cannot select a different
        # chorus occurrence without independent sentence/word evidence.
        chorus_anchor_summary = (
            {"enabled": False, "reason": "all-vocals-acoustic-timing-selected",
             "shifted_lines": 0}
            if all_vocals_selected else apply_trusted_chorus_anchors(lines)
        )
        notify(89, "Überlappende Zeilenübergänge werden gemeinsam akustisch neu geprüft")
        try:
            overlap_reanalysis = realign_overlapping_line_pairs(
                audio, lines, language, device)
        except (RuntimeError, ValueError) as overlap_error:
            overlap_reanalysis = {"enabled": True, "error": str(overlap_error),
                                  "detected_pairs": 0, "realigned_pairs": 0,
                                  "unresolved_pairs": []}
        overlap_reanalysis["unresolved_after_ctc"] = overlap_reanalysis.get("unresolved_pairs", [])
        post_overlap_consensus = reconcile_acoustic_boundaries(lines)
        overlap_display_fallback = eliminate_remaining_line_overlaps(lines)
        for line in lines:
            if line.words:
                line.timestamp = float(line.words[0]["start"])
        overlap_reanalysis["unresolved_pairs"] = [
            {"previous_line": index, "next_line": index + 1,
             "overlap_ms": round((float(lines[index - 1].words[-1]["end"]) -
                                  float(lines[index].words[0]["start"])) * 1000, 1)}
            for index in range(1, len(lines))
            if lines[index - 1].words and lines[index].words and
            float(lines[index - 1].words[-1]["end"]) >
            float(lines[index].words[0]["start"]) + 0.001
        ]
        overlap_reanalysis["boundary_consensus"] = post_overlap_consensus
        overlap_reanalysis["display_lane_fallback"] = overlap_display_fallback
        onset_summary = validate_line_onsets(audio, lines)
        onset_summary["refinement"] = onset_refinements
        if not alignment_attempts:
            selected_quality = validate(lines, cfg, repaired_lines=repaired_timings)
            alignment_attempts.append({
                "mode": alignment_selected,
                "vocal_activity_repaired_lines": activity_repaired_timings,
                "anchor_context_repaired_runs": anchor_context_repairs,
                "anchor_tail_repaired_lines": anchor_tail_repairs,
                **selected_quality["quality"],
            })
        enter_phase("missing-lyrics-recovery")
        preliminary_completeness = assess_lyric_completeness(
            lines, vocal_activity, stable_ts_summary.get("words", []), len(audio) / 16000
        )
        gap_reanalysis = {
            "enabled": False,
            "reason": "keine Vocal-Ausschläge außerhalb der Lyrics",
            "investigated_regions": 0,
            "recovered_regions": 0,
            "recovered_lines": 0,
            "unresolved_regions": [],
        }
        if preliminary_completeness.get("requires_targeted_reanalysis"):
            notify(90, "Vocal-Ausschläge ohne Text werden in kleinen GPU-Fenstern untersucht")
            try:
                gap_reanalysis = recover_vocal_gap_lines(
                    audio, lines, preliminary_completeness["investigation_regions"],
                    language, device, prompt=qwen_prompt)
                if gap_reanalysis.get("recovered_lines", 0):
                    eliminate_remaining_line_overlaps(lines)
            except (RuntimeError, ValueError) as gap_error:
                gap_reanalysis = {
                    "enabled": True,
                    "error": str(gap_error),
                    "investigated_regions": 0,
                    "recovered_regions": 0,
                    "recovered_lines": 0,
                    "unresolved_regions": preliminary_completeness["investigation_regions"],
                }
        # Independent late models may return positive but mutually overlapping
        # word intervals. Repair this after every acoustic/model pass, not only
        # after the initial Qwen alignment.
        enter_phase("late-word-geometry")
        final_activity_repairs = repair_with_vocal_activity(
            lines, cfg, vocal_activity)
        final_geometry_repairs = repair_collapsed_timings(lines, cfg)
        # The late activity/geometry pass may select a backing-vocal occurrence
        # which overlaps the neighbouring lead line. Karaoke uses one display
        # lane, so enforce the invariant after *all* timing models have run.
        final_overlap_fallback = eliminate_remaining_line_overlaps(lines)
        connector_sustain_summary = reassign_overlong_connector_sustains(
            lines, audio)
        late_sustain_summary = extend_final_word_sustains(
            lines, stage_vocal_activity, audio=stage_release_audio,
            sample_rate=stage_release_sample_rate)
        repeated_phrase_summary = refine_repeated_phrase_words(
            audio, lines, stable_ts_summary.get("words", []), language)
        enter_phase("editor-boundary-protection")
        lines, enhanced_input_internal_guard = (
            preserve_uncorroborated_internal_editor_boundaries(
                lines, enhanced_input_candidate,
                boundary_disagreement=float(os.getenv(
                    "LRC_EDITOR_INTERNAL_BOUNDARY_DISAGREEMENT", "0.14")),
                maximum_outer_disagreement=float(os.getenv(
                    "LRC_EDITOR_OUTER_BOUNDARY_DISAGREEMENT", "0.28"))))
        enter_phase("hard-boundary-precheck")
        final_overlap_fallback = eliminate_remaining_line_overlaps(lines)
        # Lane promotion is applied later, in final-hard-constraints. The gate
        # must nevertheless know which neighbour will legally overlap, so the
        # already decided lanes are projected read-only.
        pending_voice_lanes = (planned_voice_lanes(lines, multi_voice_analysis)
                               if shadow_silent_prefix_recovery else None)
        pre_verification_stage_vocal_boundaries = constrain_to_stage_vocals(
            lines, stage_vocal_activity, vocal_audio=stage_vocal_audio,
            silent_prefix_recovery=shadow_silent_prefix_recovery,
            pending_voice_lanes=pending_voice_lanes)
        stem_contrast_analysis = refine_final_releases_with_stem_contrast(
            lines, stage_vocal_audio, stage_instrumental_audio, mode="shadow")
        line_transition_analysis = analyze_line_transitions(
            lines, stage_vocal_audio, stage_instrumental_audio,
            maximum_early_onset_shift=float(os.getenv(
                "LRC_LINE_TRANSITION_MAX_EARLY_ONSET_SHIFT", ".12")),
            minimum_onset_duration=float(os.getenv(
                "LRC_LINE_TRANSITION_MIN_ONSET_DURATION", ".08")))
        # Everything above may place or resize words.  Only now reprocess each
        # complete sentence in its small local audio window, split the forced
        # IPA path back into words and verify every edge against independent
        # onset/transition/release evidence.  No later model is allowed to
        # overwrite these checked boundaries.
        enter_phase("sentence-word-verification")
        phoneme_ctc_enabled = os.getenv(
            "LRC_PHONEME_CTC_REFINE", "true").strip().lower() in {
                "1", "true", "yes", "on"
            }
        phoneme_ctc_summary = {"enabled": False, "reason": "feature-disabled"}
        micro_boundary_mode = os.getenv(
            "LRC_MICRO_BOUNDARY_MODE", "select").strip().lower()
        voicing_track = None
        voicing_summary = {"enabled": False, "reason": "phoneme-refinement-disabled"}
        micro_sustain_summary = {"enabled": False, "reason": "voicing-unavailable"}
        basic_pitch_summary = {"enabled": False, "reason": "profile-disabled",
                               "profile": alignment_profile}
        basic_pitch_enabled = os.getenv(
            "LRC_BASIC_PITCH_ENABLED",
            "true" if os.getenv("BASIC_PITCH_URL", "").strip() else "false",
        ).strip().lower() in {"1", "true", "yes", "on"}
        if basic_pitch_enabled:
            notify(91, "Basic Pitch analysiert Noten und polyphone Pitch-Ereignisse")
            basic_pitch_alignment_requested = os.getenv(
                "LRC_BASIC_PITCH_USE_FOR_ALIGNMENT", "true"
            ).strip().lower() in {"1", "true", "yes", "on"}
            basic_pitch_summary = analyze_and_refine_line_onsets(
                lines, audio, analysis_accompaniment_audio,
                use_for_alignment=(basic_pitch_alignment_requested
                                   and analysis_accompaniment_audio is not None))
            basic_pitch_summary["alignment_requested"] = (
                basic_pitch_alignment_requested)
            if basic_pitch_alignment_requested and analysis_accompaniment_audio is None:
                basic_pitch_summary["alignment_suppressed_reason"] = (
                    "matching-analysis-accompaniment-unavailable")
        else:
            basic_pitch_summary = {
                "enabled": False, "reason": "feature-disabled",
                "profile": alignment_profile,
            }
        pyin_enabled = os.getenv("LRC_PYIN_ENABLED", "true").strip().lower() in {
            "1", "true", "yes", "on"
        }
        if pyin_enabled:
            try:
                voicing_windows = sustain_voicing_intervals(
                    lines, len(audio) / 16000)
                voicing_track, voicing_summary = analyze_voicing(
                    audio, intervals=voicing_windows)
                micro_sustain_summary = refine_sustain_releases_with_voicing(
                    lines, voicing_track, mode=micro_boundary_mode)
            except (RuntimeError, ValueError, OSError) as pyin_error:
                voicing_summary = {
                    "enabled": True, "reason": "pyin-analysis-failed",
                    "error": str(pyin_error)[:1000],
                }
        else:
            voicing_summary = {"enabled": False, "reason": "feature-disabled"}
        if phoneme_ctc_enabled:
            notify(91, "Jeder Satz und jede Wortgrenze werden lokal verifiziert")
            try:
                # The instrumental stem is sample synchronous with the vocal
                # stem, so it says how much of a measured step at a word edge
                # is separator bleed rather than the singer.
                leakage_reference = (analysis_accompaniment_audio
                                     if shadow_silent_prefix_recovery else None)
                with leakage_aware_evidence(leakage_reference):
                    phoneme_ctc_summary = annotate_phoneme_boundaries(
                        audio, lines, language, device,
                        promotion_minimum_confidence=float(os.getenv(
                            "LRC_PHONEME_PROMOTION_MIN_CONFIDENCE", "0.50")),
                        promotion_minimum_evidence=float(os.getenv(
                            "LRC_PHONEME_PROMOTION_MIN_EVIDENCE", "0.60")),
                        promotion_maximum_shift=float(os.getenv(
                            "LRC_PHONEME_PROMOTION_MAX_SHIFT", "0.14")),
                        micro_boundary_mode=micro_boundary_mode,
                        micro_search_radius=float(os.getenv(
                            "LRC_MICRO_BOUNDARY_SEARCH_RADIUS", "0.055")),
                        micro_minimum_path_improvement=float(os.getenv(
                            "LRC_MICRO_BOUNDARY_MIN_IMPROVEMENT", "0.08")),
                        voicing_track=voicing_track,
                        repetition_pairs=timed_repetition_pairs,
                        stem_contrast_candidates=stem_contrast_analysis.get(
                            "details", []))
            except (RuntimeError, ValueError, OSError) as phoneme_error:
                phoneme_ctc_summary = {
                    "enabled": True,
                    "error": str(phoneme_error)[:1000],
                    "attempted_lines": 0,
                    "accepted_lines": 0,
                    "accepted_words": 0,
                }
        delayed_first_word_editor_tail = _restore_editor_tail_after_delayed_first_word(
            lines, enhanced_input_candidate)
        # IPA repairs are bounded, but the final two hard constraints remain
        # non-negotiable: one display lane and the exact exported vocal stem.
        # If either has to modify a verified edge, the final audit below marks
        # that edge as constrained rather than pretending it is still the raw
        # IPA candidate.
        enter_phase("final-hard-constraints")
        post_verification_sustain_summary = extend_final_word_sustains(
            lines, stage_vocal_activity, audio=stage_release_audio,
            sample_rate=stage_release_sample_rate)
        source_boundary_constraints = constrain_final_words_to_source_boundaries(lines)
        nonlexical_vocalization_summary = constrain_lyrics_before_nonlexical_vocalizations(
            lines, gap_reanalysis)
        # Repetition anchors are derived from a small, independent acoustic
        # window and the canonical lyric count. Candidate fusion and IPA may
        # refine other words, but must not silently replace these structural
        # anchors. Manual editor lines are restored afterwards and therefore
        # remain the ultimate authority.
        final_repetition_anchor_summary = apply_repetition_anchors(
            lines, timed_repetition_pairs)
        final_overlap_fallback = eliminate_remaining_line_overlaps(lines)
        stage_vocal_boundaries = constrain_to_stage_vocals(
            lines, stage_vocal_activity, vocal_audio=stage_vocal_audio,
            silent_prefix_recovery=shadow_silent_prefix_recovery,
            pending_voice_lanes=(
                planned_voice_lanes(lines, multi_voice_analysis)
                if shadow_silent_prefix_recovery else None))
        manual_editor_preservation = _restore_manual_editor_lines(
            lines, enhanced_input_candidate, manual_editor_ranges)
        manual_editor_preceding_boundaries = (
            _constrain_preceding_lines_before_manual_editor_starts(lines))
        manual_editor_following_boundaries = (
            _constrain_following_lines_after_manual_editor_holds(lines))
        post_manual_overlap_fallback = eliminate_remaining_line_overlaps(
            lines,
            protected_line_indices={
                int(line_number) - 1
                for line_number in manual_editor_preservation.get("line_indices", [])
            })
        multi_voice_promotion = apply_multiple_singing_voice_proposals(
            lines, multi_voice_analysis,
            full_vocals=stage_release_audio,
            sample_rate=stage_release_sample_rate,
            voice_output_dir=output_dir)
        if post_manual_overlap_fallback["adjusted_pairs"]:
            final_overlap_fallback = {
                "method": "single-karaoke-lane-fallback-v1",
                "adjusted_pairs": (
                    final_overlap_fallback.get("adjusted_pairs", 0)
                    + post_manual_overlap_fallback["adjusted_pairs"]),
                "adjustments": [
                    *final_overlap_fallback.get("adjustments", []),
                    *post_manual_overlap_fallback["adjustments"],
                ],
                "post_manual_restore": post_manual_overlap_fallback,
            }
        # Monotonic, non-overlapping geometry can still be physically
        # impossible. Runs below a human articulation rate are redistributed
        # over the vocal activity they may legally occupy. This deliberately
        # runs after lane promotion: MedleyVox both assigns the final lanes and
        # moves the promoted lines, so an earlier redistribution would compute
        # its envelope against geometry that is about to change.
        collapsed_line_repair = {"enabled": False, "reason": "research-shadow-only"}
        compressed_word_repair = {"enabled": False, "reason": "research-shadow-only"}
        if shadow_silent_prefix_recovery:
            collapsed_line_repair = repair_collapsed_lines(
                lines, stage_vocal_activity, language=cfg.language,
                audio=stage_vocal_audio)
            compressed_word_repair = repair_compressed_word_runs(
                lines, stage_vocal_activity, language=cfg.language,
                audio=stage_vocal_audio)
        final_word_boundary_audit = audit_final_word_boundaries(lines)
        enter_phase("syllable-derivation")
        acoustic_syllables_enabled = os.getenv(
            "LRC_ACOUSTIC_SYLLABLE_REFINE", "true").strip().lower() in {
                "1", "true", "yes", "on"
            }
        notify(92, ("Silbengrenzen werden lokal akustisch verfeinert"
                    if acoustic_syllables_enabled else
                    "Silben werden in den Wortfenstern zeitlich eingeordnet"))
        syllable_summary = enrich_lines_with_syllables(
            lines, language,
            audio=audio if acoustic_syllables_enabled else None,
            enforce_minimum_geometry=shadow_silent_prefix_recovery)
        manual_editor_syllable_preservation = _restore_manual_editor_syllables(
            lines, enhanced_input_candidate)
        syllable_summary["manual_editor_preservation"] = (
            manual_editor_syllable_preservation)
        delayed_first_word_editor_tail["syllables"] = (
            _restore_editor_syllable_tail_after_delayed_first_word(
                lines, enhanced_input_candidate))
        evidence_fusion_summary = fuse_syllable_evidence(
            lines, basic_pitch_summary, audio, analysis_accompaniment_audio,
            mode=os.getenv("LRC_EVIDENCE_FUSION_MODE", "shadow").strip().lower())
        note_alignment_summary = align_notes_to_syllables(
            lines, basic_pitch_summary)
        pyin_evidence_summary = serialize_voicing_evidence(
            voicing_track, voicing_summary,
            maximum_points=int(os.getenv("LRC_PYIN_REPORT_MAX_POINTS", "12000")))
        timing_reference_comparison = compare_timing_reference(
            lines, enhanced_input_candidate)
        if separator:
            detected_starts = [float(word["start"]) for line in lines for word in line.words]
            first_vocal_start = min(detected_starts) if detected_starts else None
            mute_before = (None if provided_stage_stems else
                           max(0.0, first_vocal_start - 0.35)
                           if first_vocal_start is not None else None)
            # Export both halves of the same separator result. The alignment
            # winner may be an original-mix blend, but that is intentionally
            # never exposed as a karaoke stem.
            ffmpeg_to_flac(stage_stems.instrumental, instrumental_out)
            ffmpeg_to_flac(stage_stems.vocals, vocals_out, mute_before=mute_before)
        notify(94, "Alignment wird geprüft")

    enter_phase("validation-and-export")
    summary = validate(lines, cfg, repaired_lines=repaired_timings)
    completeness = assess_lyric_completeness(
        lines, vocal_activity, stable_ts_summary.get("words", []), len(audio) / 16000
    )
    apply_targeted_gap_results(completeness, gap_reanalysis)
    apply_completeness_gate(summary, completeness)
    lrc_out = output_dir / f"{stem}.word-synced.lrc"
    report_out = output_dir / f"{stem}.alignment.json"
    stems_report_out = output_dir / f"{stem}.stems.json"
    lrc_out.write_text(render_enhanced_lrc(headers, lines), encoding="utf-8")
    report = {
        **summary,
        "language": language,
        "alignment_profile": alignment_profile,
        "research_shadow_input": research_shadow_input,
        "collapsed_line_repair": collapsed_line_repair,
        "compressed_word_repair": compressed_word_repair,
        "language_reconciliation": language_reconciliation,
        "alignment_device": device,
        "pipeline_phase_order": phase_trace,
        "input_timing": "plain-lyrics" if untimed_input else "line-synced-lrc",
        "timestamp_calibration": timestamp_calibration,
        "alignment_mode": alignment_selected,
        "alignment_attempts": alignment_attempts,
        "alignment_selected": alignment_selected,
        "lyrics_engine_v2": engine_v2_summary,
        "enhanced_input_preservation": enhanced_input_preservation,
        "enhanced_input_internal_guard": enhanced_input_internal_guard,
        "editor_guidance": editor_guidance_summary,
        "manual_editor_preservation": manual_editor_preservation,
        "manual_editor_preceding_boundaries": manual_editor_preceding_boundaries,
        "manual_editor_following_boundaries": manual_editor_following_boundaries,
        "delayed_first_word_editor_tail": delayed_first_word_editor_tail,
        "transcript_verification": transcript_verification,
        "asr_prompt": asr_prompt_summary,
        "stable_ts": {**stable_ts_summary, "alignment": stable_alignment},
        "stable_ts_short_windows": stable_ts_short_summary,
        "fragment_recovery": fragment_recovery,
        "repetition_anchors": repetition_anchor_summary,
        "final_repetition_anchors": final_repetition_anchor_summary,
        "chorus_line_anchors": chorus_anchor_summary,
        "missing_instrumental_chorus": missing_chorus_summary,
        "stem_leakage_recovery": stem_leakage_summary,
        "stem_hybridization": stem_hybrid_summary,
        "missing_lyric_reanalysis": gap_reanalysis,
        "separation": ("role-separated-analysis-and-stage" if separator else
                       "analysis-original-stage-disabled"),
        "alignment_audio": alignment_audio,
        "runtime_device": runtime_device,
        "alignment_candidate_selection": candidate_selection,
        "analysis_stem_selection": analysis_stem_selection,
        "stage_stem_selection": stage_stem_selection,
        "stage_stem_quality": stage_stem_quality,
        "separator_candidates": [
            *candidate_selection.get("candidates", []),
            *analysis_bundle.errors,
        ],
        "output_lrc": lrc_out.name,
        "stems": stem_outputs,
        "syllable_alignment": syllable_summary,
        "ctc_alignment": ctc_summary,
        "mms_alignment": mms_summary,
        "easyaligner_alignment": easyaligner_summary,
        "sofa_alignment": sofa_summary,
        "phoneme_ctc_alignment": phoneme_ctc_summary,
        "basic_pitch_evidence": {
            key: value for key, value in basic_pitch_summary.items()
            if key not in {"notes", "contour"}
        },
        "basic_pitch_analysis": basic_pitch_summary,
        "pitch_evidence": {
            "basic_pitch": basic_pitch_summary.get("alignment_confidence", {}),
            "representation": "independent-musical-evidence",
        },
        "global_phoneme_alignment": global_phoneme_summary,
        "final_word_boundary_audit": final_word_boundary_audit,
        "repeated_phrase_refinement": repeated_phrase_summary,
        "micro_voicing_analysis": voicing_summary,
        "pyin_evidence": pyin_evidence_summary,
        "evidence_fusion": evidence_fusion_summary,
        "note_alignment": note_alignment_summary,
        "micro_sustain_refinement": micro_sustain_summary,
        "timing_reference_comparison": timing_reference_comparison,
        "alignment_consensus": consensus_summary,
        "line_overlap_reanalysis": overlap_reanalysis,
        "display_durations": display_duration_summary,
        "final_word_geometry": {
            "vocal_activity_repairs": final_activity_repairs,
            "geometric_fallback_repairs": final_geometry_repairs,
            "overlap_fallback": final_overlap_fallback,
        },
        "sustain_refinement": sustain_summary,
        "connector_sustain_refinement": connector_sustain_summary,
        "late_sustain_refinement": late_sustain_summary,
        "post_verification_sustain_refinement": post_verification_sustain_summary,
        "nonlexical_vocalization_boundaries": nonlexical_vocalization_summary,
        "stage_vocal_boundaries": stage_vocal_boundaries,
        "pre_verification_stage_vocal_boundaries": (
            pre_verification_stage_vocal_boundaries),
        "stem_contrast_analysis": stem_contrast_analysis,
        "line_transitions": line_transition_analysis,
        "multiple_singing_voices": multi_voice_analysis,
        "multiple_singing_voice_promotion": multi_voice_promotion,
        "source_boundary_constraints": source_boundary_constraints,
        "onset_validation": onset_summary,
        "repaired_collapsed_lines": summary["quality"]["geometrically_repaired_lines"],
        "historically_repaired_collapsed_lines": repaired_timings,
        "vocal_activity": {
            "regions": len(vocal_activity),
            "stage_vocal_regions": len(stage_vocal_activity),
            "acoustically_repaired_lines": activity_repaired_timings,
            "anchor_context_repaired_runs": anchor_context_repairs,
            "anchor_tail_repaired_lines": anchor_tail_repairs,
            "method": "alignment-audio-plus-exact-stage-vocal-v2",
        },
        "lyrics_completeness": completeness,
        "details": [
            {
                "timestamp": line.timestamp,
                "source_timestamp": line.source_timestamp,
                "source_end_boundary": line.source_end_boundary,
                "manual_adjusted": line.manual_adjusted,
                "manual_editor_start": line.manual_editor_start,
                "manual_editor_end": line.manual_editor_end,
                "voice_lane": line.voice_lane,
                "voice_label": line.voice_label,
                "text": line.text,
                "status": line.status,
                "reason": line.reason,
                "words": line.words,
            }
            for line in lines
        ],
    }
    if stem_outputs:
        stems_report_out.write_text(json.dumps({
            "version": 2,
            "source": audio_path.name,
            "separator_model": stage_separator_model or KARAOKE_MODEL,
            "selection": stage_stem_selection,
            "roles": {
                "stage_vocals": stem_outputs["vocals"],
                "stage_instrumental": stem_outputs["instrumental"],
                "analysis_vocals": {
                    "retained": separation_config.keep_candidates,
                    "selection": analysis_stem_selection,
                },
            },
            "format": "flac",
            "sample_aligned": True,
            "first_vocal_start": first_vocal_start,
            "vocals_muted_before": mute_before,
            **stem_outputs,
        }, ensure_ascii=False, indent=2), encoding="utf-8")
        report["stems_manifest"] = stems_report_out.name
    report_out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    report["output_report"] = report_out.name
    notify(100, "Fertig")
    return report
