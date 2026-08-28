from __future__ import annotations

import gc
import json
import os
import tempfile
from collections import Counter
from pathlib import Path
from typing import Callable

from .chunk_ownership import move_ownership_seams_to_quiet_audio
from .lrc import render_enhanced_lrc
from .models import LrcLine
from .transcript_match import compare_transcripts, normalize_words

ProgressCallback = Callable[[int, str], None]
SAMPLE_RATE = 16000


def _release_memory() -> None:
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


def audio_chunk_windows(sample_count: int, *, chunk_seconds: float = 20.0,
                        overlap_seconds: float = 2.0,
                        sample_rate: int = SAMPLE_RATE) -> list[tuple[int, int, float, float]]:
    """Create bounded ASR windows with non-overlapping ownership intervals."""
    if sample_count <= 0:
        return []
    if chunk_seconds <= 0:
        raise ValueError("LRC_TRANSCRIPTION_CHUNK_SECONDS muss größer als 0 sein")
    if overlap_seconds < 0 or overlap_seconds >= chunk_seconds:
        raise ValueError(
            "LRC_TRANSCRIPTION_CHUNK_OVERLAP_SECONDS muss zwischen 0 und der Fensterlänge liegen")
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


def plan_transcription_windows(sample_count: int, *, threshold_seconds: float = 300.0,
                               chunk_seconds: float = 20.0,
                               overlap_seconds: float = 2.0,
                               sample_rate: int = SAMPLE_RATE
                               ) -> tuple[list[tuple[int, int, float, float]], bool]:
    """Keep normal tracks whole and chunk only unusually long recordings."""
    if threshold_seconds <= 0:
        raise ValueError("LRC_TRANSCRIPTION_CHUNK_THRESHOLD_SECONDS muss größer als 0 sein")
    duration = sample_count / sample_rate
    if sample_count > 0 and duration <= threshold_seconds:
        return [(0, sample_count, 0.0, duration)], False
    return (audio_chunk_windows(
        sample_count, chunk_seconds=chunk_seconds, overlap_seconds=overlap_seconds,
        sample_rate=sample_rate), True)


def fallback_transcription_windows(sample_count: int, *, initial_was_chunked: bool,
                                   chunk_seconds: float = 20.0,
                                   overlap_seconds: float = 2.0,
                                   sample_rate: int = SAMPLE_RATE
                                   ) -> list[tuple[int, int, float, float]]:
    """Retry an empty whole-song result in bounded windows, but never retry chunks twice."""
    if initial_was_chunked:
        return []
    return audio_chunk_windows(
        sample_count, chunk_seconds=chunk_seconds, overlap_seconds=overlap_seconds,
        sample_rate=sample_rate)


def quiet_transcription_windows(audio, windows: list[tuple[int, int, float, float]],
                                *, sample_rate: int = SAMPLE_RATE
                                ) -> list[tuple[int, int, float, float]]:
    """Keep ASR context windows, but avoid assigning ownership mid-vocal."""
    return move_ownership_seams_to_quiet_audio(windows, audio, sample_rate=sample_rate)


def keep_owned_words(words: list[dict], *, base_seconds: float,
                     keep_start: float, keep_end: float) -> list[dict]:
    """Offset chunk-local words and retain each overlap word exactly once."""
    owned: list[dict] = []
    for source in words:
        start = float(source["start"]) + base_seconds
        end = float(source.get("end", source["start"])) + base_seconds
        midpoint = (start + end) / 2
        if midpoint < keep_start or midpoint >= keep_end:
            continue
        owned.append({**source, "start": round(start, 3), "end": round(end, 3)})
    return owned


def _detected_language(requested: str, chunks: list[dict], full_text: str,
                       language_code: Callable[[str | None, str | None], str]) -> str:
    if requested != "auto":
        return language_code(requested, full_text)
    detected: list[str] = []
    for chunk in chunks:
        try:
            # Qwen occasionally labels clearly German rap as English. Prefer a
            # confident text-based result and retain the model label as fallback
            # for languages our lightweight text heuristic cannot distinguish.
            detected.append(language_code(None, chunk.get("text")))
        except ValueError:
            try:
                detected.append(language_code(chunk.get("language"), chunk.get("text")))
            except ValueError:
                continue
    if detected:
        return Counter(detected).most_common(1)[0][0]
    return language_code(None, full_text)


def group_words_into_lines(words: list[dict], *, gap_seconds: float = 0.9,
                           maximum_words: int = 11,
                           maximum_characters: int = 64) -> list[LrcLine]:
    """Build readable, monotonic karaoke lines without inventing timing."""
    cleaned: list[dict] = []
    previous_end = 0.0
    for source in sorted(words, key=lambda item: (float(item["start"]), float(item["end"]))):
        text = str(source.get("word", "")).strip()
        start = max(0.0, float(source["start"]), previous_end)
        end = max(start + 0.02, float(source["end"]))
        if not text:
            continue
        cleaned.append({**source, "word": text, "start": round(start, 3),
                        "end": round(end, 3)})
        previous_end = end

    groups: list[list[dict]] = []
    current: list[dict] = []
    for word in cleaned:
        projected = " ".join([*(str(item["word"]) for item in current), str(word["word"])])
        pause = float(word["start"]) - float(current[-1]["end"]) if current else 0.0
        sentence_end = bool(current and str(current[-1]["word"]).rstrip().endswith((".", "!", "?")))
        should_split = bool(current) and (
            pause >= gap_seconds or len(current) >= maximum_words or
            len(projected) > maximum_characters or (sentence_end and pause >= 0.25)
        )
        if should_split:
            groups.append(current)
            current = []
        current.append(word)
    if current:
        groups.append(current)

    return [LrcLine(
        float(group[0]["start"]),
        " ".join(str(word["word"]) for word in group),
        "",
        words=group,
        status="generated",
        reason="full-audio-transcription",
        timed_input=False,
    ) for group in groups]


def _prompt(song_name: str, language: str) -> str:
    return (f"Karaoke song '{song_name}'. Language: {language}. "
            "Transcribe every audible sung word faithfully, including repetitions and choruses. "
            "Do not summarize and do not invent missing lyrics.")


def _stable_prompt(song_name: str, language: str) -> str:
    # Whisper's initial_prompt is continuation context rather than a system
    # instruction. Keep it short so instruction words cannot leak into lyrics.
    return f"Karaoke song: {song_name}. Language: {language}."


def run(audio_path: Path, output_dir: Path, *, language: str, separate: bool,
        device: str, canonical_path: Path | None = None,
        progress: ProgressCallback | None = None) -> dict:
    # Heavy CUDA modules stay out of the long-lived API process and unit-test
    # import path. They are loaded only inside the isolated worker subprocess.
    from .aligner import QwenWordAligner
    from .analysis_stems import build_analysis_candidates
    from .audio import ffmpeg_to_mono16k, load_audio
    from .stem_roles import SeparationConfig
    from .stable_transcriber import transcribe_stable
    from .transcriber import QwenTranscriber, language_code, merge_transcript_chunks
    import numpy as np

    notify = progress or (lambda _percent, _message: None)
    output_dir.mkdir(parents=True, exist_ok=True)
    stem = audio_path.stem
    with tempfile.TemporaryDirectory(prefix="lyrics-transcribe-") as temporary:
        temp = Path(temporary)
        notify(8, "Audiospur wird für die Volltext-Erkennung vorbereitet")
        if separate:
            notify(12, "All-Vocals-Stem wird für die Transkription isoliert")
            separation_config = SeparationConfig.from_environment()
            bundle = build_analysis_candidates(
                audio_path, temp / "analysis", None, separation_config,
                minimum_rms_ratio=float(os.getenv(
                    "LRC_MIN_VOCAL_MIX_RMS_RATIO", "0.05")),
            )
            selected = bundle.candidates[0]
            recognition_audio = selected.audio
            audio_selection = {
                "source": selected.type,
                "purpose": "analysis",
                "selected_candidate": selected.id,
                "model": selected.metadata.get("model"),
                "separator": selected.metadata.get("separator"),
                "fallback_used": bundle.legacy_fallback,
                "generation_errors": bundle.errors,
                "original_mix_mode": separation_config.original_mix_mode,
            }
            notify(34, "Analysis-Separation abgeschlossen; GPU-Speicher wird freigegeben")
        else:
            mix_wav = ffmpeg_to_mono16k(audio_path, temp / "mix-16k.wav")
            recognition_audio = load_audio(mix_wav)
            audio_selection = {
                "source": "original-mix", "purpose": "analysis",
                "selected_candidate": "separation-disabled",
                "fallback_used": False,
            }
        _release_memory()

        chunk_seconds = float(os.getenv("LRC_TRANSCRIPTION_CHUNK_SECONDS", "20"))
        chunk_overlap = float(os.getenv("LRC_TRANSCRIPTION_CHUNK_OVERLAP_SECONDS", "2"))
        chunk_threshold = float(os.getenv(
            "LRC_TRANSCRIPTION_CHUNK_THRESHOLD_SECONDS", "300"))
        qwen_windows, chunked_transcription = plan_transcription_windows(
            len(recognition_audio), threshold_seconds=chunk_threshold,
            chunk_seconds=chunk_seconds, overlap_seconds=chunk_overlap)
        if chunked_transcription:
            qwen_windows = quiet_transcription_windows(recognition_audio, qwen_windows)
        if not qwen_windows:
            raise ValueError("Die Audiospur ist leer.")
        notify(40, (f"Langsong: Qwen3-ASR erkennt den Gesang in "
                    f"{len(qwen_windows)} Speicherfenstern")
               if chunked_transcription else
               "Qwen3-ASR erkennt den vollständigen Gesangstext")
        requested_language = language.strip().lower()
        qwen_prompt = _prompt(stem, requested_language)
        chunk_token_limit = int(os.getenv(
            "LRC_TRANSCRIPTION_MAX_NEW_TOKENS" if chunked_transcription
            else "LRC_ASR_MAX_NEW_TOKENS", "256" if chunked_transcription else "1024"))

        def transcribe_windows(qwen: QwenTranscriber, windows, *, token_limit: int) -> list[dict]:
            chunks: list[dict] = []
            for index, (sample_start, sample_end, keep_start, keep_end) in enumerate(windows):
                notify(40 + round(12 * index / max(1, len(windows))),
                       f"Qwen3-ASR: Audioblock {index + 1}/{len(windows)}")
                chunk_audio = np.ascontiguousarray(
                    recognition_audio[sample_start:sample_end], dtype=np.float32)
                result = qwen.transcribe(
                    chunk_audio, requested_language, prompt=qwen_prompt,
                    max_new_tokens=token_limit)
                text = str(result.get("text", "")).strip()
                if text:
                    chunks.append({
                        **result,
                        "text": text,
                        "sample_start": sample_start,
                        "sample_end": sample_end,
                        "start": round(sample_start / SAMPLE_RATE, 3),
                        "end": round(sample_end / SAMPLE_RATE, 3),
                        "keep_start": round(keep_start, 3),
                        "keep_end": round(keep_end, 3),
                    })
                del chunk_audio
                _release_memory()
            return chunks

        with QwenTranscriber(device) as qwen:
            qwen_chunks = transcribe_windows(
                qwen, qwen_windows, token_limit=chunk_token_limit)
            retry_windows = fallback_transcription_windows(
                len(recognition_audio), initial_was_chunked=chunked_transcription,
                chunk_seconds=chunk_seconds, overlap_seconds=chunk_overlap)
            if not qwen_chunks and retry_windows:
                notify(42, ("Qwen3-ASR lieferte für den Gesamttrack keinen Text; "
                            f"erneuter Versuch in {len(retry_windows)} Audioblöcken"))
                qwen_windows = quiet_transcription_windows(
                    recognition_audio, retry_windows)
                chunked_transcription = True
                chunk_token_limit = int(os.getenv(
                    "LRC_TRANSCRIPTION_MAX_NEW_TOKENS", "256"))
                qwen_chunks = transcribe_windows(
                    qwen, qwen_windows, token_limit=chunk_token_limit)
        _release_memory()
        qwen_text = merge_transcript_chunks(
            [str(chunk["text"]) for chunk in qwen_chunks])
        if not qwen_text:
            raise ValueError("Qwen3-ASR konnte keinen Gesangstext erkennen.")
        detected_language = _detected_language(
            requested_language, qwen_chunks, qwen_text, language_code)
        notify(54, f"Gesangssprache: {detected_language}; Stable-ts large-v3 prüft Text und Zeiten")

        stable_result = transcribe_stable(
            recognition_audio, detected_language, device, vad=True,
            initial_prompt=_stable_prompt(stem, detected_language),
        )
        _release_memory()
        stable_words = stable_result.get("words", [])
        stable_text = str(stable_result.get("text", "")).strip()
        comparison = compare_transcripts(qwen_text, stable_text) if stable_text else {
            "similarity": 0.0, "expected_words": len(normalize_words(qwen_text)),
            "recognized_words": 0, "operations": [],
        }

        selected_source = "stable-ts-large-v3"
        selected_words = stable_words
        forced_errors: list[dict] = []
        qwen_word_count = len(normalize_words(qwen_text))
        stable_word_count = len(stable_words)
        # Prefer Qwen's more complete singing transcript unless Stable-ts found
        # substantially more acoustic words. Qwen ForcedAligner then provides
        # monotonic word windows; the normal Neon Stage pipeline will perform a
        # second independent review after this worker exits.
        prefer_qwen = qwen_word_count > 0 and not (
            stable_word_count > qwen_word_count * 1.25 and
            float(comparison.get("similarity", 0.0)) < 0.65
        )
        if prefer_qwen:
            notify(76, "Qwen Forced Aligner setzt Wortzeiten blockweise")
            aligner = QwenWordAligner(device=device)
            try:
                aligned_words: list[dict] = []
                for index, chunk in enumerate(qwen_chunks):
                    notify(76 + round(12 * index / max(1, len(qwen_chunks))),
                           f"Forced Alignment: Audioblock {index + 1}/{len(qwen_chunks)}")
                    sample_start = int(chunk["sample_start"])
                    sample_end = int(chunk["sample_end"])
                    base_seconds = sample_start / SAMPLE_RATE
                    chunk_audio = np.ascontiguousarray(
                        recognition_audio[sample_start:sample_end], dtype=np.float32)
                    try:
                        local_words = aligner.align_text(
                            chunk_audio, str(chunk["text"]), detected_language)
                        aligned_words.extend(keep_owned_words(
                            local_words, base_seconds=base_seconds,
                            keep_start=float(chunk["keep_start"]),
                            keep_end=float(chunk["keep_end"])))
                    except (RuntimeError, ValueError) as error:
                        forced_errors.append({"chunk": index + 1, "error": str(error)})
                        aligned_words.extend([
                            word for word in stable_words
                            if float(chunk["keep_start"]) <=
                            (float(word["start"]) + float(word["end"])) / 2 <
                            float(chunk["keep_end"])
                        ])
                    finally:
                        del chunk_audio
                        _release_memory()
                selected_words = aligned_words
                selected_source = ("qwen3-asr+qwen3-forced-aligner-chunked"
                                   if not forced_errors else
                                   "qwen3-asr+qwen3-forced-aligner+stable-fallback")
            finally:
                aligner.close()
                _release_memory()

        if not selected_words:
            raise ValueError("Die Volltext-Erkennung lieferte keine belastbaren Wortzeiten.")
        notify(90, "Erkannte Wörter werden zu kollisionsfreien Karaoke-Zeilen gruppiert")
        acoustic_lines = group_words_into_lines(
            selected_words,
            gap_seconds=float(os.getenv("LRC_TRANSCRIPTION_LINE_GAP", "0.9")),
            maximum_words=int(os.getenv("LRC_TRANSCRIPTION_LINE_WORDS", "11")),
            maximum_characters=int(os.getenv("LRC_TRANSCRIPTION_LINE_CHARS", "64")),
        )
        if not acoustic_lines:
            raise ValueError("Aus der Transkription konnten keine Lyrics-Zeilen erzeugt werden.")

        headers: list[str] = []
        lines = acoustic_lines
        canonical_transfer: dict = {
            "enabled": canonical_path is not None,
            "applied": False,
            "reason": "keine kanonischen Lyrics übergeben",
        }
        if canonical_path is not None:
            from .canonical_lyrics import transfer_canonical_lines
            from .lrc import parse_lrc

            try:
                canonical_headers, canonical_lines = parse_lrc(canonical_path)
                headers, lines, transfer_report = transfer_canonical_lines(
                    canonical_headers, canonical_lines, selected_words,
                    minimum_coverage=float(os.getenv(
                        "LRC_CANONICAL_TRANSFER_MIN_COVERAGE", "0.55")))
                canonical_transfer = {
                    "enabled": True,
                    "applied": True,
                    **transfer_report,
                }
                notify(92, (f"Kanonische Lyrics übernommen: "
                            f"{transfer_report['mapping_coverage']:.1%} Wortanker"))
            except ValueError as error:
                canonical_transfer = {
                    "enabled": True,
                    "applied": False,
                    "reason": str(error),
                }
                notify(92, "Kanonische Lyrics passen nicht sicher; akustischer Text bleibt erhalten")

        lrc_out = output_dir / f"{stem}.transcribed.lrc"
        report_out = output_dir / f"{stem}.transcription.json"
        text_out = output_dir / f"{stem}.transcribed.txt"
        lrc_out.write_text(render_enhanced_lrc(headers, lines), encoding="utf-8")
        text_out.write_text("\n".join(line.text for line in lines) + "\n", encoding="utf-8")
        report = {
            "version": 2,
            "language": detected_language,
            "source": selected_source,
            "audio_selection": audio_selection,
            "qwen": {
                "model": qwen_chunks[0].get("model"),
                "prompt": qwen_prompt,
                "text": qwen_text,
                "word_count": qwen_word_count,
                "chunks": qwen_chunks,
                "chunk_seconds": chunk_seconds,
                "chunk_overlap_seconds": chunk_overlap,
                "chunk_threshold_seconds": chunk_threshold,
                "chunked": chunked_transcription,
                "max_new_tokens_per_chunk": chunk_token_limit,
            },
            "stable_ts": {**stable_result, "word_count": stable_word_count},
            "comparison": comparison,
            "forced_alignment_errors": forced_errors,
            "canonical_transfer": canonical_transfer,
            "acoustic_line_count": len(acoustic_lines),
            "line_count": len(lines),
            "word_count": sum(len(normalize_words(line.text)) for line in lines),
            "resource_policy": "isolated-process-sequential-models-bounded-audio-chunks",
            "output_lrc": lrc_out.name,
            "output_text": text_out.name,
        }
        report_out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        report["output_report"] = report_out.name
        notify(100, "Vollständige Lyrics-Erkennung abgeschlossen")
        return report
    
