"""Repair lines compressed below a physically possible articulation rate.

A forced aligner under pressure can push several complete phrases into a few
hundred milliseconds while a neighbouring line keeps a large share of the same
vocal region.  The resulting lyrics run independently of the waveform even
though the geometry is monotonic and free of overlaps, so no existing gate
objects.

The detector used here is linguistic rather than song specific: no singer
articulates faster than roughly fourteen syllables per second, so a line below
that floor cannot be a real delivery regardless of genre, language or tempo.
Repair redistributes such a run over the measured vocal activity it may legally
occupy, proportional to syllable count.  When the reachable activity is too
short to lift every line above the floor the run is left untouched and reported
with its deficit - a wrong guess is worse than a visible defect.
"""
from __future__ import annotations

import numpy as np

from .syllables import _pronunciation_syllable_count

# Fourteen syllables per second. Sustained fast rap and hardcore punk peak
# around eight to nine, so this is an impossibility bound, not a style bound.
IMPOSSIBLE_SYLLABLE_SECONDS = 0.07
# Word-local evidence is noisier than a complete phrase. Fifty-five
# milliseconds still excludes display-breaking 7--40 ms syllables without
# labelling a legitimately clipped punk/rap syllable as physically impossible.
IMPOSSIBLE_WORD_SYLLABLE_SECONDS = 0.055
# Lines outside this band do not describe an ordinary delivery and are ignored
# when estimating the song's own rate: held notes and collapses would bias it.
PLAUSIBLE_BAND = (0.09, 0.60)
# A redistribution must reach at least this share of the song's own median
# syllable duration. Outros are sung faster than verses, but not arbitrarily so.
PLAUSIBLE_RATE_FACTOR = 0.35
# A neighbour holding more than this multiple of the song's own rate cannot be
# singing all of that time and may be redistributed together with the collapse.
SLOW_NEIGHBOUR_FACTOR = 2.5


def _lane_of(lines: list, index: int, pending: dict[int, int] | None) -> int:
    if pending and index in pending:
        return int(pending[index])
    return int(getattr(lines[index], "voice_lane", 0) or 0)


def _syllables(line, language: str) -> int:
    total = 0
    for word in line.words:
        total += max(1, _pronunciation_syllable_count(
            str(word.get("word", "")), language))
    return max(1, total)


def _span(line) -> tuple[float, float]:
    return (float(line.words[0]["start"]),
            float(line.words[-1].get("end", line.words[-1]["start"])))


def _activity_within(regions: list[tuple[float, float]],
                     start: float, end: float) -> list[tuple[float, float]]:
    """Measured singing inside a window, clipped to that window."""
    pieces = []
    for region_start, region_end in regions:
        first, last = max(region_start, start), min(region_end, end)
        if last > first:
            pieces.append((first, last))
    return pieces


def _bridge_unvoiced_word_gaps(pieces: list[tuple[float, float]],
                               maximum_gap: float = 0.10) -> list[tuple[float, float]]:
    """Join tiny detector holes which may be an unvoiced consonant.

    The energy activity mask is not a phoneme mask. Stops and fricatives can
    briefly disappear from an isolated vocal stem while still belonging to a
    continuous word run. Longer gaps remain real phrase boundaries.
    """
    merged: list[tuple[float, float]] = []
    for start, end in pieces:
        if merged and start - merged[-1][1] <= maximum_gap:
            merged[-1] = (merged[-1][0], end)
        else:
            merged.append((start, end))
    return merged


def vocal_onsets(audio, start: float, end: float, *, sample_rate: int = 16000,
                 frame: int = 400, hop: int = 160,
                 minimum_separation: float = 0.08) -> tuple[np.ndarray, np.ndarray]:
    """Spectral-flux peaks of the exact Stage vocal stem inside a window.

    A proportional layout produces possible durations but places every word on
    arithmetic instead of on the voice. These peaks are the cheapest local
    evidence for where a sung word actually starts; they are used to snap an
    existing layout, never to invent one.
    """
    empty = (np.empty(0, dtype=np.float64), np.empty(0, dtype=np.float64))
    signal = np.asarray(audio, dtype=np.float32)
    if signal.ndim > 1:
        signal = signal.mean(axis=-1)
    first = max(0, int(start * sample_rate))
    last = min(len(signal), int(end * sample_rate))
    if last - first < frame * 3:
        return empty
    segment = signal[first:last]
    window = np.hanning(frame).astype(np.float32)
    spectra = np.abs(np.fft.rfft(
        np.lib.stride_tricks.sliding_window_view(segment, frame)[::hop] * window,
        axis=-1))
    if len(spectra) < 3:
        return empty
    flux = np.maximum(0.0, np.diff(spectra, axis=0)).sum(axis=-1)
    if not np.any(flux > 0):
        return empty
    # Subtract a local moving median: a decaying vowel and vibrato both keep
    # the raw flux elevated, so only a sharp rise above the local level is an
    # attack. This is the usual adaptive-whitening step before peak picking.
    span = max(1, int(round(0.15 * sample_rate / hop)))
    padded = np.pad(flux, span, mode="edge")
    baseline = np.array([np.median(padded[index:index + 2 * span + 1])
                         for index in range(len(flux))])
    detection = np.maximum(0.0, flux - baseline)
    if not np.any(detection > 0):
        return empty
    floor = float(np.percentile(detection[detection > 0], 50))
    flux = detection
    peaks = [index for index in range(1, len(flux) - 1)
             if flux[index] >= flux[index - 1] and flux[index] >= flux[index + 1]
             and flux[index] >= floor]
    if not peaks:
        return empty
    # A decaying sung vowel and vibrato both ripple the flux, so plain local
    # maxima cluster around one attack. Keep the strongest peak per
    # neighbourhood: a word onset cannot follow another within a few frames.
    separation = max(1, int(round(minimum_separation * sample_rate / hop)))
    selected: list[int] = []
    for index in sorted(peaks, key=lambda position: -flux[position]):
        if all(abs(index - chosen) >= separation for chosen in selected):
            selected.append(index)
    # +1 because a flux value describes the transition into its frame.
    selected.sort()
    times = np.asarray([start + (index + 1) * hop / sample_rate
                        for index in selected], dtype=np.float64)
    strengths = np.asarray([flux[index] for index in selected], dtype=np.float64)
    return times, strengths


def _locate(pieces: list[tuple[float, float]], offset: float) -> tuple[int, float]:
    """Map a position on the concatenated activity timeline back to real time.

    Returns the owning interval as well, so a caller can keep a word inside one
    continuous stretch of singing instead of letting it span a pause.
    """
    for index, (start, end) in enumerate(pieces):
        length = end - start
        if offset < length or index == len(pieces) - 1:
            return index, min(end, start + offset)
        offset -= length
    return len(pieces) - 1, pieces[-1][1]


def repair_collapsed_lines(lines: list, activity: list[tuple[float, float]], *,
                           language: str = "de",
                           impossible_syllable_seconds: float = IMPOSSIBLE_SYLLABLE_SECONDS,
                           plausible_band: tuple[float, float] = PLAUSIBLE_BAND,
                           plausible_rate_factor: float = PLAUSIBLE_RATE_FACTOR,
                           slow_neighbour_factor: float = SLOW_NEIGHBOUR_FACTOR,
                           maximum_absorbed_neighbours: int = 6,
                           audio=None, sample_rate: int = 16000,
                           onset_snap_radius: float = 0.30,
                           minimum_word_seconds: float = 0.05,
                           pending_voice_lanes: dict[int, int] | None = None) -> dict:
    summary = {
        "enabled": True,
        "method": "impossible-articulation-rate-redistribution-v1",
        "impossible_syllable_ms": round(impossible_syllable_seconds * 1000, 1),
        "onset_evidence": audio is not None,
        "collapsed_runs": 0,
        "repaired_runs": 0,
        "repaired_lines": 0,
        "repairs": [],
        "rejections": [],
    }
    usable = [index for index, line in enumerate(lines) if line.words]
    if not usable:
        return {**summary, "enabled": False, "reason": "no-timed-lines"}
    regions = sorted((float(begin), float(end)) for begin, end in activity
                     if end > begin)
    if not regions:
        return {**summary, "enabled": False, "reason": "no-vocal-activity"}

    syllables = {index: _syllables(lines[index], language) for index in usable}
    rates = {}
    for index in usable:
        start, end = _span(lines[index])
        rates[index] = max(0.0, end - start) / syllables[index]
    ordinary = sorted(rate for rate in rates.values()
                      if plausible_band[0] <= rate <= plausible_band[1])
    summary["song_syllable_ms"] = (round(ordinary[len(ordinary) // 2] * 1000, 1)
                                   if ordinary else None)

    # A restored manual line is human authority even when its rate looks odd.
    collapsed = [index for index in usable
                 if rates[index] < impossible_syllable_seconds
                 and not bool(getattr(lines[index], "manual_adjusted", False))]
    if not collapsed:
        return summary

    # Work inside each lane: independent lanes overlap by design and must not
    # constrain one another.
    by_lane: dict[int, list[int]] = {}
    for index in usable:
        by_lane.setdefault(
            _lane_of(lines, index, pending_voice_lanes), []).append(index)

    song_rate = (ordinary[len(ordinary) // 2] if ordinary else None)
    # A redistribution must not only be physically possible, it must also stay
    # in the delivery range this very song demonstrates elsewhere.
    minimum_rate = impossible_syllable_seconds
    if song_rate is not None:
        minimum_rate = max(minimum_rate, plausible_rate_factor * song_rate)
    slow_rate = (song_rate * slow_neighbour_factor) if song_rate else None

    runs: list[tuple[int, list[int]]] = []
    for lane, ordered in by_lane.items():
        current: list[int] = []
        for position, index in enumerate(ordered):
            if index in set(collapsed):
                if current and ordered.index(current[-1]) == position - 1:
                    current.append(index)
                else:
                    if current:
                        runs.append((lane, current))
                    current = [index]
        if current:
            runs.append((lane, current))
    summary["collapsed_runs"] = len(runs)

    for lane, run in runs:
        ordered = by_lane[lane]
        first, last = ordered.index(run[0]), ordered.index(run[-1])
        absorbed: list[int] = []

        def envelope(low: int, high: int) -> tuple[float, float]:
            start = _span(lines[ordered[low - 1]])[1] if low > 0 else 0.0
            end = (_span(lines[ordered[high + 1]])[0] if high + 1 < len(ordered)
                   else regions[-1][1])
            return start, end

        def capacity(low: int, high: int) -> tuple[list, float, int]:
            window = _activity_within(regions, *envelope(low, high))
            total = sum(end - start for start, end in window)
            demand = sum(syllables[ordered[position]]
                         for position in range(low, high + 1))
            return window, total, demand

        def absorbable(position: int) -> bool:
            """A neighbour may join only if it is not human authority."""
            return (0 <= position < len(ordered)
                    and not bool(getattr(lines[ordered[position]],
                                         "manual_adjusted", False)))

        pieces, available, demand = capacity(first, last)
        # Grow across neighbours which cannot be right either: a line far
        # slower than this song's own delivery is holding time it never sings.
        # Growth is bounded and stops as soon as the block becomes plausible.
        for _ in range(maximum_absorbed_neighbours):
            if demand and available / demand >= minimum_rate:
                break
            options = []
            for position, side in ((first - 1, "left"), (last + 1, "right")):
                if not absorbable(position):
                    continue
                options.append((rates[ordered[position]], position, side))
            if not options:
                break
            # Prefer the neighbour with the largest unjustified share of time;
            # a line far slower than the song's own delivery is the cheapest
            # source of the missing seconds. Growth continues into ordinary
            # neighbours only while the block is still implausible, and the
            # absorption count stays bounded.
            _surplus, position, side = max(options)
            absorbed.append(ordered[position])
            if side == "left":
                first = position
            else:
                last = position
            pieces, available, demand = capacity(first, last)

        block = [ordered[position] for position in range(first, last + 1)]
        window_start, window_end = envelope(first, last)
        total_syllables = demand
        detail = {
            "lines": [index + 1 for index in run],
            "absorbed_neighbours": [index + 1 for index in absorbed],
            "lane": lane,
            "syllables": total_syllables,
            "current_ms_per_syllable": round(min(
                rates[index] for index in run) * 1000, 1),
            "song_ms_per_syllable": (round(song_rate * 1000, 1)
                                     if song_rate else None),
            "envelope": [round(window_start, 3), round(window_end, 3)],
        }
        required = total_syllables * minimum_rate
        detail["available_activity_ms"] = round(available * 1000, 1)
        detail["required_activity_ms"] = round(required * 1000, 1)
        detail["resulting_ms_per_syllable"] = (
            round(available / total_syllables * 1000, 1) if total_syllables else None)
        if not pieces or available < required:
            summary["rejections"].append({
                **detail, "status": "rejected",
                "reason": "insufficient-reachable-vocal-activity",
                "deficit_ms": round((required - available) * 1000, 1),
            })
            continue
        run = block

        # Lay the block out proportionally first, then move the interior
        # boundaries onto real onsets. Proportion alone yields arithmetic
        # durations which look possible but do not track the voice.
        layout: list[dict] = []
        cursor = 0.0
        for index in run:
            share = available * syllables[index] / total_syllables
            counts = [max(1, _pronunciation_syllable_count(
                str(word.get("word", "")), language))
                for word in lines[index].words]
            inner = cursor
            for word, count in zip(lines[index].words, counts):
                piece, word_start = _locate(pieces, inner)
                inner = min(available, inner + share * count / sum(counts))
                layout.append({"line": index, "word": word, "piece": piece,
                               "start": word_start})
            cursor = min(available, cursor + share)

        onsets, onset_strength = (
            vocal_onsets(audio, window_start, window_end, sample_rate=sample_rate)
            if audio is not None
            else (np.empty(0), np.empty(0)))
        moved = 0
        if len(onsets):
            for position in range(1, len(layout)):
                entry = layout[position]
                previous = layout[position - 1]
                following = (layout[position + 1]["start"]
                             if position + 1 < len(layout)
                             else pieces[entry["piece"]][1])
                piece_start, piece_end = pieces[entry["piece"]]
                # Scale the search with the word's own share: a long word may
                # sit further from its arithmetic slot than a short one, while
                # the absolute cap keeps the block from reordering itself.
                radius = min(onset_snap_radius,
                             0.5 * max(0.0, following - entry["start"]))
                lower = max(previous["start"] + minimum_word_seconds,
                            piece_start, entry["start"] - radius)
                upper = min(following - minimum_word_seconds, piece_end,
                            entry["start"] + radius)
                if upper <= lower:
                    continue
                inside = (onsets >= lower) & (onsets <= upper)
                window = onsets[inside]
                if not len(window):
                    continue
                # The proportional slot is only a weak prior. A ripple of the
                # previous vowel can sit closer to it than the real attack, so
                # score strength against distance instead of taking the nearest.
                distance = np.abs(window - entry["start"]) / max(1e-6, radius)
                score = onset_strength[inside] / (1.0 + distance)
                choice = float(window[np.argmax(score)])
                moved += int(abs(choice - entry["start"]) > 0.005)
                entry["start"] = choice
        detail["onset_snapped_words"] = moved
        detail["onset_candidates"] = int(len(onsets))

        for position, entry in enumerate(layout):
            word = entry["word"]
            piece_end = pieces[entry["piece"]][1]
            if position + 1 < len(layout) and layout[position + 1]["piece"] == entry["piece"]:
                # Connected singing: the next onset owns the boundary.
                word_end = layout[position + 1]["start"]
            else:
                # Last word before a measured pause keeps the stretch's end.
                word_end = piece_end
            word_end = max(word_end, min(entry["start"] + minimum_word_seconds,
                                         piece_end))
            word["collapsed_line_original_start"] = round(float(word["start"]), 3)
            word["collapsed_line_original_end"] = round(
                float(word.get("end", word["start"])), 3)
            word["start"] = round(entry["start"], 3)
            word["end"] = round(word_end, 3)
            word["timing_source"] = "collapsed-line-activity-redistribution"

        for index in run:
            line = lines[index]
            line.timestamp = round(float(line.words[0]["start"]), 3)
            line_start = float(line.words[0]["start"])
            line_end = float(line.words[-1]["end"])
            summary["repaired_lines"] += 1
            detail.setdefault("placements", []).append({
                "line": index + 1,
                "start": round(line_start, 3),
                "end": round(line_end, 3),
                "syllables": syllables[index],
                "ms_per_syllable": round(
                    (line_end - line_start) / syllables[index] * 1000, 1),
            })
        summary["repaired_runs"] += 1
        summary["repairs"].append({**detail, "status": "repaired"})
    return summary


def repair_compressed_word_runs(
        lines: list, activity: list[tuple[float, float]], *,
        language: str = "de",
        impossible_syllable_seconds: float = IMPOSSIBLE_WORD_SYLLABLE_SECONDS,
        audio=None, sample_rate: int = 16000,
        onset_snap_radius: float = 0.24,
        minimum_word_seconds: float = 0.05) -> dict:
    """Repair locally collapsed words hidden by a plausible line duration.

    A long held neighbour or an internal pause can make the average syllable
    rate of a complete line look plausible even when one word has been reduced
    to a few milliseconds.  Syllable derivation cannot repair that geometry:
    it can only squeeze its syllables into the already broken word window.

    This pass therefore operates before syllable derivation.  It grows the
    smallest run containing an impossible word until measured vocal activity
    can carry all of the run's syllables, then lays only that run back onto the
    activity.  Manual editor geometry is authoritative and is never touched.
    """
    summary = {
        "enabled": True,
        "method": "local-impossible-word-rate-redistribution-v1",
        "impossible_syllable_ms": round(impossible_syllable_seconds * 1000, 1),
        "onset_evidence": audio is not None,
        "compressed_words": 0,
        "repaired_runs": 0,
        "repaired_words": 0,
        "repairs": [],
        "rejections": [],
    }
    regions = sorted((float(begin), float(end)) for begin, end in activity
                     if end > begin)
    if not regions:
        return {**summary, "enabled": False, "reason": "no-vocal-activity"}

    lane_neighbours: dict[int, tuple[float | None, float | None]] = {}
    by_lane: dict[int, list[int]] = {}
    for index, candidate in enumerate(lines):
        if getattr(candidate, "words", None):
            lane = int(getattr(candidate, "voice_lane", 0) or 0)
            by_lane.setdefault(lane, []).append(index)
    for ordered in by_lane.values():
        for position, index in enumerate(ordered):
            previous_end = (_span(lines[ordered[position - 1]])[1]
                            if position else None)
            next_start = (_span(lines[ordered[position + 1]])[0]
                          if position + 1 < len(ordered) else None)
            lane_neighbours[index] = (previous_end, next_start)

    for line_index, line in enumerate(lines):
        words = list(getattr(line, "words", []) or [])
        if not words or bool(getattr(line, "manual_adjusted", False)):
            continue
        counts = [max(1, _pronunciation_syllable_count(
            str(word.get("word", "")), language)) for word in words]
        compressed = [
            index for index, (word, count) in enumerate(zip(words, counts))
            if (float(word.get("end", word["start"])) - float(word["start"]))
            + 1e-6 < count * impossible_syllable_seconds
        ]
        summary["compressed_words"] += len(compressed)
        if not compressed:
            continue

        pending = set(compressed)
        while pending:
            seed = min(pending)
            first = last = seed

            def envelope(low: int, high: int) -> tuple[float, float]:
                left = (float(words[low - 1].get("end", words[low - 1]["start"]))
                        if low else float(words[low]["start"]))
                right = (float(words[high + 1]["start"])
                         if high + 1 < len(words)
                         else (lane_neighbours.get(line_index, (None, None))[1]
                               or regions[-1][1]))
                return left, right

            def capacity(low: int, high: int):
                pieces = _bridge_unvoiced_word_gaps(
                    _activity_within(regions, *envelope(low, high)))
                available = sum(end - start for start, end in pieces)
                demand = sum(counts[low:high + 1])
                return pieces, available, demand

            pieces, available, demand = capacity(first, last)
            # Include the cheapest adjacent word until the measured voice can
            # support the run.  This handles a short/long duration inversion
            # without moving the remaining, already credible part of a line.
            while available + 1e-6 < demand * impossible_syllable_seconds:
                choices = []
                if first > 0:
                    index = first - 1
                    duration = (float(words[index].get("end", words[index]["start"]))
                                - float(words[index]["start"]))
                    choices.append((duration / counts[index], index, "left"))
                if last + 1 < len(words):
                    index = last + 1
                    duration = (float(words[index].get("end", words[index]["start"]))
                                - float(words[index]["start"]))
                    choices.append((duration / counts[index], index, "right"))
                if not choices:
                    break
                _rate, _index, side = min(choices)
                if side == "left":
                    first -= 1
                else:
                    last += 1
                pieces, available, demand = capacity(first, last)

            detail = {
                "line": line_index + 1,
                "word_indices": list(range(first + 1, last + 2)),
                "words": [str(word.get("word", ""))
                          for word in words[first:last + 1]],
                "compressed_word_indices": [index + 1 for index in compressed
                                             if first <= index <= last],
                "envelope": [round(value, 3) for value in envelope(first, last)],
                "available_activity_ms": round(available * 1000, 1),
                "required_activity_ms": round(
                    demand * impossible_syllable_seconds * 1000, 1),
            }
            if not pieces or available + 1e-6 < demand * impossible_syllable_seconds:
                summary["rejections"].append({
                    **detail, "status": "rejected",
                    "reason": "insufficient-local-vocal-activity",
                })
                pending.difference_update(range(first, last + 1))
                continue

            # Proportional positions are a neutral linguistic prior.  As with
            # whole-line recovery, spectral-flux attacks may move interior
            # starts onto independent acoustic evidence.
            layout = []
            cursor = 0.0
            for index in range(first, last + 1):
                piece, start = _locate(pieces, cursor)
                required = max(
                    minimum_word_seconds,
                    counts[index] * impossible_syllable_seconds)
                if layout:
                    previous = layout[-1]
                    previous_required = max(
                        minimum_word_seconds,
                        counts[previous["index"]] * impossible_syllable_seconds)
                    if piece < previous["piece"]:
                        piece = previous["piece"]
                        start = previous["start"] + previous_required
                    elif piece == previous["piece"]:
                        start = max(start, previous["start"] + previous_required)
                # Concatenated activity can assign a word to the tiny tail of
                # one region although a full vocal region follows. A karaoke
                # word must live in one continuous piece; skip such a tail.
                if pieces[piece][1] - start + 1e-6 < required:
                    for candidate_piece in range(piece + 1, len(pieces)):
                        candidate_start, candidate_end = pieces[candidate_piece]
                        if candidate_end - candidate_start + 1e-6 >= required:
                            piece, start = candidate_piece, candidate_start
                            break
                layout.append({"index": index, "piece": piece, "start": start})
                cursor = min(available, cursor + available * counts[index] / demand)

            window_start, window_end = envelope(first, last)
            onsets, strengths = (
                vocal_onsets(audio, window_start, window_end, sample_rate=sample_rate)
                if audio is not None else (np.empty(0), np.empty(0)))
            snapped = 0
            if len(onsets):
                for position in range(1, len(layout)):
                    entry = layout[position]
                    previous = layout[position - 1]
                    following = (layout[position + 1]["start"]
                                 if position + 1 < len(layout)
                                 else pieces[entry["piece"]][1])
                    piece_start, piece_end = pieces[entry["piece"]]
                    previous_minimum = max(
                        minimum_word_seconds,
                        counts[previous["index"]] * impossible_syllable_seconds)
                    current_minimum = max(
                        minimum_word_seconds,
                        counts[entry["index"]] * impossible_syllable_seconds)
                    lower = max(previous["start"] + previous_minimum,
                                piece_start, entry["start"] - onset_snap_radius)
                    upper = min(following - current_minimum,
                                piece_end, entry["start"] + onset_snap_radius)
                    inside = (onsets >= lower) & (onsets <= upper)
                    if upper <= lower or not np.any(inside):
                        continue
                    candidates = onsets[inside]
                    distance = (np.abs(candidates - entry["start"])
                                / max(1e-6, onset_snap_radius))
                    score = strengths[inside] / (1.0 + distance)
                    choice = float(candidates[np.argmax(score)])
                    snapped += int(abs(choice - entry["start"]) > 0.005)
                    entry["start"] = choice

            proposed = []
            for position, entry in enumerate(layout):
                piece_end = pieces[entry["piece"]][1]
                if (position + 1 < len(layout)
                        and layout[position + 1]["piece"] == entry["piece"]):
                    end = layout[position + 1]["start"]
                else:
                    end = piece_end
                required_word_duration = max(
                    minimum_word_seconds,
                    counts[entry["index"]] * impossible_syllable_seconds)
                end = max(end, min(entry["start"] + required_word_duration,
                                   piece_end))
                proposed.append((entry, end, required_word_duration))
            if any(end - entry["start"] + 0.001 < required
                   for entry, end, required in proposed):
                summary["rejections"].append({
                    **detail, "status": "rejected",
                    "reason": "fragmented-activity-cannot-carry-word",
                })
                pending.difference_update(range(first, last + 1))
                continue

            placements = []
            for entry, end, _required in proposed:
                word = words[entry["index"]]
                old_start = float(word["start"])
                old_end = float(word.get("end", word["start"]))
                word["compressed_word_original_start"] = round(old_start, 3)
                word["compressed_word_original_end"] = round(old_end, 3)
                word["start"] = round(entry["start"], 3)
                word["end"] = round(end, 3)
                word["timing_source"] = "compressed-word-activity-redistribution"
                # Absolute child timings describe the old word interval. They
                # must be derived again inside the repaired geometry or they
                # can collapse a fresh syllable back to zero milliseconds.
                for key in ("syllables", "syllable_phonemes", "phonemes",
                            "ctc_characters", "acoustic_end",
                            "syllable_method", "syllable_confidence"):
                    word.pop(key, None)
                placements.append({
                    "word": str(word.get("word", "")),
                    "start": word["start"], "end": word["end"],
                    "syllables": counts[entry["index"]],
                })
            line.timestamp = float(words[0]["start"])
            summary["repaired_runs"] += 1
            summary["repaired_words"] += len(layout)
            summary["repairs"].append({
                **detail, "status": "repaired",
                "onset_candidates": int(len(onsets)),
                "onset_snapped_words": snapped,
                "placements": placements,
            })
            pending.difference_update(range(first, last + 1))
    return summary
