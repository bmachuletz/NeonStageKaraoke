from __future__ import annotations

from dataclasses import dataclass
from enum import StrEnum


class EvidenceRole(StrEnum):
    WORD_ONSET = "word_onset"
    WORD_END = "word_end"
    SYLLABLE_BOUNDARY = "syllable_boundary"
    VOCAL_RELEASE = "vocal_release"
    NOTE_EVENT = "note_event"
    CANDIDATE_SCORE = "candidate_score"


FAMILY_ROLES: dict[str, frozenset[EvidenceRole]] = {
    "forced_alignment": frozenset({
        EvidenceRole.WORD_ONSET, EvidenceRole.WORD_END,
        EvidenceRole.SYLLABLE_BOUNDARY,
    }),
    "qwen_asr": frozenset({EvidenceRole.WORD_ONSET, EvidenceRole.WORD_END}),
    "stable_ts": frozenset({EvidenceRole.WORD_ONSET, EvidenceRole.WORD_END}),
    "ctc_phoneme": frozenset({
        EvidenceRole.WORD_ONSET, EvidenceRole.WORD_END,
        EvidenceRole.SYLLABLE_BOUNDARY,
    }),
    "vocal_activity": frozenset({
        EvidenceRole.WORD_ONSET, EvidenceRole.WORD_END, EvidenceRole.VOCAL_RELEASE,
    }),
    "spectral_onset": frozenset({
        EvidenceRole.WORD_ONSET, EvidenceRole.SYLLABLE_BOUNDARY,
    }),
    "basic_pitch": frozenset({
        EvidenceRole.WORD_ONSET, EvidenceRole.WORD_END,
        EvidenceRole.SYLLABLE_BOUNDARY, EvidenceRole.NOTE_EVENT,
    }),
    "pyin": frozenset({EvidenceRole.VOCAL_RELEASE, EvidenceRole.NOTE_EVENT}),
    "separator_candidate": frozenset({EvidenceRole.CANDIDATE_SCORE}),
}


@dataclass(frozen=True, slots=True)
class BoundaryEvidence:
    family: str
    role: EvidenceRole
    time: float
    confidence: float
    source: str
    independent_group: str
    metadata: dict | None = None

    def __post_init__(self) -> None:
        if self.role not in FAMILY_ROLES.get(self.family, frozenset()):
            raise ValueError(
                f"Evidenzfamilie {self.family} darf Rolle {self.role.value} nicht ausüben")
        if not 0 <= self.confidence <= 1:
            raise ValueError("Evidence confidence muss zwischen 0 und 1 liegen")

    def report(self) -> dict:
        return {
            "family": self.family,
            "role": self.role.value,
            "time": round(self.time, 6),
            "confidence": round(self.confidence, 4),
            "source": self.source,
            "independent_group": self.independent_group,
            "metadata": self.metadata or {},
        }
