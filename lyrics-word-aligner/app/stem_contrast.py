from __future__ import annotations

import math

import numpy as np


def _mono(audio: np.ndarray) -> np.ndarray:
    signal = np.asarray(audio, dtype=np.float32)
    if signal.ndim > 1:
        signal = signal.mean(axis=tuple(range(1, signal.ndim)))
    return signal.reshape(-1)


def _rms_db(signal: np.ndarray, center: float, *, sample_rate: int,
            frame_seconds: float) -> float:
    half = max(1, int(round(frame_seconds * sample_rate / 2)))
    sample = int(round(center * sample_rate))
    window = signal[max(0, sample - half):min(len(signal), sample + half)]
    if not len(window):
        return -120.0
    rms = float(np.sqrt(np.mean(np.square(window)) + 1e-12))
    return 20.0 * math.log10(rms + 1e-12)


def refine_final_releases_with_stem_contrast(
        lines: list,
        vocals: np.ndarray,
        instrumental: np.ndarray,
        *,
        sample_rate: int = 16000,
        frame_seconds: float = 0.04,
        hop_seconds: float = 0.01,
        release_padding: float = 0.04,
        minimum_trim: float = 0.10,
        include_internal_phrase_ends: bool = False,
        minimum_internal_pause: float = 0.28,
        mode: str = "shadow",
) -> dict:
    """Reject separator residue after the real sung release.

    A vocal stem alone cannot distinguish a quiet held vowel from tonal
    accompaniment which leaked through the separator.  The paired
    instrumental stem supplies an independent observation: after the singer
    stops, vocal energy and vocal-to-instrumental dominance fall together and
    stay down.  A brief low-energy trough is not enough; the detector also
    rejects a candidate when a consonant/release burst returns shortly after
    it.

    By default only final words are shortened.  A caller with immutable source
    line layout may also opt into internal phrase endings: words followed by a
    substantial pause are evaluated like line endings, while connected word
    boundaries and genuinely held notes remain outside this pass. No release
    is ever extended. The small padding retains the audible consonant/release
    tail expected by the karaoke renderer.
    """
    normalized_mode = mode.strip().lower()
    if normalized_mode not in {"shadow", "select"}:
        raise ValueError("stem contrast mode must be shadow or select")
    vocal_signal = _mono(vocals)
    instrumental_signal = _mono(instrumental)
    samples = min(len(vocal_signal), len(instrumental_signal))
    report = {
        "enabled": samples >= int(sample_rate * 0.5),
        "method": "paired-stem-vocal-dominance-release-v1",
        "mode": normalized_mode,
        "attempted_words": 0,
        "candidate_words": 0,
        "adjusted_words": 0,
        "release_padding_ms": round(release_padding * 1000),
        "internal_phrase_ends": include_internal_phrase_ends,
        "minimum_internal_pause_ms": round(minimum_internal_pause * 1000),
        "details": [],
    }
    if not report["enabled"]:
        report["reason"] = "paired-stems-unavailable-or-too-short"
        return report
    vocal_signal = vocal_signal[:samples]
    instrumental_signal = instrumental_signal[:samples]
    duration = samples / sample_rate

    for line_index, line in enumerate(lines):
        if not line.words:
            continue
        if getattr(line, "manual_adjusted", False):
            continue
        for word_index, word in enumerate(line.words):
            is_line_final = word_index == len(line.words) - 1
            if not is_line_final:
                if not include_internal_phrase_ends:
                    continue
                next_start = float(line.words[word_index + 1]["start"])
                if next_start - float(word["end"]) < minimum_internal_pause:
                    continue
            if word.get("editor_word_manual_adjusted"):
                continue
            start = float(word["start"])
            current_end = min(duration, float(word["end"]))
            if current_end - start < 0.34:
                continue
            report["attempted_words"] += 1
        # The incoming start can itself be late when a forced aligner swapped
        # the duration of the final two words. Include a very small amount of
        # left context; the later IPA gate decides whether this candidate is
        # linguistically compatible with the final word.
            first_center = max(float(line.words[0]["start"]), start - 0.06)
            centers = np.arange(first_center, current_end + 0.0001,
                                hop_seconds, dtype=np.float64)
            if len(centers) < 12:
                continue
            vocal_db = np.asarray([
                _rms_db(vocal_signal, float(center), sample_rate=sample_rate,
                        frame_seconds=frame_seconds)
                for center in centers
            ])
            instrumental_db = np.asarray([
                _rms_db(instrumental_signal, float(center), sample_rate=sample_rate,
                        frame_seconds=frame_seconds)
                for center in centers
            ])
            dominance = vocal_db - instrumental_db
            selected = None
            for index, center in enumerate(centers):
                prior = ((centers >= center - 0.25)
                         & (centers < center - 0.04))
                future = ((centers >= center)
                          & (centers <= center + 0.10))
                recovery = ((centers > center + 0.03)
                            & (centers <= center + 0.20))
                if np.count_nonzero(prior) < 4 or np.count_nonzero(future) < 5:
                    continue
                prior_dominance = float(np.median(dominance[prior]))
                prior_vocal = float(np.percentile(vocal_db[prior], 65))
                future_dominance = float(np.median(dominance[future]))
                future_vocal = float(np.median(vocal_db[future]))
                dominance_gate = min(-8.0, prior_dominance - 8.0)
                # A final plosive/fricative burst may briefly return after a
                # quiet vowel trough. Any recovery to within 8 dB of the paired
                # instrumental is still meaningful vocal evidence and postpones
                # the release candidate.
                recovery_gate = -8.0
                recovered = (np.any(recovery)
                             and float(np.max(dominance[recovery])) >= recovery_gate)
                if (float(dominance[index]) <= dominance_gate
                        and future_dominance < -11.0
                        and future_vocal < prior_vocal - 7.0
                        and not recovered):
                    # ``center + frame/2`` is the right edge of the window which
                    # first proves the persistent decay. Keep one short rendering
                    # tail so stops/fricatives do not look prematurely clipped.
                    target = float(center) + frame_seconds / 2 + release_padding
                    if current_end - target >= minimum_trim:
                        selected = {
                            "target": target,
                            "center": float(center),
                            "vocal_db": float(vocal_db[index]),
                            "instrumental_db": float(instrumental_db[index]),
                            "dominance_db": float(dominance[index]),
                            "prior_dominance_db": prior_dominance,
                            "future_dominance_db": future_dominance,
                            "future_vocal_drop_db": prior_vocal - future_vocal,
                        }
                    break
            if selected is None:
                continue
            target = max(start + 0.06, min(current_end, selected["target"]))
            evidence = {
                key: round(value, 3) for key, value in selected.items()
                if key != "target"
            }
            detail = {
                "line": line_index + 1,
                "word_index": word_index + 1,
                "phrase_end": not is_line_final,
                "word": word.get("word", ""),
                "from": round(current_end, 3),
                "to": round(target, 3),
                "trim_ms": round((current_end - target) * 1000, 1),
                **evidence,
            }
            report["details"].append(detail)
            report["candidate_words"] += 1
            if normalized_mode == "select":
                word["pre_stem_contrast_end"] = round(float(word["end"]), 3)
                word["end"] = round(target, 3)
                word["stem_contrast_release_trim_ms"] = round(
                    (current_end - target) * 1000, 1)
                word["stem_contrast_release"] = evidence
                report["adjusted_words"] += 1
    return report
