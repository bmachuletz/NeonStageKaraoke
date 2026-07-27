from __future__ import annotations


ACOUSTIC_SOURCES = {None, "qwen-forced", "ctc-phoneme-alignment",
                    "ctc-context-alignment", "ctc-section-alignment",
                    "mms-forced-alignment",
                    "sofa-singing-alignment",
                    "easyaligner-global",
                    "stable-ts-whisper",
                    "asr-repetition-anchor", "asr-repetition-activity",
                    "transition-block-qwen"}
ACOUSTIC_SOURCES.add("ctc-overlap-reanalysis")
VERIFIED_ACOUSTIC_SOURCES = ACOUSTIC_SOURCES - {None}


def extend_final_word_sustains(lines: list, vocal_activity: list[tuple[float, float]],
                               *, release_padding: float = 0.2,
                               maximum_extension: float = 1.2) -> dict:
    """Keep a held final word active through its measured vocal decay.

    ASR models generally timestamp the lexical core and often cut a sung vowel
    before its audible release.  Extend only the last word of a line, only into
    measured vocal activity, and never into the next lyric onset.
    """
    adjustments = []
    for index, line in enumerate(lines):
        if not line.words:
            continue
        word = line.words[-1]
        if word.get("timing_source") not in VERIFIED_ACOUSTIC_SOURCES:
            continue
        start, end = float(word["start"]), float(word["end"])
        # A region which continues far beyond the word is usually a following
        # phrase inside one uninterrupted activity cluster, not this word's
        # release. Only accept a locally ending region; otherwise every line
        # end in a continuously sung passage would be stretched.
        candidates = [(begin, stop) for begin, stop in vocal_activity
                      if begin <= end + 0.1 and end + 0.06 < stop <= end + maximum_extension
                      and stop >= start]
        if not candidates:
            continue
        activity_end = max(stop for _begin, stop in candidates)
        target = min(end + maximum_extension, activity_end + release_padding)
        if index + 1 < len(lines) and lines[index + 1].words:
            target = min(target, float(lines[index + 1].words[0]["start"]) - 0.12)
        if target - end < 0.08:
            continue
        word["acoustic_end"] = round(end, 3)
        word["end"] = round(target, 3)
        word["sustain_activity_end"] = round(activity_end, 3)
        word["sustain_extension_ms"] = round((target - end) * 1000)
        adjustments.append({"line": index + 1, "word": word.get("word", ""),
                            "from": round(end, 3), "to": round(target, 3),
                            "activity_end": round(activity_end, 3)})
    return {"method": "vocal-activity-sustain-release-v1",
            "adjusted_words": len(adjustments), "release_padding_ms": round(release_padding * 1000),
            "adjustments": adjustments}


def stabilize_acoustic_display_durations(lines: list, *, minimum_duration: float = 0.04) -> dict:
    """Keep acoustic onsets intact while making sub-frame tokens renderable.

    CTC and singing models occasionally return only a consonant/transition core
    shorter than a video frame.  The original measured end remains available as
    ``acoustic_end``; only the karaoke display interval gets a 40 ms floor.
    """
    adjusted = 0
    for line in lines:
        for word in line.words:
            if word.get("timing_source") not in ACOUSTIC_SOURCES:
                continue
            start, end = float(word["start"]), float(word["end"])
            if end - start >= minimum_duration:
                continue
            word["acoustic_end"] = round(end, 3)
            word["end"] = round(start + minimum_duration, 3)
            word["display_duration_floor_ms"] = round(minimum_duration * 1000)
            adjusted += 1
    return {"method": "acoustic-onset-display-floor-v1", "adjusted_words": adjusted,
            "minimum_display_duration_ms": round(minimum_duration * 1000)}


def reconcile_acoustic_boundaries(lines: list, *, maximum_overlap: float = 0.45,
                                  minimum_word_duration: float = 0.03) -> dict:
    """Resolve small boundary disagreements between independently aligned phrases.

    This never moves a whole phrase. It selects the midpoint of two acoustic
    estimates only when both boundary words are non-heuristic and the overlap
    is small enough to represent decoder-frame/coarticulation uncertainty.
    """
    adjusted = rejected = 0
    adjustments: list[dict] = []
    for index in range(1, len(lines)):
        previous, current = lines[index - 1], lines[index]
        if not previous.words or not current.words:
            continue
        left, right = previous.words[-1], current.words[0]
        left_end, right_start = float(left["end"]), float(right["start"])
        overlap = left_end - right_start
        if overlap <= 0:
            continue
        if overlap > maximum_overlap:
            rejected += 1
            continue
        left_acoustic = left.get("timing_source") in VERIFIED_ACOUSTIC_SOURCES
        right_acoustic = right.get("timing_source") in VERIFIED_ACOUSTIC_SOURCES
        if not left_acoustic and not right_acoustic:
            rejected += 1
            continue
        if left_acoustic and right_acoustic:
            boundary = (left_end + right_start) / 2
            mode = "acoustic-midpoint"
        elif right_acoustic:
            # Preserve the measured onset and trim only the heuristic tail.
            boundary = right_start
            mode = "trim-heuristic-tail"
        else:
            # Preserve the measured ending and move only the heuristic onset.
            boundary = left_end
            mode = "move-heuristic-onset"
        if (boundary - float(left["start"]) < minimum_word_duration or
                float(right["end"]) - boundary < minimum_word_duration):
            rejected += 1
            continue
        left["end"] = round(boundary, 3)
        right["start"] = round(boundary, 3)
        left["consensus_boundary_adjustment_ms"] = round((boundary - left_end) * 1000, 1)
        right["consensus_boundary_adjustment_ms"] = round((boundary - right_start) * 1000, 1)
        adjustments.append({"previous_line": index, "next_line": index + 1,
                            "overlap_ms": round(overlap * 1000, 1),
                            "boundary": round(boundary, 3), "mode": mode})
        adjusted += 1
    return {"method": "acoustic-midpoint-consensus-v1", "adjusted_boundaries": adjusted,
            "rejected_boundaries": rejected, "maximum_overlap_ms": maximum_overlap * 1000,
            "adjustments": adjustments}


def eliminate_remaining_line_overlaps(lines: list, *, minimum_word_duration: float = 0.04) -> dict:
    """Produce a single monotonic karaoke lane after acoustic reanalysis.

    Real recordings can contain a backing-vocal tail and the next lead phrase
    at the same time. The aligners should analyse that conflict first. If it is
    still unresolved, preserve the next lead onset and compress only the
    colliding tail of the previous line. The result is reviewable rather than
    silently emitting two simultaneously active karaoke lines.
    """
    adjustments: list[dict] = []
    for index in range(1, len(lines)):
        previous, current = lines[index - 1], lines[index]
        if not previous.words or not current.words:
            continue
        boundary = float(current.words[0]["start"])
        previous_end = float(previous.words[-1]["end"])
        if previous_end <= boundary + 0.001:
            continue

        first = next((word_index for word_index, word in enumerate(previous.words)
                      if float(word["end"]) > boundary), len(previous.words) - 1)
        # Include enough preceding space to retain a visible duration for every
        # tail token. This is deliberately local; earlier confirmed onsets stay.
        while first > 0 and boundary - float(previous.words[first]["start"]) < (
                len(previous.words) - first) * minimum_word_duration:
            first -= 1
        old_start = float(previous.words[first]["start"])
        old_end = previous_end
        available = boundary - old_start
        if available < (len(previous.words) - first) * minimum_word_duration:
            first = 0
            old_start = float(previous.words[0]["start"])
            available = boundary - old_start
        if available <= 0 or old_end <= old_start:
            # This can only happen with a fully concurrent pair. Keep a tiny
            # monotonic display lane immediately before the next lead onset.
            available = len(previous.words) * minimum_word_duration
            old_start = max(0.0, boundary - available)
            first = 0

        scale = available / max(0.001, old_end - old_start)
        for word in previous.words[first:]:
            word_start = old_start + (float(word["start"]) - old_start) * scale
            word_end = old_start + (float(word["end"]) - old_start) * scale
            word["start"] = round(min(word_start, boundary - minimum_word_duration), 3)
            word["end"] = round(min(boundary, max(word["start"] + minimum_word_duration, word_end)), 3)
            word["timing_source"] = "overlap-display-lane-fallback"
            for syllable in word.get("syllables", []):
                syllable_start = old_start + (float(syllable["start"]) - old_start) * scale
                syllable_end = old_start + (float(syllable["end"]) - old_start) * scale
                syllable["start"] = round(max(float(word["start"]), syllable_start), 3)
                syllable["end"] = round(min(float(word["end"]), max(syllable_start, syllable_end)), 3)
        # Rounding must never leave the final token a millisecond over the lane.
        previous.words[-1]["end"] = round(boundary, 3)
        adjustments.append({
            "previous_line": index, "next_line": index + 1,
            "overlap_ms": round((previous_end - boundary) * 1000, 1),
            "tail_words": len(previous.words) - first,
            "boundary": round(boundary, 3),
            "method": "preserve-next-lead-onset",
        })
    return {"method": "single-karaoke-lane-fallback-v1",
            "adjusted_pairs": len(adjustments), "adjustments": adjustments}
