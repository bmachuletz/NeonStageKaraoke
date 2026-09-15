from __future__ import annotations

import gc
import math
import os
import statistics

import numpy as np

from .ctc_aligner import HEURISTIC_SOURCES
from .model_loading import from_pretrained_local_first


MODEL_IDS = {
    "de": "jonatasgrosman/wav2vec2-large-xlsr-53-german",
    "en": "facebook/wav2vec2-base-960h",
}

SAMPLE_RATE = 16000


def _overlapping_chunk_plan(sample_count: int, *, core_seconds: float = 20.0,
                            context_seconds: float = 1.0,
                            frame_stride_samples: int = 320) -> list[dict]:
    """Plan context-overlapped inference windows on one global CTC frame grid.

    Wav2Vec2 needs acoustic context on both sides of an inference boundary.
    Every window therefore contains a non-overlapping core plus context, while
    only logits belonging to the core survive.  Context and core sizes are
    snapped to the model stride so adjacent chunks retain exactly one common
    global frame coordinate system.
    """
    if sample_count <= 0:
        return []
    if frame_stride_samples <= 0:
        raise ValueError("CTC frame stride must be positive.")
    core_samples = max(frame_stride_samples, int(round(core_seconds * SAMPLE_RATE)))
    context_samples = max(0, int(round(context_seconds * SAMPLE_RATE)))
    core_samples = max(frame_stride_samples,
                       round(core_samples / frame_stride_samples) * frame_stride_samples)
    context_samples = round(context_samples / frame_stride_samples) * frame_stride_samples
    result = []
    for core_start in range(0, sample_count, core_samples):
        core_end = min(sample_count, core_start + core_samples)
        window_start = max(0, core_start - context_samples)
        window_end = min(sample_count, core_end + context_samples)
        first_global_frame = math.ceil(core_start / frame_stride_samples)
        final_global_frame = math.ceil(core_end / frame_stride_samples)
        window_global_frame = window_start // frame_stride_samples
        result.append({
            "core_start": core_start,
            "core_end": core_end,
            "window_start": window_start,
            "window_end": window_end,
            "keep_start": first_global_frame - window_global_frame,
            "keep_end": final_global_frame - window_global_frame,
            "global_frame_start": first_global_frame,
        })
    return result


def _mean_score(words: list[dict]) -> float:
    return sum(float(word["score"]) for word in words) / max(1, len(words))


def _probability_from_mean_log_score(value: float) -> float:
    return max(0.0, min(1.0, math.exp(float(value))))


def _line_tokens(text: str) -> list[str]:
    # Use EasyAligner's reversible normalizer so the alignment and the mapping
    # back to the original LRC always apply exactly the same transformations.
    from easyaligner.text.normalization import text_normalizer

    tokens, _mapping = text_normalizer(text)
    return tokens


class EasyGlobalAligner:
    """EasyAligner global Viterbi path over chunked Wav2Vec2 emissions."""

    def __init__(self, language: str, device: str):
        import torch
        from transformers import AutoModelForCTC, Wav2Vec2Processor

        language = language.lower().split("-")[0]
        model_id = os.getenv(f"LRC_EASYALIGNER_MODEL_{language.upper()}", MODEL_IDS[language])
        if device == "auto":
            device = "cuda" if torch.cuda.is_available() else "cpu"
        self.torch = torch
        self.device = device
        self.model_id = model_id
        self.processor = from_pretrained_local_first(Wav2Vec2Processor, model_id)
        dtype = torch.float16 if device == "cuda" else torch.float32
        self.model = from_pretrained_local_first(
            AutoModelForCTC, model_id).to(device=device, dtype=dtype).eval()
        self.last_inference: dict = {}

    def close(self) -> None:
        del self.model
        gc.collect()
        if self.torch.cuda.is_available():
            self.torch.cuda.empty_cache()

    def align(self, audio: np.ndarray, text: str, *, chunk_seconds: float = 20.0,
              context_seconds: float = 1.0) -> list[dict]:
        from easyaligner.alignment.pytorch import (
            _get_processor_case,
            align_pytorch,
            apply_tokenizer_case,
            count_alignment_targets,
            get_word_spans,
        )
        from easyaligner.text.normalization import text_normalizer

        torch = self.torch
        normalized_tokens, mapping = text_normalizer(text)
        tokenizer_case = _get_processor_case(self.processor)
        normalized_tokens = apply_tokenizer_case(mapping, tokenizer_case)
        target_count, character_count = count_alignment_targets(
            normalized_tokens, self.processor, tokenizer_case, "|"
        )
        if target_count != character_count:
            raise ValueError(
                f"EasyAligner-Tokenizer kann den Text nicht verlustfrei abbilden "
                f"({target_count} Ziele, {character_count} Zeichen)."
            )

        frame_stride = int(getattr(self.model.config, "inputs_to_logits_ratio", 320))
        plan = _overlapping_chunk_plan(
            len(audio), core_seconds=chunk_seconds, context_seconds=context_seconds,
            frame_stride_samples=frame_stride)
        emissions = []
        emitted_global_frames = []
        for window in plan:
            chunk = np.ascontiguousarray(
                audio[window["window_start"]:window["window_end"]], dtype=np.float32)
            inputs = self.processor(chunk, sampling_rate=16000, return_tensors="pt").input_values
            inputs = inputs.to(device=self.device, dtype=self.model.dtype)
            with torch.inference_mode():
                logits = self.model(inputs).logits
                # torchaudio.functional.forced_align maximizes additive CTC
                # log-likelihoods. Feeding ordinary probabilities changes that
                # objective and can let a low-probability character absorb a
                # long singing pause (for example "Denn" over two seconds).
                log_probs = torch.log_softmax(logits.float(), dim=-1).cpu()
            keep_start = min(int(window["keep_start"]), log_probs.shape[1])
            keep_end = min(int(window["keep_end"]), log_probs.shape[1])
            if keep_end <= keep_start:
                continue
            emissions.append(log_probs[:, keep_start:keep_end])
            emitted_global_frames.append({
                "start": int(window["global_frame_start"]),
                "count": keep_end - keep_start,
            })
        if not emissions:
            raise ValueError("EasyAligner konnte keine CTC-Frames aus dem Audio erzeugen.")
        for left, right in zip(emitted_global_frames, emitted_global_frames[1:]):
            if left["start"] + left["count"] != right["start"]:
                raise ValueError("Überlappende EasyAligner-Chunks bilden kein lückenloses Frame-Raster.")
        emission = torch.cat(emissions, dim=1).to(self.device)
        tokens, scores = align_pytorch(
            normalized_tokens=normalized_tokens,
            processor=self.processor,
            emissions=emission,
            blank_id=self.processor.tokenizer.pad_token_id,
            case=tokenizer_case,
            start_wildcard=False,
            end_wildcard=False,
            device=self.device,
        )
        word_spans, mapping = get_word_spans(
            tokens=tokens,
            scores=scores,
            mapping=mapping,
            blank=self.processor.tokenizer.pad_token_id,
            start_wildcard=False,
            end_wildcard=False,
            word_boundary="|",
            processor=self.processor,
        )
        frame_seconds = frame_stride / SAMPLE_RATE
        self.last_inference = {
            "method": "context-overlap-stitch-v1",
            "core_seconds": chunk_seconds,
            "context_seconds": context_seconds,
            "frame_stride_samples": frame_stride,
            "frame_seconds": frame_seconds,
            "chunks": len(emissions),
            "emission_frames": int(emission.shape[1]),
        }
        result = []
        for token, spans in zip(mapping, word_spans):
            length = sum(len(span) for span in spans)
            mean_log_probability = (
                sum(float(span.score) * len(span) for span in spans) / max(1, length))
            score = _probability_from_mean_log_score(mean_log_probability)
            result.append({
                "word": token["text"],
                "normalized": token["normalized_token"].lower(),
                "start": round(float(spans[0].start) * frame_seconds, 3),
                "end": round(float(spans[-1].end) * frame_seconds, 3),
                "score": round(score, 4),
            })
        return result


def realign_with_easyaligner(audio: np.ndarray, lines: list, language: str, device: str,
                             *, minimum_confidence: float = 0.18) -> dict:
    language = language.lower().split("-")[0]
    if language not in MODEL_IDS:
        return {"enabled": False, "reason": f"kein EasyAligner-Modell für {language}"}
    if not any(word.get("timing_source") in HEURISTIC_SOURCES
               for line in lines for word in line.words):
        return {"enabled": True, "attempted_lines": 0, "accepted_lines": 0,
                "accepted_words": 0}

    transcript = " ".join(line.text for line in lines)
    aligner = EasyGlobalAligner(language, device)
    try:
        aligned_words = aligner.align(audio, transcript)
        model_id = aligner.model_id
    finally:
        aligner.close()

    expected_counts = [len(_line_tokens(line.text)) for line in lines]
    if sum(expected_counts) != len(aligned_words):
        return {"enabled": True, "model": model_id, "error": "word-count-mismatch",
                "expected_words": sum(expected_counts), "actual_words": len(aligned_words),
                "attempted_lines": 0, "accepted_lines": 0, "accepted_words": 0}

    aligned_lines, cursor = [], 0
    for line, count in zip(lines, expected_counts):
        words = aligned_words[cursor:cursor + count]
        cursor += count
        if len(words) != len(line.words):
            aligned_lines.append(None)
        else:
            aligned_lines.append(words)

    calibration = []
    for line, words in zip(lines, aligned_lines):
        if words is None or not line.words or any(
                word.get("timing_source") in HEURISTIC_SOURCES for word in line.words):
            continue
        start_delta = (words[0]["start"] - float(line.words[0]["start"])) * 1000
        end_delta = (words[-1]["end"] - float(line.words[-1]["end"])) * 1000
        calibration.append({"score": _mean_score(words), "start_delta_ms": start_delta,
                            "end_delta_ms": end_delta})
    trusted = [item["score"] for item in calibration
               if abs(item["start_delta_ms"]) <= 350 and abs(item["end_delta_ms"]) <= 500]
    calibrated_minimum = minimum_confidence
    if len(trusted) >= 4:
        calibrated_minimum = max(minimum_confidence, statistics.median(trusted) * 0.68)

    attempted = accepted_lines = accepted_words = 0
    diagnostics = []
    for index, (line, replacements) in enumerate(zip(lines, aligned_lines)):
        if not line.words or not any(
                word.get("timing_source") in HEURISTIC_SOURCES for word in line.words):
            continue
        attempted += 1
        if replacements is None:
            diagnostics.append({"line": index + 1, "status": "word-count-mismatch"})
            continue
        score = _mean_score(replacements)
        supported_ratio = sum(word["score"] >= calibrated_minimum * 0.55
                              for word in replacements) / len(replacements)
        if score < calibrated_minimum or supported_ratio < 0.70:
            diagnostics.append({"line": index + 1, "status": "low-confidence",
                                "score": round(score, 4),
                                "supported_word_ratio": round(supported_ratio, 4)})
            continue
        # A global path may legitimately correct a poor LRC hint, but a shift of
        # several seconds still needs a second acoustic vote before publication.
        original_start = float(line.words[0]["start"])
        if abs(replacements[0]["start"] - original_start) > 1.5:
            diagnostics.append({"line": index + 1, "status": "needs-consensus",
                                "score": round(score, 4), "aligned_start": replacements[0]["start"],
                                "original_start": original_start})
            continue
        for word, replacement in zip(line.words, replacements):
            word["start"] = replacement["start"]
            word["end"] = replacement["end"]
            word["timing_source"] = "easyaligner-global"
            word["easyaligner_score"] = replacement["score"]
        line.timestamp = float(line.words[0]["start"])
        accepted_lines += 1
        accepted_words += len(line.words)
        diagnostics.append({"line": index + 1, "status": "accepted", "score": round(score, 4)})

    return {
        "enabled": True,
        "model": model_id,
        "method": "easyaligner-global-viterbi-v1",
        "attempted_lines": attempted,
        "accepted_lines": accepted_lines,
        "accepted_words": accepted_words,
        "minimum_confidence": minimum_confidence,
        "calibrated_minimum_confidence": round(calibrated_minimum, 4),
        "calibration_lines": len(calibration),
        "trusted_calibration_lines": len(trusted),
        "line_diagnostics": diagnostics,
    }
