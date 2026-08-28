from __future__ import annotations

import gc
import math
import os
import statistics

import numpy as np

from .chunk_ownership import move_ownership_seams_to_quiet_audio
from .ctc_aligner import HEURISTIC_SOURCES
from .transcript_match import normalize_words


SAMPLE_RATE = 16000


class StableTranscriberSession:
    """Keep one Stable-TS model resident for several bounded song windows."""

    def __init__(self, device: str):
        import stable_whisper
        import torch

        if device == "auto":
            device = "cuda" if torch.cuda.is_available() else "cpu"
        self._torch = torch
        self._model_name = os.getenv("LRC_STABLE_TS_MODEL", "turbo")
        self._model = stable_whisper.load_model(
            self._model_name, device=device,
            download_root=os.getenv("LRC_WHISPER_CACHE", "/models/whisper"))
        self._closed = False

    def transcribe(self, audio, language: str, *, vad: bool = True,
                   initial_prompt: str | None = None,
                   chunk_seconds: float | None = None,
                   overlap_seconds: float | None = None) -> dict:
        if self._closed:
            raise RuntimeError("Stable-TS-Session wurde bereits geschlossen")
        chunk_seconds = (float(os.getenv("LRC_STABLE_TS_CHUNK_SECONDS", "30"))
                         if chunk_seconds is None else float(chunk_seconds))
        overlap_seconds = (float(os.getenv("LRC_STABLE_TS_CHUNK_OVERLAP_SECONDS", "3"))
                           if overlap_seconds is None else float(overlap_seconds))
        return _transcribe_with_loaded_model(
            self._model, audio, language, self._torch, vad=vad,
            initial_prompt=initial_prompt, model_name=self._model_name,
            chunk_seconds=chunk_seconds, overlap_seconds=overlap_seconds)

    def close(self) -> None:
        if self._closed:
            return
        self._closed = True
        model = self._model
        self._model = None
        del model
        _release_cuda(self._torch)

    def __enter__(self):
        return self

    def __exit__(self, _exception_type, _exception, _traceback):
        self.close()


def _chunk_windows(sample_count: int, *, sample_rate: int = SAMPLE_RATE,
                   chunk_seconds: float = 30.0,
                   overlap_seconds: float = 3.0) -> list[tuple[int, int, float, float]]:
    """Return bounded windows and their non-overlapping ownership intervals.

    Whisper natively works on roughly 30 second contexts.  The overlap keeps
    words at a window edge audible in both chunks, while the ownership interval
    makes sure that each timestamp is emitted exactly once.
    """
    if sample_count <= 0:
        return []
    if chunk_seconds <= 0:
        raise ValueError("LRC_STABLE_TS_CHUNK_SECONDS muss größer als 0 sein")
    if overlap_seconds < 0 or overlap_seconds >= chunk_seconds:
        raise ValueError(
            "LRC_STABLE_TS_CHUNK_OVERLAP_SECONDS muss zwischen 0 und der Fensterlänge liegen")
    chunk_samples = max(1, round(chunk_seconds * sample_rate))
    overlap_samples = round(overlap_seconds * sample_rate)
    step = max(1, chunk_samples - overlap_samples)
    windows: list[tuple[int, int, float, float]] = []
    start = 0
    while start < sample_count:
        end = min(sample_count, start + chunk_samples)
        keep_start = 0.0 if start == 0 else (start + overlap_samples / 2) / sample_rate
        keep_end = (sample_count / sample_rate if end == sample_count
                    else (end - overlap_samples / 2) / sample_rate)
        windows.append((start, end, keep_start, keep_end))
        if end == sample_count:
            break
        start += step
    return windows


def _release_cuda(torch) -> None:
    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.synchronize()
        torch.cuda.empty_cache()
        try:
            torch.cuda.ipc_collect()
        except (RuntimeError, AttributeError):
            pass


def _is_cuda_oom(error: BaseException) -> bool:
    return "cuda out of memory" in str(error).lower()


def _transcribe_with_loaded_model(model, audio, language: str, torch, *, vad: bool,
                                  initial_prompt: str | None,
                                  model_name: str, chunk_seconds: float,
                                  overlap_seconds: float) -> dict:
    windows = _chunk_windows(
        len(audio), chunk_seconds=chunk_seconds, overlap_seconds=overlap_seconds)
    windows = move_ownership_seams_to_quiet_audio(windows, audio, sample_rate=SAMPLE_RATE)
    words: list[dict] = []
    segment_count = 0
    for sample_start, sample_end, keep_start, keep_end in windows:
        base = sample_start / SAMPLE_RATE
        result = None
        raw = None
        chunk = None
        try:
            chunk = np.ascontiguousarray(audio[sample_start:sample_end], dtype=np.float32)
            result = model.transcribe(
                chunk, language=language, vad=vad, regroup=True, verbose=False,
                word_timestamps=True,
                initial_prompt=initial_prompt or None,
            )
            raw = result.to_dict()
            segment_count += len(raw.get("segments", []))
            for segment in raw.get("segments", []):
                for word in segment.get("words", []):
                    text = str(word.get("word", "")).strip()
                    if not text:
                        continue
                    start = float(word["start"]) + base
                    end = float(word["end"]) + base
                    midpoint = (start + end) / 2
                    if midpoint < keep_start or midpoint >= keep_end:
                        continue
                    words.append({
                        "word": text,
                        "start": round(start, 3),
                        "end": round(end, 3),
                        "probability": round(float(word.get("probability", 0.0)), 4),
                    })
        finally:
            # Results own transient CUDA tensors; the loaded model intentionally
            # survives so another window strategy can reuse it without a second
            # 6–7 GiB allocation.
            del raw
            del result
            del chunk
            _release_cuda(torch)
    return {
        "model": f"stable-ts/{model_name}",
        "language": language,
        "text": " ".join(str(word["word"]) for word in words),
        "words": words,
        "segments": segment_count,
        "chunks": len(windows),
        "chunk_seconds": chunk_seconds,
        "chunk_overlap_seconds": overlap_seconds,
    }


def transcribe_stable_variants(audio, language: str, device: str,
                               variants: list[tuple[str, float, float]], *,
                               vad: bool = True,
                               initial_prompt: str | None = None) -> dict[str, dict]:
    import stable_whisper
    import torch

    if device == "auto":
        device = "cuda" if torch.cuda.is_available() else "cpu"
    model_name = os.getenv("LRC_STABLE_TS_MODEL", "turbo")
    model = stable_whisper.load_model(
        model_name, device=device, download_root=os.getenv("LRC_WHISPER_CACHE", "/models/whisper")
    )
    try:
        results: dict[str, dict] = {}
        for index, (variant_id, chunk_seconds, overlap_seconds) in enumerate(variants):
            retry_count = max(0, int(os.getenv("LRC_STABLE_TS_CUDA_OOM_RETRIES", "1")))
            error: RuntimeError | ValueError | None = None
            for attempt in range(retry_count + 1):
                try:
                    results[variant_id] = _transcribe_with_loaded_model(
                        model, audio, language, torch, vad=vad,
                        initial_prompt=initial_prompt, model_name=model_name,
                        chunk_seconds=float(chunk_seconds),
                        overlap_seconds=float(overlap_seconds))
                    if attempt:
                        results[variant_id]["cuda_oom_retries"] = attempt
                    error = None
                    break
                except (RuntimeError, ValueError) as current_error:
                    error = current_error
                    if not _is_cuda_oom(current_error) or attempt >= retry_count:
                        break
                    # A neighbouring CUDA stage can leave fragmented cached
                    # blocks even after its model was destroyed.  Stable-TS is
                    # essential evidence, so clear all collectable blocks and
                    # retry the same bounded variant once.
                    _release_cuda(torch)
            if error is not None:
                if index == 0:
                    raise error
                # An optional short-window experiment must never discard the
                # already completed long-context transcript.
                results[variant_id] = {
                    "model": f"stable-ts/{model_name}", "language": language,
                    "words": [], "text": "", "error": str(error),
                    "chunk_seconds": float(chunk_seconds),
                    "chunk_overlap_seconds": float(overlap_seconds),
                }
                _release_cuda(torch)
        return results
    finally:
        del model
        _release_cuda(torch)


def transcribe_stable(audio, language: str, device: str, *, vad: bool = True,
                      initial_prompt: str | None = None,
                      chunk_seconds: float | None = None,
                      overlap_seconds: float | None = None) -> dict:
    chunk_seconds = (float(os.getenv("LRC_STABLE_TS_CHUNK_SECONDS", "30"))
                     if chunk_seconds is None else float(chunk_seconds))
    overlap_seconds = (float(os.getenv("LRC_STABLE_TS_CHUNK_OVERLAP_SECONDS", "3"))
                       if overlap_seconds is None else float(overlap_seconds))
    with StableTranscriberSession(device) as session:
        return session.transcribe(
            audio, language, vad=vad, initial_prompt=initial_prompt,
            chunk_seconds=chunk_seconds, overlap_seconds=overlap_seconds)


def realign_with_stable_words(lines: list, stable_words: list[dict], comparison: dict,
                              *, max_start_deviation: float = 2.0,
                              allow_unanchored: bool = False) -> dict:
    """Promote only complete lyric lines matched by timestamped Whisper words."""
    recognized = []
    for word in stable_words:
        for normalized in normalize_words(str(word["word"])):
            recognized.append((normalized, word))
    accepted_operations = {"match", "approximate"}
    if allow_unanchored:
        # With plain lyrics there is no prior timing to preserve. A recognized
        # replacement still supplies a valid acoustic word window while the
        # original lyric spelling remains untouched.
        accepted_operations.add("replace")
    by_expected = {
        int(operation["expected_index"]): operation
        for operation in comparison.get("operations", [])
        if operation["type"] in accepted_operations
    }
    timing_by_expected = {
        int(operation["expected_index"]): operation
        for operation in comparison.get("operations", [])
        if operation["type"] in {"match", "approximate", "replace"}
        and "expected_index" in operation and "recognized_index" in operation
    }
    expected_cursor = attempted = accepted_lines = accepted_words = 0
    diagnostics = []
    for line_index, line in enumerate(lines):
        tokens = normalize_words(line.text)
        indices = list(range(expected_cursor, expected_cursor + len(tokens)))
        expected_cursor += len(tokens)
        timing_available = [(offset, timing_by_expected[index])
                            for offset, index in enumerate(indices)
                            if index in timing_by_expected]
        exact_available = [(offset, by_expected[index])
                           for offset, index in enumerate(indices)
                           if index in by_expected]
        timing_indices = [int(operation["recognized_index"])
                          for _offset, operation in timing_available]
        timing_replacements = [recognized[index][1] for index in timing_indices
                               if 0 <= index < len(recognized)]
        exact_or_approximate = sum(
            operation["type"] in {"match", "approximate"}
            for _offset, operation in timing_available)
        replacement_count = sum(operation["type"] == "replace"
                                for _offset, operation in timing_available)
        existing_span = (float(line.words[-1]["end"]) - float(line.words[0]["start"])
                         if line.words else 0.0)
        existing_durations = [float(word["end"]) - float(word["start"])
                              for word in line.words]
        existing_internal_overlap = any(
            float(left["end"]) > float(right["start"]) + 0.005
            for left, right in zip(line.words, line.words[1:]))
        existing_nonmonotonic = any(
            float(right["start"]) + 0.015 < float(left["start"])
            for left, right in zip(line.words, line.words[1:]))
        existing_invalid_geometry = bool(existing_durations) and (
            any(duration <= 0.025 or duration > 6.0
                for duration in existing_durations)
            or existing_internal_overlap or existing_nonmonotonic
        )
        stable_span = (float(timing_replacements[-1]["end"])
                       - float(timing_replacements[0]["start"])
                       if len(timing_replacements) == len(tokens) and tokens else 0.0)
        probabilities = [float(word.get("probability", 0.0))
                         for word in timing_replacements]
        source_start = (float(line.source_timestamp)
                        if line.source_timestamp is not None else
                        float(line.timestamp))
        boundary_ok = (
            line.source_end_boundary is None or not timing_replacements
            or float(timing_replacements[-1]["end"])
            <= float(line.source_end_boundary) + 0.25
        )
        # A forced singing model can claim a line even when it stretched a
        # short, continuously recognized phrase over the complete gap up to the
        # next lyric.  Such a line no longer carries a "heuristic" source and
        # used to be invisible to this final Stable-TS pass.  Rescue only gross
        # geometry errors with a complete, consecutive, high-confidence local
        # transcript.  The strict duration ratio deliberately leaves already
        # plausible acoustic alignments untouched.
        geometry_rescue = (
            bool(line.words) and len(line.words) == len(tokens) and bool(tokens)
            and len(timing_available) == len(tokens)
            and len(timing_replacements) == len(tokens)
            and timing_indices == list(range(timing_indices[0],
                                              timing_indices[0] + len(tokens)))
            and exact_or_approximate >= math.ceil(len(tokens) * 0.75)
            and replacement_count <= max(2, math.ceil(len(tokens) * 0.20))
            and statistics.median(probabilities) >= 0.60
            and stable_span >= max(0.25, len(tokens) * 0.035)
            and (existing_invalid_geometry
                 or existing_span >= max(stable_span * 1.65, stable_span + 2.0))
            and abs(float(timing_replacements[0]["start"]) - source_start)
            <= max_start_deviation
            and boundary_ok
        )
        # A bounded LRC cue and a complete, consecutive Stable-TS phrase form
        # stronger onset evidence than a forced model which starts hundreds of
        # milliseconds before both.  This is deliberately stricter than the
        # geometry rescue: the Stable onset must sit almost exactly on the
        # calibrated source cue and improve the existing deviation materially.
        source_anchor_rescue = (
            bool(line.words) and len(line.words) == len(tokens) and bool(tokens)
            and line.source_timestamp is not None
            and len(exact_available) == len(tokens)
            and len(timing_replacements) == len(tokens)
            and timing_indices == list(range(timing_indices[0],
                                              timing_indices[0] + len(tokens)))
            and exact_or_approximate == len(tokens)
            and statistics.median(probabilities) >= 0.75
            and stable_span >= max(0.25, len(tokens) * 0.035)
            and abs(float(timing_replacements[0]["start"]) - source_start) <= 0.12
            and abs(float(line.words[0]["start"]) - source_start) >= 0.25
            and (abs(float(line.words[0]["start"]) - source_start)
                 - abs(float(timing_replacements[0]["start"]) - source_start) >= 0.18)
            and boundary_ok
        )
        # Stable-TS sometimes drops a short pickup (commonly "and") and gives
        # the following low-confidence token the entire musical lead-in.  If
        # every later lyric word is consecutive and confident, interpolate
        # only those first two tokens from the trusted cue to the first strong
        # Stable word.  The rest keeps its measured acoustic timestamps.
        exact_offsets = [offset for offset, _operation in exact_available]
        leading_source_anchor_rescue = (
            bool(line.words) and len(line.words) == len(tokens) and len(tokens) >= 3
            and line.source_timestamp is not None
            and exact_offsets == list(range(1, len(tokens)))
            and len(timing_available) == len(tokens) - 1
            and timing_indices == list(range(timing_indices[0],
                                              timing_indices[0] + len(tokens) - 1))
            and exact_or_approximate == len(tokens) - 1
            and len(probabilities) == len(tokens) - 1
            and statistics.median(probabilities[1:]) >= 0.75
            and abs(float(line.words[0]["start"]) - source_start) >= 0.25
            and (float(timing_replacements[0].get("probability", 0.0)) < 0.15
                 or float(timing_replacements[0]["start"]) >= source_start + 0.08)
            and source_start + 0.15 <= float(timing_replacements[1]["start"])
            <= source_start + 1.20
            and boundary_ok
        )
        replaceable_sources = HEURISTIC_SOURCES | {
            "asr-repetition-anchor", "asr-repetition-activity",
        }
        heuristic = bool(line.words) and any(
            word.get("timing_source") in replaceable_sources for word in line.words
        )
        if (not heuristic and not allow_unanchored and not geometry_rescue
                and not source_anchor_rescue and not leading_source_anchor_rescue):
            continue
        attempted += 1
        if line.words and len(tokens) != len(line.words):
            diagnostics.append({"line": line_index + 1, "status": "word-count-mismatch"})
            continue
        available = exact_available
        complete = len(available) == len(tokens)
        replacement_timing_consensus = False
        if not complete and not allow_unanchored:
            replacement_timing_consensus = (
                len(timing_available) == len(tokens)
                and replacement_count <= max(2, math.ceil(len(tokens) * 0.25))
                and len(available) >= math.ceil(len(tokens) * 0.7)
                and timing_indices
                and timing_indices == list(range(timing_indices[0],
                                                  timing_indices[0] + len(tokens)))
                and len(probabilities) == len(tokens)
                and statistics.median(probabilities) >= 0.55
            )
            if replacement_timing_consensus:
                available = timing_available
                complete = True
        if not line.words and not complete:
            diagnostics.append({"line": line_index + 1, "status": "incomplete-unanchored-line",
                                "matched_words": len(available), "required_words": len(tokens)})
            continue
        minimum_partial = max(2, math.ceil(len(tokens) * 0.5))
        if not complete and len(available) < minimum_partial:
            diagnostics.append({"line": line_index + 1, "status": "incomplete-transcript-match",
                                "matched_words": len(available), "required_words": minimum_partial})
            continue
        operations = [operation for _offset, operation in available]
        recognized_indices = [int(operation["recognized_index"]) for operation in operations]
        if (recognized_indices != sorted(recognized_indices)
                or any(index >= len(recognized) for index in recognized_indices)):
            diagnostics.append({"line": line_index + 1, "status": "non-monotonic-match"})
            continue
        replacements = [recognized[index][1] for index in recognized_indices]
        replacements = [dict(word) for word in replacements]
        if leading_source_anchor_rescue:
            strong_start = float(replacements[1]["start"])
            weights = [max(1, len(tokens[0])), max(1, len(tokens[1]))]
            split = source_start + (strong_start - source_start) * weights[0] / sum(weights)
            synthetic = [
                {"word": tokens[0], "start": source_start, "end": split,
                 "probability": 0.0, "source_anchor_interpolation": True},
                {"word": tokens[1], "start": split, "end": strong_start,
                 "probability": float(replacements[0].get("probability", 0.0)),
                 "source_anchor_interpolation": True},
            ]
            replacements = synthetic + replacements[1:]
            available = [
                (0, {"type": "source-anchor-interpolation"}),
                (1, {"type": "source-anchor-interpolation"}),
            ] + available[1:]
            complete = True
        if ((replacement_timing_consensus or geometry_rescue)
                and line.words and line.source_timestamp is not None
                and replacements):
            durations = [float(word["end"]) - float(word["start"]) for word in replacements]
            median_duration = statistics.median(durations)
            first_duration = durations[0]
            source = float(line.source_timestamp)
            # Whisper occasionally assigns the entire pre-phrase lead-in to
            # the first token. Keep the independently aligned onset in that
            # very specific outlier case while retaining the stable boundaries
            # that resolved the misheard chorus later in the same line.
            if (float(replacements[0]["start"]) < source - 0.35
                    and (geometry_rescue
                         or first_duration > max(1.0, median_duration * 3.5))):
                old = line.words[0]
                preserved_start = max(source, float(old["start"]))
                replacements[0]["start"] = min(
                    preserved_start, float(replacements[0]["end"]) - 0.04)
                if not geometry_rescue:
                    replacements[0]["end"] = (float(replacements[1]["start"])
                                                if len(replacements) > 1 else float(old["end"]))
                replacements[0]["stable_ts_leadin_outlier_rejected"] = True
        if allow_unanchored:
            for word in replacements:
                if float(word["end"]) <= float(word["start"]):
                    word["end"] = round(float(word["start"]) + 0.04, 3)
        if any(float(word["end"]) <= float(word["start"]) for word in replacements):
            diagnostics.append({"line": line_index + 1, "status": "invalid-word-duration"})
            continue
        source_consistent = (
            line.source_timestamp is not None and replacements
            and abs(float(replacements[0]["start"]) - float(line.source_timestamp))
            <= max_start_deviation
        )
        if line.words and not source_consistent and any(
                abs(float(replacement["start"]) - float(line.words[offset]["start"]))
                > max_start_deviation
                for (offset, _operation), replacement in zip(available, replacements)):
            diagnostics.append({"line": line_index + 1, "status": "needs-consensus",
                                "matched_words": len(available)})
            continue
        if allow_unanchored and not complete:
            phrase_start = float(replacements[0]["start"])
            phrase_end = float(replacements[-1]["end"])
            weights = [max(1, len(token)) for token in tokens]
            if phrase_end - phrase_start < len(tokens) * 0.03:
                diagnostics.append({"line": line_index + 1,
                                    "status": "interpolation-window-too-short"})
                continue
            cursor = phrase_start
            total = sum(weights)
            line.words = []
            for token, weight in zip(tokens, weights):
                end = cursor + (phrase_end - phrase_start) * weight / total
                line.words.append({
                    "word": token, "start": round(cursor, 3), "end": round(end, 3),
                    "timing_source": "stable-ts-interpolated",
                })
                cursor = end
            line.timestamp = phrase_start
            accepted_lines += 1
            accepted_words += len(available)
            diagnostics.append({"line": line_index + 1, "status": "accepted-interpolated",
                                "matched_words": len(available), "line_words": len(tokens)})
            continue
        proposed = ([dict(word) for word in line.words] if line.words else [
            {"word": token, "start": float(replacement["start"]),
             "end": float(replacement["end"])}
            for token, replacement in zip(tokens, replacements)
        ])
        for (offset, _operation), replacement in zip(available, replacements):
            proposed[offset]["start"] = float(replacement["start"])
            proposed[offset]["end"] = float(replacement["end"])
        starts = [float(word["start"]) for word in proposed]
        ends = [float(word["end"]) for word in proposed]
        if (any(right < left for left, right in zip(starts, starts[1:]))
                or any(end <= start or end - start > 6.0 for start, end in zip(starts, ends))):
            diagnostics.append({"line": line_index + 1, "status": "mixed-geometry-rejected",
                                "matched_words": len(available)})
            continue
        if not line.words:
            line.words = proposed
        for (offset, operation), replacement in zip(available, replacements):
            word = line.words[offset]
            for stale_key in (
                    "phonemes", "phoneme_source", "phoneme_confidence",
                    "syllables", "syllable_confidence", "syllable_method",
                    "acoustic_end", "sustain_activity_end",
                    "sustain_release_confidence", "sustain_extension_ms",
                    "display_duration_floor_ms"):
                word.pop(stale_key, None)
            word["start"] = float(replacement["start"])
            word["end"] = float(replacement["end"])
            word["timing_source"] = (
                "stable-ts-source-anchor-interpolation"
                if replacement.get("source_anchor_interpolation") else
                "stable-ts-whisper")
            word["stable_ts_probability"] = replacement["probability"]
            word["stable_ts_match"] = operation["type"]
            if replacement.get("stable_ts_leadin_outlier_rejected"):
                word["stable_ts_leadin_outlier_rejected"] = True
        line.timestamp = float(line.words[0]["start"])
        accepted_lines += 1
        accepted_words += len(available)
        diagnostics.append({"line": line_index + 1,
                            "status": ("accepted-leading-source-anchor-rescue"
                                       if leading_source_anchor_rescue else
                                       "accepted-source-anchor-rescue"
                                       if source_anchor_rescue else
                                       "accepted-gross-geometry-rescue"
                                       if geometry_rescue else
                                       "accepted-replacement-timing-consensus"
                                       if replacement_timing_consensus else
                                       "accepted" if complete else "accepted-partial"),
                            "words": len(available), "line_words": len(line.words)})
    return {"method": "stable-ts-complete-line-v1", "attempted_lines": attempted,
            "accepted_lines": accepted_lines, "accepted_words": accepted_words,
            "line_diagnostics": diagnostics}
