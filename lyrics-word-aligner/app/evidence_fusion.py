from __future__ import annotations

import os

from .boundary_evidence import LeakageReference, banded_boundary_step
from .evidence_models import BoundaryEvidence, EvidenceRole


def _phoneme_edges(word: dict) -> list[tuple[float, float]]:
    edges = []
    for phone in word.get("phonemes", []):
        if phone.get("start") is not None:
            edges.append((float(phone["start"]), float(phone.get("confidence", .75))))
    return edges


def fuse_syllable_evidence(
    lines: list,
    basic_pitch: dict,
    vocal_audio,
    accompaniment_audio,
    *,
    sample_rate: int = 16000,
    mode: str = "shadow",
) -> dict:
    """Evaluate existing syllable edges; never derive syllable count from notes."""
    if mode not in {"off", "shadow", "select"}:
        raise ValueError("LRC_EVIDENCE_FUSION_MODE muss off, shadow oder select sein")
    notes = basic_pitch.get("notes", []) if basic_pitch.get("enabled") else []
    report = {
        "version": 1,
        "mode": mode,
        "families": sorted({
            "forced_alignment", "ctc_phoneme", "spectral_onset",
            "basic_pitch", "pyin",
        }),
        "decisions": [],
        "candidate_boundaries": 0,
        "selected_boundaries": 0,
        "reason": "completed",
    }
    if mode == "off":
        return {**report, "reason": "feature-disabled"}
    if not notes:
        return {**report, "reason": "no-basic-pitch-events"}
    reference = (LeakageReference(accompaniment_audio, sample_rate)
                 if accompaniment_audio is not None else None)
    radius = float(os.getenv("LRC_EVIDENCE_FUSION_SYLLABLE_RADIUS", ".065"))
    minimum_spectral_db = float(os.getenv(
        "LRC_EVIDENCE_FUSION_MIN_SPECTRAL_DB", "3.5"))
    minimum_shift = float(os.getenv("LRC_EVIDENCE_FUSION_MIN_SHIFT", ".02"))
    minimum_duration = float(os.getenv("LRC_EVIDENCE_FUSION_MIN_SYLLABLE", ".055"))

    for line_index, line in enumerate(lines):
        for word_index, word in enumerate(line.words):
            syllables = word.get("syllables", [])
            phone_edges = _phoneme_edges(word)
            for syllable_index in range(1, len(syllables)):
                current = float(syllables[syllable_index]["start"])
                nearby = [note for note in notes
                          if abs(float(note["start"]) - current) <= radius]
                if not nearby:
                    continue
                note = max(nearby, key=lambda item: (
                    float(item.get("confidence", item.get("amplitude", 0))),
                    -abs(float(item["start"]) - current)))
                candidate = float(note["start"])
                spectral = banded_boundary_step(
                    vocal_audio, candidate, reference=reference,
                    sample_rate=sample_rate)
                independent_db = float(spectral.get("independent_db", 0)) if spectral else 0.0
                pitch_confidence = min(1.0, max(0.0, float(
                    note.get("confidence", note.get("amplitude", 0)))))
                evidence = [
                    BoundaryEvidence(
                        "forced_alignment", EvidenceRole.SYLLABLE_BOUNDARY,
                        current, min(1.0, max(0.0, float(
                            word.get("confidence") or .72))),
                        str(word.get("timing_source", "derived-syllable")), "lexical-model"),
                    BoundaryEvidence(
                        "basic_pitch", EvidenceRole.SYLLABLE_BOUNDARY,
                        candidate, pitch_confidence,
                        str(basic_pitch.get("service", "basic-pitch")), "musical-pitch"),
                    BoundaryEvidence(
                        "spectral_onset", EvidenceRole.SYLLABLE_BOUNDARY,
                        candidate, min(1.0, max(0.0, independent_db / 8.0)),
                        "leakage-aware-band-step", "local-acoustics",
                        {"independent_db": round(independent_db, 2),
                         "leakage_corrected": reference is not None}),
                ]
                nearest_phone = min(phone_edges, key=lambda item: abs(item[0] - candidate),
                                    default=None)
                if nearest_phone is not None and abs(nearest_phone[0] - candidate) <= radius:
                    evidence.append(BoundaryEvidence(
                        "ctc_phoneme", EvidenceRole.SYLLABLE_BOUNDARY,
                        nearest_phone[0], min(1.0, max(0.0, nearest_phone[1])),
                        "phoneme-edge", "phonetic-model"))
                independent_groups = {
                    item.independent_group for item in evidence
                    if item.confidence >= .45
                }
                geometry_valid = (
                    candidate >= float(syllables[syllable_index - 1]["start"]) + minimum_duration
                    and candidate <= float(syllables[syllable_index]["end"]) - minimum_duration)
                supported = (pitch_confidence >= .45
                             and independent_db >= minimum_spectral_db
                             and reference is not None
                             and len(independent_groups) >= 3
                             and geometry_valid)
                selected = (mode == "select" and supported
                            and abs(candidate - current) >= minimum_shift)
                confidence = min(1.0, sum(item.confidence for item in evidence)
                                 / max(1, len(evidence)) + .04 * (len(independent_groups) - 2))
                decision = {
                    "role": EvidenceRole.SYLLABLE_BOUNDARY.value,
                    "line": line_index + 1,
                    "word": word_index + 1,
                    "syllable": syllable_index + 1,
                    "current_boundary": round(current, 6),
                    "candidate_boundary": round(candidate, 6),
                    "selected_boundary": round(candidate if selected else current, 6),
                    "confidence": round(confidence, 4),
                    "supported": supported,
                    "selected": selected,
                    "independent_families": len(independent_groups),
                    "evidence": [item.report() for item in evidence],
                    "reason": ("promoted-multi-family-evidence" if selected else
                               "confirmed-existing" if supported else
                               "insufficient-independent-evidence"),
                }
                report["decisions"].append(decision)
                report["candidate_boundaries"] += 1
                if not selected:
                    continue
                syllables[syllable_index - 1]["end"] = round(candidate, 6)
                syllables[syllable_index]["start"] = round(candidate, 6)
                syllables[syllable_index]["boundary_source"] = "evidence-fusion"
                report["selected_boundaries"] += 1
    return report
