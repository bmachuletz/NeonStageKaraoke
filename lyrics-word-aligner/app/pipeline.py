from __future__ import annotations
import json
import os
import tempfile
import gc
import soundfile as sf
from collections import Counter
from copy import deepcopy
from dataclasses import replace
from pathlib import Path
from typing import Callable
from .aligner import QwenWordAligner
from .audio import ffmpeg_to_flac, ffmpeg_to_mono16k, load_audio, select_alignment_audio
from .asr_prompt import build_asr_prompt
from .candidate_selection import (AudioAlignmentCandidate, CandidateSelectionConfig,
                                  blend_audio, select_alignment_candidate)
from .ctc_aligner import realign_heuristic_lines, realign_overlapping_line_pairs
from .easy_aligner import realign_with_easyaligner
from .consensus import (eliminate_remaining_line_overlaps, extend_final_word_sustains, reconcile_acoustic_boundaries,
                        stabilize_acoustic_display_durations)
from .lrc import parse_lrc, render_enhanced_lrc
from .mms_aligner import realign_remaining_lines
from .models import AlignmentConfig
from .onset_validation import apply_supported_onset_refinements, validate_line_onsets
from .separator import KARAOKE_MODEL, separate_stems
from .sofa_aligner import realign_english_singing
from .stable_transcriber import realign_with_stable_words, transcribe_stable
from .repair import repair_collapsed_timings
from .repetition_anchors import (apply_local_timestamp, apply_repetition_anchors,
                                 apply_transition_words, local_repetition_requests,
                                 refine_stretched_repetition_anchors, transition_requests)
from .syllables import enrich_lines_with_syllables
from .transcriber import QwenTranscriber, language_code, merge_transcript_chunks
from .transcript_match import compare_transcripts
from .validator import validate
from .chorus_anchors import apply_trusted_chorus_anchors, recover_missing_initial_chorus
from .vocal_activity import (detect_vocal_activity, repair_anchor_context,
                             repair_anchor_line_tails, repair_with_vocal_activity)
from .completeness import (apply_completeness_gate, apply_targeted_gap_results,
                           assess_lyric_completeness)
from .gap_recovery import recover_vocal_gap_lines
from .full_transcription import plan_transcription_windows
from .timestamp_calibration import calibrate_source_timestamps

ProgressCallback = Callable[[int, str], None]

def _noop(_percent: int, _message: str) -> None:
    pass


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
    """Run ordinary songs whole and bound long-song ASR memory by audio windows."""
    threshold = float(os.getenv("LRC_TRANSCRIPTION_CHUNK_THRESHOLD_SECONDS", "300"))
    chunk_seconds = float(os.getenv("LRC_TRANSCRIPTION_CHUNK_SECONDS", "20"))
    overlap_seconds = float(os.getenv("LRC_TRANSCRIPTION_CHUNK_OVERLAP_SECONDS", "2"))
    windows, chunked = plan_transcription_windows(
        len(audio), threshold_seconds=threshold, chunk_seconds=chunk_seconds,
        overlap_seconds=overlap_seconds)
    if not windows:
        raise ValueError("Die ASR-Prüfung erhielt eine leere Audiospur.")
    token_limit = int(os.getenv(
        "LRC_TRANSCRIPTION_MAX_NEW_TOKENS" if chunked else "LRC_ASR_MAX_NEW_TOKENS",
        "256" if chunked else "1024"))
    results: list[dict] = []
    for sample_start, sample_end, _keep_start, _keep_end in windows:
        chunk = audio[sample_start:sample_end]
        try:
            result = session.transcribe(
                chunk, language, prompt=prompt, max_new_tokens=token_limit)
            if str(result.get("text", "")).strip():
                results.append(result)
        finally:
            del chunk
            _release_stage_gpu_memory()
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
        progress: ProgressCallback | None = None) -> dict:
    notify = progress or _noop
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
    untimed_input = all(not line.timed_input for line in lines)
    if not lines:
        raise ValueError("Die LRC-Datei enthält keine synchronisierten Textzeilen.")

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
    with tempfile.TemporaryDirectory(prefix="lyrics-align-") as temp:
        temp_dir = Path(temp)
        instrumental_audio = None
        if separator:
            notify(12, "Lead-Vocals werden isoliert")
            separated = separate_stems(audio_path, temp_dir / "separated")
            source = separated.vocals
            vocals_out = output_dir / f"{stem}.vocals.flac"
            instrumental_out = output_dir / f"{stem}.instrumental.flac"
            ffmpeg_to_flac(separated.instrumental, instrumental_out)
            stem_outputs = {
                "vocals": vocals_out.name,
                "instrumental": instrumental_out.name,
            }
            notify(48, "Vocal-Separation abgeschlossen")
        else:
            source = audio_path
            notify(25, "Vocal-Separation wurde übersprungen")

        notify(52, "Audio wird für das Alignment vorbereitet")
        wav = ffmpeg_to_mono16k(source, temp_dir / "vocals-16k.wav")
        separated_audio = load_audio(wav)
        audio = separated_audio
        alignment_audio = {"source": "input-audio", "fallback_used": False,
                           "method": "direct-input-v1"}
        mix_audio = None
        if separator:
            mix_wav = ffmpeg_to_mono16k(audio_path, temp_dir / "mix-16k.wav")
            mix_audio = load_audio(mix_wav)
            instrumental_wav = ffmpeg_to_mono16k(
                separated.instrumental, temp_dir / "instrumental-16k.wav")
            instrumental_audio = load_audio(instrumental_wav)
            audio, alignment_audio = select_alignment_audio(
                audio, mix_audio,
                minimum_rms_ratio=float(os.getenv("LRC_MIN_VOCAL_MIX_RMS_RATIO", "0.05")),
            )
            if alignment_audio["fallback_used"]:
                notify(53, "Vocal-Stem ist zu leise; Alignment verwendet den Originalmix")
        candidate_config = CandidateSelectionConfig.from_environment()
        selected_candidate_transcript = None
        candidate_generation_errors: list[dict] = []
        baseline_type = "original-mix-fallback" if alignment_audio["fallback_used"] else "existing-vocals"
        candidates = [AudioAlignmentCandidate(
            "existing-pipeline", "Bisherige Pipeline", audio, baseline_type, legacy=True,
            metadata={"energy_fallback_used": alignment_audio["fallback_used"]},
        )]
        selected_candidate = candidates[0]
        if candidate_config.enabled and mix_audio is not None:
            if candidate_config.include_original_mix:
                candidates.append(AudioAlignmentCandidate(
                    "original-mix", "Originalmix", mix_audio, "original-mix"))
            for ratio in candidate_config.blend_ratios:
                candidate_id = f"vocals-original-{ratio:.2f}"
                try:
                    candidates.append(AudioAlignmentCandidate(
                        candidate_id, f"Vocals + {ratio:.0%} Originalmix",
                        blend_audio(separated_audio, mix_audio, ratio), "vocal-original-blend",
                        metadata={"original_mix_ratio": ratio,
                                  "source_length_samples": len(separated_audio),
                                  "original_length_samples": len(mix_audio),
                                  "tail_adjustment_samples": len(mix_audio) - len(separated_audio)},
                    ))
                except Exception as error:
                    candidate_generation_errors.append({
                        "id": candidate_id, "status": "failed", "error": str(error)[:500],
                        "source_length_samples": len(separated_audio),
                        "original_length_samples": len(mix_audio),
                    })
            if os.getenv("LRC_ALIGNMENT_CANDIDATE_ALTERNATIVE", "false").strip().lower() in {
                    "1", "true", "yes", "on"}:
                alternative_model = os.getenv("LRC_ALIGNMENT_ALTERNATIVE_MODEL", "").strip()
                if not alternative_model:
                    candidate_generation_errors.append({
                        "id": "alternative-separator", "status": "failed",
                        "error": "LRC_ALIGNMENT_ALTERNATIVE_MODEL ist nicht konfiguriert",
                    })
                else:
                    try:
                        alternative = separate_stems(
                            audio_path, temp_dir / "alternative-separated", model_name=alternative_model)
                        alternative_wav = ffmpeg_to_mono16k(
                            alternative.vocals, temp_dir / "alternative-vocals-16k.wav")
                        candidates.append(AudioAlignmentCandidate(
                            "alternative-separator", "Alternatives Separator-Modell",
                            load_audio(alternative_wav), "alternative-separator",
                            metadata={"model": alternative_model},
                        ))
                    except Exception as error:
                        candidate_generation_errors.append({
                            "id": "alternative-separator", "status": "failed",
                            "error": str(error)[:500], "model": alternative_model,
                        })
        expected_text = "\n".join(line.text for line in lines)
        if candidate_config.enabled:
            notify(54, f"{len(candidates)} Audio-Kandidaten werden anhand der Lyrics bewertet")

            with QwenTranscriber(device) as candidate_transcriber:
                def evaluate_candidate(candidate: AudioAlignmentCandidate) -> dict:
                    candidate_asr = _transcribe_for_verification(
                        candidate_transcriber, candidate.audio, language, qwen_prompt)
                    return {**candidate_asr,
                            "comparison": compare_transcripts(
                                expected_text, candidate_asr["text"], min_repetitions=2)}

                selected_candidate, selected_candidate_transcript, candidate_selection = (
                    select_alignment_candidate(candidates, evaluate_candidate, candidate_config)
                )
            _release_stage_gpu_memory()
            audio = selected_candidate.audio
            candidate_selection["generation_errors"] = candidate_generation_errors
            alignment_audio = {**alignment_audio, "selected_candidate": selected_candidate.id,
                               "selected_candidate_type": selected_candidate.type}
            if candidate_config.keep_candidate_files:
                for candidate in candidates:
                    sf.write(output_dir / f"{stem}.alignment-candidate-{candidate.id}.wav",
                             candidate.audio, 16000, subtype="PCM_16")
        else:
            candidate_selection = {"enabled": False, "selected_candidate": "existing-pipeline",
                                   "reason": "feature-disabled", "candidates": []}
        vocal_activity = detect_vocal_activity(audio)
        enable_anchor_context = os.getenv("LRC_EXPERIMENTAL_ANCHOR_CONTEXT", "false").strip().lower() in {
            "1", "true", "yes", "on"
        }
        transcript_verification: dict = {"enabled": False}
        stable_ts_summary: dict = {"enabled": False, "reason": "Qwen-ASR ausreichend"}
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
                    and (untimed_input
                         or recognized_ratio < stable_min_ratio
                         or comparison["similarity"] < stable_min_similarity)):
                notify(56, ("Plain Lyrics benötigen einen zweiten Zeitgeber; Stable-ts prüft die Vocalspur"
                            if untimed_input else
                            "Qwen-ASR unvollständig; Stable-ts prüft die Vocalspur"))
                try:
                    stable = transcribe_stable(
                        audio, language, device, initial_prompt=stable_prompt)
                    # Alignment indices must refer to the timestamped word list.
                    # Stable-ts' regrouped segment text can contain a different
                    # token count and would shift every later word timestamp.
                    stable_word_text = " ".join(
                        str(word.get("word", "")) for word in stable.get("words", [])
                    )
                    stable_comparison = compare_transcripts(expected_text, stable_word_text)
                    stable_ts_summary = {"enabled": True, **stable,
                                         "comparison": stable_comparison}
                    qwen_rank = (comparison["matching_words"], comparison["similarity"])
                    stable_rank = (stable_comparison["matching_words"],
                                   stable_comparison["similarity"])
                    if stable_rank > qwen_rank:
                        transcript_verification = {
                            "enabled": True, "model": stable["model"],
                            "language": stable["language"], "text": stable["text"],
                            "comparison": stable_comparison,
                            "selected_from": "stable-ts",
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
        aligner = QwenWordAligner(device=device)
        alignment_attempts: list[dict] = []
        timed_repetition_pairs: list[dict] = []
        repetition_anchor_summary = {"blocks": 0, "words": 0, "rejected_blocks": 0,
                                     "method": "asr-structural-repetition-v1"}
        activity_repaired_timings = 0
        anchor_context_repairs = 0
        anchor_tail_repairs = 0
        try:
            if transcript_verification.get("enabled") and transcript_verification.get("text"):
                notify(57, "ASR-Wiederholungsblöcke erhalten akustische Zeitanker")
                requests = local_repetition_requests(
                    lines, transcript_verification["comparison"], len(audio) / 16000
                )
                for request in requests:
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
                    if untimed_input:
                        raise
                    # Timed LRC remains a safe fallback for unusually long songs
                    # or GPUs that cannot hold a complete track in one pass.
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
        finally:
            aligner.close()
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
        sustain_summary = extend_final_word_sustains(lines, vocal_activity)
        preliminary_onsets = validate_line_onsets(audio, lines)
        onset_refinements = apply_supported_onset_refinements(lines, preliminary_onsets)
        display_duration_summary = stabilize_acoustic_display_durations(lines)
        consensus_summary = reconcile_acoustic_boundaries(lines)
        all_vocals_selected = selected_candidate.type == "alternative-separator"
        missing_chorus_summary = (
            {"enabled": True, "recovered_lines": 0,
             "reason": "all-vocals-alignment-source-selected"}
            if all_vocals_selected else
            recover_missing_initial_chorus(
                separated_audio, instrumental_audio, lines, language, device,
                initial_prompt=stable_prompt)
        )
        # This is intentionally the last timing transformation. Chorus entries
        # missed by singing ASR must not drift away from their trusted LRC cue
        # again during a later onset or consensus pass.
        chorus_anchor_summary = (
            {"enabled": False, "reason": "all-vocals-acoustic-timing-selected",
             "shifted_lines": 0}
            if all_vocals_selected else apply_trusted_chorus_anchors(lines)
        )
        notify(88, "Überlappende Zeilenübergänge werden gemeinsam akustisch neu geprüft")
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
            notify(89, "Vocal-Ausschläge ohne Text werden in kleinen GPU-Fenstern untersucht")
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
        notify(91, "Silben werden in den Wortfenstern zeitlich eingeordnet")
        syllable_summary = enrich_lines_with_syllables(lines, language)
        if separator:
            detected_starts = [float(word["start"]) for line in lines for word in line.words]
            first_vocal_start = min(detected_starts) if detected_starts else None
            mute_before = max(0.0, first_vocal_start - 0.35) if first_vocal_start is not None else None
            ffmpeg_to_flac(separated.vocals, vocals_out, mute_before=mute_before)
        notify(90, "Alignment wird geprüft")

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
        "alignment_device": device,
        "input_timing": "plain-lyrics" if untimed_input else "line-synced-lrc",
        "timestamp_calibration": timestamp_calibration,
        "alignment_mode": alignment_selected,
        "alignment_attempts": alignment_attempts,
        "alignment_selected": alignment_selected,
        "transcript_verification": transcript_verification,
        "asr_prompt": asr_prompt_summary,
        "stable_ts": {**stable_ts_summary, "alignment": stable_alignment},
        "repetition_anchors": repetition_anchor_summary,
        "chorus_line_anchors": chorus_anchor_summary,
        "missing_instrumental_chorus": missing_chorus_summary,
        "missing_lyric_reanalysis": gap_reanalysis,
        "separation": "uvr-karaoke" if separator else "none",
        "alignment_audio": alignment_audio,
        "alignment_candidate_selection": candidate_selection,
        "output_lrc": lrc_out.name,
        "stems": stem_outputs,
        "syllable_alignment": syllable_summary,
        "ctc_alignment": ctc_summary,
        "mms_alignment": mms_summary,
        "easyaligner_alignment": easyaligner_summary,
        "sofa_alignment": sofa_summary,
        "alignment_consensus": consensus_summary,
        "line_overlap_reanalysis": overlap_reanalysis,
        "display_durations": display_duration_summary,
        "sustain_refinement": sustain_summary,
        "onset_validation": onset_summary,
        "repaired_collapsed_lines": summary["quality"]["geometrically_repaired_lines"],
        "historically_repaired_collapsed_lines": repaired_timings,
        "vocal_activity": {
            "regions": len(vocal_activity),
            "acoustically_repaired_lines": activity_repaired_timings,
            "anchor_context_repaired_runs": anchor_context_repairs,
            "anchor_tail_repaired_lines": anchor_tail_repairs,
            "method": "adaptive-rms-on-vocal-stem-v1",
        },
        "lyrics_completeness": completeness,
        "details": [
            {
                "timestamp": line.timestamp,
                "source_timestamp": line.source_timestamp,
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
            "version": 1,
            "source": audio_path.name,
            "separator_model": KARAOKE_MODEL,
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
