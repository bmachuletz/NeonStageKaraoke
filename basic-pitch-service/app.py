from __future__ import annotations

import os
from pathlib import Path
import tempfile

import numpy as np
from basic_pitch import ICASSP_2022_MODEL_PATH
from basic_pitch.constants import ANNOTATION_HOP
from basic_pitch.inference import Model, predict
from fastapi import FastAPI, File, HTTPException, UploadFile


app = FastAPI(title="Neon Stage Basic Pitch evidence", version="0.2")
_model: Model | None = None


def model() -> Model:
    global _model
    if _model is None:
        _model = Model(ICASSP_2022_MODEL_PATH)
    return _model


def onset_peaks(raw: dict) -> list[dict]:
    """Reduce the frame matrix to compact, language-neutral onset evidence."""
    matrix = np.asarray(raw.get("onset", []), dtype=np.float32)
    if matrix.ndim != 2 or len(matrix) < 3:
        return []
    strengths = matrix.max(axis=1)
    pitches = matrix.argmax(axis=1)
    threshold = float(os.getenv("BASIC_PITCH_RAW_ONSET_THRESHOLD", "0.35"))
    minimum_frames = max(1, int(round(float(os.getenv(
        "BASIC_PITCH_RAW_ONSET_SPACING_SECONDS", "0.04")) / ANNOTATION_HOP)))
    candidates = [index for index in range(1, len(strengths) - 1)
                  if strengths[index] >= threshold
                  and strengths[index] >= strengths[index - 1]
                  and strengths[index] > strengths[index + 1]]
    selected: list[int] = []
    for index in candidates:
        if selected and index - selected[-1] < minimum_frames:
            if strengths[index] > strengths[selected[-1]]:
                selected[-1] = index
            continue
        selected.append(index)
    # Basic Pitch's note matrix begins at MIDI 21 (A0).
    return [{
        "time": round(index * ANNOTATION_HOP, 6),
        "confidence": round(float(strengths[index]), 6),
        "pitch": int(21 + pitches[index]),
    } for index in selected]


def contour_peaks(raw: dict) -> list[dict]:
    """Keep a compact frame-level contour instead of discarding model output.

    Basic Pitch uses three contour bins per semitone starting at MIDI 21. The
    result remains polyphonic at note-event level; this frame track describes
    the strongest contour bin and is attached to every overlapping note below.
    """
    matrix = np.asarray(raw.get("contour", []), dtype=np.float32)
    if matrix.ndim != 2 or not len(matrix):
        return []
    threshold = float(os.getenv("BASIC_PITCH_CONTOUR_THRESHOLD", "0.20"))
    stride = max(1, int(os.getenv("BASIC_PITCH_CONTOUR_STRIDE", "2")))
    maximum = max(1, int(os.getenv("BASIC_PITCH_MAX_CONTOUR_POINTS", "30000")))
    result = []
    for index in range(0, len(matrix), stride):
        pitch_bin = int(np.argmax(matrix[index]))
        confidence = float(matrix[index, pitch_bin])
        if confidence < threshold:
            continue
        result.append({
            "time": round(index * ANNOTATION_HOP, 6),
            "midi": round(21.0 + pitch_bin / 3.0, 4),
            "confidence": round(confidence, 6),
        })
        if len(result) >= maximum:
            break
    return result


def note_contour(raw: dict, start: float, end: float, pitch: int) -> list[dict]:
    """Extract the local contour around one decoded note, preserving overlaps."""
    matrix = np.asarray(raw.get("contour", []), dtype=np.float32)
    if matrix.ndim != 2 or not len(matrix):
        return []
    first = max(0, int(np.floor(start / ANNOTATION_HOP)))
    last = min(len(matrix), int(np.ceil(end / ANNOTATION_HOP)) + 1)
    center = int(round((pitch - 21) * 3))
    radius = max(1, int(os.getenv("BASIC_PITCH_NOTE_CONTOUR_RADIUS_BINS", "6")))
    lower, upper = max(0, center - radius), min(matrix.shape[1], center + radius + 1)
    stride = max(1, int(os.getenv("BASIC_PITCH_CONTOUR_STRIDE", "2")))
    threshold = float(os.getenv("BASIC_PITCH_CONTOUR_THRESHOLD", "0.20"))
    result = []
    for index in range(first, last, stride):
        local = matrix[index, lower:upper]
        if not len(local):
            continue
        local_bin = int(np.argmax(local))
        confidence = float(local[local_bin])
        if confidence < threshold:
            continue
        pitch_bin = lower + local_bin
        result.append({
            "time": round(index * ANNOTATION_HOP, 6),
            "midi": round(21.0 + pitch_bin / 3.0, 4),
            "confidence": round(confidence, 6),
        })
    return result


def polyphony_summary(notes: list[dict]) -> dict:
    changes = []
    for note in notes:
        changes.append((float(note["start"]), 1))
        changes.append((float(note["end"]), -1))
    active = maximum = 0
    polyphonic_seconds = 0.0
    previous = None
    for timestamp, change in sorted(changes, key=lambda item: (item[0], item[1])):
        if previous is not None and active > 1:
            polyphonic_seconds += max(0.0, timestamp - previous)
        active += change
        maximum = max(maximum, active)
        previous = timestamp
    return {
        "maximum_simultaneous_notes": maximum,
        "polyphonic_seconds": round(polyphonic_seconds, 3),
        "identity_assignment": "not-performed",
    }


@app.get("/health")
def health() -> dict:
    return {"status": "ok", "service": "basic-pitch-evidence"}


@app.post("/analyze")
async def analyze(audio: UploadFile = File(...)) -> dict:
    suffix = Path(audio.filename or "vocals.wav").suffix or ".wav"
    maximum = int(os.getenv("BASIC_PITCH_MAX_UPLOAD_BYTES", str(200 * 1024 * 1024)))
    payload = await audio.read(maximum + 1)
    if not payload or len(payload) > maximum:
        raise HTTPException(413, "audio upload is empty or too large")
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(suffix=suffix, delete=False) as stream:
            stream.write(payload)
            temporary = stream.name
        raw, _, events = predict(temporary, model())
        contour = contour_peaks(raw)
        notes = []
        for event in events:
            start, end, pitch, amplitude = event[:4]
            points = note_contour(raw, float(start), float(end), int(pitch))
            notes.append({
                "start": round(float(start), 6),
                "end": round(float(end), 6),
                "pitch": int(pitch),
                "amplitude": round(float(amplitude), 6),
                "midi": int(pitch),
                "confidence": round(float(amplitude), 6),
                "track_id": None,
                "singer_id": None,
                "contour": points,
                "pitch_bend": [{
                    "time": point["time"],
                    "semitones": round(float(point["midi"]) - float(pitch), 4),
                    "confidence": point["confidence"],
                } for point in points],
            })
        return {
            "schema_version": 2,
            "model": "spotify/basic-pitch-0.4.0",
            "device": "cpu",
            "notes": notes,
            "onsets": onset_peaks(raw),
            "contour": contour,
            "polyphony": polyphony_summary(notes),
            "frames": int(len(raw.get("onset", []))),
        }
    finally:
        if temporary:
            Path(temporary).unlink(missing_ok=True)
