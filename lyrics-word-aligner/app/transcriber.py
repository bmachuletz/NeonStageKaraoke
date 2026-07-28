from __future__ import annotations

import gc
import os
import re

import numpy as np
import torch
from transformers import AutoProcessor, Qwen3ASRForConditionalGeneration

ASR_MODEL_ID = "Qwen/Qwen3-ASR-1.7B-hf"
LANGUAGE_NAMES = {"de": "German", "en": "English", "fr": "French", "es": "Spanish",
                  "it": "Italian", "pt": "Portuguese", "ru": "Russian", "ja": "Japanese",
                  "ko": "Korean", "zh": "Chinese", "yue": "Cantonese"}


def language_code(value: str | None, fallback_text: str | None = None) -> str:
    normalized = (value or "").strip().lower()
    reverse = {name.lower(): code for code, name in LANGUAGE_NAMES.items()}
    code = reverse.get(normalized, normalized.split("-")[0])
    if code not in LANGUAGE_NAMES and fallback_text:
        words = re.findall(r"[^\W_]+", fallback_text.lower(), re.UNICODE)
        german = {"aber", "auch", "auf", "das", "dass", "dein", "der", "die", "du",
                  "ein", "eine", "für", "ich", "ist", "kein", "mit", "nicht", "nur",
                  "sie", "und", "uns", "von", "was", "wenn", "wir", "zu"}
        english = {"a", "and", "are", "baby", "but", "down", "for", "got", "have", "i",
                   "in", "is", "it", "me", "my", "not", "of", "on", "that", "the", "to",
                   "we", "what", "when", "with", "you", "your"}
        german_score = sum(word in german for word in words) + 3 * sum(
            any(character in word for character in "äöüß") for word in words
        )
        english_score = sum(word in english for word in words)
        if german_score or english_score:
            code = "de" if german_score > english_score else "en"
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
        self.processor = AutoProcessor.from_pretrained(self.model_id)
        self.model = Qwen3ASRForConditionalGeneration.from_pretrained(
            self.model_id, dtype=self.dtype).to(device).eval()

    def transcribe(self, audio: np.ndarray, language: str, prompt: str | None = None,
                   *, max_new_tokens: int | None = None) -> dict:
        language_hint = None if language.strip().lower() == "auto" else LANGUAGE_NAMES.get(language, language)
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
            output_ids = self.model.generate(**inputs, max_new_tokens=max_tokens, do_sample=False)
        generated = output_ids[:, inputs["input_ids"].shape[1]:]
        parsed = self.processor.decode(generated, return_format="parsed")[0]
        return {
            "model": self.model_id,
            "language": parsed.get("language") or language_hint,
            "text": parsed.get("transcription", "").strip(),
        }

    def close(self) -> None:
        del self.model
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

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
