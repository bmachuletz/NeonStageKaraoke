from __future__ import annotations

import base64
import json
import os
import re
import tempfile
from pathlib import Path
from typing import Callable

from .audio import ffmpeg_to_flac, ffmpeg_to_mono16k, load_audio
from .lrc import parse_lrc, render_enhanced_lrc
from .separator import KARAOKE_MODEL, StemPaths, separate_stems
from .vocal_activity import detect_vocal_activity


ProgressCallback = Callable[[int, str], None]
EDITOR_SYLLABLE_RE = re.compile(
    r"^\[neon-editor-syllables:(?P<payload>[A-Za-z0-9_-]+)\]$")
RECORDING_OFFSET_RE = re.compile(
    r"^\[neon-usdb-recording-offset:(?P<seconds>-?\d+(?:\.\d+)?)\]$")


def _first_lyric_start(lines) -> float:
    starts = [float(word["start"]) for line in lines for word in line.words]
    if starts:
        return min(starts)
    return min(float(line.timestamp) for line in lines)


def choose_rigid_offset(first_lyric_start: float,
                        activity: list[tuple[float, float]], *,
                        preserve_window: float = 0.45,
                        search_window: float = 4.0) -> tuple[float, dict]:
    """Find only a recording-level offset; never infer internal word timing.

    UltraStar commonly includes a small perceptual/beat lead.  A nearby vocal
    onset therefore means that the chart is already on the intended timeline
    and must remain unchanged.  Only a substantial common recording offset is
    corrected.
    """
    candidates = [start for start, end in activity
                  if end >= first_lyric_start - search_window
                  and start <= first_lyric_start + search_window]
    if not candidates:
        return 0.0, {"applied": False, "reason": "no-nearby-vocal-onset"}
    onset = min(candidates, key=lambda value: abs(value - first_lyric_start))
    delta = onset - first_lyric_start
    if abs(delta) <= preserve_window:
        return 0.0, {
            "applied": False, "reason": "ultrastar-perceptual-lead-preserved",
            "first_lyric_start": round(first_lyric_start, 3),
            "nearest_vocal_onset": round(onset, 3),
            "measured_delta_ms": round(delta * 1000, 1),
        }
    return delta, {
        "applied": True, "reason": "recording-level-leading-offset",
        "first_lyric_start": round(first_lyric_start, 3),
        "nearest_vocal_onset": round(onset, 3),
        "offset_ms": round(delta * 1000, 1),
    }


def _shift_syllable_headers(headers: list[str], offset: float) -> list[str]:
    if abs(offset) < 0.0005:
        return headers
    shifted: list[str] = []
    for header in headers:
        match = EDITOR_SYLLABLE_RE.fullmatch(header.strip())
        if match is None:
            shifted.append(header)
            continue
        try:
            token = match.group("payload")
            payload = json.loads(base64.urlsafe_b64decode(
                token + "=" * (-len(token) % 4)))
            for key in ("Start", "End"):
                if key in payload:
                    payload[key] = max(0.0, float(payload[key]) + offset)
            for syllable in payload.get("Syllables", []):
                for key in ("Start", "End"):
                    if key in syllable:
                        syllable[key] = max(0.0, float(syllable[key]) + offset)
            encoded = base64.urlsafe_b64encode(json.dumps(
                payload, ensure_ascii=False, separators=(",", ":")
            ).encode("utf-8")).decode("ascii").rstrip("=")
            shifted.append(f"[neon-editor-syllables:{encoded}]")
        except (ValueError, TypeError, json.JSONDecodeError):
            shifted.append(header)
    return shifted


def apply_rigid_offset(headers: list[str], lines, offset: float) -> list[str]:
    if abs(offset) < 0.0005:
        return headers
    for line in lines:
        line.timestamp = max(0.0, float(line.timestamp) + offset)
        if line.source_timestamp is not None:
            line.source_timestamp = max(0.0, float(line.source_timestamp) + offset)
        if line.source_end_boundary is not None:
            line.source_end_boundary = max(0.0, float(line.source_end_boundary) + offset)
        for word in line.words:
            word["start"] = max(0.0, float(word["start"]) + offset)
            word["end"] = max(word["start"], float(word.get("end", word["start"])) + offset)
            word["timing_source"] = "trusted-ultrastar-rigid"
            for key in ("editor_word_start", "editor_word_end"):
                if key in word:
                    word[key] = max(0.0, float(word[key]) + offset)
            for collection in ("syllables", "editor_syllables", "editor_reference_syllables"):
                for syllable in word.get(collection, []):
                    syllable["start"] = max(0.0, float(syllable["start"]) + offset)
                    syllable["end"] = max(
                        syllable["start"], float(syllable.get("end", syllable["start"])) + offset)
    return _shift_syllable_headers(headers, offset)


def resolve_recording_offset(headers: list[str], measured_offset: float,
                             diagnostic: dict) -> tuple[float, dict]:
    declared = next((float(match.group("seconds")) for header in headers
                     if (match := RECORDING_OFFSET_RE.fullmatch(header.strip()))), 0.0)
    if abs(declared) < 0.0005:
        return measured_offset, diagnostic
    if diagnostic.get("applied") and abs(measured_offset - declared) > 0.75:
        raise ValueError(
            "Der USDB-Randstille-Versatz widerspricht dem getrennten Vocal-Einsatz; "
            "das reguläre Alignment ist erforderlich.")
    return declared, {
        **diagnostic, "applied": True,
        "reason": "server-verified-edge-silence-offset",
        "offset_ms": round(declared * 1000, 1),
        "vocal_onset_offset_ms": round(measured_offset * 1000, 1),
    }


def run_trusted_ultrastar(audio_path: Path, lrc_path: Path, output_dir: Path, *,
                          separator: bool, provided_vocals: Path | None = None,
                          provided_instrumental: Path | None = None,
                          progress: ProgressCallback | None = None, **_) -> dict:
    if not separator:
        raise ValueError("Der vertrauenswürdige UltraStar-Import benötigt die Stem-Trennung.")
    if (provided_vocals is None) != (provided_instrumental is None):
        raise ValueError("Vorhandene Vocal- und Instrumental-Stems müssen gemeinsam angegeben werden.")
    notify = progress or (lambda _percent, _message: None)
    output_dir.mkdir(parents=True, exist_ok=True)
    headers, lines = parse_lrc(lrc_path)
    if not lines or any(not line.words for line in lines):
        raise ValueError("Der vertrauenswürdige UltraStar-Import benötigt Noten-/Worttimings.")
    stem = audio_path.stem
    notify(10, "UltraStar-Timings werden unverändert übernommen")
    with tempfile.TemporaryDirectory(prefix="trusted-ultrastar-") as temporary:
        temp = Path(temporary)
        if provided_vocals is not None:
            stems = StemPaths(provided_vocals, provided_instrumental)
            separator_model = "provided-library-stems"
        else:
            notify(18, "Vocal- und Instrumental-Stems werden erzeugt")
            stems = separate_stems(audio_path, temp / "separated")
            separator_model = KARAOKE_MODEL
        notify(72, "Globaler Vocal-Einsatz der Aufnahme wird geprüft")
        vocal_wav = ffmpeg_to_mono16k(stems.vocals, temp / "vocals-16k.wav")
        activity = detect_vocal_activity(load_audio(vocal_wav))
        first_start = _first_lyric_start(lines)
        measured_offset, offset_diagnostic = choose_rigid_offset(first_start, activity)
        offset, offset_diagnostic = resolve_recording_offset(
            headers, measured_offset, offset_diagnostic)
        headers = apply_rigid_offset(headers, lines, offset)
        vocals_out = output_dir / f"{stem}.vocals.flac"
        instrumental_out = output_dir / f"{stem}.instrumental.flac"
        ffmpeg_to_flac(stems.vocals, vocals_out)
        ffmpeg_to_flac(stems.instrumental, instrumental_out)

    lrc_out = output_dir / f"{stem}.word-synced.lrc"
    report_out = output_dir / f"{stem}.alignment.json"
    stems_out = output_dir / f"{stem}.stems.json"
    lrc_out.write_text(render_enhanced_lrc(headers, lines), encoding="utf-8")
    details = [{
        "timestamp": line.timestamp, "source_timestamp": line.source_timestamp,
        "source_end_boundary": line.source_end_boundary, "text": line.text,
        "status": "ok", "reason": None, "voice_lane": line.voice_lane,
        "voice_label": line.voice_label, "words": line.words,
    } for line in lines]
    report = {
        "lines": len(lines), "ok": len(lines), "uncertain": 0,
        "quality": {
            "score": 100.0, "grade": "excellent", "publishable": True,
            "word_duration_coverage": 1.0,
            "acoustically_aligned_word_coverage": 1.0,
            "heuristically_placed_words": 0, "geometrically_repaired_lines": 0,
            "line_overlap_conflicts": 0, "stage_vocal_onset_conflicts": 0,
            "word_overlap_conflicts": 0, "compressed_word_runs": 0,
        },
        "language": "auto", "alignment_profile": "trusted-ultrastar",
        "alignment_mode": "trusted-ultrastar-rigid-offset",
        "input_timing": "ultrastar-note-timing",
        "ai_alignment_skipped": True,
        "recording_offset": offset_diagnostic,
        "vocal_activity": {"regions": len(activity), "method": "stem-energy-diagnostic-only"},
        "separation": "provided-library-stems" if provided_vocals else "uvr-karaoke",
        "output_lrc": lrc_out.name,
        "stems": {"vocals": vocals_out.name, "instrumental": instrumental_out.name},
        "details": details,
    }
    stems_out.write_text(json.dumps({
        "version": 1, "source": audio_path.name, "separator_model": separator_model,
        "format": "flac", "sample_aligned": True,
        "first_vocal_start": activity[0][0] if activity else None,
        "vocals_muted_before": None,
        "vocals": vocals_out.name, "instrumental": instrumental_out.name,
    }, ensure_ascii=False, indent=2), encoding="utf-8")
    report["stems_manifest"] = stems_out.name
    report_out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    report["output_report"] = report_out.name
    notify(100, "UltraStar-Import ohne AI-Lyrics-Alignment abgeschlossen")
    return report
