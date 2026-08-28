from __future__ import annotations


def align_notes_to_syllables(lines: list, basic_pitch: dict,
                             *, minimum_overlap_seconds: float = .015) -> dict:
    """Attach musical events without inferring singer identity or syllable count."""
    notes = basic_pitch.get("notes", []) if basic_pitch.get("enabled") else []
    attached = 0
    notes_with_multiple_syllables = 0
    notes_without_syllable = 0
    assignments_per_note: dict[int, int] = {}
    for line_index, line in enumerate(lines):
        for word_index, word in enumerate(line.words):
            syllables = word.get("syllables", [])
            word_notes = []
            for syllable_index, syllable in enumerate(syllables):
                start, end = float(syllable["start"]), float(syllable["end"])
                aligned = []
                for note_index, note in enumerate(notes):
                    overlap = min(end, float(note["end"])) - max(start, float(note["start"]))
                    if overlap < minimum_overlap_seconds:
                        continue
                    midi = int(note.get("midi", note.get("pitch")))
                    contour = [point for point in note.get("contour", [])
                               if start <= float(point["time"]) <= end]
                    association = {
                        "event_id": f"basic-pitch-{note_index + 1}",
                        "note_track_id": note.get("track_id") or "basic-pitch",
                        "singer_id": None,
                        "start": float(note["start"]),
                        "end": float(note["end"]),
                        "midi": midi,
                        "confidence": float(note.get(
                            "confidence", note.get("amplitude", 0))),
                        "overlap_start": round(max(start, float(note["start"])), 6),
                        "overlap_end": round(min(end, float(note["end"])), 6),
                        "contour": contour,
                        "pitch_bend": [point for point in note.get("pitch_bend", [])
                                       if start <= float(point["time"]) <= end],
                        "source": "basic_pitch",
                    }
                    aligned.append(association)
                    word_notes.append(association)
                    attached += 1
                    assignments_per_note[note_index] = assignments_per_note.get(note_index, 0) + 1
                syllable["notes"] = aligned
                syllable["note_alignment"] = {
                    "track": "basic-pitch", "singer_assignment": None,
                    "polyphony_preserved": len(aligned) > 1,
                }
            word["notes"] = word_notes
    notes_with_multiple_syllables = sum(count > 1 for count in assignments_per_note.values())
    notes_without_syllable = max(0, len(notes) - len(assignments_per_note))
    return {
        "version": 1,
        "enabled": bool(notes),
        "source": "basic_pitch",
        "note_events": len(notes),
        "assignments": attached,
        "assigned_note_events": len(assignments_per_note),
        "unassigned_note_events": notes_without_syllable,
        "notes_spanning_multiple_syllables": notes_with_multiple_syllables,
        "singer_assignment": "not-performed",
        "pitch_range_used_for_identity": False,
    }
