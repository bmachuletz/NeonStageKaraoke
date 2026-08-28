from __future__ import annotations

import json
from pathlib import Path
import os

from .audio import ffmpeg_to_mono16k, load_audio
from .basic_pitch_evidence import analyze_and_refine_line_onsets
from .candidate_selection import blend_audio
from .consensus import eliminate_remaining_line_overlaps
from .lrc import parse_lrc, render_enhanced_lrc
from .models import AlignmentConfig
from .validator import validate
from .separator import separate_stems


def run(audio_path: Path, lrc_path: Path, output_dir: Path, *, language: str,
        separator: bool, device: str, progress=None,
        provided_vocals: Path | None = None,
        provided_instrumental: Path | None = None,
        baseline_report: Path | None = None,
        alignment_profile: str = "basic-pitch-postprocess") -> dict:
    """Derive B from A without running any stochastic alignment model again."""
    if provided_vocals is None or provided_instrumental is None:
        raise ValueError("Basic-Pitch-Postprocessing benötigt beide gespeicherten Stems.")
    notify = progress or (lambda _percent, _message: None)
    output_dir.mkdir(parents=True, exist_ok=True)
    notify(10, "Kontrollversion A wird unverändert als gemeinsame Basis geladen")
    headers, lines = parse_lrc(lrc_path)
    baseline = {}
    if baseline_report is not None and baseline_report.exists():
        try:
            baseline = json.loads(baseline_report.read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError):
            baseline = {}
    selected_analysis = baseline.get("analysis_stem_selection", {})
    selected_model = str(selected_analysis.get("model") or "").strip()
    if selected_model:
        notify(25, "Der Analysis-Stem der Kontrollversion wird deterministisch rekonstruiert")
        pair = separate_stems(
            audio_path, output_dir / "basic-pitch-analysis-separated",
            model_name=selected_model)
        vocals_wav = ffmpeg_to_mono16k(
            pair.vocals, output_dir / "basic-pitch-analysis-vocals-16k.wav")
        instrumental_wav = ffmpeg_to_mono16k(
            pair.instrumental, output_dir / "basic-pitch-analysis-accompaniment-16k.wav")
        vocal_audio = load_audio(vocals_wav)
        instrumental_audio = load_audio(instrumental_wav)
        selected_type = str(selected_analysis.get("selected_candidate_type") or "")
        if "blend" in selected_type:
            original_wav = ffmpeg_to_mono16k(
                audio_path, output_dir / "basic-pitch-original-16k.wav")
            vocal_audio = blend_audio(
                vocal_audio, load_audio(original_wav),
                float(os.getenv("LRC_ORIGINAL_MIX_BLEND_RATIO", ".15")))
        analysis_source = "reconstructed-selected-analysis-stem"
    else:
        vocals_wav = ffmpeg_to_mono16k(
            provided_vocals, output_dir / "basic-pitch-vocals-16k.wav")
        instrumental_wav = ffmpeg_to_mono16k(
            provided_instrumental, output_dir / "basic-pitch-instrumental-16k.wav")
        vocal_audio, instrumental_audio = load_audio(vocals_wav), load_audio(instrumental_wav)
        analysis_source = "legacy-stage-stem-fallback"
    notify(45, "Basic Pitch analysiert Onsets, Releases und Wiederholungen im Analysis-Stem")
    evidence = analyze_and_refine_line_onsets(lines, vocal_audio, instrumental_audio)
    evidence["analysis_audio_source"] = analysis_source
    overlap_repairs = eliminate_remaining_line_overlaps(lines)
    summary = validate(lines, AlignmentConfig(language=language))
    stem = audio_path.stem
    lrc_out = output_dir / f"{stem}.word-synced.lrc"
    report_out = output_dir / f"{stem}.alignment.json"
    lrc_out.write_text(render_enhanced_lrc(headers, lines), encoding="utf-8")
    report = {
        **baseline, **summary,
        "alignment_profile": alignment_profile,
        "basic_pitch_evidence": evidence,
        "basic_pitch_analysis": evidence,
        "ab_baseline": {
            "shared": True, "method": "immutable-control-output-v1",
            "source_lrc": lrc_path.name,
            "source_report": baseline_report.name if baseline_report else None,
            "stochastic_models_rerun": False,
        },
        "postprocess_overlap_repairs": overlap_repairs,
        "output_lrc": lrc_out.name,
        "output_report": report_out.name,
    }
    report_out.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    notify(100, "Basic-Pitch-Behandlung B wurde aus Kontrolle A abgeleitet")
    return {
        **summary,
        "alignment_profile": alignment_profile,
        "basic_pitch_evidence": evidence,
        "ab_baseline": report["ab_baseline"],
        "output_lrc": lrc_out.name,
        "output_report": report_out.name,
        "stems": {},
    }
