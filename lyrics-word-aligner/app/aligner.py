from __future__ import annotations
import gc
import os
from typing import Iterable
import numpy as np
import torch
from transformers import AutoModelForTokenClassification, AutoProcessor
from .model_loading import from_pretrained_local_first
from .models import AlignmentConfig, LrcLine
from .sections import plan_sections
from .window_edges import mark_leading_window_edge_fallback

MODEL_ID = "Qwen/Qwen3-ForcedAligner-0.6B-hf"
SAMPLE_RATE = 16000
SUPPORTED = {"en", "zh", "yue", "fr", "de", "it", "ja", "ko", "pt", "ru", "es"}


class QwenWordAligner:
    def __init__(self, device: str = "cpu", model_id: str = MODEL_ID):
        if device == "auto":
            device = "cuda" if torch.cuda.is_available() else "cpu"
        self.device = device
        self.dtype = torch.bfloat16 if device == "cuda" else torch.float32
        self.processor = from_pretrained_local_first(AutoProcessor, model_id)
        self.model = from_pretrained_local_first(
            AutoModelForTokenClassification, model_id, dtype=self.dtype)
        self.model.to(device)
        self.model.eval()
        self.timestamp_token_id = self.model.config.timestamp_token_id

    def close(self) -> None:
        del self.model
        gc.collect()
        if torch.cuda.is_available():
            torch.cuda.empty_cache()

    def align(self, audio: np.ndarray, lines: list[LrcLine], cfg: AlignmentConfig) -> list[LrcLine]:
        if cfg.language not in SUPPORTED:
            raise ValueError(f"Qwen Forced Aligner unterstützt '{cfg.language}' nicht.")
        total_duration = len(audio) / SAMPLE_RATE
        jobs: list[tuple[LrcLine, float, np.ndarray]] = []
        for index, line in enumerate(lines):
            next_start = lines[index + 1].timestamp if index + 1 < len(lines) else min(
                total_duration, line.timestamp + cfg.last_line_duration
            )
            if line.source_end_boundary is not None:
                next_start = min(float(next_start), float(line.source_end_boundary))
            start = max(0.0, line.timestamp - cfg.pre_roll)
            end = min(total_duration, max(line.timestamp + 0.25, next_start + cfg.post_roll))
            if line.source_end_boundary is not None:
                end = min(end, max(line.timestamp + 0.25, float(line.source_end_boundary)))
            chunk = np.ascontiguousarray(audio[int(start*SAMPLE_RATE):int(end*SAMPLE_RATE)], dtype=np.float32)
            jobs.append((line, start, chunk))

        # Four simultaneous line windows can add more than 3 GiB of transient
        # CUDA memory on top of the model.  One bounded window is slower but is
        # deterministic on 8 GiB GPUs and does not affect alignment quality.
        default_batch_size = "1"
        batch_size = max(1, int(os.getenv("LRC_ALIGNMENT_BATCH_SIZE", default_batch_size)))
        for offset in range(0, len(jobs), batch_size):
            batch = jobs[offset:offset + batch_size]
            inputs, word_lists = self.processor.prepare_forced_aligner_inputs(
                audio=[x[2] for x in batch],
                transcript=[x[0].text for x in batch],
                language=cfg.language,
            )
            inputs = inputs.to(self.model.device, self.model.dtype)
            with torch.inference_mode():
                outputs = self.model(**inputs)
            decoded = self.processor.decode_forced_alignment(
                logits=outputs.logits,
                input_ids=inputs["input_ids"],
                word_lists=word_lists,
                timestamp_token_id=self.timestamp_token_id,
            )
            for (line, base, _), tokens in zip(batch, decoded):
                words = [
                    {
                        "word": token["text"],
                        "start": round(float(token["start_time"]) + base, 3),
                        "end": round(float(token["end_time"]) + base, 3),
                        "timing_source": "qwen-forced",
                    }
                    for token in tokens
                ]
                # A decoder miss at the beginning of a bounded line window is
                # represented by Qwen at timestamp zero.  After adding the
                # window base this looks like a precise acoustic boundary even
                # though it is merely the configured pre-roll edge.  Preserve
                # the words, but mark that leading boundary as provisional so
                # candidate fusion and the quality gate cannot treat it as a
                # verified onset.
                mark_leading_window_edge_fallback(
                    words, line_timestamp=float(line.timestamp),
                    window_base=base, pre_roll=cfg.pre_roll)
                line.words = words
        return lines

    def align_full_song(self, audio: np.ndarray, lines: list[LrcLine], cfg: AlignmentConfig) -> list[LrcLine]:
        """Align one global transcript and map its monotonic words back to LRC lines.

        A single pass prevents repeated phrases in overlapping line windows from
        being assigned to the same piece of audio.
        """
        if cfg.language not in SUPPORTED:
            raise ValueError(f"Qwen Forced Aligner unterstützt '{cfg.language}' nicht.")
        duration = len(audio) / SAMPLE_RATE
        max_seconds = cfg.full_song_max_seconds
        if duration > max_seconds:
            raise ValueError(
                f"Unsynchronisierte Lyrics benötigen ein Vollspur-Alignment; "
                f"{duration:.1f}s überschreiten das Limit von {max_seconds:.0f}s."
            )
        transcript = "\n".join(line.text for line in lines)
        inputs, word_lists = self.processor.prepare_forced_aligner_inputs(
            audio=[np.ascontiguousarray(audio, dtype=np.float32)],
            transcript=[transcript],
            language=cfg.language,
        )
        inputs = inputs.to(self.model.device, self.model.dtype)
        with torch.inference_mode():
            outputs = self.model(**inputs)
        decoded = self.processor.decode_forced_alignment(
            logits=outputs.logits,
            input_ids=inputs["input_ids"],
            word_lists=word_lists,
            timestamp_token_id=self.timestamp_token_id,
        )[0]
        tokens = [
            {"word": token["text"], "start": round(float(token["start_time"]), 3),
             "end": round(float(token["end_time"]), 3), "timing_source": "qwen-forced"}
            for token in decoded
        ]
        # Ask the same processor for per-line token counts. Whitespace counts
        # are wrong for punctuation-only fragments and CJK languages.
        expected_counts = []
        probe_audio = np.zeros(SAMPLE_RATE, dtype=np.float32)
        for line in lines:
            _, line_word_lists = self.processor.prepare_forced_aligner_inputs(
                audio=[probe_audio], transcript=[line.text], language=cfg.language
            )
            expected_counts.append(len(line_word_lists[0]))
        if sum(expected_counts) != len(tokens):
            raise ValueError(
                f"Das Vollspur-Alignment lieferte {len(tokens)} Wörter für "
                f"{sum(expected_counts)} Wörter im LRCLIB-Text."
            )
        cursor = 0
        for line, count in zip(lines, expected_counts):
            line.words = tokens[cursor:cursor + count]
            if line.words:
                line.timestamp = float(line.words[0]["start"])
            cursor += count
        return lines

    def align_text(self, audio: np.ndarray, transcript: str, language: str) -> list[dict]:
        """Align an arbitrary transcript and return its monotonic word timestamps."""
        if language not in SUPPORTED:
            raise ValueError(f"Qwen Forced Aligner unterstützt '{language}' nicht.")
        inputs, word_lists = self.processor.prepare_forced_aligner_inputs(
            audio=[np.ascontiguousarray(audio, dtype=np.float32)],
            transcript=[transcript],
            language=language,
        )
        inputs = inputs.to(self.model.device, self.model.dtype)
        with torch.inference_mode():
            outputs = self.model(**inputs)
        decoded = self.processor.decode_forced_alignment(
            logits=outputs.logits,
            input_ids=inputs["input_ids"],
            word_lists=word_lists,
            timestamp_token_id=self.timestamp_token_id,
        )[0]
        return [{
            "word": token["text"],
            "start": round(float(token["start_time"]), 3),
            "end": round(float(token["end_time"]), 3),
            "timing_source": "qwen-forced",
        } for token in decoded]

    def align_sections(self, audio: np.ndarray, lines: list[LrcLine], cfg: AlignmentConfig) -> list[LrcLine]:
        """Align bounded groups of consecutive LRC lines and restore line structure."""
        if cfg.language not in SUPPORTED:
            raise ValueError(f"Qwen Forced Aligner unterstützt '{cfg.language}' nicht.")
        sections = plan_sections(lines, len(audio) / SAMPLE_RATE,
                                 pause_gap=cfg.section_pause_gap,
                                 max_duration=cfg.section_max_duration,
                                 last_line_duration=cfg.last_line_duration)
        probe_audio = np.zeros(SAMPLE_RATE, dtype=np.float32)
        for section in sections:
            selected = lines[section["first"]:section["end"]]
            transcript = "\n".join(line.text for line in selected)
            start, end = section["audio_start"], section["audio_end"]
            chunk = np.ascontiguousarray(audio[int(start * SAMPLE_RATE):int(end * SAMPLE_RATE)], dtype=np.float32)
            tokens = self.align_text(chunk, transcript, cfg.language)
            for token in tokens:
                token["start"] = round(float(token["start"]) + start, 3)
                token["end"] = round(float(token["end"]) + start, 3)
            counts = []
            for line in selected:
                _, word_lists = self.processor.prepare_forced_aligner_inputs(
                    audio=[probe_audio], transcript=[line.text], language=cfg.language
                )
                counts.append(len(word_lists[0]))
            if sum(counts) != len(tokens):
                raise ValueError(
                    f"Abschnitt {section['first']}–{section['end']} lieferte {len(tokens)} statt "
                    f"{sum(counts)} Wörtern."
                )
            cursor = 0
            for line, count in zip(selected, counts):
                line.words = tokens[cursor:cursor + count]
                if line.words:
                    line.timestamp = float(line.words[0]["start"])
                cursor += count
        return lines

    def align_untimed(self, audio: np.ndarray, lines: list[LrcLine], cfg: AlignmentConfig) -> list[LrcLine]:
        """Backward-compatible name for full-song alignment."""
        return self.align_full_song(audio, lines, cfg)
