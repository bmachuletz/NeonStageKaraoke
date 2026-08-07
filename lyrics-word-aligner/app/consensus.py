from __future__ import annotations

import numpy as np


ACOUSTIC_SOURCES = {None, "qwen-forced", "ctc-phoneme-alignment",
                    "ctc-context-alignment", "ctc-section-alignment",
                    "mms-forced-alignment",
                    "sofa-singing-alignment",
                    "easyaligner-global",
                    "stable-ts-whisper",
                    "targeted-deleted-fragment-qwen",
                    "asr-repetition-anchor", "asr-repetition-activity",
                    "transition-block-qwen", "ipa-collapsed-run-repair",
                    "ipa-vocal-hole-repair",
                    "stable-repetition-acoustic-onset"}
ACOUSTIC_SOURCES.add("ctc-overlap-reanalysis")
VERIFIED_ACOUSTIC_SOURCES = ACOUSTIC_SOURCES - {None}


def _measure_sung_release(audio: np.ndarray, word_start: float, lexical_end: float,
                          upper_bound: float, *, sample_rate: int = 16000,
                          bridge_gap: float = 0.24) -> tuple[float, float] | None:
    """Follow a connected, tonal vocal decay beyond an ASR lexical boundary.

    Energy-only VAD is intentionally conservative and often cuts quiet vibrato
    or a held vowel.  This local detector uses hysteresis plus spectral flatness:
    a low-energy harmonic tail remains singing, while broadband separator noise
    does not acquire the word.  It only follows a region connected to the
    measured word ending and can therefore never jump to a later phrase.
    """
    signal = np.asarray(audio, dtype=np.float32)
    if signal.ndim > 1:
        signal = np.mean(signal, axis=-1)
    signal = signal.reshape(-1)
    duration = len(signal) / sample_rate
    upper_bound = min(float(upper_bound), duration)
    window_start = max(0.0, min(word_start, lexical_end - 0.45))
    if upper_bound <= lexical_end + 0.04 or upper_bound <= window_start:
        return None
    frame_size = max(1, int(sample_rate * 0.04))
    hop_size = max(1, int(sample_rate * 0.01))
    first_sample = max(0, int(window_start * sample_rate))
    last_sample = min(len(signal), int(upper_bound * sample_rate))
    segment = signal[first_sample:last_sample]
    if len(segment) < frame_size:
        return None
    starts = np.arange(0, len(segment) - frame_size + 1, hop_size)
    window = np.hanning(frame_size).astype(np.float32)
    frames = np.stack([segment[start:start + frame_size] for start in starts])
    rms = np.sqrt(np.mean(frames * frames, axis=1) + 1e-12)
    spectrum = np.abs(np.fft.rfft(frames * window, axis=1))[:, 2:] + 1e-10
    flatness = np.exp(np.mean(np.log(spectrum), axis=1)) / np.mean(spectrum, axis=1)
    times = window_start + starts / sample_rate
    core = (times >= max(word_start, lexical_end - 0.35)) & (times <= lexical_end + 0.04)
    if not np.any(core):
        return None
    reference = float(np.percentile(rms[core], 60))
    # The local window can be dominated by one very long held note. A 20th
    # percentile then mistakes its quiet decay for the noise floor and cuts it
    # early; the 10th percentile still rejects separator hiss while preserving
    # that last audible part of the vowel.
    noise = float(np.percentile(rms, 10))
    if reference < 2e-5:
        return None
    low_threshold = max(2e-5, noise * 1.35, reference * 0.055)
    strong_threshold = max(4e-5, noise * 2.1, reference * 0.22)
    # Quiet frames must remain recognisably tonal. Louder consonants/releases
    # are accepted regardless of flatness so that the word is not cut early.
    active = (rms >= strong_threshold) | ((rms >= low_threshold) & (flatness <= 0.58))
    end_index = int(np.argmin(np.abs(times - lexical_end)))
    seed_candidates = np.flatnonzero(active &
                                     (times >= lexical_end - 0.13) &
                                     (times <= lexical_end + 0.16))
    if not len(seed_candidates):
        return None
    cursor = int(seed_candidates[np.argmin(np.abs(seed_candidates - end_index))])
    last_active = cursor
    maximum_gap_frames = max(1, int(bridge_gap * sample_rate / hop_size))
    inactive_frames = 0
    for index in range(cursor + 1, len(active)):
        if active[index]:
            last_active = index
            inactive_frames = 0
        else:
            inactive_frames += 1
            if inactive_frames > maximum_gap_frames:
                break
    release = min(upper_bound, times[last_active] + frame_size / sample_rate)
    if release <= lexical_end + 0.06:
        return None
    tail = slice(cursor, last_active + 1)
    tonal_share = float(np.mean(flatness[tail] <= 0.58)) if last_active >= cursor else 0.0
    energy_share = float(np.mean(rms[tail] >= low_threshold)) if last_active >= cursor else 0.0
    confidence = max(0.0, min(1.0, 0.55 * energy_share + 0.45 * tonal_share))
    return float(release), float(confidence)


def extend_final_word_sustains(lines: list, vocal_activity: list[tuple[float, float]],
                               *, audio: np.ndarray | None = None,
                               sample_rate: int = 16000,
                               release_padding: float = 0.0,
                               maximum_extension: float = 1.2,
                               maximum_sung_extension: float = 3.5) -> dict:
    """Keep a held word active through its measured vocal decay.

    ASR models generally timestamp the lexical core and often cut a sung vowel
    before its audible release. Extend line endings and unambiguous internal
    words followed by a pause, only into measured activity, never into the next
    lyric onset.
    """
    adjustments = []
    for index, line in enumerate(lines):
        if not line.words:
            continue
        for word_index, word in enumerate(line.words):
            if word.get("timing_source") not in VERIFIED_ACOUSTIC_SOURCES:
                continue
            start, end = float(word["start"]), float(word["end"])
            next_start = None
            if word_index + 1 < len(line.words):
                next_start = float(line.words[word_index + 1]["start"])
            elif index + 1 < len(lines) and lines[index + 1].words:
                next_start = float(lines[index + 1].words[0]["start"])
            internal = word_index + 1 < len(line.words)
            if internal and next_start is not None and next_start - end < 0.18:
                continue
            # A region which continues far beyond the word is usually a later
            # phrase inside one uninterrupted cluster. Internal words therefore
            # need a locally ending island; line endings retain the established
            # boundary cap because backing-vocal tails may touch the next line.
            candidates = [(begin, stop) for begin, stop in vocal_activity
                          if begin <= end + 0.1 and end + 0.06 < stop <= end + maximum_extension
                          and stop >= start
                          and (not internal or next_start is None or stop <= next_start + 0.08)]
            activity_end = max((stop for _begin, stop in candidates), default=end)
            sung_release = None
            if audio is not None:
                upper_bound = end + maximum_sung_extension
                if next_start is not None:
                    upper_bound = min(upper_bound, next_start - 0.12)
                sung_release = _measure_sung_release(
                    audio, start, end, upper_bound, sample_rate=sample_rate)
            measured_end = max(activity_end, sung_release[0] if sung_release else end)
            if measured_end <= end + 0.06:
                continue
            target = min(end + maximum_sung_extension, measured_end + release_padding)
            if next_start is not None:
                target = min(target, next_start - 0.12)
            if target - end < 0.08:
                continue
            word["acoustic_end"] = round(end, 3)
            word["end"] = round(target, 3)
            word["sustain_activity_end"] = round(activity_end, 3)
            if sung_release:
                word["sustain_release_confidence"] = round(sung_release[1], 3)
            word["sustain_extension_ms"] = round((target - end) * 1000)
            adjustments.append({"line": index + 1, "word_index": word_index + 1,
                                "word": word.get("word", ""),
                                "from": round(end, 3), "to": round(target, 3),
                                "activity_end": round(activity_end, 3),
                                "tonal_release": round(sung_release[0], 3) if sung_release else None,
                                "confidence": round(sung_release[1], 3) if sung_release else None})
    return {"method": "local-tonal-sustain-release-v4",
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
        tail = previous.words[first:]
        originals = [(float(word["start"]), float(word["end"])) for word in tail]
        cursor = old_start
        for offset, (word, (original_start, original_end)) in enumerate(zip(tail, originals)):
            remaining = len(tail) - offset - 1
            latest_end = boundary - remaining * minimum_word_duration
            mapped_start = old_start + (original_start - old_start) * scale
            mapped_end = old_start + (original_end - old_start) * scale
            word_start = min(max(cursor, mapped_start), latest_end - minimum_word_duration)
            word_end = min(latest_end, max(word_start + minimum_word_duration, mapped_end))
            word["start"] = round(word_start, 3)
            word["end"] = round(word_end, 3)
            word["timing_source"] = "overlap-display-lane-fallback"
            for syllable in word.get("syllables", []):
                syllable_start = old_start + (float(syllable["start"]) - old_start) * scale
                syllable_end = old_start + (float(syllable["end"]) - old_start) * scale
                syllable["start"] = round(max(float(word["start"]), syllable_start), 3)
                syllable["end"] = round(min(float(word["end"]), max(syllable_start, syllable_end)), 3)
            cursor = float(word["end"])
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
