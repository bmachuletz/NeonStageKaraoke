from __future__ import annotations


def _overlap(start: float, end: float, region: tuple[float, float]) -> float:
    return max(0.0, min(end, region[1]) - max(start, region[0]))


def constrain_to_stage_vocals(
        lines: list,
        activity: list[tuple[float, float]], *,
        release_tolerance: float = 0.12,
        release_padding: float = 0.04,
        onset_search: float = 1.5,
        dying_onset_window: float = 0.08,
        minimum_onset_conflict: float = 0.18) -> dict:
    """Reject impossible lyric edges using the exported Stage vocal stem.

    Recognition can use a blend with the original mix to recover backing
    vocals.  Such a blend is deliberately unsuitable for display boundaries:
    tonal instruments can look like a held vowel.  This final gate therefore
    uses only the exact vocal stem later played by the Stage and shown as the
    editor waveform.

    End corrections are safe and local: the activity island overlapping the
    last word owns its release and the algorithm never jumps to a later island.
    Suspicious starts are reported to the quality gate instead of moving a
    complete line without sufficient phonetic evidence.
    """
    regions = sorted((float(begin), float(end)) for begin, end in activity
                     if end > begin)
    release_corrections: list[dict] = []
    onset_conflicts: list[dict] = []

    for line_index, line in enumerate(lines):
        if not line.words:
            continue

        last = line.words[-1]
        word_start = float(last["start"])
        word_end = float(last["end"])
        supporting = [region for region in regions
                      if _overlap(word_start, word_end, region) > 0]
        if supporting:
            # Maximum overlap binds the word to its own vocal island. A later
            # phrase can never extend the current word merely because the
            # automatic timestamp was already too long.
            owner = max(supporting,
                        key=lambda region: (_overlap(word_start, word_end, region),
                                            -abs(region[0] - word_start)))
            owner_end = owner[1]
            if word_end > owner_end + release_tolerance:
                target = min(word_end, owner_end + release_padding)
                if target >= word_start + 0.03:
                    last["pre_stage_vocal_end"] = round(word_end, 3)
                    last["end"] = round(target, 3)
                    last["stage_vocal_release_trim_ms"] = round(
                        (word_end - target) * 1000, 1)
                    release_corrections.append({
                        "line": line_index + 1,
                        "word": last.get("word", ""),
                        "from": round(word_end, 3),
                        "to": round(target, 3),
                        "vocal_activity_end": round(owner_end, 3),
                    })

        first = line.words[0]
        onset = float(first["start"])
        first_end = float(first["end"])
        containing = next((region for region in regions
                           if region[0] - 0.03 <= onset <= region[1]), None)
        search_after = containing[1] if containing is not None else onset
        next_region = next((region for region in regions
                            if region[0] > search_after + 0.03), None)
        dying_previous = (containing is not None
                          and containing[1] - onset <= dying_onset_window)
        outside_activity = containing is None
        if (next_region is not None
                and minimum_onset_conflict <= next_region[0] - onset <= onset_search
                and first_end >= next_region[0] - 0.12
                and (dying_previous or outside_activity)):
            delta_ms = round((next_region[0] - onset) * 1000, 1)
            first["stage_vocal_onset_conflict_ms"] = delta_ms
            first["stage_vocal_onset_candidate"] = round(next_region[0], 3)
            onset_conflicts.append({
                "line": line_index + 1,
                "word": first.get("word", ""),
                "aligned": round(onset, 3),
                "vocal_candidate": round(next_region[0], 3),
                "delta_ms": delta_ms,
                "reason": "dying-previous-island" if dying_previous
                          else "outside-stage-vocal-activity",
            })

    return {
        "method": "exact-stage-vocal-boundary-gate-v1",
        "release_corrections": len(release_corrections),
        "onset_conflicts": len(onset_conflicts),
        "release_details": release_corrections,
        "onset_details": onset_conflicts,
    }
