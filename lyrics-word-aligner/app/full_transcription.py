from __future__ import annotations

import gc
import json
import os
import tempfile
from pathlib import Path
from typing import Callable

from .lrc import render_enhanced_lrc
from .models import LrcLine
from .transcript_match import compare_transcripts, normalize_words

ProgressCallback = Callable[[int, str], None]


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
        device: str, progress: ProgressCallback | None = None) -> dict:
    # Heavy CUDA modules stay out of the long-lived API process and unit-test
    # import path. They are loaded only inside the isolated worker subprocess.
    from .aligner import QwenWordAligner
    from .audio import ffmpeg_to_mono16k, load_audio, select_alignment_audio
    from .candidate_selection import blend_audio
    from .separator import separate_stems
    from .stable_transcriber import transcribe_stable
    from .transcriber import QwenTranscriber, language_code

    notify = progress or (lambda _percent, _message: None)
    output_dir.mkdir(parents=True, exist_ok=True)
    stem = audio_path.stem
    with tempfile.TemporaryDirectory(prefix="lyrics-transcribe-") as temporary:
        temp = Path(temporary)
        notify(8, "Audiospur wird für die Volltext-Erkennung vorbereitet")
        if separate:
            notify(12, "Lead-Vocals werden für die Transkription isoliert")
            separated = separate_stems(audio_path, temp / "separated")
            vocal_source = separated.vocals
            notify(34, "Vocal-Separation abgeschlossen; GPU-Speicher wird freigegeben")
        else:
            vocal_source = audio_path
        _release_memory()

        vocal_wav = ffmpeg_to_mono16k(vocal_source, temp / "vocals-16k.wav")
        mix_wav = ffmpeg_to_mono16k(audio_path, temp / "mix-16k.wav")
        vocals = load_audio(vocal_wav)
        mix = load_audio(mix_wav)
        recognition_audio, audio_selection = select_alignment_audio(
            vocals, mix,
            minimum_rms_ratio=float(os.getenv("LRC_MIN_VOCAL_MIX_RMS_RATIO", "0.05")),
        )
        if separate and not audio_selection["fallback_used"]:
            mix_ratio = float(os.getenv("LRC_TRANSCRIPTION_MIX_RATIO", "0.15"))
            recognition_audio = blend_audio(recognition_audio, mix, mix_ratio)
            audio_selection = {**audio_selection, "source": "vocals-original-blend",
                               "original_mix_ratio": mix_ratio}

        notify(40, "Qwen3-ASR erkennt den vollständigen Gesangstext")
        requested_language = language.strip().lower()
        qwen_prompt = _prompt(stem, requested_language)
        with QwenTranscriber(device) as qwen:
            qwen_result = qwen.transcribe(recognition_audio, requested_language,
                                          prompt=qwen_prompt)
        _release_memory()
        qwen_text = str(qwen_result.get("text", "")).strip()
        if not qwen_text:
            raise ValueError("Qwen3-ASR konnte keinen Gesangstext erkennen.")
        detected_language = language_code(qwen_result.get("language"), qwen_text)
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
        forced_error = None
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
            notify(76, "Qwen Forced Aligner setzt Wortzeiten für den erkannten Volltext")
            aligner = QwenWordAligner(device=device)
            try:
                selected_words = aligner.align_text(
                    recognition_audio, qwen_text, detected_language)
                selected_source = "qwen3-asr+qwen3-forced-aligner"
            except (RuntimeError, ValueError) as error:
                forced_error = str(error)
                selected_words = stable_words
            finally:
                aligner.close()
                _release_memory()

        if not selected_words:
            raise ValueError("Die Volltext-Erkennung lieferte keine belastbaren Wortzeiten.")
        notify(90, "Erkannte Wörter werden zu kollisionsfreien Karaoke-Zeilen gruppiert")
        lines = group_words_into_lines(
            selected_words,
            gap_seconds=float(os.getenv("LRC_TRANSCRIPTION_LINE_GAP", "0.9")),
            maximum_words=int(os.getenv("LRC_TRANSCRIPTION_LINE_WORDS", "11")),
            maximum_characters=int(os.getenv("LRC_TRANSCRIPTION_LINE_CHARS", "64")),
        )
        if not lines:
            raise ValueError("Aus der Transkription konnten keine Lyrics-Zeilen erzeugt werden.")

        lrc_out = output_dir / f"{stem}.transcribed.lrc"
        report_out = output_dir / f"{stem}.transcription.json"
        text_out = output_dir / f"{stem}.transcribed.txt"
        lrc_out.write_text(render_enhanced_lrc([], lines), encoding="utf-8")
        text_out.write_text("\n".join(line.text for line in lines) + "\n", encoding="utf-8")
        report = {
            "version": 1,
            "language": detected_language,
            "source": selected_source,
            "audio_selection": audio_selection,
            "qwen": {**qwen_result, "prompt": qwen_prompt,
                     "word_count": qwen_word_count},
            "stable_ts": {**stable_result, "word_count": stable_word_count},
            "comparison": comparison,
            "forced_alignment_error": forced_error,
            "line_count": len(lines),
            "word_count": sum(len(line.words) for line in lines),
            "resource_policy": "isolated-process-sequential-models",
            "output_lrc": lrc_out.name,
            "output_text": text_out.name,
        }
        report_out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        report["output_report"] = report_out.name
        notify(100, "Vollständige Lyrics-Erkennung abgeschlossen")
        return report
    
