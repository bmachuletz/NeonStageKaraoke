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
                    "ipa-vocal-hole-repair", "ipa-delayed-phrase-repair",
                    "stable-repetition-acoustic-onset",
                    "coherent-sentence-ipa-path",
                    "verified-local-ipa-interval",
                    "cross-line-transition-onset",
                    "isolated-supported-ipa-onset",
                    "cross-line-acoustic-onset-repair"}
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
            source = word.get("timing_source")
            is_final = word_index == len(line.words) - 1
            # The exact Stage-vocal gate may already have rejected a tonal
            # tail because it continued beyond the vocal island exported to
            # the editor and Stage. A later sustain pass must never restore
            # that rejected candidate (for example separator residue after a
            # line ending).
            if word.get("stage_vocal_release_trim_ms") is not None:
                continue
            # Paired-stem contrast has independently shown that the remaining
            # tonal tail belongs to separator leakage/accompaniment. Looking
            # at the vocal stem alone must not restore that rejected tail.
            if word.get("stem_contrast_release_trim_ms") is not None:
                continue
            # A local IPA interval ending in a measured consonant release and
            # followed by verified silence is already complete.  Re-extending
            # it here would recreate the stale tail this pass just removed.
            if word.get("phoneme_release_locked"):
                continue
            # A complete local IPA word path can already provide a precise
            # lexical release.  If that independently measured end agrees
            # with the current boundary, do not reinterpret a later vocal-
            # stem residue as a held vowel.  Genuine sustains are unaffected:
            # for those, the lexical IPA candidate ends noticeably before the
            # currently measured sung release.
            phoneme_end = word.get("phoneme_word_end_candidate")
            phoneme_confidence = float(word.get("phoneme_alignment_confidence", 0.0) or 0.0)
            if (word.get("phoneme_word_verified") is True
                    and phoneme_end is not None
                    and phoneme_confidence >= 0.40
                    and abs(float(phoneme_end) - float(word["end"])) <= 0.08):
                continue
            # A persisted editor onset is a useful anchor, but only the final
            # word may use the audio detector to extend it. Internal editor
            # words still require a verified acoustic model to avoid silently
            # rewriting intentional manual timing.
            # A display-lane fallback may have shortened the previous phrase
            # only because the following first word was still misplaced. Once
            # the sentence verifier repairs that onset, re-measure the final
            # sung vowel from audio instead of preserving the obsolete clip.
            enhanced_final = is_final and source in {
                "input-enhanced-lrc", "overlap-display-lane-fallback"}
            if source not in VERIFIED_ACOUSTIC_SOURCES and not enhanced_final:
                continue
            start = float(word["start"])
            current_end = float(word["end"])
            lexical_end = float(word.get("acoustic_end", current_end))
            next_start = None
            if word_index + 1 < len(line.words):
                next_start = float(line.words[word_index + 1]["start"])
            elif index + 1 < len(lines) and lines[index + 1].words:
                next_start = float(lines[index + 1].words[0]["start"])
            internal = word_index + 1 < len(line.words)
            if internal and next_start is not None and next_start - lexical_end < 0.18:
                continue
            # A region which continues far beyond the word is usually a later
            # phrase inside one uninterrupted cluster. Internal words therefore
            # need a locally ending island; line endings retain the established
            # boundary cap because backing-vocal tails may touch the next line.
            candidates = [(begin, stop) for begin, stop in vocal_activity
                          if begin <= lexical_end + 0.1
                          and lexical_end + 0.06 < stop <= lexical_end + maximum_extension
                          and stop >= start
                          and (not internal or next_start is None or stop <= next_start + 0.08)]
            activity_end = max((stop for _begin, stop in candidates), default=lexical_end)
            sung_release = None
            if audio is not None:
                upper_bound = lexical_end + maximum_sung_extension
                if next_start is not None:
                    upper_bound = min(upper_bound, next_start - 0.12)
                sung_release = _measure_sung_release(
                    audio, start, lexical_end, upper_bound, sample_rate=sample_rate,
                    # A held final vowel can contain a breath/reverb trough.
                    # Internal words retain the strict bridge so this cannot
                    # jump into the following lyric.
                    bridge_gap=0.68 if is_final else 0.24)
            measured_end = max(
                activity_end, sung_release[0] if sung_release else lexical_end)
            if measured_end <= lexical_end + 0.06:
                continue
            target = min(
                lexical_end + maximum_sung_extension, measured_end + release_padding)
            if next_start is not None:
                target = min(target, next_start - 0.12)
            # Multiple pipeline phases deliberately call this detector. Keep
            # the operation idempotent: a second pass must evaluate the same
            # lexical boundary and may refine it, but never walk forward by
            # another extension window.
            if target - current_end < 0.08:
                continue
            word.setdefault("acoustic_end", round(lexical_end, 3))
            word["end"] = round(target, 3)
            word["sustain_activity_end"] = round(activity_end, 3)
            if sung_release:
                word["sustain_tonal_release"] = round(sung_release[0], 3)
                word["sustain_release_confidence"] = round(sung_release[1], 3)
            word["sustain_extension_ms"] = round((target - lexical_end) * 1000)
            adjustments.append({"line": index + 1, "word_index": word_index + 1,
                                "word": word.get("word", ""),
                                "from": round(current_end, 3), "to": round(target, 3),
                                "activity_end": round(activity_end, 3),
                                "tonal_release": round(sung_release[0], 3) if sung_release else None,
                                "confidence": round(sung_release[1], 3) if sung_release else None})
    return {"method": "local-tonal-sustain-release-v5",
            "adjusted_words": len(adjustments), "release_padding_ms": round(release_padding * 1000),
            "adjustments": adjustments}


def reassign_overlong_connector_sustains(
        lines: list, audio: np.ndarray | None, *, sample_rate: int = 16000) -> dict:
    """Return a held vowel that was accidentally assigned to a connector.

    Forced aligners sometimes put a complete long decay into ``and``/``und``
    between two content words.  Reassign only when the connector is unusually
    long, has no independent phone timing, a measured tonal release reaches
    most of its window and the following word has a nearby onset.  The
    connector remains present in a short transition window; no lyric token is
    deleted.
    """
    if audio is None:
        return {"method": "acoustic-connector-sustain-reassignment-v1",
                "adjusted_words": 0, "adjustments": []}
    connectors = {"and", "&", "und"}
    adjustments = []
    for line_index, line in enumerate(lines):
        for index in range(1, len(line.words) - 1):
            previous, connector, following = (
                line.words[index - 1], line.words[index], line.words[index + 1])
            token = str(connector.get("word", "")).strip(".,!?;:'\"()[]{}").lower()
            if token not in connectors or connector.get("phonemes"):
                continue
            connector_start = float(connector["start"])
            connector_end = float(connector["end"])
            following_start = float(following["start"])
            if (connector_end - connector_start < 0.65
                    or not 0.04 <= following_start - connector_end <= 0.35):
                continue
            release = _measure_sung_release(
                audio, float(previous["start"]), float(previous["end"]),
                following_start, sample_rate=sample_rate, bridge_gap=0.28)
            if (release is None or release[1] < 0.70
                    or release[0] < connector_end - 0.32):
                continue
            old_previous_end = float(previous["end"])
            old_connector_start = connector_start
            previous["acoustic_end"] = round(old_previous_end, 3)
            # Keep the visual word alive until the historical connector end.
            # This includes the short measured release padding singers expect,
            # while the following connector gets the remaining transition.
            previous["end"] = round(connector_end, 3)
            previous["sustain_activity_end"] = round(release[0], 3)
            previous["sustain_release_confidence"] = round(release[1], 3)
            previous["sustain_extension_ms"] = round(
                (connector_end - old_previous_end) * 1000)
            connector["start"] = round(connector_end, 3)
            connector["end"] = round(following_start, 3)
            connector["timing_source"] = "connector-transition-reassignment"
            connector["connector_original_start"] = round(old_connector_start, 3)
            adjustments.append({
                "line": line_index + 1,
                "word": previous.get("word", ""),
                "connector": connector.get("word", ""),
                "from": round(old_previous_end, 3),
                "to": round(connector_end, 3),
                "measured_release": round(release[0], 3),
                "confidence": round(release[1], 3),
            })
    return {"method": "acoustic-connector-sustain-reassignment-v1",
            "adjusted_words": len(adjustments), "adjustments": adjustments}


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


def eliminate_remaining_line_overlaps(lines: list, *, minimum_word_duration: float = 0.04,
                                      protected_line_indices: set[int] | None = None) -> dict:
    """Produce a single monotonic karaoke lane after acoustic reanalysis.

    Real recordings can contain a backing-vocal tail and the next lead phrase
    at the same time. The aligners should analyse that conflict first. If it is
    still unresolved, preserve the next lead onset and compress only the
    colliding tail of the previous line. The result is reviewable rather than
    silently emitting two simultaneously active karaoke lines.
    """
    lanes = sorted({max(0, int(getattr(line, "voice_lane", 0))) for line in lines})
    if len(lanes) > 1:
        protected = protected_line_indices or set()
        combined: list[dict] = []
        for lane in lanes:
            global_indices = [index for index, line in enumerate(lines)
                              if max(0, int(getattr(line, "voice_lane", 0))) == lane]
            subset = [lines[index] for index in global_indices]
            local_protected = {local for local, original in enumerate(global_indices)
                               if original in protected}
            result = eliminate_remaining_line_overlaps(
                subset, minimum_word_duration=minimum_word_duration,
                protected_line_indices=local_protected)
            for adjustment in result["adjustments"]:
                item = dict(adjustment)
                item["voice_lane"] = lane
                combined.append(item)
        return {"method": "multi-karaoke-lane-fallback-v1",
                "adjusted_pairs": len(combined), "adjustments": combined}

    adjustments: list[dict] = []
    protected = protected_line_indices or set()
    for index in range(1, len(lines)):
        previous, current = lines[index - 1], lines[index]
        if not previous.words or not current.words:
            continue
        boundary = float(current.words[0]["start"])
        previous_end = float(previous.words[-1]["end"])
        if previous_end <= boundary + 0.001:
            continue

        # A manually corrected previous line is the stronger boundary. Move
        # only the colliding prefix of the generated following line. This is
        # the mirror image of the ordinary fallback below and prevents the
        # final manual-restore pass from reintroducing an invalid overlap.
        if index - 1 in protected and index not in protected:
            prefix_end = 0
            while (prefix_end + 1 < len(current.words)
                   and float(current.words[prefix_end + 1]["start"]) < previous_end):
                prefix_end += 1
            next_start = (float(current.words[prefix_end + 1]["start"])
                          if prefix_end + 1 < len(current.words) else
                          float(current.words[prefix_end]["end"]))
            prefix_limit = max(float(current.words[prefix_end]["end"]), next_start)
            count = prefix_end + 1
            available = prefix_limit - previous_end
            if available >= count * minimum_word_duration:
                old_start = float(current.words[0]["start"])
                old_limit = max(prefix_limit, old_start + 0.001)
                scale = available / (old_limit - old_start)
                cursor = previous_end
                for offset, word in enumerate(current.words[:count]):
                    original_start = float(word["start"])
                    original_end = float(word["end"])
                    remaining = count - offset - 1
                    latest_end = prefix_limit - remaining * minimum_word_duration
                    mapped_start = previous_end + (original_start - old_start) * scale
                    mapped_end = previous_end + (original_end - old_start) * scale
                    word_start = min(max(cursor, mapped_start), latest_end - minimum_word_duration)
                    word_end = min(latest_end, max(word_start + minimum_word_duration, mapped_end))
                    word["start"] = round(word_start, 3)
                    word["end"] = round(word_end, 3)
                    word["timing_source"] = "manual-neighbor-overlap-fallback"
                    for syllable in word.get("syllables", []):
                        syllable_start = previous_end + (
                            float(syllable["start"]) - old_start) * scale
                        syllable_end = previous_end + (
                            float(syllable["end"]) - old_start) * scale
                        syllable["start"] = round(
                            max(float(word["start"]), syllable_start), 3)
                        syllable["end"] = round(
                            min(float(word["end"]), max(syllable_start, syllable_end)), 3)
                    cursor = float(word["end"])
                current.words[0]["start"] = round(previous_end, 3)
                adjustments.append({
                    "previous_line": index, "next_line": index + 1,
                    "overlap_ms": round((previous_end - boundary) * 1000, 1),
                    "prefix_words": count,
                    "boundary": round(previous_end, 3),
                    "method": "preserve-manual-previous-release",
                })
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
