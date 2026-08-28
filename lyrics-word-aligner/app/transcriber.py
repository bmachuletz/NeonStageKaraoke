from __future__ import annotations

import gc
import os
import re

import numpy as np
import torch
from transformers import AutoProcessor, Qwen3ASRForConditionalGeneration

from .model_loading import from_pretrained_local_first

ASR_MODEL_ID = "Qwen/Qwen3-ASR-1.7B-hf"
LANGUAGE_NAMES = {"de": "German", "en": "English", "fr": "French", "es": "Spanish",
                  "it": "Italian", "pt": "Portuguese", "ru": "Russian", "ja": "Japanese",
                  "ko": "Korean", "zh": "Chinese", "yue": "Cantonese"}


def merge_transcript_chunks(texts: list[str], *, maximum_overlap_words: int = 24) -> str:
    """Merge overlapping ASR text chunks without repeating their shared tail."""
    merged: list[str] = []

    def normalized(token: str) -> str:
        return re.sub(r"[^\w']+", "", token.casefold(), flags=re.UNICODE)

    for text in texts:
        incoming = text.strip().split()
        if not incoming:
            continue
        overlap = 0
        maximum = min(maximum_overlap_words, len(merged), len(incoming))
        merged_normalized = [normalized(token) for token in merged]
        incoming_normalized = [normalized(token) for token in incoming]
        for size in range(maximum, 0, -1):
            if (merged_normalized[-size:] == incoming_normalized[:size]
                    and all(merged_normalized[-size:])):
                overlap = size
                break
        merged.extend(incoming[overlap:])
    return " ".join(merged)


GERMAN_MARKERS = {"aber", "auch", "auf", "das", "dass", "dein", "der", "die", "du",
                  "ein", "eine", "für", "ich", "ist", "kein", "mit", "nicht", "nur",
                  "sie", "und", "uns", "von", "was", "wenn", "wir", "zu"}
ENGLISH_MARKERS = {"a", "and", "are", "baby", "but", "down", "for", "got", "have", "i",
                   "in", "is", "it", "me", "my", "not", "of", "on", "that", "the", "to",
                   "we", "what", "when", "with", "you", "your"}


def score_text_language(text: str | None) -> dict:
    """Score a lyric text for German versus English with function words.

    Function words and umlauts are chosen deliberately: they survive spelling
    variation, are frequent in every register, and do not depend on the topic
    of a song. The result is evidence, not a decision.
    """
    words = re.findall(r"[^\W_]+", (text or "").lower(), re.UNICODE)
    umlauts = sum(any(character in word for character in "äöüß") for word in words)
    german = sum(word in GERMAN_MARKERS for word in words) + 3 * umlauts
    english = sum(word in ENGLISH_MARKERS for word in words)
    winner = None
    if german or english:
        winner = "de" if german > english else "en"
    return {"german_score": german, "english_score": english,
            "umlaut_words": umlauts, "words": len(words), "winner": winner}


def reconcile_detected_language(detected: str, lyrics_text: str | None, *,
                                minimum_margin: int = 8,
                                minimum_share: float = 0.04) -> tuple[str, dict]:
    """Let unambiguous canonical lyrics overrule a contradicting ASR guess.

    Automatic speech recognition on shouted, distorted or heavily accompanied
    singing regularly reports a confident but wrong language. The canonical
    lyric text is the stronger evidence in that conflict because it is exact
    and was never passed through an acoustic model. The override is applied
    only when the text is decisive: a clear margin plus a minimum share of
    marker words, so a handful of loan words can never flip a song.
    """
    evidence = score_text_language(lyrics_text)
    evidence["detected"] = detected
    winner = evidence["winner"]
    margin = abs(evidence["german_score"] - evidence["english_score"])
    share = margin / max(1, evidence["words"])
    evidence.update({"margin": margin, "share": round(share, 4),
                     "minimum_margin": minimum_margin,
                     "minimum_share": minimum_share})
    if (winner is None or winner == detected
            or winner not in LANGUAGE_NAMES
            or margin < minimum_margin or share < minimum_share):
        evidence["applied"] = False
        evidence["reason"] = ("text-agrees-with-asr" if winner == detected
                              else "text-evidence-too-weak")
        return detected, evidence
    evidence["applied"] = True
    evidence["reason"] = "canonical-lyrics-contradict-asr"
    return winner, evidence


def language_code(value: str | None, fallback_text: str | None = None) -> str:
    normalized = (value or "").strip().lower()
    reverse = {name.lower(): code for code, name in LANGUAGE_NAMES.items()}
    code = reverse.get(normalized, normalized.split("-")[0])
    if code not in LANGUAGE_NAMES and fallback_text:
        winner = score_text_language(fallback_text)["winner"]
        if winner:
            code = winner
    if code not in LANGUAGE_NAMES:
        raise ValueError(f"Automatisch erkannte Sprache wird nicht unterstützt: {value or 'unbekannt'}")
    return code


class QwenTranscriber:
    """Reusable Qwen ASR session for bounded sequential candidate evaluation."""

    def __init__(self, device: str):
        if device == "auto":
            device = "cuda" if torch.cuda.is_available() else "cpu"
        self.device = device
        self.dtype = torch.bfloat16 if device == "cuda" else torch.float32
        self.model_id = os.getenv("LRC_ASR_MODEL", ASR_MODEL_ID)
        self.processor = from_pretrained_local_first(AutoProcessor, self.model_id)
        self.model = from_pretrained_local_first(
            Qwen3ASRForConditionalGeneration, self.model_id, dtype=self.dtype).to(device).eval()

    def transcribe(self, audio: np.ndarray, language: str, prompt: str | None = None,
                   *, max_new_tokens: int | None = None) -> dict:
        language_hint = None if language.strip().lower() == "auto" else LANGUAGE_NAMES.get(language, language)
        inputs = output_ids = generated = parsed = None
        try:
            inputs = self.processor.apply_transcription_request(
                audio=np.ascontiguousarray(audio, dtype=np.float32),
                language=language_hint,
                prompt=prompt or None,
                sampling_rate=16000,
            ).to(self.model.device, self.model.dtype)
            max_tokens = (max_new_tokens if max_new_tokens is not None
                          else int(os.getenv("LRC_ASR_MAX_NEW_TOKENS", "1024")))
            if max_tokens <= 0:
                raise ValueError("max_new_tokens muss größer als 0 sein")
            with torch.inference_mode():
                output_ids = self.model.generate(
                    **inputs, max_new_tokens=max_tokens, do_sample=False)
            generated = output_ids[:, inputs["input_ids"].shape[1]:]
            parsed = self.processor.decode(generated, return_format="parsed")[0]
            return {
                "model": self.model_id,
                "language": parsed.get("language") or language_hint,
                "text": parsed.get("transcription", "").strip(),
            }
        finally:
            # BatchFeature, generation output and its sliced view all own CUDA
            # storage.  Relying on the function frame to disappear made the
            # following Stable-TS load intermittently overlap Qwen's peak.
            del parsed
            del generated
            del output_ids
            del inputs

    def close(self) -> None:
        model = getattr(self, "model", None)
        processor = getattr(self, "processor", None)
        self.model = None
        self.processor = None
        del model
        del processor
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.synchronize()
            torch.cuda.empty_cache()
            try:
                torch.cuda.ipc_collect()
            except (RuntimeError, AttributeError):
                pass

    def __enter__(self) -> "QwenTranscriber":
        return self

    def __exit__(self, *_args) -> None:
        self.close()


def transcribe(audio: np.ndarray, language: str, device: str,
               prompt: str | None = None) -> dict:
    session = QwenTranscriber(device)
    try:
        return session.transcribe(audio, language, prompt=prompt)
    finally:
        session.close()
