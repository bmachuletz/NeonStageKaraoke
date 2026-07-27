from __future__ import annotations
from dataclasses import dataclass, field


@dataclass(slots=True)
class LrcLine:
    timestamp: float
    text: str
    original: str
    words: list[dict] = field(default_factory=list)
    status: str = "pending"
    reason: str | None = None
    timed_input: bool = True
    source_timestamp: float | None = None


@dataclass(slots=True)
class AlignmentConfig:
    language: str = "de"
    pre_roll: float = 0.45
    post_roll: float = 0.35
    last_line_duration: float = 12.0
    max_word_duration: float = 8.0
    max_start_deviation: float = 2.0
    full_song_max_seconds: float = 300.0
    section_pause_gap: float = 7.0
    section_max_duration: float = 45.0
