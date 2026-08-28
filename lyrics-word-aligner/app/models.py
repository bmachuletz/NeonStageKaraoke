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
    # A timestamped empty LRC entry marks the end of the preceding lyric
    # phrase.  Keeping this independently from the text lines prevents a
    # forced aligner from consuming a following instrumental section.
    source_end_boundary: float | None = None
    # Set only when an exact editor line is restored at the end of alignment.
    # This state is reported so the next editor revision retains human
    # authority across repeated realignment cycles.
    manual_adjusted: bool = False
    manual_editor_start: float | None = None
    manual_editor_end: float | None = None
    # Independent karaoke/singer lane. Lane 0 remains the lead-vocal lane.
    # Lines in different lanes are intentionally allowed to overlap.
    voice_lane: int = 0
    voice_label: str | None = None


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
