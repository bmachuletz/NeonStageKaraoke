from __future__ import annotations
import json
import shutil
import subprocess
import sys
import tempfile
import threading
from pathlib import Path
from fastapi import BackgroundTasks, FastAPI, File, Form, HTTPException, UploadFile
from fastapi.responses import FileResponse, HTMLResponse
from .alignment_profiles import ALIGNMENT_PROFILES
from .vocal_start import detect_first_vocal

app = FastAPI(title="Lyrics Word Aligner", version="0.2.0")
OUTPUT_ROOT = Path("/data/output")
OUTPUT_ROOT.mkdir(parents=True, exist_ok=True)
STATIC_ROOT = Path(__file__).parent / "static"
_LOCK = threading.Lock()
_PROCESS_LOCK = threading.Lock()


def _status_path(job_dir: Path) -> Path:
    return job_dir / "status.json"


def _write_status(job_dir: Path, **changes) -> dict:
    with _LOCK:
        path = _status_path(job_dir)
        current = {}
        if path.exists():
            try:
                current = json.loads(path.read_text(encoding="utf-8"))
            except Exception:
                current = {}
        current.update(changes)
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps(current, ensure_ascii=False, indent=2), encoding="utf-8")
        tmp.replace(path)
        return current


def _process_job(job_id: str, audio_path: Path, lrc_path: Path, language: str,
                 separate: bool, device: str, alignment_profile: str = "standard",
                 provided_vocals: Path | None = None,
                 provided_instrumental: Path | None = None,
                 baseline_report: Path | None = None) -> None:
    job_dir = OUTPUT_ROOT / job_id
    try:
        _write_status(job_dir, state="queued", percent=1, message="Job wartet auf den freien Verarbeitungsslot")
        with _PROCESS_LOCK:
            _write_status(job_dir, state="processing", percent=2, message="Verarbeitung wird gestartet")

            completed = subprocess.run(
                _worker_command(
                    job_id, audio_path, lrc_path, job_dir, language, separate, device,
                    provided_vocals, provided_instrumental, alignment_profile,
                    baseline_report),
                check=False,
            )
            status = json.loads(_status_path(job_dir).read_text(encoding="utf-8"))
            if completed.returncode != 0 and status.get("state") != "failed":
                _write_status(job_dir, state="failed", percent=100,
                              message=f"Aligner-Worker endete mit Code {completed.returncode}")
    except Exception as exc:
        _write_status(job_dir, state="failed", percent=100, message=str(exc), error=str(exc))


def _worker_command(job_id: str, audio_path: Path, lrc_path: Path, job_dir: Path,
                    language: str, separate: bool, device: str,
                    provided_vocals: Path | None = None,
                    provided_instrumental: Path | None = None,
                    alignment_profile: str = "standard",
                    baseline_report: Path | None = None) -> list[str]:
    command = [
        sys.executable, "-m", "app.job_worker",
        "--job-id", job_id,
        "--audio", str(audio_path),
        "--lyrics", str(lrc_path),
        "--output", str(job_dir),
        "--language", language,
        "--device", device,
        "--alignment-profile", alignment_profile,
    ]
    if separate:
        command.append("--separate")
    if provided_vocals is not None:
        command.extend(["--provided-vocals", str(provided_vocals)])
    if provided_instrumental is not None:
        command.extend(["--provided-instrumental", str(provided_instrumental)])
    if baseline_report is not None:
        command.extend(["--baseline-report", str(baseline_report)])
    return command


def _transcription_worker_command(job_id: str, audio_path: Path, job_dir: Path,
                                  language: str, separate: bool, device: str,
                                  canonical_path: Path | None = None) -> list[str]:
    command = [
        sys.executable, "-m", "app.transcription_worker",
        "--job-id", job_id, "--audio", str(audio_path), "--output", str(job_dir),
        "--language", language, "--device", device,
    ]
    if separate:
        command.append("--separate")
    if canonical_path is not None:
        command.extend(["--canonical", str(canonical_path)])
    return command


def _process_transcription_job(job_id: str, audio_path: Path, language: str,
                               separate: bool, device: str,
                               canonical_path: Path | None = None) -> None:
    job_dir = OUTPUT_ROOT / job_id
    try:
        _write_status(job_dir, state="queued", percent=1,
                      message="Volltext-Job wartet auf den freien GPU-Slot")
        with _PROCESS_LOCK:
            _write_status(job_dir, state="processing", percent=2,
                          message="Isolierter Volltext-Worker wird gestartet")
            completed = subprocess.run(
                _transcription_worker_command(
                    job_id, audio_path, job_dir, language, separate, device,
                    canonical_path),
                check=False,
            )
            status = json.loads(_status_path(job_dir).read_text(encoding="utf-8"))
            if completed.returncode != 0 and status.get("state") != "failed":
                _write_status(job_dir, state="failed", percent=100,
                              message=f"Transkriptions-Worker endete mit Code {completed.returncode}")
    except Exception as exc:
        _write_status(job_dir, state="failed", percent=100, message=str(exc), error=str(exc))


@app.get("/", response_class=HTMLResponse)
def index():
    return (STATIC_ROOT / "index.html").read_text(encoding="utf-8")


@app.get("/health")
def health():
    return {"status": "ok"}


@app.post("/api/jobs", status_code=202)
def create_job(
    background_tasks: BackgroundTasks,
    audio: UploadFile = File(...),
    lyrics: UploadFile = File(...),
    vocals: UploadFile | None = File(None),
    instrumental: UploadFile | None = File(None),
    baseline_report: UploadFile | None = File(None),
    language: str = Form("de"),
    separate: bool = Form(True),
    alignment_device: str = Form("cuda"),
    alignment_profile: str = Form("standard"),
):
    if alignment_device not in {"cpu", "cuda", "auto"}:
        raise HTTPException(400, "alignment_device muss cpu, cuda oder auto sein")
    if alignment_profile not in ALIGNMENT_PROFILES:
        raise HTTPException(
            400, "unbekanntes alignment_profile")
    if not (lyrics.filename or "").lower().endswith(".lrc"):
        raise HTTPException(400, "Die Lyrics-Datei muss eine .lrc-Datei sein")
    if (vocals is None) != (instrumental is None):
        raise HTTPException(
            400, "Vorhandene Vocal- und Instrumentalspur müssen gemeinsam hochgeladen werden")
    if vocals is not None and not separate:
        raise HTTPException(400, "Vorhandene Stems erfordern separate=true")
    if alignment_profile == "basic-pitch-postprocess" and (
            vocals is None or instrumental is None or baseline_report is None):
        raise HTTPException(400, "Basic-Pitch-Postprocessing benötigt Stems und Baseline-Report")

    job_id = next(tempfile._get_candidate_names())
    job_dir = OUTPUT_ROOT / job_id
    job_dir.mkdir(parents=True)
    audio_path = job_dir / Path(audio.filename or "song.mp3").name
    lrc_path = job_dir / Path(lyrics.filename or "song.lrc").name
    with audio_path.open("wb") as f:
        shutil.copyfileobj(audio.file, f)
    with lrc_path.open("wb") as f:
        shutil.copyfileobj(lyrics.file, f)
    provided_vocals = None
    provided_instrumental = None
    if vocals is not None and instrumental is not None:
        vocals_suffix = Path(vocals.filename or "vocals.flac").suffix or ".flac"
        instrumental_suffix = Path(instrumental.filename or "instrumental.flac").suffix or ".flac"
        provided_vocals = job_dir / f"provided.vocals{vocals_suffix}"
        provided_instrumental = job_dir / f"provided.instrumental{instrumental_suffix}"
        with provided_vocals.open("wb") as target:
            shutil.copyfileobj(vocals.file, target)
        with provided_instrumental.open("wb") as target:
            shutil.copyfileobj(instrumental.file, target)
    saved_baseline_report = None
    if baseline_report is not None:
        saved_baseline_report = job_dir / "baseline.alignment.json"
        with saved_baseline_report.open("wb") as target:
            shutil.copyfileobj(baseline_report.file, target)

    status = {
        "job_id": job_id,
        "state": "queued",
        "percent": 0,
        "message": "Dateien hochgeladen; Job wartet auf Verarbeitung",
        "audio_name": audio_path.name,
        "lyrics_name": lrc_path.name,
        "audio_reference": ("provided-library-stems"
                            if provided_vocals is not None else "new-separation"),
        "alignment_profile": alignment_profile,
    }
    _write_status(job_dir, **status)
    background_tasks.add_task(
        _process_job, job_id, audio_path, lrc_path, language, separate,
        alignment_device, alignment_profile, provided_vocals, provided_instrumental,
        saved_baseline_report)
    return status


@app.post("/api/transcription-jobs", status_code=202)
def create_transcription_job(
    background_tasks: BackgroundTasks,
    audio: UploadFile = File(...),
    lyrics: UploadFile | None = File(None),
    language: str = Form("auto"),
    separate: bool = Form(True),
    alignment_device: str = Form("cuda"),
):
    if alignment_device not in {"cuda", "auto"}:
        raise HTTPException(400, "Die Volltext-Erkennung muss auf cuda oder auto laufen.")
    if language.strip().lower() not in {"auto", "de", "en", "fr", "es", "it", "pt", "ru", "ja", "ko", "zh", "yue"}:
        raise HTTPException(400, "Die gewählte Sprache wird nicht unterstützt.")
    if lyrics is not None and not (lyrics.filename or "").lower().endswith(".lrc"):
        raise HTTPException(400, "Die kanonischen Lyrics müssen eine .lrc-Datei sein.")
    job_id = next(tempfile._get_candidate_names())
    job_dir = OUTPUT_ROOT / job_id
    job_dir.mkdir(parents=True)
    audio_path = job_dir / Path(audio.filename or "song.mp3").name
    with audio_path.open("wb") as target:
        shutil.copyfileobj(audio.file, target)
    canonical_path = None
    if lyrics is not None:
        canonical_path = job_dir / "canonical.lrc"
        with canonical_path.open("wb") as target:
            shutil.copyfileobj(lyrics.file, target)
    status = {
        "job_id": job_id, "state": "queued", "percent": 0,
        "message": "Audio hochgeladen; Volltext-Job wartet auf Verarbeitung",
        "audio_name": audio_path.name, "job_type": "full-transcription",
        "canonical_lyrics_name": Path(lyrics.filename).name if lyrics and lyrics.filename else None,
    }
    _write_status(job_dir, **status)
    background_tasks.add_task(
        _process_transcription_job, job_id, audio_path, language, separate,
        alignment_device, canonical_path)
    return status


@app.get("/api/jobs/{job_id}")
def job_status(job_id: str):
    job_dir = OUTPUT_ROOT / Path(job_id).name
    path = _status_path(job_dir)
    if not path.is_file():
        raise HTTPException(404, "Job nicht gefunden")
    return json.loads(path.read_text(encoding="utf-8"))


# Bestehende synchrone API bleibt kompatibel.
@app.post("/align")
def align(
    audio: UploadFile = File(...),
    lyrics: UploadFile = File(...),
    vocals: UploadFile | None = File(None),
    instrumental: UploadFile | None = File(None),
    language: str = Form("de"),
    separate: bool = Form(True),
    alignment_device: str = Form("cuda"),
):
    if alignment_device not in {"cpu", "cuda", "auto"}:
        raise HTTPException(400, "alignment_device muss cpu, cuda oder auto sein")
    if (vocals is None) != (instrumental is None):
        raise HTTPException(
            400, "Vorhandene Vocal- und Instrumentalspur müssen gemeinsam hochgeladen werden")
    if vocals is not None and not separate:
        raise HTTPException(400, "Vorhandene Stems erfordern separate=true")
    job_id = next(tempfile._get_candidate_names())
    job_dir = OUTPUT_ROOT / job_id
    job_dir.mkdir(parents=True)
    try:
        audio_path = job_dir / Path(audio.filename or "song.mp3").name
        lrc_path = job_dir / Path(lyrics.filename or "song.lrc").name
        with audio_path.open("wb") as f:
            shutil.copyfileobj(audio.file, f)
        with lrc_path.open("wb") as f:
            shutil.copyfileobj(lyrics.file, f)
        provided_vocals = None
        provided_instrumental = None
        if vocals is not None and instrumental is not None:
            vocals_suffix = Path(vocals.filename or "vocals.flac").suffix or ".flac"
            instrumental_suffix = Path(instrumental.filename or "instrumental.flac").suffix or ".flac"
            provided_vocals = job_dir / f"provided.vocals{vocals_suffix}"
            provided_instrumental = job_dir / f"provided.instrumental{instrumental_suffix}"
            with provided_vocals.open("wb") as target:
                shutil.copyfileobj(vocals.file, target)
            with provided_instrumental.open("wb") as target:
                shutil.copyfileobj(instrumental.file, target)
        _write_status(job_dir, job_id=job_id, state="processing", percent=2,
                      message="Verarbeitung wird gestartet")
        with _PROCESS_LOCK:
            completed = subprocess.run(
                _worker_command(job_id, audio_path, lrc_path, job_dir, language,
                                separate, alignment_device, provided_vocals,
                                provided_instrumental),
                check=False,
            )
        result = json.loads(_status_path(job_dir).read_text(encoding="utf-8"))
        if completed.returncode != 0 or result.get("state") != "completed":
            raise RuntimeError(result.get("error") or result.get("message") or
                               f"Aligner-Worker endete mit Code {completed.returncode}")
        return result
    except Exception as exc:
        raise HTTPException(500, str(exc)) from exc


@app.post("/analyze/vocal-start")
def analyze_vocal_start(
    audio: UploadFile = File(...),
    text: str = Form(...),
    language: str = Form("de"),
    separate: bool = Form(True),
    alignment_device: str = Form("cuda"),
    search_seconds: float = Form(30.0),
):
    if alignment_device not in {"cuda", "auto"}:
        raise HTTPException(400, "Die Vocal-Start-Analyse muss auf cuda oder auto laufen.")
    job_id = next(tempfile._get_candidate_names())
    job_dir = OUTPUT_ROOT / job_id
    job_dir.mkdir(parents=True)
    audio_path = job_dir / Path(audio.filename or "song.mp3").name
    try:
        with audio_path.open("wb") as f:
            shutil.copyfileobj(audio.file, f)
        with _PROCESS_LOCK:
            result = detect_first_vocal(
                audio_path,
                text,
                language=language,
                separator=separate,
                device=alignment_device,
                search_seconds=search_seconds,
            )
        result.update({"job_id": job_id, "audio_name": audio_path.name})
        report_path = job_dir / "vocal-start.json"
        report_path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
        return result
    except ValueError as exc:
        raise HTTPException(400, str(exc)) from exc
    except Exception as exc:
        raise HTTPException(500, str(exc)) from exc


@app.get("/jobs/{job_id}/{filename}")
def download(job_id: str, filename: str):
    safe_job = Path(job_id).name
    safe_name = Path(filename).name
    path = OUTPUT_ROOT / safe_job / safe_name
    if not path.is_file():
        raise HTTPException(404, "Datei nicht gefunden")
    return FileResponse(path, filename=safe_name)
