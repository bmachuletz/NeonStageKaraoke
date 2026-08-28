"""Leakage-aware, band-split evidence for a single word boundary.

The historical evidence measure asks one question on the vocal stem: does
broadband energy rise (or fall) across this instant? Two properties of dense,
distorted material defeat it.

Separation is never perfect, so drums and guitars survive in the vocal stem.
Their attacks land on the beat - exactly where word onsets also land - so the
supposedly independent confirmation is correlated with the error it is meant to
catch. The instrumental stem is sample synchronous with the vocal stem and says
precisely how much of a rise is not the singer; subtracting it turns a
correlated measurement into an independent one.

Broadband energy also under-reads consonantal onsets. A word starting with a
fricative or an unvoiced plosive carries almost all of its onset energy above
roughly three kilohertz, so the broadband step stays small and a real word
start counts as unsupported. Measuring a voicing band and a sibilance band
separately, and accepting either, recovers those onsets. Both properties are
acoustic and language neutral; they are not tuned to one song or genre.
"""
from __future__ import annotations

import math
from contextlib import contextmanager
from dataclasses import dataclass

import numpy as np

# Voicing and first formants versus sibilance and plosive bursts.
VOICING_BAND = (80.0, 3500.0)
SIBILANCE_BAND = (3500.0, 8000.0)


@dataclass(frozen=True)
class LeakageReference:
    """The sample synchronous instrumental stem for one alignment run."""

    audio: np.ndarray
    sample_rate: int = 16000


_REFERENCE: LeakageReference | None = None


@contextmanager
def leakage_aware_evidence(instrumental, *, sample_rate: int = 16000):
    """Enable leakage-corrected boundary evidence for the enclosed work.

    A context manager rather than a parameter: the evidence measure is called
    from a dozen places inside the phoneme aligner, and threading one optional
    reference through all of them would touch far more code than the change
    itself is worth. Passing ``None`` keeps the historical behaviour.

    Note: restricting the reference to the transient half of the accompaniment
    was measured on 236 real word boundaries and made the gate stricter, not
    better - that half carries roughly half of the instrumental energy and
    includes every pick attack, which lands on the same beats as the words.
    The full instrumental stays the reference.
    """
    global _REFERENCE
    previous = _REFERENCE
    if instrumental is None:
        _REFERENCE = None
    else:
        signal = np.asarray(instrumental, dtype=np.float32)
        if signal.ndim > 1:
            signal = signal.mean(axis=tuple(range(1, signal.ndim)))
        _REFERENCE = LeakageReference(signal.reshape(-1), sample_rate)
    try:
        yield _REFERENCE
    finally:
        _REFERENCE = previous


def active_reference() -> LeakageReference | None:
    return _REFERENCE


def _flanks(signal: np.ndarray, boundary: float, sample_rate: int,
            flank_seconds: float, guard_seconds: float):
    center = int(round(boundary * sample_rate))
    flank = max(64, int(round(flank_seconds * sample_rate)))
    guard = max(0, int(round(guard_seconds * sample_rate)))
    left = signal[max(0, center - flank):max(0, center - guard)]
    right = signal[min(len(signal), center + guard):min(len(signal), center + flank)]
    return left, right


def _band_energy(segment: np.ndarray, sample_rate: int,
                 band: tuple[float, float]) -> float:
    if len(segment) < 32:
        return 0.0
    window = np.hanning(len(segment)).astype(np.float32)
    spectrum = np.abs(np.fft.rfft(segment * window))
    frequencies = np.fft.rfftfreq(len(segment), 1.0 / sample_rate)
    mask = (frequencies >= band[0]) & (frequencies < band[1])
    if not mask.any():
        return 0.0
    return float(np.sqrt(np.mean(np.square(spectrum[mask])) + 1e-20))


def _step_db(left: np.ndarray, right: np.ndarray, sample_rate: int,
             band: tuple[float, float]) -> float:
    before = _band_energy(left, sample_rate, band)
    after = _band_energy(right, sample_rate, band)
    return 20.0 * math.log10((after + 1e-9) / (before + 1e-9))


def banded_boundary_step(vocal, boundary: float, *,
                         reference: LeakageReference | None = None,
                         sample_rate: int = 16000,
                         flank_seconds: float = 0.055,
                         guard_seconds: float = 0.008) -> dict | None:
    """Per-band energy step at a boundary, with the instrumental removed.

    Returns ``None`` when either flank holds too little audio to judge.
    """
    signal = np.asarray(vocal, dtype=np.float32)
    if signal.ndim > 1:
        signal = signal.mean(axis=tuple(range(1, signal.ndim)))
    signal = signal.reshape(-1)
    left, right = _flanks(signal, boundary, sample_rate,
                          flank_seconds, guard_seconds)
    if len(left) < 128 or len(right) < 128:
        return None

    result: dict = {"bands": {}, "leakage_corrected": reference is not None}
    for name, band in (("voicing", VOICING_BAND), ("sibilance", SIBILANCE_BAND)):
        measured = _step_db(left, right, sample_rate, band)
        explained = 0.0
        if reference is not None:
            other_left, other_right = _flanks(
                reference.audio, boundary, reference.sample_rate,
                flank_seconds, guard_seconds)
            if len(other_left) >= 128 and len(other_right) >= 128:
                explained = _step_db(other_left, other_right,
                                     reference.sample_rate, band)
        # Only a step in the same direction can be explained by the
        # accompaniment, and never by more than the vocal stem actually shows.
        if measured >= 0.0:
            shared = max(0.0, min(measured, explained))
        else:
            shared = min(0.0, max(measured, explained))
        result["bands"][name] = {
            "measured_db": round(measured, 2),
            "instrumental_db": round(explained, 2),
            "independent_db": round(measured - shared, 2),
        }
    voicing = result["bands"]["voicing"]["independent_db"]
    sibilance = result["bands"]["sibilance"]["independent_db"]
    # Either band may carry the onset: a shouted vowel loads the voicing band,
    # while /f/, /s/ or an unvoiced plosive burst load the sibilance band.
    result["independent_db"] = max(voicing, sibilance, key=abs)
    result["leading_band"] = ("sibilance" if abs(sibilance) > abs(voicing)
                              else "voicing")
    return result
