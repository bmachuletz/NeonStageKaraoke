from __future__ import annotations

import gc
import json
import math
import os
import re
import subprocess
import statistics
from typing import Iterable

import numpy as np

from .boundary_evidence import active_reference, banded_boundary_step
from .completeness import is_nonlexical_vocalization
from .micro_boundaries import IPA_VOWELS, VoicingTrack, refine_ipa_phone_path
from .model_loading import from_pretrained_local_first, hub_file_local_first


LANGUAGE_CODES = {
    "de": "de",
    "en": "en-us",
    "fr": "fr-fr",
    "es": "es",
    "it": "it",
    "pt": "pt",
    "nl": "nl",
}


def _display_words(text: str) -> list[str]:
    # German inclusive spellings such as ``Romantiker*innen`` or
    # ``Sänger:innen`` are one spoken word.  Treating the visual marker as a
    # token boundary gives the phoneme aligner one word more than the lyric
    # model and used to discard an otherwise useful complete sentence path.
    # Joining only markers *between* letters keeps ordinary punctuation and
    # line structure untouched.
    spoken = re.sub(r"(?<=[^\W_])[*:_·](?=[^\W_])", "", text.lower())
    return re.findall(r"[^\W_]+(?:['’][^\W_]+)?", spoken, re.UNICODE)


class PhonemeCtcAligner:
    """Multilingual IPA phone aligner used as a local boundary vote.

    eSpeak supplies the expected pronunciation. XLSR supplies frame-wise IPA
    probabilities, and CTC forced alignment chooses a monotone path through
    those probabilities. The model is intentionally used on short lyric-line
    windows, never as an unconstrained full-song transcription.
    """

    def __init__(self, language: str, device: str):
        import torch
        import torchaudio
        from transformers import AutoFeatureExtractor, Wav2Vec2ForCTC

        code = LANGUAGE_CODES.get(language.lower().split("-")[0])
        if code is None:
            raise ValueError(f"Kein eSpeak-Phonemcode für '{language}' konfiguriert.")
        if device == "auto":
            device = "cuda" if torch.cuda.is_available() else "cpu"
        self.torch = torch
        self.torchaudio = torchaudio
        self.device = device
        self.language_code = code
        self.model_id = os.getenv(
            "LRC_PHONEME_CTC_MODEL", "facebook/wav2vec2-xlsr-53-espeak-cv-ft")
        self.feature_extractor = from_pretrained_local_first(AutoFeatureExtractor, self.model_id)
        self.model = from_pretrained_local_first(
            Wav2Vec2ForCTC, self.model_id).to(device).eval()
        vocabulary_path = hub_file_local_first(self.model_id, "vocab.json")
        with open(vocabulary_path, encoding="utf-8") as vocabulary_file:
            self.vocabulary = json.load(vocabulary_file)
        self.blank = int(self.vocabulary["<pad>"])
        self.unknown = int(self.vocabulary["<unk>"])

    def close(self) -> None:
        del self.model
        gc.collect()
        if self.torch.cuda.is_available():
            self.torch.cuda.empty_cache()

    def _phones(self, word: str) -> tuple[list[str], list[int]]:
        # Keep the GPL eSpeak implementation behind its command-line boundary
        # instead of importing a GPL Python package into the Apache application.
        completed = subprocess.run(
            ["espeak-ng", "-q", "--ipa=1", "-v", self.language_code, word],
            check=True, capture_output=True, text=True, timeout=5)
        # eSpeak expands numbers and abbreviations into multiple spoken words.
        # Their word boundary is whitespace while ordinary phone boundaries
        # are underscores; both belong to the one displayed lyric token.
        phones = [
            phone.replace("ˈ", "").replace("ˌ", "").replace("\u200d", "")
            for phone in re.split(r"[_\s]+", completed.stdout.strip())
        ]
        phones = [phone for phone in phones if phone]
        ids = [int(self.vocabulary.get(phone, self.unknown)) for phone in phones]
        if not phones or any(token == self.unknown for token in ids):
            return [], []
        return phones, ids

    def align(self, audio, text: str, base_seconds: float) -> list[dict]:
        words = _display_words(text)
        if not words:
            return []
        target: list[int] = []
        phone_labels: list[str] = []
        word_ranges: list[tuple[int, int]] = []
        for word in words:
            phones, ids = self._phones(word.replace("’", "'"))
            if not ids:
                return []
            first = len(target)
            target.extend(ids)
            phone_labels.extend(phones)
            word_ranges.append((first, len(target)))

        inputs = self.feature_extractor(
            audio, sampling_rate=16000, return_tensors="pt").input_values.to(self.device)
        with self.torch.inference_mode():
            logits = self.model(inputs).logits
            log_probs = self.torch.log_softmax(logits, dim=-1)
            targets = self.torch.tensor([target], dtype=self.torch.int32, device=self.device)
            paths, scores = self.torchaudio.functional.forced_align(
                log_probs, targets, blank=self.blank)
        spans = self.torchaudio.functional.merge_tokens(
            paths[0].cpu(), scores[0].cpu(), self.blank)
        if len(spans) != len(target):
            return []
        frame_seconds = (len(audio) / 16000) / max(1, log_probs.shape[1])
        phones = [{
            "phone": label,
            "start": round(base_seconds + float(span.start) * frame_seconds, 3),
            "end": round(base_seconds + float(span.end) * frame_seconds, 3),
            "confidence": round(math.exp(float(span.score)), 4),
        } for label, span in zip(phone_labels, spans)]
        result = []
        for word, (first, end) in zip(words, word_ranges):
            selected = phones[first:end]
            confidence = sum(phone["confidence"] for phone in selected) / len(selected)
            result.append({
                "word": word,
                "start": selected[0]["start"],
                "end": selected[-1]["end"],
                "confidence": round(confidence, 4),
                "phonemes": selected,
            })
        return result

    def align_global(self, audio, lines: Iterable, *, chunk_seconds: float = 24.0,
                     context_seconds: float = 1.2) -> dict:
        """Place canonical lyrics on the complete track without timing anchors.

        XLSR inference is bounded to overlapping GPU windows. Only each
        window's context-free core is retained, then one CTC path is solved
        over the concatenated full-track posteriors and the complete known
        phoneme sequence. Consequently neither input line timestamps nor an
        ASR scaffold can bias the selected occurrence of a repeated phrase.
        """
        materialized = list(lines)
        target: list[int] = []
        phone_labels: list[str] = []
        word_ranges: list[tuple[int, int, int, str]] = []
        line_ranges: list[tuple[int, int]] = []
        for line_index, line in enumerate(materialized):
            displayed = _display_words(line.text)
            if not displayed:
                raise ValueError(
                    f"Zeile {line_index + 1} enthält keine aussprechbaren Wörter.")
            line_first = len(word_ranges)
            for word in displayed:
                phones, ids = self._phones(word.replace("’", "'"))
                if not ids:
                    raise ValueError(
                        f"Zeile {line_index + 1}: '{word}' ist nicht vollständig im "
                        "XLSR-Phonemvokabular enthalten.")
                first = len(target)
                target.extend(ids)
                phone_labels.extend(phones)
                word_ranges.append((first, len(target), line_index, word))
            line_ranges.append((line_first, len(word_ranges)))

        log_probs, frame_starts, frame_ends, chunks = self._global_emissions(
            audio, chunk_seconds=chunk_seconds, context_seconds=context_seconds)
        repeated = sum(left == right for left, right in zip(target, target[1:]))
        if log_probs.shape[0] < len(target) + repeated:
            raise ValueError(
                "Das Audiosignal enthält zu wenige CTC-Frames für den vollständigen Text.")
        targets = self.torch.tensor([target], dtype=self.torch.int32)
        paths, scores = self.torchaudio.functional.forced_align(
            log_probs.unsqueeze(0), targets, blank=self.blank)
        spans = self.torchaudio.functional.merge_tokens(
            paths[0].cpu(), scores[0].cpu(), self.blank)
        if len(spans) != len(target):
            raise ValueError(
                f"Der globale Phonempfad ist unvollständig ({len(spans)}/{len(target)}).")
        phones = []
        for label, span in zip(phone_labels, spans):
            first_frame = max(0, min(len(frame_starts) - 1, int(span.start)))
            last_frame = max(first_frame, min(len(frame_ends) - 1, int(span.end) - 1))
            phones.append({
                "phone": label,
                "start": round(float(frame_starts[first_frame]), 3),
                "end": round(float(frame_ends[last_frame]), 3),
                "confidence": round(math.exp(float(span.score)), 4),
            })

        words = []
        for first, end, line_index, word in word_ranges:
            selected = phones[first:end]
            words.append({
                "word": word,
                "line_index": line_index,
                "start": selected[0]["start"],
                "end": selected[-1]["end"],
                "confidence": round(statistics.mean(
                    phone["confidence"] for phone in selected), 4),
                "phonemes": selected,
            })
        return {
            "words": words,
            "line_ranges": line_ranges,
            "chunks": chunks,
            "frames": int(log_probs.shape[0]),
            "phonemes": len(target),
        }

    def _global_emissions(self, audio, *, chunk_seconds: float,
                          context_seconds: float):
        sample_rate = 16000
        core_samples = max(sample_rate, int(chunk_seconds * sample_rate))
        context_samples = max(0, int(context_seconds * sample_rate))
        probabilities = []
        frame_starts: list[float] = []
        frame_ends: list[float] = []
        chunks = 0
        for core_start in range(0, len(audio), core_samples):
            core_end = min(len(audio), core_start + core_samples)
            input_start = max(0, core_start - context_samples)
            input_end = min(len(audio), core_end + context_samples)
            chunk = audio[input_start:input_end]
            inputs = self.feature_extractor(
                chunk, sampling_rate=sample_rate,
                return_tensors="pt").input_values.to(self.device)
            with self.torch.inference_mode():
                logits = self.model(inputs).logits[0]
                local = self.torch.log_softmax(logits, dim=-1).detach().cpu()
            # Model frames are nearly uniform, but deriving their coordinates
            # from every real chunk avoids accumulated rounding drift.
            step = len(chunk) / sample_rate / max(1, local.shape[0])
            local_starts = (input_start / sample_rate
                            + np.arange(local.shape[0], dtype=np.float64) * step)
            local_ends = local_starts + step
            keep = ((local_starts >= core_start / sample_rate)
                    & (local_starts < core_end / sample_rate))
            kept_indices = np.flatnonzero(keep)
            if kept_indices.size:
                probabilities.append(local[kept_indices.tolist()])
                frame_starts.extend(local_starts[kept_indices].tolist())
                frame_ends.extend(np.minimum(
                    local_ends[kept_indices], core_end / sample_rate).tolist())
            del inputs, logits, local
            chunks += 1
        if not probabilities:
            raise ValueError("Für die Vollspur wurden keine XLSR-Frames erzeugt.")
        return self.torch.cat(probabilities, dim=0), frame_starts, frame_ends, chunks


def align_global_phoneme_path(audio, lines: Iterable, language: str, device: str,
                              *, chunk_seconds: float = 24.0,
                              context_seconds: float = 1.2) -> dict:
    """Replace line/word geometry with an anchor-free full-track IPA path."""
    materialized = list(lines)
    aligner = PhonemeCtcAligner(language, device)
    try:
        result = aligner.align_global(
            audio, materialized, chunk_seconds=chunk_seconds,
            context_seconds=context_seconds)
        confidences = []
        for line, (first, end) in zip(materialized, result["line_ranges"]):
            aligned_words = result["words"][first:end]
            if not aligned_words:
                raise ValueError("Der globale Phonempfad enthält eine leere Textzeile.")
            line.words = []
            for item in aligned_words:
                confidence = float(item["confidence"])
                confidences.append(confidence)
                line.words.append({
                    "word": item["word"],
                    "start": item["start"],
                    "end": item["end"],
                    "timing_source": "xlsr-espeak-global-primary",
                    "ctc_confidence": confidence,
                    "phoneme_confidence": confidence,
                    "phoneme_source": "xlsr-espeak-global-primary",
                    "phonemes": item["phonemes"],
                    "phoneme_word_start_candidate": item["start"],
                    "phoneme_word_end_candidate": item["end"],
                })
            line.timestamp = float(line.words[0]["start"])
            line.status = "aligned"
            line.reason = None
        return {
            "enabled": True,
            "method": "global-chunked-xlsr-espeak-ctc-v1",
            "model": aligner.model_id,
            "placed_lines": len(materialized),
            "placed_words": len(result["words"]),
            "phonemes": result["phonemes"],
            "frames": result["frames"],
            "chunks": result["chunks"],
            "mean_confidence": round(statistics.mean(confidences), 4),
            "uses_input_timestamps": False,
        }
    finally:
        aligner.close()


def annotate_phoneme_boundaries(
        audio,
        lines: Iterable,
        language: str,
        device: str,
        *,
        padding: float = 0.38,
        minimum_confidence: float = 0.18,
        maximum_edge_delta: float = 0.32,
        maximum_word_edge_delta: float = 0.18,
        promotion_minimum_confidence: float = 0.50,
        promotion_minimum_evidence: float = 0.60,
        promotion_minimum_shift: float = 0.018,
        promotion_maximum_shift: float = 0.14,
        micro_boundary_mode: str = "select",
        micro_search_radius: float = 0.055,
        micro_minimum_path_improvement: float = 0.08,
        voicing_track: VoicingTrack | None = None,
        repetition_pairs: list[dict] | None = None,
        stem_contrast_candidates: list[dict] | None = None,
) -> dict:
    """Attach IPA phone spans and promote independently supported onsets.

    IPA/CTC alone is not allowed to rewrite a trusted karaoke word window.
    Version 1.2 first searches a class-aware, duration-constrained micro path
    around the IPA boundaries.  A word onset is still promoted only when a
    separate short-time acoustic measurement supports the same boundary and
    the move remains monotonic.  Final word ends deliberately stay owned by
    the singing-sustain detector.
    """
    materialized = list(lines)
    candidates = [(index, line) for index, line in enumerate(materialized)
                  if line.words and not all(
                      word.get("phoneme_source") == "sofa-singing-alignment"
                      for word in line.words)]
    if not candidates:
        return {"enabled": True, "attempted_lines": 0, "accepted_lines": 0,
                "accepted_words": 0, "promoted_word_onsets": 0,
                "diagnostics": []}
    aligner = PhonemeCtcAligner(language, device)
    duration = len(audio) / 16000
    accepted_lines = accepted_words = promoted_onsets = 0
    collapsed_run_repairs = 0
    collapsed_words_repaired = 0
    vocal_hole_repairs = 0
    vocal_hole_words_repaired = 0
    delayed_phrase_repairs = 0
    delayed_phrase_words_repaired = 0
    delayed_first_word_repairs = 0
    reduced_connector_repairs = 0
    reduced_connector_words_repaired = 0
    clipped_tail_repairs = 0
    clipped_tail_words_repaired = 0
    local_interval_repairs = 0
    local_duration_inversion_repairs = 0
    local_duration_inversion_words = 0
    connected_blank_repairs = 0
    stem_contrast_release_repairs = 0
    cross_line_transition_repairs = 0
    isolated_internal_onset_repairs = 0
    coherent_late_phrase_repairs = 0
    syllable_only_phone_paths = 0
    micro_summary = {
        "enabled": micro_boundary_mode != "off", "mode": micro_boundary_mode,
        "method": "class-aware-multires-duration-viterbi-v1.2",
        "attempted_words": 0, "candidate_words": 0, "applied_words": 0,
        "moved_phone_onsets": 0, "lines": [],
    }
    diagnostics = []
    repetition_path_summary = {
        "method": "continuous-phrase-ipa-repetition-v1",
        "attempted_blocks": 0,
        "accepted_blocks": 0,
        "accepted_words": 0,
        "diagnostics": [],
    }
    post_vocalization_summary = {
        "method": "nonlexical-separator-lexical-restart-v1",
        "separators": 0, "adjusted_lines": 0, "adjusted_words": 0,
        "adjusted_separator_boundaries": 0, "diagnostics": [],
    }
    try:
        for line_index, line in candidates:
            original_start = float(line.words[0]["start"])
            original_end = float(line.words[-1]["end"])
            start = max(0.0, original_start - padding)
            # The single-lane overlap fallback may have clipped a real held
            # tail at the (possibly false) onset of the following line. Give
            # IPA enough right context to prove or disprove that boundary.
            end_padding = (max(padding, 1.25)
                           if line.words[-1].get("timing_source")
                           == "overlap-display-lane-fallback" else padding)
            end = min(duration, original_end + end_padding)
            if end - start < 0.12:
                continue
            aligned = aligner.align(
                audio[int(start * 16000):int(end * 16000)], line.text, start)
            if len(aligned) != len(line.words):
                diagnostics.append({"line": line_index + 1,
                                    "status": "word-count-or-oov-mismatch"})
                continue
            micro = refine_ipa_phone_path(
                audio, aligned, voicing_track, mode=micro_boundary_mode,
                search_radius=micro_search_radius,
                minimum_path_improvement=micro_minimum_path_improvement)
            delayed_first_word_repair = _repair_delayed_first_word_onset(
                audio, line, aligned)
            if delayed_first_word_repair is not None:
                delayed_first_word_repairs += 1
            # A weak final phone must not discard a strongly recognised
            # internal phrase. Recover a run only when IPA and an independent
            # local energy/spectral boundary agree on the delayed onset.
            delayed_repairs = _repair_delayed_ipa_phrase(audio, line, aligned)
            delayed_phrase_repairs += len(delayed_repairs)
            delayed_phrase_words_repaired += sum(
                len(repair["word_indices"]) for repair in delayed_repairs)
            connector_repairs = _repair_reduced_connector_after_sustain(
                audio, line, aligned)
            reduced_connector_repairs += len(connector_repairs)
            reduced_connector_words_repaired += sum(
                len(repair["word_indices"]) for repair in connector_repairs)
            tail_repairs = _repair_clipped_final_phrase(
                audio, line, aligned,
                materialized[line_index + 1]
                if line_index + 1 < len(materialized) else None)
            clipped_tail_repairs += len(tail_repairs)
            clipped_tail_words_repaired += sum(
                len(repair["word_indices"]) for repair in tail_repairs)
            cross_line_repairs = _repair_repetition_tail_cross_line_transition(
                audio, line, aligned,
                materialized[line_index + 1]
                if line_index + 1 < len(materialized) else None)
            cross_line_transition_repairs += len(cross_line_repairs)
            preliminary_word_verification = _verify_aligned_word_boundaries(
                audio, line, aligned, maximum_word_edge_delta)
            isolated_onset_repairs = _promote_isolated_supported_internal_onsets(
                line, aligned, preliminary_word_verification)
            isolated_internal_onset_repairs += len(isolated_onset_repairs)
            stem_release_repairs = _repair_final_release_from_stem_contrast(
                line_index, line, aligned, stem_contrast_candidates or [])
            stem_contrast_release_repairs += len(stem_release_repairs)
            duration_inversion_repairs = _repair_local_duration_inversion_pair(
                line, aligned)
            local_duration_inversion_repairs += len(duration_inversion_repairs)
            local_duration_inversion_words += sum(
                len(repair["word_indices"])
                for repair in duration_inversion_repairs)
            blank_repairs = _bridge_supported_connected_ipa_blanks(line, aligned)
            connected_blank_repairs += len(blank_repairs)
            syllable_only_phone_paths += _attach_syllable_only_phone_paths(
                line, aligned)
            confidence = sum(word["confidence"] for word in aligned) / len(aligned)
            start_delta = aligned[0]["start"] - original_start
            end_delta = aligned[-1]["end"] - original_end
            coherent_promotion = _promote_coherent_sentence_path(
                materialized, line_index, aligned,
                preliminary_word_verification, audio=audio)
            if coherent_promotion is not None:
                if (coherent_promotion.get("promotion_reason")
                        == "coherent-late-phrase-rescue"):
                    coherent_late_phrase_repairs += 1
                for word, phone_word in zip(line.words, aligned):
                    word["phonemes"] = phone_word["phonemes"]
                    word["phoneme_source"] = "xlsr-espeak-ctc"
                    word["phoneme_confidence"] = phone_word["confidence"]
                    word["phoneme_word_start_candidate"] = phone_word["start"]
                    word["phoneme_word_end_candidate"] = phone_word["end"]
                word_verification = _verify_aligned_word_boundaries(
                    audio, line, aligned, maximum_word_edge_delta)
                for word_index, word in enumerate(line.words):
                    verified = word_verification[word_index]
                    word["phoneme_word_verified"] = verified["verified"]
                    word["phoneme_word_verification"] = verified
                accepted_lines += 1
                accepted_words += len(line.words)
                diagnostics.append({
                    "line": line_index + 1,
                    "status": "accepted-coherent-sentence-path",
                    "mean_confidence": round(confidence, 4),
                    "start_delta_ms": round(start_delta * 1000, 1),
                    "end_delta_ms": round(end_delta * 1000, 1),
                    "coherent_sentence_promotion": coherent_promotion,
                    "word_verification": word_verification,
                })
                continue
            if confidence < minimum_confidence:
                diagnostics.append({
                    "line": line_index + 1, "status": "low-confidence",
                    "mean_confidence": round(confidence, 4),
                    "word_verification": preliminary_word_verification,
                })
                continue
            if abs(start_delta) > maximum_edge_delta or abs(end_delta) > maximum_edge_delta:
                diagnostics.append({
                    "line": line_index + 1, "status": "outside-trusted-line-window",
                    "mean_confidence": round(confidence, 4),
                    "start_delta_ms": round(start_delta * 1000, 1),
                    "end_delta_ms": round(end_delta * 1000, 1),
                    "delayed_ipa_phrase_repairs": delayed_repairs,
                    "delayed_first_word_onset_repair": delayed_first_word_repair,
                    "reduced_connector_repairs": connector_repairs,
                    "clipped_final_phrase_repairs": tail_repairs,
                    "cross_line_transition_repairs": cross_line_repairs,
                    "isolated_internal_onset_repairs": isolated_onset_repairs,
                    "local_duration_inversion_repairs": duration_inversion_repairs,
                    "stem_contrast_release_repairs": stem_release_repairs,
                    "connected_ipa_blank_repairs": blank_repairs,
                    "word_verification": preliminary_word_verification,
                })
                continue
            if any(right["start"] < left["end"]
                   for left, right in zip(aligned, aligned[1:])):
                diagnostics.append({"line": line_index + 1, "status": "nonmonotonic"})
                continue
            # Count a micro decision as applied only after the complete IPA
            # line passed the confidence, trusted-window and geometry gates.
            # Otherwise the diagnostic report would claim changes which were
            # discarded together with the temporary alignment candidate.
            for field in ("attempted_words", "candidate_words", "applied_words",
                          "moved_phone_onsets"):
                micro_summary[field] += int(micro.get(field, 0))
            micro_summary["lines"].append({"line": line_index + 1, **micro})
            collapsed_repairs = _repair_collapsed_ipa_runs(audio, line, aligned)
            hole_repairs = _repair_ipa_vocal_holes(audio, line, aligned)
            repaired_indices = {
                word_index
                for repair in (collapsed_repairs + hole_repairs
                               + delayed_repairs + connector_repairs)
                               + tail_repairs + duration_inversion_repairs
                               + stem_release_repairs + blank_repairs
                               + cross_line_repairs + isolated_onset_repairs
                for word_index in repair["word_indices"]
            }
            local_intervals = _promote_supported_local_word_intervals(
                audio, line, aligned, preliminary_word_verification)
            repaired_indices.update(repair["word_index"] for repair in local_intervals)
            local_interval_repairs += len(local_intervals)
            collapsed_run_repairs += len(collapsed_repairs)
            collapsed_words_repaired += len({
                word_index
                for repair in collapsed_repairs
                for word_index in repair["word_indices"]
            })
            vocal_hole_repairs += len(hole_repairs)
            vocal_hole_words_repaired += len({
                word_index
                for repair in hole_repairs
                for word_index in repair["word_indices"]
            })
            attached = 0
            rejected_words = 0
            for word_index, (word, phone_word) in enumerate(zip(line.words, aligned)):
                if word_index in repaired_indices:
                    attached += 1
                    continue
                if word.get("phoneme_source") == "sofa-singing-alignment":
                    continue
                word_start_delta = phone_word["start"] - float(word["start"])
                word_end_delta = phone_word["end"] - float(word["end"])
                if (phone_word["confidence"] < minimum_confidence
                        or abs(word_start_delta) > maximum_word_edge_delta
                        or abs(word_end_delta) > maximum_word_edge_delta):
                    rejected_words += 1
                    continue
                word["phonemes"] = phone_word["phonemes"]
                word["phoneme_source"] = "xlsr-espeak-ctc"
                word["phoneme_confidence"] = phone_word["confidence"]
                word["phoneme_word_start_candidate"] = phone_word["start"]
                word["phoneme_word_end_candidate"] = phone_word["end"]
                attached += 1
            promoted = _promote_supported_word_onsets(
                audio, line, aligned,
                minimum_start=_previous_line_end(materialized, line_index),
                minimum_confidence=promotion_minimum_confidence,
                minimum_evidence=promotion_minimum_evidence,
                minimum_shift=promotion_minimum_shift,
                maximum_shift=promotion_maximum_shift)
            promoted_onsets += len(promoted)
            # Repairs and onset promotion above mutate the actual line.  The
            # report and persisted proof must describe that final state, not
            # the pre-repair candidate against which the line was admitted.
            word_verification = _verify_aligned_word_boundaries(
                audio, line, aligned, maximum_word_edge_delta)
            for word_index, word in enumerate(line.words):
                if word_index >= len(word_verification):
                    break
                verified = word_verification[word_index]
                word["phoneme_word_verified"] = verified["verified"]
                word["phoneme_word_verification"] = verified
                if verified["start_evidence"].get("supported"):
                    word["phoneme_start_evidence"] = verified["start_evidence"]
                if verified["end_evidence"].get("supported"):
                    word["phoneme_end_evidence"] = verified["end_evidence"]
            accepted_lines += int(attached > 0)
            accepted_words += attached
            diagnostics.append({
                "line": line_index + 1,
                "status": ("accepted-boundary-vote" if attached
                           else "no-reliable-word-boundary-vote"),
                "mean_confidence": round(confidence, 4),
                "start_delta_ms": round(start_delta * 1000, 1),
                "end_delta_ms": round(end_delta * 1000, 1),
                "accepted_words": attached,
                "rejected_words": rejected_words,
                "promoted_onsets": promoted,
                "local_interval_repairs": local_intervals,
                "collapsed_ipa_repairs": collapsed_repairs,
                "ipa_vocal_hole_repairs": hole_repairs,
                "delayed_ipa_phrase_repairs": delayed_repairs,
                "reduced_connector_repairs": connector_repairs,
                "clipped_final_phrase_repairs": tail_repairs,
                "cross_line_transition_repairs": cross_line_repairs,
                "isolated_internal_onset_repairs": isolated_onset_repairs,
                "local_duration_inversion_repairs": duration_inversion_repairs,
                "stem_contrast_release_repairs": stem_release_repairs,
                "connected_ipa_blank_repairs": blank_repairs,
                "word_verification": word_verification,
            })
        if repetition_pairs:
            repetition_path_summary = _refine_repetition_phoneme_paths(
                aligner, audio, materialized, repetition_pairs)
        post_vocalization_summary = _refine_post_vocalization_lexical_restarts(
            aligner, audio, materialized, voicing_track=voicing_track,
            stem_contrast_candidates=stem_contrast_candidates,
            micro_boundary_mode=micro_boundary_mode,
            micro_search_radius=micro_search_radius,
            micro_minimum_path_improvement=micro_minimum_path_improvement)
    finally:
        aligner.close()
    return {
        "enabled": True,
        "model": aligner.model_id,
        "method": "local-sentence-ipa-word-verification-v1.3",
        "attempted_lines": len(candidates),
        "accepted_lines": accepted_lines,
        "accepted_words": accepted_words,
        "promoted_word_onsets": promoted_onsets,
        "collapsed_ipa_run_repairs": collapsed_run_repairs,
        "collapsed_ipa_words_repaired": collapsed_words_repaired,
        "ipa_vocal_hole_repairs": vocal_hole_repairs,
        "ipa_vocal_hole_words_repaired": vocal_hole_words_repaired,
        "delayed_ipa_phrase_repairs": delayed_phrase_repairs,
        "delayed_ipa_phrase_words_repaired": delayed_phrase_words_repaired,
        "delayed_first_word_onset_repairs": delayed_first_word_repairs,
        "reduced_connector_repairs": reduced_connector_repairs,
        "reduced_connector_words_repaired": reduced_connector_words_repaired,
        "clipped_final_phrase_repairs": clipped_tail_repairs,
        "clipped_final_phrase_words_repaired": clipped_tail_words_repaired,
        "supported_local_interval_repairs": local_interval_repairs,
        "local_duration_inversion_repairs": local_duration_inversion_repairs,
        "local_duration_inversion_words_repaired": local_duration_inversion_words,
        "stem_contrast_release_repairs": stem_contrast_release_repairs,
        "connected_ipa_blank_repairs": connected_blank_repairs,
        "cross_line_transition_repairs": cross_line_transition_repairs,
        "isolated_internal_onset_repairs": isolated_internal_onset_repairs,
        "coherent_late_phrase_repairs": coherent_late_phrase_repairs,
        "syllable_only_phone_paths": syllable_only_phone_paths,
        "micro_boundary_refinement": micro_summary,
        "minimum_confidence": minimum_confidence,
        "promotion_minimum_confidence": promotion_minimum_confidence,
        "promotion_minimum_evidence": promotion_minimum_evidence,
        "promotion_maximum_shift_ms": round(promotion_maximum_shift * 1000),
        "maximum_edge_delta_ms": round(maximum_edge_delta * 1000),
        "maximum_word_edge_delta_ms": round(maximum_word_edge_delta * 1000),
        "diagnostics": diagnostics,
        "continuous_repetition_paths": repetition_path_summary,
        "post_vocalization_lexical_restarts": post_vocalization_summary,
    }


def _refine_post_vocalization_lexical_restarts(
        aligner: PhonemeCtcAligner,
        audio,
        lines: list,
        *,
        stem_contrast_candidates: list[dict] | None = None,
        voicing_track: VoicingTrack | None = None,
        micro_boundary_mode: str = "select",
        micro_search_radius: float = 0.055,
        micro_minimum_path_improvement: float = 0.08,
        maximum_lines: int = 3,
        maximum_words: int = 22,
        maximum_gap: float = 2.0,
) -> dict:
    """Reset lexical timing after a written non-lexical sung call.

    Whole-song ASR commonly omits or paraphrases ``wohohohoh``/``lalala``.
    The following repeated lyric then inherits a window belonging to the
    preceding occurrence and every later onset appears to drift.  Re-align the
    small lexical block between two such calls as one monotone phone path.  The
    first line of a multi-line block remains the trusted contextual anchor;
    only later lines (or a single line directly after a call) may move.

    This is intentionally additive and conservative: a candidate needs several
    lexical phone anchors plus an independent acoustic edge, is bounded to the
    neighbouring call, and cannot move more than three seconds.  Ordinary held
    words are not classified as calls and therefore never enter this path.
    """
    duration = len(audio) / 16000
    separator_indices = [
        index for index, line in enumerate(lines)
        if len(getattr(line, "words", [])) == 1
        and _is_structural_vocalization_line(getattr(line, "text", ""))
    ]
    diagnostics: list[dict] = []
    adjusted_lines = adjusted_words = adjusted_separators = 0
    contrast_releases = {
        int(item["line"]): float(item["to"])
        for item in (stem_contrast_candidates or [])
        if item.get("line") is not None and item.get("to") is not None
    }
    for separator_index in separator_indices:
        block_indices: list[int] = []
        word_count = 0
        previous_end: float | None = None
        next_separator_index: int | None = None
        for line_index in range(separator_index + 1, len(lines)):
            line = lines[line_index]
            if (len(getattr(line, "words", [])) == 1
                    and _is_structural_vocalization_line(
                        getattr(line, "text", ""))):
                next_separator_index = line_index
                break
            if not getattr(line, "words", []):
                continue
            line_start = float(line.words[0]["start"])
            if previous_end is not None and line_start - previous_end >= maximum_gap:
                break
            if (len(block_indices) >= maximum_lines
                    or word_count + len(line.words) > maximum_words):
                break
            block_indices.append(line_index)
            word_count += len(line.words)
            previous_end = float(line.words[-1]["end"])
        if not block_indices:
            continue

        separator = lines[separator_index]
        separator_start = float(separator.words[0]["start"])
        window_start = max(0.0, separator_start - 0.25)
        if (next_separator_index is not None
                and next_separator_index == block_indices[-1] + 1):
            window_end = min(
                duration,
                float(lines[next_separator_index].words[0]["start"]) + 0.35)
            window_ends = [window_end]
        else:
            window_end = min(
                duration, float(lines[block_indices[-1]].words[-1]["end"]) + 1.8)
            # A long right tail gives CTC room to find delayed vocals, but may
            # also offer a later repeated phrase as an attractive false path.
            # Compete it against tighter views of the same restart.
            block_end = float(lines[block_indices[-1]].words[-1]["end"])
            window_ends = sorted({
                round(min(duration, block_end + padding), 3)
                for padding in (0.4, 1.0, 1.8)
            })
        transcript = " ".join(lines[index].text for index in block_indices)
        window_starts = sorted({
            round(max(0.0, separator_start + offset), 3)
            for offset in (-0.25, 0.15, 0.55)
        })
        # A repeated call can contain several strong internal attacks. A
        # window beginning at the written call onset may therefore force the
        # first lexical phone onto a late ``ho``/``la`` rather than onto the
        # following word. The paired vocal/instrumental pass independently
        # measures where that call loses vocal dominance. Add focused views
        # around this release; keep the broad views because calls can also
        # lead directly into the next word without a quiet boundary.
        separator_release = contrast_releases.get(separator_index + 1)
        if separator_release is not None:
            window_starts = sorted(set(window_starts) | {
                round(max(0.0, separator_release + offset), 3)
                for offset in (-0.45, -0.15, 0.15)
                if separator_release + offset < max(window_ends) - 0.4
            })
        context_words = (len(lines[block_indices[0]].words)
                         if len(block_indices) > 1 else 0)
        path_candidates: list[dict] = []
        received_counts: list[int] = []
        for candidate_start_window in window_starts:
            for candidate_end_window in window_ends:
                if candidate_end_window - candidate_start_window < 0.4:
                    continue
                candidate = aligner.align(
                    audio[int(candidate_start_window * 16000):
                          int(candidate_end_window * 16000)],
                    transcript, candidate_start_window)
                received_counts.append(len(candidate))
                if len(candidate) != word_count:
                    continue
                scored = candidate[context_words:]
                lexical_anchors = sum(
                    float(word.get("confidence", 0.0)) >= 0.25
                    for word in scored)
                mean_confidence = statistics.mean(
                    float(word.get("confidence", 0.0)) for word in scored)
                first_boundary = float(scored[0]["start"])
                onset_vote = _acoustic_boundary_evidence(
                    audio, first_boundary, "onset")
                transition_vote = _acoustic_boundary_evidence(
                    audio, first_boundary, "transition")
                edge_score = max(float(onset_vote.get("score", 0.0)),
                                 float(transition_vote.get("score", 0.0)))
                path_candidates.append({
                    "aligned": candidate,
                    "window_start": candidate_start_window,
                    "window_end": candidate_end_window,
                    "candidate_start": first_boundary,
                    "candidate_end": float(scored[-1]["end"]),
                    "lexical_anchors": lexical_anchors,
                    "mean_confidence": mean_confidence,
                    "edge_score": edge_score,
                })
        if not path_candidates:
            diagnostics.append({
                "separator_line": separator_index + 1,
                "status": "word-count-or-oov-mismatch",
                "expected_words": word_count,
                "received_words": received_counts,
            })
            continue
        # Anchor count is deliberately the primary discriminator. Among paths
        # with the same number of real lexical anchors, prefer the complete
        # phonetic likelihood before a generic acoustic edge. A sung call has
        # several strong attacks of its own; one of those must not win merely
        # because it looks like a word onset. The edge remains a gate below.
        selected_path = max(path_candidates, key=lambda item: (
            item["lexical_anchors"], item["mean_confidence"],
            item["edge_score"], -item["window_end"] + item["window_start"]))
        aligned = selected_path["aligned"]
        window_start = selected_path["window_start"]
        window_end = selected_path["window_end"]
        refine_ipa_phone_path(
            audio, aligned, voicing_track, mode=micro_boundary_mode,
            search_radius=micro_search_radius,
            minimum_path_improvement=micro_minimum_path_improvement)

        candidates_by_line: dict[int, list[dict]] = {}
        cursor = 0
        for line_index in block_indices:
            count = len(lines[line_index].words)
            candidates_by_line[line_index] = aligned[cursor:cursor + count]
            cursor += count

        target_indices = block_indices[1:] if len(block_indices) > 1 else block_indices
        applied_in_block: list[int] = []
        for line_index in target_indices:
            line = lines[line_index]
            candidates = candidates_by_line[line_index]
            candidate_start = float(candidates[0]["start"])
            candidate_end = float(candidates[-1]["end"])
            current_start = float(line.words[0]["start"])
            current_end = float(line.words[-1]["end"])
            current_duration = max(0.04, current_end - current_start)
            candidate_duration = candidate_end - candidate_start
            anchors = sum(
                float(word.get("confidence", 0.0)) >= 0.25
                for word in candidates)
            required_anchors = max(2, math.ceil(len(candidates) * 0.25))
            mean_confidence = statistics.mean(
                float(word.get("confidence", 0.0)) for word in candidates)
            onset = _acoustic_boundary_evidence(audio, candidate_start, "onset")
            transition = _acoustic_boundary_evidence(
                audio, candidate_start, "transition")
            edge_score = max(float(onset.get("score", 0.0)),
                             float(transition.get("score", 0.0)))
            previous_candidate_end = (
                float(candidates_by_line[block_indices[
                    block_indices.index(line_index) - 1]][-1]["end"])
                if block_indices.index(line_index) > 0
                else float(separator.words[0]["start"]) + 0.12)
            reliable = (
                mean_confidence >= 0.14
                and anchors >= required_anchors
                and edge_score >= 0.38
                and 0.42 <= candidate_duration / current_duration <= 2.20
                and abs(candidate_start - current_start) <= 3.0
                and abs(candidate_end - current_end) <= 3.0
                and candidate_start >= previous_candidate_end - 0.04
                and candidate_end <= window_end + 0.001)
            detail = {
                "line": line_index + 1,
                "text": line.text,
                "from_start": round(current_start, 3),
                "to_start": round(candidate_start, 3),
                "from_end": round(current_end, 3),
                "to_end": round(candidate_end, 3),
                "mean_confidence": round(mean_confidence, 4),
                "lexical_anchors": anchors,
                "required_anchors": required_anchors,
                "onset_evidence": onset,
                "transition_evidence": transition,
                "selected_window_start": window_start,
                "selected_window_end": window_end,
                "separator_release": separator_release,
                "window_candidates": [{
                    "start": item["window_start"],
                    "end": item["window_end"],
                    "candidate_start": round(item["candidate_start"], 3),
                    "candidate_end": round(item["candidate_end"], 3),
                    "lexical_anchors": item["lexical_anchors"],
                    "mean_confidence": round(item["mean_confidence"], 4),
                    "edge_score": round(item["edge_score"], 4),
                } for item in path_candidates],
                "status": "accepted" if reliable else "rejected",
            }
            diagnostics.append(detail)
            if not reliable:
                continue
            for word, candidate in zip(line.words, candidates):
                word["post_vocalization_original_start"] = round(
                    float(word["start"]), 3)
                word["post_vocalization_original_end"] = round(
                    float(word["end"]), 3)
                word["start"] = round(float(candidate["start"]), 3)
                word["end"] = round(float(candidate["end"]), 3)
                word["timing_source"] = "post-vocalization-lexical-restart"
                word["phonemes"] = candidate.get("phonemes", [])
                word["phoneme_source"] = "xlsr-espeak-ctc-vocalization-restart"
                word["phoneme_confidence"] = candidate.get("confidence")
                word["phoneme_word_start_candidate"] = word["start"]
                word["phoneme_word_end_candidate"] = word["end"]
            line.timestamp = candidate_start
            verification = _verify_aligned_word_boundaries(
                audio, line, candidates, maximum_word_edge_delta=0.18)
            for word, proof in zip(line.words, verification):
                word["phoneme_word_verified"] = proof["verified"]
                word["phoneme_word_verification"] = proof
            adjusted_lines += 1
            adjusted_words += len(line.words)
            applied_in_block.append(line_index)

        if not applied_in_block:
            continue
        first_applied = min(applied_in_block)
        if first_applied == separator_index + 1:
            target = float(lines[first_applied].words[0]["start"])
            separator_word = separator.words[-1]
            if float(separator_word["start"]) + 0.04 < target < float(separator_word["end"]):
                separator_word["post_vocalization_neighbor_original_end"] = round(
                    float(separator_word["end"]), 3)
                separator_word["end"] = round(target, 3)
                separator_word["timing_source"] = "nonlexical-vocalization-neighbor-boundary"
                adjusted_separators += 1
        last_applied = max(applied_in_block)
        if (next_separator_index is not None
                and next_separator_index == last_applied + 1):
            target = float(lines[last_applied].words[-1]["end"])
            separator_word = lines[next_separator_index].words[0]
            if float(separator_word["start"]) < target < float(separator_word["end"]) - 0.04:
                separator_word["post_vocalization_neighbor_original_start"] = round(
                    float(separator_word["start"]), 3)
                separator_word["start"] = round(target, 3)
                separator_word["timing_source"] = "nonlexical-vocalization-neighbor-boundary"
                lines[next_separator_index].timestamp = target
                adjusted_separators += 1
    return {
        "method": "nonlexical-separator-lexical-restart-v1",
        "separators": len(separator_indices),
        "adjusted_lines": adjusted_lines,
        "adjusted_words": adjusted_words,
        "adjusted_separator_boundaries": adjusted_separators,
        "diagnostics": diagnostics,
    }


def _is_structural_vocalization_line(text: str) -> bool:
    """Limit restart anchors to visibly extended/repeated sung calls.

    A short ``Oh`` or ``Yeah`` may be an intentional lexical line.  It remains
    a non-lexical vocalization for completeness checks, but is too ambiguous
    to reset neighbouring word timing.  Repeated/extended spellings provide
    the additional structural evidence required by this repair.
    """
    tokens = re.findall(r"[^\W_]+", str(text).casefold(), flags=re.UNICODE)
    return (len(tokens) == 1 and len(tokens[0]) >= 6
            and is_nonlexical_vocalization(text))


def _attach_syllable_only_phone_paths(
        line, aligned: list[dict], *,
        minimum_confidence: float = 0.28,
        maximum_start_delta: float = 0.16,
        maximum_end_delta: float = 0.70,
        maximum_relative_end_delta: float = 0.40) -> int:
    """Retain a local IPA path solely for internal syllable boundaries.

    A singer may hold the last vowel long after CTC has completed its lexical
    token. Rejecting that word as a *word-window* replacement is correct, but
    throwing away its well-supported internal phone onsets forces syllables
    back to equal-duration guesses. This stores the path in a separate field:
    it cannot move the word and is consumed only by syllable derivation.
    """
    if len(line.words) != len(aligned):
        return 0
    attached = 0
    for word, candidate in zip(line.words, aligned):
        if word.get("phonemes"):
            continue
        phones = candidate.get("phonemes")
        if not isinstance(phones, list) or len(phones) < 2:
            continue
        current_start = float(word["start"])
        current_end = float(word.get("end", current_start))
        candidate_start = float(candidate["start"])
        candidate_end = float(candidate["end"])
        duration = max(0.001, current_end - current_start)
        end_delta = abs(candidate_end - current_end)
        if (float(candidate.get("confidence", 0.0)) < minimum_confidence
                or abs(candidate_start - current_start) > maximum_start_delta
                or end_delta > maximum_end_delta
                or end_delta / duration > maximum_relative_end_delta
                or candidate_end <= current_start
                or candidate_start >= current_end):
            continue
        clipped = _clip_phonemes(phones, current_start, current_end)
        if len(clipped) < 2:
            continue
        word["syllable_phonemes"] = clipped
        word["syllable_phoneme_source"] = "xlsr-espeak-ctc-internal-only"
        word["syllable_phoneme_confidence"] = candidate.get("confidence")
        word["syllable_phoneme_candidate_start"] = round(candidate_start, 3)
        word["syllable_phoneme_candidate_end"] = round(candidate_end, 3)
        attached += 1
    return attached


def _promote_coherent_sentence_path(lines: list, line_index: int,
                                    aligned: list[dict],
                                    verification: list[dict],
                                    *, audio=None) -> dict | None:
    """Promote a complete constrained sentence when the baseline is clearly worse.

    Singing CTC probabilities are often numerically low even while the
    monotone forced path follows the right phonemes.  Treating the already
    suspect baseline as a hard 180/320 ms truth window made large errors
    impossible to repair.  This gate instead requires exact word cardinality,
    chronological geometry, neighbouring-line bounds, at least one lexical
    anchor and several independent acoustic edges.
    """
    line = lines[line_index]
    if len(aligned) != len(line.words) or not aligned:
        return None
    # Decoder-frame rounding can make two otherwise monotone IPA words overlap
    # by a few milliseconds.  Treat that as one shared boundary; a real
    # overlap still invalidates the path.  The old strict comparison discarded
    # complete, independently anchored sentence paths because of a 3 ms
    # rounding artefact.
    normalized_rounding_overlaps = 0
    for left, right in zip(aligned, aligned[1:]):
        overlap = float(left["end"]) - float(right["start"])
        if overlap <= 0:
            continue
        if overlap > 0.015:
            return None
        boundary = (float(left["end"]) + float(right["start"])) / 2
        left["end"] = round(boundary, 3)
        right["start"] = round(boundary, 3)
        normalized_rounding_overlaps += 1
    if any(float(word["end"]) - float(word["start"]) < 0.025 for word in aligned):
        return None
    deltas = [
        abs(float(candidate[edge]) - float(current[edge]))
        for current, candidate in zip(line.words, aligned)
        for edge in ("start", "end")
    ]
    median_disagreement = statistics.median(deltas)
    maximum_disagreement = max(deltas)
    candidate_start = float(aligned[0]["start"])
    candidate_start_delta = (float(aligned[0]["start"])
                             - float(line.words[0]["start"]))
    first_start_evidence = (verification[0].get("start_evidence", {})
                            if verification else {})
    lexical_anchors = sum(float(word.get("confidence", 0.0)) >= 0.30
                          for word in aligned)
    acoustic_anchors = sum(
        float(edge.get("score", 0.0)) >= 0.60
        for item in verification
        for edge in (item.get("start_evidence", {}), item.get("end_evidence", {}))
        if edge.get("supported"))
    # A stale LRC line edge can compress a complete phrase into the preceding
    # instrumental gap.  The forced IPA path may still be coherent while the
    # first (often reduced) word has a low CTC probability.  In that case the
    # old strong-first-token rescue is too strict.  Admit the complete path
    # only when most word starts move in the same later direction, the final
    # edge stays local, and an independent short-time measurement confirms
    # the proposed phrase onset.  This is intentionally later-only: it cannot
    # pull lyrics into an earlier vocal occurrence.
    signed_start_deltas = [
        float(candidate["start"]) - float(current["start"])
        for current, candidate in zip(line.words, aligned)
    ]
    coherently_late_words = sum(delta >= 0.12 for delta in signed_start_deltas)
    onset_vote = None
    if (audio is not None and 0.24 <= candidate_start_delta <= 2.20):
        onset_vote = _best_supported_boundary(
            audio, candidate_start, 0.12, 0.68)
    coherent_late_phrase_rescue = (
        0.24 <= candidate_start_delta <= 2.20
        and coherently_late_words >= math.ceil(len(aligned) * 0.50)
        and min(signed_start_deltas) >= -0.12
        and abs(float(aligned[-1]["end"])
                - float(line.words[-1]["end"])) <= 0.55
        and lexical_anchors >= max(2, math.ceil(len(aligned) * 0.25))
        and onset_vote is not None
    )
    # A single clipped/extended word end is not evidence that the complete
    # sentence path is wrong.  It used to promote whole otherwise-correct
    # lines whenever the final sustain differed by >550 ms.  Normal coherent
    # replacement therefore requires a majority of boundaries to disagree.
    #
    # Keep a separate escape hatch for a genuinely misplaced line onset (for
    # example a lyric token squeezed into a preceding vocal hole): the first
    # IPA word itself and an independent acoustic onset must both be unusually
    # strong.  This rejects plausible-looking paths into a neighbouring punk
    # phrase while still repairing multi-second first-word errors.
    majority_path = (median_disagreement >= 0.20
                     and abs(candidate_start_delta) <= 0.60)
    # Replacing a complete long sentence from a merely coherent CTC path is
    # unsafe when only one or two of its many acoustic edges are independently
    # visible.  This was able to overwrite otherwise accurate input timing in
    # dense singing. Require evidence proportional to sentence length for the
    # ordinary majority path. A severely malformed baseline (median error at
    # least 400 ms) retains the escape hatch, as its geometry itself proves
    # that preservation is not useful. Dedicated onset/duration rescues below
    # keep their stricter, purpose-built evidence rules.
    majority_required_acoustic_anchors = max(
        2, math.ceil(len(aligned) * 0.34))
    if (majority_path and median_disagreement < 0.40
            and acoustic_anchors < majority_required_acoustic_anchors):
        majority_path = False
    displaced_onset_rescue = (
        maximum_disagreement >= 0.55
        and abs(candidate_start_delta) >= 0.55
        and float(aligned[0].get("confidence", 0.0)) >= 0.60
        and bool(first_start_evidence.get("supported"))
        and float(first_start_evidence.get("score", 0.0)) >= 0.75
    )
    # A common bad baseline stretches a weak first function word across the
    # entire instrumental gap until the real phrase begins.  In that geometry
    # the IPA onset lands at the *old end* of the stretched word.  It can be
    # repaired even when that tiny word itself has low CTC confidence, but only
    # when several later lexical/acoustic boundaries confirm the same path.
    current_first_start = float(line.words[0]["start"])
    current_first_end = float(line.words[0]["end"])
    collapsed_first_word_rescue = (
        candidate_start_delta >= 0.55
        and current_first_end - current_first_start >= 0.65
        and float(aligned[0]["end"]) - float(aligned[0]["start"]) <= 0.35
        and abs(candidate_start - current_first_end) <= 0.05
        and lexical_anchors >= 3
        and acoustic_anchors >= 3
    )
    # A forced candidate may preserve both sentence edges while assigning the
    # duration of a content word to its short predecessor.  Median line error
    # deliberately does not notice such a local swap: most edges are still
    # close. Detect the characteristic *paired* distortion instead. One word
    # must be substantially longer than its IPA counterpart, its immediate
    # successor substantially shorter, and the two-word outer span must agree
    # with the independent monotone IPA path. This is a duration-conservation
    # rule, not a token- or song-specific connector heuristic.
    duration_inversion = None
    for index in range(len(aligned) - 1):
        current_left, current_right = line.words[index:index + 2]
        ipa_left, ipa_right = aligned[index:index + 2]
        current_left_duration = (float(current_left["end"])
                                 - float(current_left["start"]))
        current_right_duration = (float(current_right["end"])
                                  - float(current_right["start"]))
        ipa_left_duration = float(ipa_left["end"]) - float(ipa_left["start"])
        ipa_right_duration = float(ipa_right["end"]) - float(ipa_right["start"])
        excess = current_left_duration - ipa_left_duration
        deficit = ipa_right_duration - current_right_duration
        current_span = float(current_right["end"]) - float(current_left["start"])
        ipa_span = float(ipa_right["end"]) - float(ipa_left["start"])
        current_gap = (float(current_right["start"])
                       - float(current_left["end"]))
        internal_boundary_shift = abs(
            (float(ipa_left["end"]) + float(ipa_right["start"])) * 0.5
            - (float(current_left["end"]) + float(current_right["start"])) * 0.5)
        if (excess >= 0.24 and deficit >= 0.20
                and abs(excess - deficit) <= 0.18
                and current_gap <= 0.06
                and abs(current_span - ipa_span) <= 0.16
                and internal_boundary_shift >= 0.20
                and abs(float(ipa_left["start"])
                        - float(current_left["start"])) <= 0.16
                and abs(float(ipa_right["end"])
                        - float(current_right["end"])) <= 0.16
                and ipa_left_duration >= 0.07 and ipa_right_duration >= 0.12
                and float(ipa_left.get("confidence", 0.0)) >= 0.20
                and float(ipa_right.get("confidence", 0.0)) >= 0.25):
            duration_inversion = {
                "word_indices": [index, index + 1],
                "words": [current_left.get("word", ""),
                          current_right.get("word", "")],
                "excess_ms": round(excess * 1000, 1),
                "deficit_ms": round(deficit * 1000, 1),
                "span_delta_ms": round((ipa_span - current_span) * 1000, 1),
                "internal_boundary_shift_ms": round(
                    internal_boundary_shift * 1000, 1),
            }
            break
    local_duration_inversion_rescue = duration_inversion is not None
    if (not majority_path and not displaced_onset_rescue
            and not collapsed_first_word_rescue
            and not local_duration_inversion_rescue
            and not coherent_late_phrase_rescue):
        return None
    required_anchors = max(3, math.ceil(len(aligned) * 0.25))
    if lexical_anchors < 1 or lexical_anchors + acoustic_anchors < required_anchors:
        return None
    if coherent_late_phrase_rescue and onset_vote is not None:
        voted_start = float(onset_vote["time"])
        if voted_start < float(aligned[0]["end"]) - 0.025:
            aligned[0]["start"] = round(voted_start, 3)
            candidate_start = voted_start

    previous_end = _previous_line_end(lines, line_index)
    next_start = None
    for following in lines[line_index + 1:]:
        if following.words:
            next_start = float(following.words[0]["start"])
            break
    candidate_final_start = float(aligned[-1]["start"])
    if candidate_start < previous_end - 0.12:
        return None
    if next_start is not None and candidate_final_start >= next_start + 0.12:
        return None

    old_final_end = float(line.words[-1]["end"])
    duration_inversion_affects_final = bool(
        duration_inversion
        and duration_inversion["word_indices"][-1] == len(aligned) - 1)
    for index, (word, candidate) in enumerate(zip(line.words, aligned)):
        # These flags describe a boundary from an earlier geometry.  Once the
        # complete sentence is replaced by a newly verified IPA path they are
        # stale and would incorrectly block the final acoustic release pass.
        for stale_boundary_key in (
                "pre_stage_vocal_end", "stage_vocal_release_trim_ms",
                "stage_vocal_onset_trim_ms", "stage_vocal_onset_conflict_ms",
                "stage_vocal_onset_candidate", "stage_vocal_pickup_reflow"):
            word.pop(stale_boundary_key, None)
        word["start"] = round(float(candidate["start"]), 3)
        if index + 1 < len(aligned):
            word["end"] = round(float(candidate["end"]), 3)
        else:
            candidate_end = float(candidate["end"])
            upper = next_start - 0.02 if next_start is not None else candidate_end
            # Preserve an already plausible measured sustain, but never carry
            # a multi-second stale baseline tail into a newly accepted IPA
            # sentence.  The later tonal-release pass can extend the lexical
            # candidate again from actual connected singing.
            preserve_old_end = (
                old_final_end >= candidate_final_start + 0.04
                and old_final_end - candidate_end <= 0.55
                and not duration_inversion_affects_final
            )
            preserved = old_final_end if preserve_old_end else candidate_end
            word["end"] = round(max(candidate_final_start + 0.04,
                                    min(max(candidate_end, preserved), upper)), 3)
        word["timing_source"] = "coherent-sentence-ipa-path"
    line.timestamp = float(line.words[0]["start"])
    return {
        "words": len(aligned),
        "median_baseline_disagreement_ms": round(median_disagreement * 1000, 1),
        "maximum_baseline_disagreement_ms": round(maximum_disagreement * 1000, 1),
        "lexical_anchors": lexical_anchors,
        "acoustic_anchors": acoustic_anchors,
        "normalized_rounding_overlaps": normalized_rounding_overlaps,
        "promotion_reason": (
            "majority-boundary-disagreement" if majority_path
            else ("verified-displaced-onset" if displaced_onset_rescue
                  else ("collapsed-first-word-gap-rescue"
                        if collapsed_first_word_rescue
                        else ("local-duration-inversion-rescue"
                              if local_duration_inversion_rescue
                              else "coherent-late-phrase-rescue")))),
        "duration_inversion": duration_inversion,
        "coherent_late_words": coherently_late_words,
        "phrase_onset_vote": onset_vote,
        "majority_required_acoustic_anchors": majority_required_acoustic_anchors,
    }


def _promote_supported_local_word_intervals(
        audio, line, aligned: list[dict], verification: list[dict], *,
        minimum_start_evidence: float = 0.70,
        minimum_candidate_confidence: float = 0.06,
        maximum_start_shift: float = 0.14,
        maximum_end_shift: float = 0.45,
        minimum_removed_tail: float = 0.10,
        minimum_following_pause: float = 0.24,
        maximum_pause_activity_share: float = 0.22) -> list[dict]:
    """Trim a locally overlong consonant word when three signals agree.

    Singing CTC may assign a low lexical probability to a shouted word while
    still locating its phone path accurately.  A low score alone must not keep
    an old word stretched through a real pause.  This repair therefore needs
    all of: a close monotone IPA interval, a strong independent onset, a final
    consonant phone, and acoustically quiet space before the following word.
    It cannot extend words or jump across phrases.
    """
    if len(line.words) != len(aligned) or len(verification) != len(aligned):
        return []
    repairs: list[dict] = []
    for index in range(1, len(aligned) - 1):
        word = line.words[index]
        candidate = aligned[index]
        proof = verification[index]
        phones = candidate.get("phonemes") or []
        if not phones:
            continue
        final_phone = str(phones[-1].get("phone", ""))
        if any(character in IPA_VOWELS for character in final_phone):
            continue
        candidate_start = float(candidate["start"])
        candidate_end = float(candidate["end"])
        current_start = float(word["start"])
        current_end = float(word["end"])
        next_candidate_start = float(aligned[index + 1]["start"])
        next_current_start = float(line.words[index + 1]["start"])
        start_evidence = proof.get("start_evidence", {})
        pause_start = candidate_end + 0.035
        pause_end = min(next_candidate_start, next_current_start) - 0.035
        candidate_confidence = float(candidate.get("confidence", 0.0))
        close_lexical_onset = (
            candidate_confidence >= 0.35
            and abs(candidate_start - current_start) <= 0.06)
        independently_supported_onset = (
            start_evidence.get("supported")
            and float(start_evidence.get("score", 0.0)) >= minimum_start_evidence)
        if (candidate_confidence < minimum_candidate_confidence
                or not (independently_supported_onset or close_lexical_onset)
                or abs(candidate_start - current_start) > maximum_start_shift
                or abs(candidate_end - current_end) > maximum_end_shift
                or current_end - candidate_end < minimum_removed_tail
                or next_candidate_start - candidate_end < minimum_following_pause
                or candidate_start < float(line.words[index - 1]["end"])
                or candidate_end - candidate_start < 0.06
                or pause_end <= pause_start):
            continue
        activity_share = _vocal_activity_share(audio, pause_start, pause_end)
        if activity_share > maximum_pause_activity_share:
            continue
        old_start, old_end = current_start, current_end
        word["start"] = round(candidate_start, 3)
        word["end"] = round(candidate_end, 3)
        word["timing_source"] = "verified-local-ipa-interval"
        word["phonemes"] = candidate["phonemes"]
        word["phoneme_source"] = "xlsr-espeak-ctc"
        word["phoneme_confidence"] = candidate.get("confidence")
        word["phoneme_word_start_candidate"] = round(candidate_start, 3)
        word["phoneme_word_end_candidate"] = round(candidate_end, 3)
        word["phoneme_start_evidence"] = start_evidence
        word["phoneme_release_locked"] = True
        repairs.append({
            "word_index": index,
            "word": word.get("word", ""),
            "old_start": round(old_start, 3),
            "old_end": round(old_end, 3),
            "new_start": round(candidate_start, 3),
            "new_end": round(candidate_end, 3),
            "quiet_gap_activity_share": round(activity_share, 4),
            "start_evidence_score": round(float(start_evidence.get("score", 0.0)), 4),
            "onset_support": ("independent-acoustic"
                              if independently_supported_onset
                              else "close-high-confidence-ipa"),
        })
    if repairs and line.words:
        line.timestamp = float(line.words[0]["start"])
    return repairs


def _repair_local_duration_inversion_pair(
        line, aligned: list[dict], *,
        minimum_excess: float = 0.24,
        minimum_deficit: float = 0.20,
        maximum_outer_disagreement: float = 0.18,
        maximum_candidate_blank: float = 0.18,
        minimum_boundary_shift: float = 0.20) -> list[dict]:
    """Repair one swapped word-duration boundary without replacing a line.

    A forced ASR path can give a short word most of its successor's duration.
    When the complete two-word span and a monotone IPA path agree, move only
    their shared boundary. The CTC blank is split between the two words; this
    is the neutral boundary for connected singing and avoids inventing a
    silent karaoke hole.
    """
    if len(line.words) != len(aligned) or len(aligned) < 2:
        return []
    repairs = []
    occupied: set[int] = set()
    for index in range(len(aligned) - 1):
        if index in occupied or index + 1 in occupied:
            continue
        left, right = line.words[index:index + 2]
        ipa_left, ipa_right = aligned[index:index + 2]
        left_start = float(left["start"])
        left_end = float(left["end"])
        right_start = float(right["start"])
        right_end = float(right["end"])
        ipa_left_start = float(ipa_left["start"])
        ipa_left_end = float(ipa_left["end"])
        ipa_right_start = float(ipa_right["start"])
        ipa_right_end = float(ipa_right["end"])
        excess = (left_end - left_start) - (ipa_left_end - ipa_left_start)
        deficit = (ipa_right_end - ipa_right_start) - (right_end - right_start)
        current_gap = right_start - left_end
        ipa_blank = ipa_right_start - ipa_left_end
        old_boundary = (left_end + right_start) / 2
        new_boundary = (ipa_left_end + ipa_right_start) / 2
        if (excess < minimum_excess or deficit < minimum_deficit
                or abs(excess - deficit) > maximum_outer_disagreement
                # This repair moves a misplaced shared edge. An existing
                # pause instead supports a real held left word and must not be
                # collapsed into the successor without separate evidence.
                or current_gap > 0.06
                or not 0.0 <= ipa_blank <= maximum_candidate_blank
                or abs(new_boundary - old_boundary) < minimum_boundary_shift
                or abs(ipa_left_start - left_start) > 0.08
                or abs(ipa_right_end - right_end) > maximum_outer_disagreement
                or float(ipa_left.get("confidence", 0.0)) < 0.18
                or float(ipa_right.get("confidence", 0.0)) < 0.35
                or new_boundary - left_start < 0.06
                or right_end - new_boundary < 0.06):
            continue
        left["duration_inversion_original_end"] = round(left_end, 3)
        right["duration_inversion_original_start"] = round(right_start, 3)
        left["end"] = right["start"] = round(new_boundary, 3)
        for word, candidate in ((left, ipa_left), (right, ipa_right)):
            word["timing_source"] = "verified-local-ipa-duration-inversion"
            word["phonemes"] = _clip_phonemes(
                candidate.get("phonemes", []),
                float(word["start"]), float(word["end"]))
            word["phoneme_source"] = "xlsr-espeak-ctc-duration-inversion"
            word["phoneme_confidence"] = candidate.get("confidence")
            word["phoneme_word_start_candidate"] = round(
                float(candidate["start"]), 3)
            word["phoneme_word_end_candidate"] = round(
                float(candidate["end"]), 3)
        repairs.append({
            "word_indices": [index, index + 1],
            "words": [left.get("word", ""), right.get("word", "")],
            "old_boundary": round(old_boundary, 3),
            "new_boundary": round(new_boundary, 3),
            "boundary_shift_ms": round((new_boundary - old_boundary) * 1000, 1),
            "left_excess_ms": round(excess * 1000, 1),
            "right_deficit_ms": round(deficit * 1000, 1),
            "ipa_blank_ms": round(ipa_blank * 1000, 1),
            "source": "local-ipa-duration-conservation-v1",
        })
        occupied.update((index, index + 1))
    return repairs


def _repair_final_release_from_stem_contrast(
        line_index: int, line, aligned: list[dict], candidates: list[dict], *,
        minimum_ipa_confidence: float = 0.35,
        maximum_ipa_disagreement: float = 0.20,
        minimum_trim: float = 0.10) -> list[dict]:
    """Apply a stem-contrast release only with compatible IPA linguistics.

    Tonal separator residue can look voiced to pitch trackers, while a quiet
    genuinely held vowel can look like accompaniment to a raw stem-ratio
    detector. Requiring the independent monotone IPA path to end nearby and
    on a consonant gives the two observations complementary failure modes.
    The rule is deliberately local and shortening-only.
    """
    if not line.words or len(line.words) != len(aligned):
        return []
    evidence = next((item for item in candidates
                     if int(item.get("line", -1)) == line_index + 1), None)
    if evidence is None:
        return []
    word = line.words[-1]
    ipa = aligned[-1]
    phones = ipa.get("phonemes") or []
    if not phones:
        return []
    final_phone = str(phones[-1].get("phone", ""))
    # A held final vowel is exactly where stem dominance is least reliable;
    # it can be quiet but still musically intentional.
    if any(character in IPA_VOWELS for character in final_phone):
        return []
    candidate_end = float(evidence["to"])
    ipa_end = float(ipa["end"])
    ipa_confidence = float(ipa.get("confidence", 0.0))
    current_end = float(word["end"])
    start = float(word["start"])
    if (ipa_confidence < minimum_ipa_confidence
            or abs(candidate_end - ipa_end) > maximum_ipa_disagreement
            or current_end - candidate_end < minimum_trim
            or candidate_end - start < 0.04):
        return []
    # A very strong terminal-phone path is temporally sharper than the 40 ms
    # stem-analysis window. Let it win only inside the independently confirmed
    # decay region; weaker IPA releases remain advisory because instruments in
    # separator residue can attract a plausible final consonant.
    selected_end = (ipa_end
                    if ipa_confidence >= 0.80
                    and 0.0 <= candidate_end - ipa_end <= 0.12
                    else candidate_end)
    word["pre_stem_contrast_end"] = round(current_end, 3)
    word["end"] = round(selected_end, 3)
    word["stem_contrast_release_trim_ms"] = round(
        (current_end - selected_end) * 1000, 1)
    word["stem_contrast_release"] = {
        key: value for key, value in evidence.items()
        if key not in {"line", "word", "from", "to", "trim_ms"}
    }
    word["stem_contrast_ipa_end"] = round(ipa_end, 3)
    word["stem_contrast_terminal_phone"] = final_phone
    word["phoneme_release_locked"] = True
    return [{
        "word_indices": [len(line.words) - 1],
        "word": word.get("word", ""),
        "from": round(current_end, 3),
        "to": round(selected_end, 3),
        "stem_candidate": round(candidate_end, 3),
        "ipa_end": round(ipa_end, 3),
        "ipa_disagreement_ms": round((candidate_end - ipa_end) * 1000, 1),
        "ipa_confidence": round(ipa_confidence, 4),
        "selected_boundary": ("high-confidence-terminal-ipa"
                              if selected_end == ipa_end else "stem-contrast"),
        "terminal_phone": final_phone,
        "source": "paired-stem-plus-terminal-ipa-v1",
    }]


def _bridge_supported_connected_ipa_blanks(
        line, aligned: list[dict], *,
        minimum_current_gap: float = 0.06,
        maximum_current_gap: float = 0.14,
        maximum_ipa_blank: float = 0.06) -> list[dict]:
    """Keep a word highlighted through a decoder blank in connected singing.

    CTC reserves short blank frames between lexical tokens. They are useful
    for decoding but are not necessarily a sung pause. When both adjacent IPA
    words confidently agree with the existing edges and their blank is at
    most 60 ms, extend only the left display end to the already trusted next
    onset. No onset moves and real pauses remain untouched.
    """
    if len(line.words) != len(aligned):
        return []
    repairs = []
    for index in range(len(line.words) - 1):
        left, right = line.words[index:index + 2]
        ipa_left, ipa_right = aligned[index:index + 2]
        current_gap = float(right["start"]) - float(left["end"])
        ipa_gap = float(ipa_right["start"]) - float(ipa_left["end"])
        if (not minimum_current_gap <= current_gap <= maximum_current_gap
                or not 0.0 <= ipa_gap <= maximum_ipa_blank
                or abs(float(ipa_left["end"]) - float(left["end"])) > 0.08
                or abs(float(ipa_right["start"]) - float(right["start"])) > 0.04
                or float(ipa_left.get("confidence", 0.0)) < 0.25
                or float(ipa_right.get("confidence", 0.0)) < 0.25):
            continue
        old_end = float(left["end"])
        left["end"] = round(float(right["start"]), 3)
        left["connected_ipa_blank_original_end"] = round(old_end, 3)
        left["timing_source"] = "connected-ipa-display-bridge"
        repairs.append({
            "word_indices": [index],
            "words": [left.get("word", ""), right.get("word", "")],
            "from": round(old_end, 3),
            "to": round(float(right["start"]), 3),
            "bridged_ms": round(current_gap * 1000, 1),
            "ipa_blank_ms": round(ipa_gap * 1000, 1),
            "source": "connected-ipa-blank-display-bridge-v1",
        })
    return repairs


def _refine_repetition_phoneme_paths(aligner: PhonemeCtcAligner, audio,
                                     lines: list, pairs: list[dict]) -> dict:
    flattened = [word for line in lines for word in line.words]
    diagnostics = []
    accepted_blocks = accepted_words = 0
    duration = len(audio) / 16000
    for pair in pairs:
        expected = pair.get("expected", {})
        first, end = int(expected.get("start", -1)), int(expected.get("end", -1))
        trailing = max(1, int(pair.get("trailing_words_in_line", 0)))
        context_end = min(len(flattened), end + trailing)
        if first < 0 or end <= first or context_end < end or context_end > len(flattened):
            diagnostics.append({"status": "missing-word-context"})
            continue
        selected = flattened[first:context_end]
        transcript = " ".join(str(word.get("word", "")) for word in selected)
        repeated_count = end - first
        stable_start = float(pair["audio_start"])
        stable_end = float(pair["audio_end"])
        unit_words = max(1, int(expected.get("unit_words", 1)))
        trailing_end = float(selected[-1].get("end", stable_end))
        window_end = min(duration, max(stable_end + 2.5, trailing_end + 1.0))
        window_starts = sorted({
            round(max(0.0, stable_start + offset), 3)
            for offset in (-0.55, -0.15, 0.25, 0.65)
            if stable_start + offset < window_end - 0.4
        })
        path_candidates = []
        mismatched_counts = []
        for window_start in window_starts:
            candidate = aligner.align(
                audio[int(window_start * 16000):int(window_end * 16000)],
                transcript, window_start)
            if len(candidate) != len(selected):
                mismatched_counts.append(len(candidate))
                continue
            repeated_candidate = candidate[:repeated_count]
            unit_onsets = [float(repeated_candidate[index]["start"])
                           for index in range(0, repeated_count, unit_words)]
            intervals = [right - left
                         for left, right in zip(unit_onsets, unit_onsets[1:])]
            cadence = (statistics.pstdev(intervals) / statistics.mean(intervals)
                       if len(intervals) >= 2 and statistics.mean(intervals) > 0
                       else 0.0)
            onset = _acoustic_boundary_evidence(
                audio, float(repeated_candidate[0]["start"]), "onset")
            edge_distance = float(repeated_candidate[0]["start"]) - window_start
            reliable = (
                repeated_count >= unit_words * 3
                and context_end > end
                and cadence <= 0.30
                and bool(onset.get("supported"))
                and float(onset.get("score", 0.0)) >= 0.60
                and edge_distance >= 0.08
            )
            mean_confidence = statistics.mean(
                float(word.get("confidence", 0.0))
                for word in repeated_candidate)
            path_candidates.append({
                "aligned": candidate,
                "window_start": window_start,
                "candidate_start": float(repeated_candidate[0]["start"]),
                "candidate_end": float(repeated_candidate[-1]["end"]),
                "cadence_cv": cadence,
                "first_onset": onset,
                "edge_distance": edge_distance,
                "mean_confidence": mean_confidence,
                "reliable": reliable,
            })
        if not path_candidates:
            diagnostics.append({"status": "word-count-mismatch",
                                "expected": len(selected),
                                "received": mismatched_counts})
            continue
        # A reliable onset/cadence vote wins first. Among equally reliable
        # windows prefer the path nearest the structural Stable anchor; this
        # prevents jumping by one whole refrain repetition.
        chosen = max(path_candidates, key=lambda item: (
            int(item["reliable"]),
            -abs(item["candidate_start"] - stable_start),
            float(item["first_onset"].get("score", 0.0)),
            -item["cadence_cv"], item["mean_confidence"],
        ))
        aligned = chosen["aligned"]
        repeated = aligned[:repeated_count]
        start_delta = (float(repeated[0]["start"])
                       - stable_start)
        end_delta = (float(repeated[-1]["end"])
                     - stable_end)
        outside_stable_context = abs(start_delta) > 1.0 or abs(end_delta) > 1.0
        # Stable-TS can hallucinate one additional call in a dense chorus and
        # the canonical collapse then points at the preceding vocal tail.  Do
        # not make that noisy anchor a veto over a complete monotone phoneme
        # path.  A path outside the Stable window is admitted only for a real
        # repeated structure with trailing lexical context, regular cadence,
        # and a separately measured strong onset at its first call.
        cadence_cv = chosen["cadence_cv"]
        first_onset = chosen["first_onset"]
        independent_repetition_path = (
            repeated_count >= unit_words * 3
            and context_end > end
            and cadence_cv <= 0.30
            and bool(first_onset.get("supported"))
            and float(first_onset.get("score", 0.0)) >= 0.60
            and chosen["edge_distance"] >= 0.08
        )
        if outside_stable_context and not independent_repetition_path:
            diagnostics.append({
                "status": "outside-stable-ts-context",
                "candidate_start": round(float(repeated[0]["start"]), 3),
                "candidate_end": round(float(repeated[-1]["end"]), 3),
                "stable_start_delta_ms": round(start_delta * 1000),
                "stable_end_delta_ms": round(end_delta * 1000),
                "cadence_cv": round(cadence_cv, 4),
                "first_onset": first_onset,
                "window_candidates": _summarize_repetition_windows(path_candidates),
            })
            continue
        measured = []
        for index, word in enumerate(repeated):
            next_start = (float(aligned[index + 1]["start"])
                          if index + 1 < len(aligned) else float(word["end"]))
            own_end = float(word["end"])
            # Repeated sung calls usually hold until the following attack.
            # Only bridge a bounded local gap; genuine pauses stay untouched.
            if own_end > next_start:
                # The context word owns its onset.  A CTC phone may extend a
                # few frames past it, but that must never create overlapping
                # karaoke words at a display-line boundary.
                end_time = max(float(word["start"]) + 0.04, next_start)
            else:
                end_time = (next_start
                            if next_start - own_end <= 0.65 else own_end)
            measured.append({"start": round(float(word["start"]), 3),
                             "end": round(end_time, 3)})
        pair["audio_start"] = measured[0]["start"]
        pair["audio_end"] = measured[-1]["end"]
        pair["audio_words"] = measured
        pair["timestamp_method"] = "continuous-phrase-ipa-repetition-v1"
        pair["phoneme_path_mean_confidence"] = round(
            sum(float(word.get("confidence", 0.0)) for word in repeated)
            / len(repeated), 4)
        accepted_blocks += 1
        accepted_words += len(measured)
        diagnostics.append({"status": "accepted", "words": len(measured),
                            "start": measured[0]["start"], "end": measured[-1]["end"],
                            "stable_context_overridden": outside_stable_context,
                            "cadence_cv": round(cadence_cv, 4),
                            "first_onset": first_onset,
                            "selected_window_start": chosen["window_start"],
                            "window_candidates": _summarize_repetition_windows(
                                path_candidates)})
    return {"method": "continuous-phrase-ipa-repetition-v1",
            "attempted_blocks": len(pairs), "accepted_blocks": accepted_blocks,
            "accepted_words": accepted_words, "diagnostics": diagnostics}


def _summarize_repetition_windows(candidates: list[dict]) -> list[dict]:
    return [{
        "window_start": item["window_start"],
        "candidate_start": round(item["candidate_start"], 3),
        "candidate_end": round(item["candidate_end"], 3),
        "cadence_cv": round(item["cadence_cv"], 4),
        "onset_score": round(float(item["first_onset"].get("score", 0.0)), 4),
        "onset_supported": bool(item["first_onset"].get("supported")),
        "edge_distance_ms": round(item["edge_distance"] * 1000),
        "mean_confidence": round(item["mean_confidence"], 4),
        "reliable": bool(item["reliable"]),
    } for item in candidates]


def _repair_delayed_ipa_phrase(
        audio,
        line,
        aligned: list[dict],
        *,
        minimum_first_shift: float = 0.22,
        minimum_following_shift: float = 0.16,
        maximum_shift: float = 0.75,
        minimum_first_confidence: float = 0.62,
        minimum_following_confidence: float = 0.12,
        minimum_boundary_evidence: float = 0.68,
        boundary_search_radius: float = 0.12,
        minimum_word_duration: float = 0.07,
) -> list[dict]:
    """Recover a late internal phrase hidden by one weak line-edge phone.

    Singing recognisers sometimes compress the tail of a line: the first
    words remain correct while two or more following words are placed too
    early.  The IPA path can locate that run precisely, yet the traditional
    all-or-nothing line gate rejects it when the last word has a weak release.
    This repair is deliberately atomic and conservative: at least two words
    must agree on a late shift, the first word needs a strong IPA score, and a
    separately measured acoustic boundary must confirm its onset.  The trusted
    existing run end is retained, so a poor final phone cannot stretch the
    line or create an overlap.
    """
    if len(line.words) != len(aligned) or len(line.words) < 3:
        return []
    repairs: list[dict] = []
    cursor = 1
    while cursor < len(line.words) - 1:
        current = line.words[cursor]
        candidate = aligned[cursor]
        first_shift = float(candidate["start"]) - float(current["start"])
        if (not minimum_first_shift <= first_shift <= maximum_shift
                or float(candidate.get("confidence", 0.0)) < minimum_first_confidence):
            cursor += 1
            continue
        end = cursor + 1
        while end < len(line.words):
            following_shift = (float(aligned[end]["start"])
                               - float(line.words[end]["start"]))
            if (not minimum_following_shift <= following_shift <= maximum_shift
                    or float(aligned[end].get("confidence", 0.0))
                    < minimum_following_confidence):
                break
            end += 1
        if end - cursor < 2:
            cursor += 1
            continue
        acoustic = _best_supported_boundary(
            audio, float(candidate["start"]), boundary_search_radius,
            minimum_boundary_evidence)
        if acoustic is None:
            cursor = end
            continue
        run_end = float(line.words[end - 1]["end"])
        if abs(float(aligned[end - 1]["end"]) - run_end) > 0.45:
            cursor = end
            continue
        starts = [float(acoustic["time"])] + [
            float(aligned[index]["start"])
            for index in range(cursor + 1, end)
        ]
        boundaries = [*starts, run_end]
        if (any(right - left < minimum_word_duration
                for left, right in zip(boundaries, boundaries[1:]))
                or starts[0] < float(line.words[cursor - 1]["end"])):
            cursor = end
            continue
        previous = line.words[cursor - 1]
        previous_end = float(previous["end"])
        if (starts[0] - previous_end <= maximum_shift
                and _vocal_activity_share(audio, previous_end, starts[0]) >= 0.55):
            previous["ipa_delayed_phrase_original_end"] = round(previous_end, 3)
            previous["end"] = round(starts[0], 3)
            previous["sustain_extension_ms"] = round((starts[0] - previous_end) * 1000)
        detail = {
            "words": [line.words[index].get("word", "")
                      for index in range(cursor, end)],
            "word_indices": list(range(cursor, end)),
            "old_start": round(float(line.words[cursor]["start"]), 3),
            "new_start": round(starts[0], 3),
            "retained_end": round(run_end, 3),
            "ipa_start": round(float(candidate["start"]), 3),
            "boundary_evidence": acoustic["evidence"],
            "source": "ipa-ctc-plus-independent-onset",
        }
        for offset, index in enumerate(range(cursor, end)):
            word = line.words[index]
            phone_word = aligned[index]
            start, stop = boundaries[offset], boundaries[offset + 1]
            word["ipa_delayed_phrase_original_start"] = round(float(word["start"]), 3)
            word["ipa_delayed_phrase_original_end"] = round(float(word["end"]), 3)
            word["start"] = round(start, 3)
            word["end"] = round(stop, 3)
            word["timing_source"] = "ipa-delayed-phrase-repair"
            word["phonemes"] = _clip_phonemes(
                phone_word.get("phonemes", []), start, stop)
            word["phoneme_source"] = "xlsr-espeak-ctc-delayed-phrase"
            word["phoneme_confidence"] = phone_word.get("confidence")
            word["phoneme_word_start_candidate"] = round(
                float(phone_word["start"]), 3)
            word["phoneme_word_end_candidate"] = round(
                float(phone_word["end"]), 3)
        repairs.append(detail)
        cursor = end
    return repairs


def _repair_delayed_first_word_onset(
        audio,
        line,
        aligned: list[dict],
        *,
        minimum_stretched_duration: float = 0.65,
        minimum_removed_prefix: float = 0.45,
        maximum_remaining_duration: float = 0.24,
        maximum_candidate_end_delta: float = 0.25,
        minimum_candidate_confidence: float = 0.35,
        minimum_onset_evidence: float = 0.75,
        minimum_following_anchors: int = 2,
) -> dict | None:
    """Trim a false prefix from a stretched first word, never shift the line.

    A preceding sung tail can be mistaken for the first word of the next
    sentence when line windows touch. IPA may still locate the real lexical
    onset near the end of that oversized word. Only that onset is promoted:
    all following word boundaries and the trusted first-word release remain
    unchanged. This needs an independently measured onset and several later
    IPA anchors, preventing a lone CTC guess from moving a karaoke phrase.
    """
    if len(line.words) != len(aligned) or len(aligned) < 3:
        return None
    current = line.words[0]
    candidate = aligned[0]
    old_start = float(current["start"])
    old_end = float(current["end"])
    candidate_start = float(candidate["start"])
    candidate_end = float(candidate["end"])
    if (old_end - old_start < minimum_stretched_duration
            or candidate_start - old_start < min(minimum_removed_prefix, 0.35)
            or not old_start < candidate_start < old_end
            or abs(candidate_end - old_end) > maximum_candidate_end_delta):
        return None
    following_anchors = sum(
        float(item.get("confidence", 0.0)) >= 0.15
        and -0.18 <= float(item["start"]) - float(word["start"]) <= 0.55
        for word, item in zip(line.words[1:], aligned[1:])
    )
    if following_anchors < minimum_following_anchors:
        return None
    candidate_confidence = float(candidate.get("confidence", 0.0))
    compact_candidate = (
        candidate_start - old_start >= minimum_removed_prefix
        and old_end - candidate_start <= maximum_remaining_duration)
    # A longer first word (for example a three-syllable German word) can have
    # a perfectly measured lexical release even though it is not a tiny
    # pickup.  The late onset is still safe when IPA confidence is useful, the
    # release stays at the old independently measured edge and a local onset
    # detector confirms it. This generalises the compact-word repair without
    # imposing a language- or token-specific duration.
    coherent_release_candidate = (
        candidate_start - old_start >= 0.35
        and candidate_end - candidate_start <= 1.20
        and abs(candidate_end - old_end) <= 0.10
        and candidate_confidence >= 0.30)
    acoustic_threshold = (0.68 if coherent_release_candidate
                          else minimum_onset_evidence)
    acoustic = (_best_supported_boundary(
        audio, candidate_start, 0.12, acoustic_threshold)
        if candidate_confidence >= (0.30 if coherent_release_candidate
                                    else minimum_candidate_confidence)
        else None)
    end_evidence = _acoustic_boundary_evidence(audio, candidate_end, "release")
    duration_informed_end_rescue = (
        following_anchors >= max(4, minimum_following_anchors)
        and candidate_end - candidate_start <= 0.28
        and bool(end_evidence.get("supported"))
        and float(end_evidence.get("score", 0.0)) >= 0.72)
    # In connected/legato singing there may be no energy onset at all. Accept
    # a compact late first word only if its end agrees tightly with the old
    # release and the following IPA word starts at the existing next-word
    # boundary. The complete following path has already supplied at least two
    # lexical anchors above, so a lone CTC peak cannot trigger this rescue.
    legato_release_geometry = (
        candidate_start - old_start >= 0.35
        and candidate_end - candidate_start <= 0.38
        and abs(candidate_end - old_end) <= 0.08
        and candidate_confidence >= 0.30
        and abs(float(aligned[1]["start"])
                - float(line.words[1]["start"])) <= 0.08)
    if (acoustic is None and not duration_informed_end_rescue
            and not legato_release_geometry):
        return None
    new_start = (float(acoustic["time"]) if acoustic is not None else candidate_start)
    new_end = (candidate_end if (duration_informed_end_rescue
                                 or legato_release_geometry) else old_end)
    maximum_new_duration = (1.20 if coherent_release_candidate and acoustic is not None
                            else (0.38 if legato_release_geometry
                                  else maximum_remaining_duration))
    if (new_start - old_start < min(minimum_removed_prefix, 0.35)
            or new_end - new_start < 0.04
            or new_end - new_start > maximum_new_duration):
        return None
    current["ipa_delayed_first_word_original_start"] = round(old_start, 6)
    current["start"] = round(new_start, 6)
    current["end"] = round(new_end, 6)
    current["timing_source"] = "ipa-delayed-first-word-onset"
    current["phonemes"] = _clip_phonemes(
        candidate.get("phonemes", []), new_start, new_end)
    current["phoneme_source"] = "xlsr-espeak-ctc-delayed-first-word"
    current["phoneme_confidence"] = candidate.get("confidence")
    current["phoneme_word_start_candidate"] = round(candidate_start, 3)
    current["phoneme_word_end_candidate"] = round(candidate_end, 3)
    line.timestamp = new_start
    return {
        "word": current.get("word", ""),
        "old_start": round(old_start, 6),
        "new_start": round(new_start, 6),
        "old_end": round(old_end, 6),
        "new_end": round(new_end, 6),
        "ipa_start": round(candidate_start, 3),
        "ipa_end": round(candidate_end, 3),
        "following_anchors": following_anchors,
        "boundary_evidence": (acoustic["evidence"] if acoustic is not None
                              else end_evidence),
        "source": (
            "ipa-ctc-plus-independent-onset-and-following-anchors"
            if acoustic is not None else
            ("legato-release-geometry-plus-following-ipa-anchors"
             if legato_release_geometry else
             "duration-informed-ipa-plus-independent-release-and-following-anchors")),
    }


def _best_supported_boundary(audio, candidate: float, radius: float,
                             minimum_score: float) -> dict | None:
    """Find an independent onset/transition vote close to an IPA onset."""
    best = None
    for offset in np.arange(-radius, radius + 0.0001, 0.01):
        boundary = candidate + float(offset)
        for kind in ("onset", "transition"):
            evidence = _acoustic_boundary_evidence(audio, boundary, kind)
            score = float(evidence.get("score", 0.0))
            if (evidence.get("supported") and score >= minimum_score
                    and (best is None or score > best["score"])):
                best = {"time": round(boundary, 3), "score": score,
                        "evidence": evidence}
    return best


def _repair_reduced_connector_after_sustain(
        audio,
        line,
        aligned: list[dict],
        *,
        minimum_current_connector_duration: float = 0.65,
        minimum_late_shift: float = 0.45,
        maximum_late_shift: float = 1.8,
        maximum_connector_confidence: float = 0.20,
        minimum_following_confidence: float = 0.18,
        connector_display_duration: float = 0.10,
        maximum_following_onset_delta: float = 0.30,
) -> list[dict]:
    """Repair a reduced connector after a held vowel or non-lexical tail.

    In phrases such as ``away and play`` a forced word model can allocate the
    long held /a/ to ``and`` and start the following content word too early.
    A full-context IPA path still locates the tiny reduced connector close to
    the lexical restart, although its label confidence is naturally low.  Use
    that onset only when the following content word independently stays near
    its existing start.  The connector receives a short anticipation window;
    the preceding held word owns the remaining sung vowel.
    """
    if len(line.words) != len(aligned) or len(line.words) < 3:
        return []
    connectors = {"and", "&", "und"}
    repairs = []
    for index in range(1, len(line.words) - 1):
        previous, connector, following = (
            line.words[index - 1], line.words[index], line.words[index + 1])
        candidate = aligned[index]
        following_candidate = aligned[index + 1]
        token = str(connector.get("word", "")).strip(".,!?;:'\"()[]{}").lower()
        if token not in connectors:
            continue
        connector_start, connector_end = (
            float(connector["start"]), float(connector["end"]))
        candidate_start = float(candidate["start"])
        shift = candidate_start - connector_start
        if (connector_end - connector_start < minimum_current_connector_duration
                or not minimum_late_shift <= shift <= maximum_late_shift
                or float(candidate.get("confidence", 1.0)) > maximum_connector_confidence
                or float(following_candidate.get("confidence", 0.0))
                < minimum_following_confidence
                or abs(float(following_candidate["start"])
                       - float(following["start"])) > maximum_following_onset_delta
                or not connector_start < candidate_start <= connector_end + 0.12):
            continue
        # The preceding word must already be a measured sustain and the IPA
        # path must show a real held interval before the reduced connector.
        lexical_end = float(previous.get("acoustic_end", previous["end"]))
        candidate_previous_end = float(aligned[index - 1]["end"])
        if ("sustain_release_confidence" not in previous
                or candidate_start - max(lexical_end, candidate_previous_end) < 0.45):
            continue
        new_connector_start = candidate_start - connector_display_duration
        if new_connector_start <= float(previous["start"]):
            continue
        old_previous_end = float(previous["end"])
        old_following_start = float(following["start"])
        previous["end"] = round(new_connector_start, 3)
        previous["reduced_connector_original_end"] = round(old_previous_end, 3)
        previous["sustain_extension_ms"] = round(
            (new_connector_start - lexical_end) * 1000)
        connector["start"] = round(new_connector_start, 3)
        connector["end"] = round(candidate_start, 3)
        connector["timing_source"] = "ipa-reduced-connector-repair"
        following["start"] = round(candidate_start, 3)
        following["timing_source"] = "ipa-reduced-connector-repair"
        for word, phone_word in ((connector, candidate),
                                 (following, following_candidate)):
            word["phoneme_source"] = "xlsr-espeak-ctc-reduced-connector"
            word["phoneme_confidence"] = phone_word.get("confidence")
            word["phoneme_word_start_candidate"] = round(
                float(phone_word["start"]), 3)
        repairs.append({
            "words": [previous.get("word", ""), connector.get("word", ""),
                      following.get("word", "")],
            "word_indices": [index - 1, index, index + 1],
            "old_previous_end": round(old_previous_end, 3),
            "new_previous_end": round(new_connector_start, 3),
            "old_connector_start": round(connector_start, 3),
            "new_connector_start": round(new_connector_start, 3),
            "new_connector_end": round(candidate_start, 3),
            "old_following_start": round(old_following_start, 3),
            "new_following_start": round(candidate_start, 3),
            "source": "full-context-ipa-reduced-connector-v1",
        })
    return repairs


def _promote_isolated_supported_internal_onsets(
        line,
        aligned: list[dict],
        verification: list[dict],
        *,
        minimum_shift: float = 0.18,
        maximum_shift: float = 1.50,
        minimum_confidence: float = 0.35,
        minimum_evidence: float = 0.68,
) -> list[dict]:
    """Keep a strong local word onset even when the whole line is rejected.

    A held sung word can make the sentence-level IPA end disagree by more than
    the trusted line window.  That must not discard a later internal onset
    which is independently visible in the audio.  Only later moves are
    allowed: they open space for the preceding vowel sustain and cannot pull a
    lyric into an earlier vocal event.
    """
    if len(line.words) != len(aligned) or len(verification) != len(aligned):
        return []
    repairs = []
    for index in range(1, len(line.words)):
        word = line.words[index]
        candidate = aligned[index]
        evidence = verification[index].get("start_evidence", {})
        old_start = float(word["start"])
        candidate_start = float(candidate["start"])
        shift = candidate_start - old_start
        if (not minimum_shift <= shift <= maximum_shift
                or float(candidate.get("confidence", 0.0)) < minimum_confidence
                or not evidence.get("supported")
                or float(evidence.get("score", 0.0)) < minimum_evidence
                or candidate_start >= float(word["end"]) - 0.04
                or candidate_start < float(line.words[index - 1]["end"]) + 0.04):
            continue
        word["isolated_ipa_onset_original_start"] = round(old_start, 3)
        word["start"] = round(candidate_start, 3)
        word["timing_source"] = "isolated-supported-ipa-onset"
        word["phoneme_start_evidence"] = evidence
        repairs.append({
            "word_indices": [index],
            "word": word.get("word", ""),
            "from": round(old_start, 3),
            "to": round(candidate_start, 3),
            "shift_ms": round(shift * 1000, 1),
            "ipa_confidence": round(float(candidate.get("confidence", 0.0)), 4),
            "onset_evidence": evidence,
            "source": "isolated-ipa-plus-independent-onset-v1",
        })
    return repairs


def _repair_repetition_tail_cross_line_transition(
        audio,
        line,
        aligned: list[dict],
        following_line=None,
        *,
        minimum_extension: float = 0.12,
        maximum_extension: float = 0.65,
        minimum_confidence: float = 0.08,
) -> list[dict]:
    """Restore a quiet line transition after a structurally anchored refrain.

    The repeated part and its lexical tail are deliberately estimated by
    different models.  If the provisional next line starts inside the tail,
    the display-lane fallback used to compress the last refrain word.  A local
    final-phone end plus a strong low-energy spectral transition provides a
    safer shared boundary for both lines.
    """
    if (not line.words or len(line.words) != len(aligned)
            or following_line is None
            or not getattr(following_line, "words", None)
            or not any(word.get("timing_source") == "asr-repetition-anchor"
                       for word in line.words[:-1])):
        return []
    final = line.words[-1]
    candidate = aligned[-1]
    following = following_line.words[0]
    old_end = float(final["end"])
    candidate_start = float(candidate["start"])
    candidate_end = float(candidate["end"])
    extension = candidate_end - old_end
    if (not minimum_extension <= extension <= maximum_extension
            or float(candidate.get("confidence", 0.0)) < minimum_confidence
            or abs(candidate_start - float(final["start"])) > 0.18
            or float(following["start"]) > old_end + 0.04
            or candidate_end >= float(following["end"]) - 0.04):
        return []
    evidence = _acoustic_boundary_evidence(audio, candidate_end, "transition")
    # A line may continue without an energy onset.  In that case the spectral
    # envelope change is the independent observation; the IPA model supplies
    # the linguistic identity, so neither signal is trusted alone.
    if (float(evidence.get("spectral_distance", 0.0)) < 0.26
            or float(evidence.get("score", 0.0)) < 0.42):
        return []
    old_following_start = float(following["start"])
    final["cross_line_transition_original_end"] = round(old_end, 3)
    final["end"] = round(candidate_end, 3)
    final["timing_source"] = "repetition-tail-cross-line-transition"
    final["phoneme_release_locked"] = True
    following["cross_line_transition_original_start"] = round(
        old_following_start, 3)
    following["start"] = round(candidate_end, 3)
    following["timing_source"] = "cross-line-transition-onset"
    following_line.timestamp = round(candidate_end, 3)
    return [{
        "word_indices": [len(line.words) - 1],
        "word": final.get("word", ""),
        "from": round(old_end, 3),
        "to": round(candidate_end, 3),
        "following_word": following.get("word", ""),
        "following_from": round(old_following_start, 3),
        "following_to": round(candidate_end, 3),
        "transition_evidence": evidence,
        "source": "repetition-tail-ipa-spectral-transition-v1",
    }]


def _repair_clipped_single_final_word(
        audio,
        line,
        aligned: list[dict],
        following_line=None,
        *,
        minimum_extension: float = 0.20,
        maximum_extension: float = 1.10,
        minimum_confidence: float = 0.45,
) -> list[dict]:
    """Restore one clipped final word and relocate a false successor onset."""
    if (not line.words or len(line.words) != len(aligned)
            or following_line is None
            or len(getattr(following_line, "words", [])) < 2):
        return []
    final = line.words[-1]
    candidate = aligned[-1]
    if final.get("timing_source") != "overlap-display-lane-fallback":
        return []
    old_end = float(final["end"])
    candidate_start = float(candidate["start"])
    candidate_end = float(candidate["end"])
    extension = candidate_end - old_end
    if (not minimum_extension <= extension <= maximum_extension
            or float(candidate.get("confidence", 0.0)) < minimum_confidence
            or abs(candidate_start - float(final["start"])) > 0.10):
        return []
    first, second = following_line.words[:2]
    if float(first["start"]) >= candidate_end:
        return []
    successor = _first_supported_onset(
        audio, candidate_end + 0.04,
        min(candidate_end + 1.25, float(second["start"]) + 0.14), .68)
    if successor is None or float(successor["time"]) <= candidate_end + 0.04:
        return []
    successor_start = float(successor["time"])
    old_first_start = float(first["start"])
    old_first_end = float(first["end"])
    old_duration = max(0.04, old_first_end - old_first_start)
    final["clipped_single_original_end"] = round(old_end, 3)
    final["end"] = round(candidate_end, 3)
    final["timing_source"] = "clipped-final-word-repair"
    final["phoneme_release_locked"] = True
    first["cross_line_orphan_original_start"] = round(old_first_start, 3)
    first["cross_line_orphan_original_end"] = round(old_first_end, 3)
    first["start"] = round(successor_start, 3)
    if old_first_end <= successor_start + 0.04:
        first["end"] = round(min(float(second["start"]) - 0.02,
                                 successor_start + old_duration), 3)
    first["timing_source"] = "cross-line-acoustic-onset-repair"
    following_line.timestamp = round(successor_start, 3)
    return [{
        "words": [final.get("word", "")],
        "word_indices": [len(line.words) - 1],
        "old_final_end": round(old_end, 3),
        "new_final_end": round(candidate_end, 3),
        "successor_start": round(successor_start, 3),
        "successor_onset_evidence": successor["evidence"],
        "source": "single-final-ipa-cross-line-acoustic-repair-v1",
    }]


def _repair_clipped_final_phrase(
        audio,
        line,
        aligned: list[dict],
        following_line=None,
        *,
        minimum_extension: float = 0.35,
        maximum_extension: float = 1.55,
        minimum_confidence: float = 0.45,
        onset_search_radius: float = 0.30,
        release_padding: float = 0.03,
) -> list[dict]:
    """Restore a real line tail clipped by a false successor onset.

    Complementary-stem ASR occasionally maps a tiny first word from the next
    line into the held final word of the previous line. The display-lane
    fallback then cuts the previous tail at that false onset. An expanded
    full-context IPA path, a separately measured onset for the penultimate
    word and a separately measured successor onset must all agree before this
    repair changes either line.
    """
    single_word_repair = _repair_clipped_single_final_word(
        audio, line, aligned, following_line)
    if single_word_repair:
        return single_word_repair
    if (len(line.words) < 2 or len(line.words) != len(aligned)
            or line.words[-1].get("timing_source")
            != "overlap-display-lane-fallback"):
        return []
    penultimate, final = line.words[-2], line.words[-1]
    penultimate_candidate, final_candidate = aligned[-2], aligned[-1]
    old_final_end = float(final["end"])
    candidate_end = float(final_candidate["end"])
    extension = candidate_end - old_final_end
    if (not minimum_extension <= extension <= maximum_extension
            or float(penultimate_candidate.get("confidence", 0.0))
            < minimum_confidence
            or float(final_candidate.get("confidence", 0.0))
            < minimum_confidence):
        return []
    onset = _best_supported_boundary(
        audio, float(penultimate_candidate["start"]), onset_search_radius, .68)
    if onset is None:
        return []
    previous_end = (float(line.words[-3]["end"])
                    if len(line.words) >= 3 else float(line.words[0]["start"]))
    phrase_start = float(onset["time"])
    if not previous_end <= phrase_start < candidate_end:
        return []
    penultimate_duration = max(.07, float(penultimate["end"])
                               - float(penultimate["start"]))
    phrase_boundary = min(candidate_end - .07,
                          phrase_start + penultimate_duration)
    restored_end = candidate_end + release_padding
    successor = None
    if following_line is not None and len(getattr(following_line, "words", [])) >= 2:
        first, second = following_line.words[0], following_line.words[1]
        internal_gap = float(second["start"]) - float(first["end"])
        if float(first["start"]) < restored_end and internal_gap >= .45:
            successor = _first_supported_onset(
                audio, max(restored_end + .04, float(second["start"]) - .30),
                float(second["start"]) + .14, .68)
            if successor is None:
                return []
            successor_start = float(successor["time"])
            if successor_start <= restored_end:
                return []
            first["cross_line_orphan_original_start"] = round(float(first["start"]), 3)
            first["cross_line_orphan_original_end"] = round(float(first["end"]), 3)
            first["start"] = round(successor_start, 3)
            minimum_first_end = successor_start + .04
            if float(second["start"]) < minimum_first_end:
                second["cross_line_orphan_original_start"] = round(
                    float(second["start"]), 3)
                second["start"] = round(minimum_first_end, 3)
            first["end"] = round(float(second["start"]), 3)
            first["timing_source"] = "cross-line-acoustic-onset-repair"
            following_line.timestamp = round(successor_start, 3)
    old_values = {
        "penultimate_start": float(penultimate["start"]),
        "penultimate_end": float(penultimate["end"]),
        "final_start": float(final["start"]),
        "final_end": old_final_end,
    }
    penultimate["start"] = round(phrase_start, 3)
    penultimate["end"] = round(phrase_boundary, 3)
    penultimate["timing_source"] = "clipped-final-phrase-repair"
    final["start"] = round(phrase_boundary, 3)
    final["end"] = round(restored_end, 3)
    final["timing_source"] = "clipped-final-phrase-repair"
    for word, candidate in ((penultimate, penultimate_candidate),
                            (final, final_candidate)):
        word["phoneme_source"] = "xlsr-espeak-ctc-clipped-tail"
        word["phoneme_confidence"] = candidate.get("confidence")
        word["phoneme_word_start_candidate"] = round(
            float(candidate["start"]), 3)
        word["phoneme_word_end_candidate"] = round(float(candidate["end"]), 3)
    return [{
        "words": [penultimate.get("word", ""), final.get("word", "")],
        "word_indices": [len(line.words) - 2, len(line.words) - 1],
        "old": {key: round(value, 3) for key, value in old_values.items()},
        "new_penultimate_start": round(phrase_start, 3),
        "new_boundary": round(phrase_boundary, 3),
        "new_final_end": round(restored_end, 3),
        "successor_start": (round(float(successor["time"]), 3)
                            if successor else None),
        "tail_onset_evidence": onset["evidence"],
        "successor_onset_evidence": successor["evidence"] if successor else None,
        "source": "expanded-ipa-cross-line-acoustic-repair-v1",
    }]


def _first_supported_onset(audio, start: float, end: float,
                           minimum_score: float) -> dict | None:
    """Return the earliest independently supported onset in a bounded gap."""
    for boundary in np.arange(start, end + .0001, .01):
        evidence = _acoustic_boundary_evidence(audio, float(boundary), "onset")
        if evidence.get("supported") and float(evidence.get("score", 0.0)) >= minimum_score:
            return {"time": round(float(boundary), 3), "evidence": evidence}
    return None


def _repair_collapsed_ipa_runs(
        audio,
        line,
        aligned: list[dict],
        *,
        maximum_current_duration: float = 0.09,
        minimum_candidate_duration: float = 0.16,
        minimum_run_expansion: float = 0.16,
        maximum_boundary_shift: float = 0.45,
        minimum_activity_share: float = 0.55,
) -> list[dict]:
    """Repair adjacent decoder-frame collapses as one bounded phrase.

    A single 60--90 ms function word can be sung legitimately and therefore
    remains protected.  Two or more adjacent words which all collapsed to one
    or two decoder frames are different: when the IPA path fills independently
    measured vocal activity between reliable neighbours, the complete run is
    adopted atomically.  This also lets a low-confidence sung vowel inherit the
    trustworthy geometry of the surrounding phrase without trusting its label
    probability in isolation.
    """
    if len(line.words) != len(aligned) or len(line.words) < 2:
        return []
    suspicious = []
    for word, phone_word in zip(line.words, aligned):
        current_start = float(word["start"])
        current_end = float(word["end"])
        candidate_start = float(phone_word["start"])
        candidate_end = float(phone_word["end"])
        current_duration = current_end - current_start
        candidate_duration = candidate_end - candidate_start
        suspicious.append(
            0 < current_duration <= maximum_current_duration
            and candidate_duration >= minimum_candidate_duration
            and candidate_duration >= current_duration * 1.75
            and abs(candidate_start - current_start) <= maximum_boundary_shift
            and abs(candidate_end - current_end) <= maximum_boundary_shift)
    repairs = []
    cursor = 0
    while cursor < len(suspicious):
        if not suspicious[cursor]:
            cursor += 1
            continue
        end = cursor + 1
        while end < len(suspicious) and suspicious[end]:
            end += 1
        # One short word may be correct in rapid singing.  Atomic repair starts
        # only with a consecutive collapse, which is the characteristic forced
        # alignment failure seen in otherwise well aligned lines.
        if end - cursor < 2:
            cursor = end
            continue
        left_fence = (float(line.words[cursor - 1]["end"]) + 0.01
                      if cursor else float(line.words[cursor]["start"]) - 0.12)
        right_fence = (float(line.words[end]["start"]) - 0.01
                       if end < len(line.words)
                       else float(line.words[end - 1]["end"]) + 0.45)
        candidate_start = max(left_fence, float(aligned[cursor]["start"]))
        candidate_end = min(right_fence, float(aligned[end - 1]["end"]))
        current_total = sum(float(line.words[index]["end"])
                            - float(line.words[index]["start"])
                            for index in range(cursor, end))
        candidate_total = sum(float(aligned[index]["end"])
                              - float(aligned[index]["start"])
                              for index in range(cursor, end))
        anchor_support = _collapsed_run_anchor_support(
            line.words, aligned, cursor, end)
        activity_share = _vocal_activity_share(
            audio, candidate_start, candidate_end)
        if (candidate_end - candidate_start < minimum_run_expansion
                or candidate_total - current_total < minimum_run_expansion
                or not anchor_support
                or activity_share < minimum_activity_share):
            cursor = end
            continue
        proposed = []
        valid = True
        for index in range(cursor, end):
            start = max(left_fence, float(aligned[index]["start"]))
            following_start = (float(aligned[index + 1]["start"])
                               if index + 1 < end else right_fence)
            stop = min(float(aligned[index]["end"]), following_start - 0.005,
                       right_fence)
            if stop - start < 0.07:
                valid = False
                break
            proposed.append((start, stop))
            left_fence = stop + 0.005
        if not valid:
            cursor = end
            continue
        detail = {
            "words": [line.words[index].get("word", "")
                      for index in range(cursor, end)],
            "word_indices": list(range(cursor, end)),
            "old_start": round(float(line.words[cursor]["start"]), 3),
            "old_end": round(float(line.words[end - 1]["end"]), 3),
            "new_start": round(proposed[0][0], 3),
            "new_end": round(proposed[-1][1], 3),
            "activity_share": round(activity_share, 4),
            "source": "ipa-ctc-plus-vocal-activity",
        }
        for index, (start, stop) in zip(range(cursor, end), proposed):
            word = line.words[index]
            phone_word = aligned[index]
            word["ipa_collapsed_original_start"] = round(float(word["start"]), 3)
            word["ipa_collapsed_original_end"] = round(float(word["end"]), 3)
            word["start"] = round(start, 3)
            word["end"] = round(stop, 3)
            word["timing_source"] = "ipa-collapsed-run-repair"
            word["phonemes"] = _clip_phonemes(
                phone_word.get("phonemes", []), start, stop)
            word["phoneme_source"] = "xlsr-espeak-ctc-collapsed-run"
            word["phoneme_confidence"] = phone_word.get("confidence")
            word["phoneme_word_start_candidate"] = round(start, 3)
            word["phoneme_word_end_candidate"] = round(stop, 3)
        repairs.append(detail)
        cursor = end
    if line.words:
        line.timestamp = float(line.words[0]["start"])
    return repairs


def _collapsed_run_anchor_support(words: list[dict], aligned: list[dict],
                                  first: int, end: int) -> bool:
    anchors = []
    if first:
        anchors.append((words[first - 1], aligned[first - 1]))
    if end < len(words):
        anchors.append((words[end], aligned[end]))
    return any(
        float(candidate.get("confidence", 0.0)) >= 0.45
        and abs(float(candidate["start"]) - float(word["start"])) <= 0.12
        for word, candidate in anchors)


def _repair_ipa_vocal_holes(
        audio,
        line,
        aligned: list[dict],
        *,
        minimum_current_gap: float = 0.16,
        maximum_candidate_gap: float = 0.10,
        minimum_gap_reduction: float = 0.12,
        minimum_boundary_shift: float = 0.04,
        maximum_boundary_shift: float = 0.35,
        minimum_activity_share: float = 0.55,
) -> list[dict]:
    """Assign an acoustically occupied hole to its adjacent IPA words.

    A phrase can be locally correct while a conservative earlier pass leaves
    several hundred milliseconds of singing between two words unassigned.  We
    move only the two *inner* boundaries when the IPA path closes that hole,
    the vocal stem independently contains activity there, and at least one of
    the two words has a trustworthy unchanged outer edge.  Real pauses remain
    protected by the activity gate and neither word's outer edge is touched.
    """
    if len(line.words) != len(aligned) or len(line.words) < 2:
        return []
    repairs: list[dict] = []
    repaired_indices: set[int] = set()
    for left_index in range(len(line.words) - 1):
        right_index = left_index + 1
        # One word must not become the bridge for two separately inferred
        # holes in the same pass. Such a three-word region needs a dedicated
        # phrase-level alignment instead of chained pair corrections.
        if left_index in repaired_indices or right_index in repaired_indices:
            continue
        left = line.words[left_index]
        right = line.words[right_index]
        left_candidate = aligned[left_index]
        right_candidate = aligned[right_index]
        old_left_end = float(left["end"])
        old_right_start = float(right["start"])
        current_gap = old_right_start - old_left_end
        candidate_left_end = float(left_candidate["end"])
        candidate_right_start = float(right_candidate["start"])
        candidate_gap = candidate_right_start - candidate_left_end
        left_shift = candidate_left_end - old_left_end
        right_shift = old_right_start - candidate_right_start
        if (current_gap < minimum_current_gap
                or not 0.0 <= candidate_gap <= maximum_candidate_gap
                or current_gap - candidate_gap < minimum_gap_reduction
                or left_shift < minimum_boundary_shift
                or right_shift < minimum_boundary_shift
                or left_shift > maximum_boundary_shift
                or right_shift > maximum_boundary_shift
                or not _vocal_hole_anchor_support(
                    left, right, left_candidate, right_candidate)):
            continue
        activity_share = _vocal_activity_share(
            audio, old_left_end, old_right_start)
        if activity_share < minimum_activity_share:
            continue
        if (candidate_left_end - float(left["start"]) < 0.07
                or float(right["end"]) - candidate_right_start < 0.07):
            continue
        detail = {
            "words": [left.get("word", ""), right.get("word", "")],
            "word_indices": [left_index, right_index],
            "old_gap_ms": round(current_gap * 1000, 1),
            "new_gap_ms": round(candidate_gap * 1000, 1),
            "old_left_end": round(old_left_end, 3),
            "new_left_end": round(candidate_left_end, 3),
            "old_right_start": round(old_right_start, 3),
            "new_right_start": round(candidate_right_start, 3),
            "activity_share": round(activity_share, 4),
            "source": "ipa-ctc-plus-vocal-activity",
        }
        left["ipa_vocal_hole_original_end"] = round(old_left_end, 3)
        right["ipa_vocal_hole_original_start"] = round(old_right_start, 3)
        left["end"] = round(candidate_left_end, 3)
        right["start"] = round(candidate_right_start, 3)
        for word, candidate in ((left, left_candidate),
                                (right, right_candidate)):
            word["timing_source"] = "ipa-vocal-hole-repair"
            word["phonemes"] = _clip_phonemes(
                candidate.get("phonemes", []),
                float(word["start"]), float(word["end"]))
            word["phoneme_source"] = "xlsr-espeak-ctc-vocal-hole"
            word["phoneme_confidence"] = candidate.get("confidence")
            word["phoneme_word_start_candidate"] = round(
                float(candidate["start"]), 3)
            word["phoneme_word_end_candidate"] = round(
                float(candidate["end"]), 3)
        repairs.append(detail)
        repaired_indices.update((left_index, right_index))
    return repairs


def _vocal_hole_anchor_support(
        left: dict, right: dict,
        left_candidate: dict, right_candidate: dict) -> bool:
    """Require one reliable outer edge before changing the inner gap."""
    left_anchor = (
        float(left_candidate.get("confidence", 0.0)) >= 0.45
        and abs(float(left_candidate["start"]) - float(left["start"])) <= 0.12)
    right_anchor = (
        float(right_candidate.get("confidence", 0.0)) >= 0.45
        and abs(float(right_candidate["end"]) - float(right["end"])) <= 0.12)
    return left_anchor or right_anchor


def _vocal_activity_share(audio, start: float, end: float,
                          sample_rate: int = 16000) -> float:
    signal = np.asarray(audio, dtype=np.float32).reshape(-1)
    if end <= start or not len(signal):
        return 0.0
    frame = max(64, int(round(sample_rate * 0.02)))
    hop = max(1, int(round(sample_rate * 0.01)))
    local_start = max(0, int(round((start - 0.25) * sample_rate)))
    local_end = min(len(signal), int(round((end + 0.25) * sample_rate)))
    local = signal[local_start:local_end]
    if len(local) < frame:
        return 0.0
    offsets = np.arange(0, len(local) - frame + 1, hop)
    rms = np.sqrt(np.asarray([
        np.mean(np.square(local[offset:offset + frame]))
        for offset in offsets
    ]) + 1e-12)
    times = (local_start + offsets + frame / 2) / sample_rate
    threshold = max(1e-5, float(np.percentile(rms, 20)) * 1.8,
                    float(np.percentile(rms, 90)) * 0.06)
    selected = rms[(times >= start) & (times <= end)]
    return float(np.mean(selected >= threshold)) if len(selected) else 0.0


def _clip_phonemes(phonemes: list[dict], start: float, end: float) -> list[dict]:
    result = []
    for phone in phonemes:
        phone_start = max(start, float(phone["start"]))
        phone_end = min(end, float(phone["end"]))
        if phone_end <= phone_start:
            continue
        result.append({**phone, "start": round(phone_start, 3),
                       "end": round(phone_end, 3)})
    return result


def _promote_supported_word_onsets(
        audio,
        line,
        aligned: list[dict],
        *,
        minimum_start: float,
        minimum_confidence: float,
        minimum_evidence: float,
        minimum_shift: float,
        maximum_shift: float,
        minimum_word_duration: float = 0.04,
) -> list[dict]:
    """Apply only IPA onsets which have an independent acoustic vote."""
    promoted: list[dict] = []
    for index, (word, phone_word) in enumerate(zip(line.words, aligned)):
        if word.get("phoneme_source") != "xlsr-espeak-ctc":
            continue
        confidence = float(phone_word.get("confidence", 0.0))
        if confidence < minimum_confidence:
            continue
        old = float(word["start"])
        candidate = float(phone_word["start"])
        shift = candidate - old
        if not minimum_shift <= abs(shift) <= maximum_shift:
            continue
        previous_end = (float(line.words[index - 1]["end"])
                        if index else minimum_start)
        if candidate < previous_end + (0.001 if index else 0.0):
            continue
        if float(word["end"]) - candidate < minimum_word_duration:
            continue
        previous_phone_end = (float(aligned[index - 1]["end"])
                              if index else None)
        kind = ("onset" if previous_phone_end is None
                or candidate - previous_phone_end >= 0.055 else "transition")
        evidence = _acoustic_boundary_evidence(audio, candidate, kind)
        if kind == "onset" and not evidence["supported"]:
            # Connected phrases do not necessarily show an energy rise. A
            # considerably stronger spectral transition may still confirm the
            # IPA candidate without relying on the recogniser a second time.
            transition = _acoustic_boundary_evidence(audio, candidate, "transition")
            if transition["supported"] and float(transition["score"]) >= max(
                    minimum_evidence, 0.68):
                evidence = transition
        if not evidence["supported"] or float(evidence["score"]) < minimum_evidence:
            continue
        word["start"] = round(candidate, 3)
        word["phoneme_start_original"] = round(old, 3)
        word["phoneme_start_refinement_ms"] = round(shift * 1000, 1)
        word["phoneme_start_evidence"] = evidence
        promoted.append({
            "word": index + 1,
            "old": round(old, 3),
            "new": round(candidate, 3),
            "delta_ms": round(shift * 1000, 1),
            "evidence": evidence,
        })
    if line.words:
        line.timestamp = float(line.words[0]["start"])
    return promoted


def _previous_line_end(lines: list, index: int) -> float:
    for previous in reversed(lines[:index]):
        if previous.words:
            return float(previous.words[-1]["end"])
    return 0.0


def _verify_aligned_word_boundaries(
        audio,
        line,
        aligned: list[dict],
        maximum_word_edge_delta: float,
) -> list[dict]:
    """Verify every word in a sentence against independent local acoustics.

    The IPA/CTC path answers *which* phone is expected at a location.  It is
    deliberately not the sole timing authority: for every proposed word edge
    we additionally inspect energy direction and the spectral envelope around
    that exact point.  Phrase edges use onset/release evidence, while connected
    words use spectral transitions.  This makes the result useful as a final
    word-by-word quality gate instead of merely trusting a plausible sentence
    start and end.
    """
    if len(line.words) != len(aligned):
        return []
    verified: list[dict] = []
    for index, (current, candidate) in enumerate(zip(line.words, aligned)):
        candidate_start = float(candidate["start"])
        candidate_end = float(candidate["end"])
        previous_phone_end = (float(aligned[index - 1]["end"])
                              if index else None)
        next_phone_start = (float(aligned[index + 1]["start"])
                            if index + 1 < len(aligned) else None)
        start_kind = ("onset" if previous_phone_end is None
                      or candidate_start - previous_phone_end >= 0.055
                      else "transition")
        end_kind = ("release" if next_phone_start is None
                    or next_phone_start - candidate_end >= 0.055
                    else "transition")
        start_evidence = _acoustic_boundary_evidence(
            audio, candidate_start, start_kind)
        end_evidence = _acoustic_boundary_evidence(
            audio, candidate_end, end_kind)
        start_delta = candidate_start - float(current["start"])
        end_delta = candidate_end - float(current["end"])
        within_window = (abs(start_delta) <= maximum_word_edge_delta
                         and abs(end_delta) <= maximum_word_edge_delta)
        confidence = float(candidate.get("confidence", 0.0))
        # Connected legato words need not have two hard acoustic edges.  A
        # confident lexical phone path plus one independently supported edge
        # is enough; phrase boundaries still receive explicit onset/release
        # evidence in the diagnostic report.
        independently_supported = bool(start_evidence["supported"]
                                       or end_evidence["supported"])
        verified.append({
            "word": index + 1,
            "text": current.get("word", ""),
            "ipa_start": round(candidate_start, 3),
            "ipa_end": round(candidate_end, 3),
            "current_start": round(float(current["start"]), 3),
            "current_end": round(float(current["end"]), 3),
            "start_delta_ms": round(start_delta * 1000, 1),
            "end_delta_ms": round(end_delta * 1000, 1),
            "ipa_confidence": round(confidence, 4),
            "within_trusted_word_window": within_window,
            "start_evidence": start_evidence,
            "end_evidence": end_evidence,
            "verified": bool(confidence >= 0.18 and within_window
                             and independently_supported),
        })
    return verified


def _acoustic_boundary_evidence(
        audio,
        boundary: float,
        kind: str,
        sample_rate: int = 16000,
        flank_seconds: float = 0.055,
        guard_seconds: float = 0.008,
) -> dict:
    """Score a local boundary independently of the IPA model.

    Energy direction is useful for phrase onsets while a normalised spectral
    envelope change can also support connected word transitions. Both sides
    must contain enough audio; zero/silent windows never count as evidence.
    """
    signal = np.asarray(audio, dtype=np.float32)
    if signal.ndim > 1:
        signal = signal.mean(axis=tuple(range(1, signal.ndim)))
    signal = signal.reshape(-1)
    center = int(round(boundary * sample_rate))
    flank = max(64, int(round(flank_seconds * sample_rate)))
    guard = max(0, int(round(guard_seconds * sample_rate)))
    left = signal[max(0, center - flank):max(0, center - guard)]
    right = signal[min(len(signal), center + guard):min(len(signal), center + flank)]
    if len(left) < 128 or len(right) < 128:
        return {"supported": False, "score": 0.0, "kind": kind,
                "reason": "insufficient-audio"}
    left_rms = float(np.sqrt(np.mean(np.square(left)) + 1e-12))
    right_rms = float(np.sqrt(np.mean(np.square(right)) + 1e-12))
    if max(left_rms, right_rms) < 1e-4:
        return {"supported": False, "score": 0.0, "kind": kind,
                "reason": "silence"}
    energy_db = 20.0 * math.log10((right_rms + 1e-8) / (left_rms + 1e-8))
    spectral = _spectral_envelope_distance(left, right, sample_rate)
    spectral_score = float(np.clip((spectral - 0.10) / 0.32, 0.0, 1.0))
    # Diagnostic only. Substituting the leakage-corrected band-split step for
    # the broadband one was measured against the manually corrected reference
    # of a punk track: verified words rose from 36 to 45 while the mean word
    # onset error grew from 208 ms to 297 ms. The same "supported" flag also
    # opens the IPA promotion gate, so a more permissive measure buys more
    # replacements rather than better ones. The values stay in the report so
    # the effect can be studied, but they no longer decide anything.
    banded = None
    reference = active_reference()
    if reference is not None:
        banded = banded_boundary_step(
            signal, boundary, reference=reference, sample_rate=sample_rate,
            flank_seconds=flank_seconds, guard_seconds=guard_seconds)
    if kind == "onset":
        energy_score = float(np.clip((energy_db - 1.5) / 9.0, 0.0, 1.0))
        score = 0.58 * energy_score + 0.42 * spectral_score
        supported = energy_db >= 3.0 and spectral >= 0.12 and score >= 0.60
    elif kind == "release":
        energy_score = float(np.clip((-energy_db - 1.5) / 9.0, 0.0, 1.0))
        score = 0.58 * energy_score + 0.42 * spectral_score
        supported = energy_db <= -3.0 and spectral >= 0.12 and score >= 0.60
    else:
        energy_score = float(np.clip((abs(energy_db) - 1.0) / 8.0, 0.0, 1.0))
        score = 0.30 * energy_score + 0.70 * spectral_score
        supported = spectral >= 0.22 and score >= 0.60
    evidence = {
        "supported": bool(supported),
        "score": round(float(score), 4),
        "kind": kind,
        "energy_delta_db": round(energy_db, 2),
        "spectral_distance": round(spectral, 4),
    }
    if banded is not None:
        evidence["bands"] = banded["bands"]
        evidence["leading_band"] = banded["leading_band"]
        evidence["leakage_corrected"] = True
    return evidence


def _spectral_envelope_distance(left: np.ndarray, right: np.ndarray,
                                sample_rate: int) -> float:
    size = min(len(left), len(right))
    if size < 128:
        return 0.0
    window = np.hanning(size).astype(np.float32)
    n_fft = 1 << (size - 1).bit_length()
    frequencies = np.fft.rfftfreq(n_fft, 1.0 / sample_rate)
    edges = np.geomspace(80.0, min(6200.0, sample_rate * 0.47), 25)

    def envelope(values: np.ndarray) -> np.ndarray:
        power = np.abs(np.fft.rfft(values[-size:] * window, n=n_fft)) ** 2
        bands = np.array([
            float(power[(frequencies >= lower) & (frequencies < upper)].mean())
            if np.any((frequencies >= lower) & (frequencies < upper)) else 0.0
            for lower, upper in zip(edges[:-1], edges[1:])
        ], dtype=np.float64)
        result = np.log1p(bands)
        result -= result.mean()
        norm = float(np.linalg.norm(result))
        return result / norm if norm > 1e-9 else np.zeros_like(result)

    return float(np.linalg.norm(envelope(right) - envelope(left))
                 / math.sqrt(len(edges) - 1))


def audit_final_word_boundaries(lines: Iterable, *, tolerance: float = 0.002) -> dict:
    """Audit that persisted words still match their final IPA verification.

    This is intentionally read-only and runs after every timing mutation.  A
    hard Stage-vocal or single-lane correction is valid, but must be reported
    as such instead of retaining a stale ``verified`` label from the preceding
    sentence pass.
    """
    details: list[dict] = []
    counts = {"verified": 0, "hard-constrained": 0, "unverified": 0}
    previous_line_end: float | None = None
    geometry_violations = 0
    total = 0
    for line_index, line in enumerate(lines):
        previous_word_end: float | None = None
        for word_index, word in enumerate(line.words):
            total += 1
            start = float(word["start"])
            end = float(word["end"])
            invalid_geometry = end < start - tolerance
            if previous_word_end is not None and start < previous_word_end - tolerance:
                invalid_geometry = True
            if word_index == 0 and previous_line_end is not None:
                if start < previous_line_end - tolerance:
                    invalid_geometry = True
            geometry_violations += int(invalid_geometry)
            proof = word.get("phoneme_word_verification")
            proof_matches = False
            if isinstance(proof, dict):
                proof_matches = (
                    abs(start - float(proof.get("current_start", start + 1))) <= tolerance
                    and abs(end - float(proof.get("current_end", end + 1))) <= tolerance)
            hard_constrained = bool(
                word.get("stage_vocal_release_trim_ms") is not None
                or word.get("stem_contrast_release_trim_ms") is not None
                or word.get("stage_vocal_onset_trim_ms") is not None
                or word.get("stage_vocal_pickup_reflow")
                or word.get("source_boundary_trim_ms") is not None
                or word.get("nonlexical_vocalization_trim_ms") is not None
                or word.get("timing_source") == "overlap-display-lane-fallback")
            if (not invalid_geometry and proof_matches
                    and bool(proof.get("verified"))):
                status = "verified"
            elif not invalid_geometry and hard_constrained:
                status = "hard-constrained"
            else:
                status = "unverified"
            counts[status] += 1
            details.append({
                "line": line_index + 1,
                "word": word_index + 1,
                "text": word.get("word", ""),
                "start": round(start, 3),
                "end": round(end, 3),
                "status": status,
                "geometry_valid": not invalid_geometry,
                "verification_matches_final_timing": proof_matches,
            })
            previous_word_end = end
        if line.words:
            previous_line_end = float(line.words[-1]["end"])
    return {
        "method": "post-mutation-word-boundary-audit-v1",
        "words": total,
        "verified_words": counts["verified"],
        "hard_constrained_words": counts["hard-constrained"],
        "unverified_words": counts["unverified"],
        "geometry_violations": geometry_violations,
        "details": details,
    }
