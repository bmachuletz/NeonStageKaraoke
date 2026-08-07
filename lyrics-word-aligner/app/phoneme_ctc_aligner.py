from __future__ import annotations

import gc
import json
import math
import os
import re
import subprocess
from typing import Iterable

import numpy as np

from .micro_boundaries import VoicingTrack, refine_ipa_phone_path


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
    return re.findall(r"[^\W_]+(?:['’][^\W_]+)?", text.lower(), re.UNICODE)


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
        from huggingface_hub import hf_hub_download
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
        self.feature_extractor = AutoFeatureExtractor.from_pretrained(self.model_id)
        self.model = Wav2Vec2ForCTC.from_pretrained(self.model_id).to(device).eval()
        vocabulary_path = hf_hub_download(repo_id=self.model_id, filename="vocab.json")
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
        phones = [
            phone.replace("ˈ", "").replace("ˌ", "").replace("\u200d", "")
            for phone in completed.stdout.strip().split("_")
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
    micro_summary = {
        "enabled": micro_boundary_mode != "off", "mode": micro_boundary_mode,
        "method": "class-aware-multires-duration-viterbi-v1.2",
        "attempted_words": 0, "candidate_words": 0, "applied_words": 0,
        "moved_phone_onsets": 0, "lines": [],
    }
    diagnostics = []
    try:
        for line_index, line in candidates:
            original_start = float(line.words[0]["start"])
            original_end = float(line.words[-1]["end"])
            start = max(0.0, original_start - padding)
            end = min(duration, original_end + padding)
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
            confidence = sum(word["confidence"] for word in aligned) / len(aligned)
            start_delta = aligned[0]["start"] - original_start
            end_delta = aligned[-1]["end"] - original_end
            if confidence < minimum_confidence:
                diagnostics.append({"line": line_index + 1, "status": "low-confidence",
                                    "mean_confidence": round(confidence, 4)})
                continue
            if abs(start_delta) > maximum_edge_delta or abs(end_delta) > maximum_edge_delta:
                diagnostics.append({
                    "line": line_index + 1, "status": "outside-trusted-line-window",
                    "mean_confidence": round(confidence, 4),
                    "start_delta_ms": round(start_delta * 1000, 1),
                    "end_delta_ms": round(end_delta * 1000, 1),
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
                for repair in collapsed_repairs + hole_repairs
                for word_index in repair["word_indices"]
            }
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
                "collapsed_ipa_repairs": collapsed_repairs,
                "ipa_vocal_hole_repairs": hole_repairs,
            })
    finally:
        aligner.close()
    return {
        "enabled": True,
        "model": aligner.model_id,
        "method": "espeak-ipa-xlsr-ctc-micro-consensus-v1.2",
        "attempted_lines": len(candidates),
        "accepted_lines": accepted_lines,
        "accepted_words": accepted_words,
        "promoted_word_onsets": promoted_onsets,
        "collapsed_ipa_run_repairs": collapsed_run_repairs,
        "collapsed_ipa_words_repaired": collapsed_words_repaired,
        "ipa_vocal_hole_repairs": vocal_hole_repairs,
        "ipa_vocal_hole_words_repaired": vocal_hole_words_repaired,
        "micro_boundary_refinement": micro_summary,
        "minimum_confidence": minimum_confidence,
        "promotion_minimum_confidence": promotion_minimum_confidence,
        "promotion_minimum_evidence": promotion_minimum_evidence,
        "promotion_maximum_shift_ms": round(promotion_maximum_shift * 1000),
        "maximum_edge_delta_ms": round(maximum_edge_delta * 1000),
        "maximum_word_edge_delta_ms": round(maximum_word_edge_delta * 1000),
        "diagnostics": diagnostics,
    }


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
    if kind == "onset":
        energy_score = float(np.clip((energy_db - 1.5) / 9.0, 0.0, 1.0))
        score = 0.58 * energy_score + 0.42 * spectral_score
        supported = energy_db >= 3.0 and spectral >= 0.12 and score >= 0.60
    else:
        energy_score = float(np.clip((abs(energy_db) - 1.0) / 8.0, 0.0, 1.0))
        score = 0.30 * energy_score + 0.70 * spectral_score
        supported = spectral >= 0.22 and score >= 0.60
    return {
        "supported": bool(supported),
        "score": round(float(score), 4),
        "kind": kind,
        "energy_delta_db": round(energy_db, 2),
        "spectral_distance": round(spectral, 4),
    }


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
