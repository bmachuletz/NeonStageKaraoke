from __future__ import annotations

from functools import lru_cache

import numpy as np

from .boundary_evidence import banded_boundary_step


def _onset_score(vocal: np.ndarray, timestamp: float) -> float:
    evidence = banded_boundary_step(vocal, timestamp, sample_rate=16000)
    if not evidence:
        return -999.0
    return abs(float(evidence.get("independent_db", 0.0)))


def _snap_to_vocal_onset(vocal: np.ndarray, timestamp: float, *,
                         search_radius: float = .075,
                         minimum_move: float = .015) -> float:
    candidates = np.arange(timestamp - search_radius,
                           timestamp + search_radius + .001, .005)
    if not len(candidates):
        return timestamp
    best = min(
        (float(candidate) for candidate in candidates
         if _onset_score(vocal, candidate) >= 6.0),
        key=lambda candidate: abs(candidate - timestamp),
        default=timestamp)
    return best if abs(best - timestamp) >= minimum_move else timestamp


def _vocal_onset_peaks(vocal: np.ndarray, start: float, end: float,
                       *, threshold_db: float = 95.0,
                       minimum_separation: float = .08) -> list[tuple[float, float]]:
    times = np.arange(start, end + .001, .005)
    scores = [_onset_score(vocal, float(time)) for time in times]
    peaks: list[tuple[float, float]] = []
    for index in range(1, len(times) - 1):
        time, score = float(times[index]), scores[index]
        if (score < threshold_db
                or score < scores[index - 1]
                or score < scores[index + 1]):
            continue
        if peaks and time - peaks[-1][0] < minimum_separation:
            if score > peaks[-1][1]:
                peaks[-1] = (time, score)
        else:
            peaks.append((time, score))
    return peaks


def _ordered_vocal_onsets(peaks: list[tuple[float, float]],
                          centers: list[float]) -> list[float]:
    """Assign one increasing onset peak to each expected word onset."""
    times = [peak[0] for peak in peaks]

    @lru_cache(maxsize=None)
    def solve(word_index: int, peak_index: int) -> tuple[float, tuple[int, ...]]:
        if word_index == len(centers):
            return 0.0, ()
        if peak_index == len(times):
            return float("inf"), ()
        skip_cost, skip_path = solve(word_index, peak_index + 1)
        take_cost, take_path = solve(word_index + 1, peak_index + 1)
        take_cost += (times[peak_index] - centers[word_index]) ** 2
        if take_cost < skip_cost:
            return take_cost, (peak_index,) + take_path
        return skip_cost, skip_path

    _, selected = solve(0, 0)
    return [times[index] for index in selected]


def _clamp(value: float, lower: float, upper: float) -> float:
    return min(max(value, lower), max(lower, upper))


def _overlap(start: float, end: float, region: tuple[float, float]) -> float:
    return max(0.0, min(end, region[1]) - max(start, region[0]))


def _lane_of(lines: list, index: int, pending: dict[int, int] | None) -> int:
    """Voice lane a line will have, including lane promotions decided later."""
    if pending and index in pending:
        return int(pending[index])
    return int(getattr(lines[index], "voice_lane", 0) or 0)


def _next_line_start_in_lane(lines: list, line_index: int, lane: int,
                             pending: dict[int, int] | None) -> float | None:
    """Only lines sharing a voice lane constrain each other.

    Independent lanes are allowed to overlap by design, so a backing phrase in
    another lane must not prevent a lead line from being moved onto its real
    vocal onset.
    """
    for offset, following in enumerate(lines[line_index + 1:], start=line_index + 1):
        if not following.words:
            continue
        if _lane_of(lines, offset, pending) != lane:
            continue
        return float(following.words[0]["start"])
    return None


def _silent_leading_words(words: list, island_start: float) -> int:
    """Count complete leading words which end before the vocal island starts."""
    count = 0
    for word in words:
        if float(word["end"]) > island_start:
            break
        count += 1
    return count


def _reflow_after_silent_prefix(line, target: float, *,
                                 proposed_release: float | None = None,
                                 vocal_audio: np.ndarray | None = None,
                                 minimum_word: float = .03) -> bool:
    """Reflow a line while keeping each word's relative timing evidence.

    The old timestamps already contain the relative word timing from ASR.  They
    are first moved coherently to the measured line onset; only then is each
    resulting boundary allowed to snap to local vocal evidence.  Snapping from
    the unshifted position would reproduce the fixed +400 ms translation that
    was wrong in the Madsen regression case.
    """
    if not line.words:
        return False
    final_release = min(float(line.words[-1]["end"]),
                        proposed_release
                        if proposed_release is not None else float("inf"))
    first_start = float(line.words[0]["start"])
    last_end = float(line.words[-1]["end"])
    shift = target - first_start
    translated_release = last_end + shift
    source_span = last_end - first_start
    if final_release < target or source_span <= 0:
        return False
    scale = ((final_release - target) / source_span
             if translated_release > final_release else 1.0)
    if scale <= 0 or final_release - target < minimum_word * len(line.words):
        return False

    centers = [target + (float(word["start"]) - first_start) * scale
               for word in line.words]
    if vocal_audio is not None:
        raw_starts = _ordered_vocal_onsets(
            _vocal_onset_peaks(vocal_audio, target, final_release), centers)
    else:
        raw_starts = centers
    if len(raw_starts) != len(line.words):
        raw_starts = centers
    raw_starts = [_clamp(start, target, final_release - minimum_word)
                  for start in raw_starts]

    starts: list[float] = []
    previous_start = target - minimum_word
    for word in line.words:
        start = max(raw_starts[len(starts)], previous_start + minimum_word)
        starts.append(_clamp(start, target, final_release - minimum_word))
        previous_start = starts[-1]

    placements: list[tuple[float, float]] = []
    for index, (word, start) in enumerate(zip(line.words, starts)):
        next_start = starts[index + 1] if index + 1 < len(starts) else final_release
        duration = max(minimum_word, float(word["end"]) - float(word["start"]))
        end = min(final_release, start + duration, next_start)
        placements.append((start, max(start + minimum_word, end)))

    for word, (start, end) in zip(line.words, placements):
        old_start = float(word["start"])
        old_end = float(word["end"])
        word["stage_vocal_silent_prefix_original_start"] = round(old_start, 3)
        word["stage_vocal_silent_prefix_original_end"] = round(old_end, 3)
        word["start"] = round(start, 3)
        word["end"] = round(end, 3)
        word["stage_vocal_silent_prefix_shift_ms"] = round((start - old_start) * 1000, 1)
    line.timestamp = round(float(line.words[0]["start"]), 3)
    return True


def _recover_silently_placed_line(
        lines: list, line_index: int, line, regions: list[tuple[float, float]], *,
        previous_end: float,
        onset_search: float,
        minimum_onset_conflict: float,
        maximum_shift: float = 2.20,
        minimum_shift: float = 0.06,
        minimum_island: float = 0.18,
        minimum_preceding_gap: float = 0.35,
        lane_separation: float = 0.04,
        vocal_audio: np.ndarray | None = None,
        pending_voice_lanes: dict[int, int] | None = None) -> dict | None:
    """Translate a line whose leading words lie entirely in measured silence.

    The ordinary onset gate asks whether the *first word* reaches into the next
    activity island.  That test loses sensitivity exactly where the error is
    largest: once a line is displaced by more than its first word's duration,
    the word no longer touches the island and the conflict becomes invisible.

    This detector measures the line instead.  A complete leading run of words
    ending before the next island cannot be sung material, so the whole line is
    early rather than merely its onset.  The correction is therefore a rigid
    translation of every word edge - unlike the coherent-IPA pickup above, the
    measured release is displaced together with the onset and must move too.

    Every rejection is returned with its reason so the report can show why an
    obviously early line was left untouched.
    """
    first = line.words[0]
    onset = float(first["start"])
    if any(region[0] - 0.03 <= onset <= region[1] for region in regions):
        return None
    next_region = next((region for region in regions if region[0] > onset), None)
    if next_region is None:
        return None
    gap = next_region[0] - onset
    if gap < minimum_onset_conflict:
        return None
    silent_words = _silent_leading_words(line.words, next_region[0])
    if silent_words < 1:
        return None

    target = float(next_region[0])
    shift = target - onset
    last_start = float(line.words[-1]["start"])
    last_end = float(line.words[-1]["end"])
    lane = _lane_of(lines, line_index, pending_voice_lanes)
    next_lane_start = _next_line_start_in_lane(
        lines, line_index, lane, pending_voice_lanes)
    detail = {
        "line": line_index + 1,
        "word": first.get("word", ""),
        "aligned": round(onset, 3),
        "vocal_candidate": round(target, 3),
        "silent_leading_words": silent_words,
        "shift_ms": round(shift * 1000, 1),
        "lane": lane,
    }

    def rejected(reason: str) -> dict:
        return {**detail, "status": "rejected", "reason": reason}

    if gap > onset_search:
        return rejected("island-beyond-onset-search")
    if next_region[1] - next_region[0] < minimum_island:
        return rejected("target-island-too-short")
    if not minimum_shift <= abs(shift) <= maximum_shift:
        return rejected("shift-out-of-bounds")
    if target - previous_end < minimum_preceding_gap:
        return rejected("no-preceding-quiet-gap")
    if next_lane_start is not None and target >= next_lane_start - lane_separation:
        return rejected("collides-with-next-line-in-lane")
    if (next_lane_start is not None
            and last_end + shift > next_lane_start - lane_separation):
        if not _reflow_after_silent_prefix(
                line, target,
                proposed_release=min(last_end, next_region[1]),
                vocal_audio=vocal_audio):
            return rejected("shifted-line-overruns-next-line-in-lane")
        return {
            **detail,
            "status": "corrected",
            "correction": "release-preserving-reflow",
            "retained_release": round(last_end, 3),
            "source": "stage-vocal-silent-leading-run-v1",
        }
    if vocal_audio is not None:
        if not _reflow_after_silent_prefix(line, target, vocal_audio=vocal_audio):
            return rejected("release-preserving-reflow-failed")
        return {
            **detail,
            "status": "corrected",
            "correction": "release-preserving-reflow",
            "retained_release": round(last_end, 3),
            "source": "stage-vocal-silent-leading-run-v1",
        }
    if not any(_overlap(last_start + shift, last_end + shift, region) > 0
               for region in regions):
        return rejected("shifted-release-outside-activity")

    for word in line.words:
        word["stage_vocal_silent_prefix_original_start"] = round(
            float(word["start"]), 3)
        word["stage_vocal_silent_prefix_original_end"] = round(
            float(word["end"]), 3)
        word["start"] = round(float(word["start"]) + shift, 3)
        word["end"] = round(float(word["end"]) + shift, 3)
        word["stage_vocal_silent_prefix_shift_ms"] = round(shift * 1000, 1)
    line.timestamp = target
    return {
        **detail,
        "status": "corrected",
        "preceding_quiet_gap_ms": round((target - previous_end) * 1000, 1),
        "shifted_release": round(last_end + shift, 3),
        "source": "stage-vocal-silent-leading-run-v1",
    }


def constrain_to_stage_vocals(
        lines: list,
        activity: list[tuple[float, float]], *,
        vocal_audio: np.ndarray | None = None,
        release_tolerance: float = 0.12,
        release_padding: float = 0.04,
        onset_search: float = 1.5,
        dying_onset_window: float = 0.08,
        minimum_onset_conflict: float = 0.18,
        silent_prefix_recovery: bool = False,
        pending_voice_lanes: dict[int, int] | None = None) -> dict:
    """Reject impossible lyric edges using the exported Stage vocal stem.

    Recognition can use a blend with the original mix to recover backing
    vocals.  Such a blend is deliberately unsuitable for display boundaries:
    tonal instruments can look like a held vowel.  This final gate therefore
    uses only the exact vocal stem later played by the Stage and shown as the
    editor waveform.

    End corrections are safe and local: the activity island overlapping the
    last word owns its release and the algorithm never jumps to a later island.
    Suspicious starts are normally reported to the quality gate.  The one
    exception is a low-confidence Stable-TS lead token recovered from another
    separator: once the final Stage stem contains a later, exact vocal onset,
    that onset is a stronger boundary than Whisper's hallucinated lead-in.

    ``silent_prefix_recovery`` additionally recovers lines whose leading words
    lie completely in measured silence.  It is opt-in because it moves complete
    lines: the research shadow enables it, while variant 1.2 and every editor
    aware path keep the historical behaviour unchanged.  ``pending_voice_lanes``
    supplies lane decisions which are applied only after this gate, so an
    already recognised backing phrase does not look like a blocking lead line.
    """
    regions = sorted((float(begin), float(end)) for begin, end in activity
                     if end > begin)
    release_corrections: list[dict] = []
    release_overrides: list[dict] = []
    onset_conflicts: list[dict] = []
    onset_corrections: list[dict] = []
    phrase_onset_corrections: list[dict] = []
    silent_prefix_corrections: list[dict] = []
    silent_prefix_rejections: list[dict] = []

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
                # Binary vocal activity is intentionally conservative and can
                # end before the decay of a quiet held vowel.  A release that
                # was measured directly from the tonal tail is the more exact
                # local boundary when it agrees with the current word end.
                measured_release = last.get("sustain_tonal_release")
                release_confidence = last.get("sustain_release_confidence")
                verified_sung_release = (
                    measured_release is not None
                    and release_confidence is not None
                    and float(release_confidence) >= 0.80
                    and abs(float(measured_release) - word_end) <= 0.08
                    and 0.08 <= float(measured_release) - owner_end <= 0.50
                )
                if verified_sung_release:
                    release_overrides.append({
                        "line": line_index + 1,
                        "word": last.get("word", ""),
                        "retained": round(word_end, 3),
                        "vocal_activity_end": round(owner_end, 3),
                        "measured_tonal_release": round(
                            float(measured_release), 3),
                        "confidence": round(float(release_confidence), 3),
                    })
                    continue
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

        # A complete IPA sentence is linguistically constrained, but CTC can
        # start at the first strong vowel instead of the quiet consonant that
        # follows a real singing pause.  The exact Stage-vocal activity has a
        # complementary strength here: after an independently observed quiet
        # gap, its next island marks the phrase pickup.  Translate all internal
        # boundaries together and retain the already measured final release.
        # Restricting this to complete coherent IPA paths prevents a separator
        # artefact from moving heuristic or manually anchored lyrics.
        previous_end = 0.0
        for previous in reversed(lines[:line_index]):
            if previous.words:
                previous_end = float(previous.words[-1]["end"])
                break
        containing_region = next((region for region in regions
                                  if region[0] <= onset <= region[1]), None)
        next_region = next((region for region in regions
                            if region[0] > onset), None)
        phrase_region = containing_region or next_region
        if (first.get("timing_source") == "coherent-sentence-ipa-path"
                and phrase_region is not None
                and phrase_region[0] - previous_end >= 0.35
                and phrase_region[1] - phrase_region[0] >= 0.18):
            target = float(phrase_region[0])
            shift = target - onset
            next_line_start = next((float(following.words[0]["start"])
                                    for following in lines[line_index + 1:]
                                    if following.words), None)
            shifted_last_start = float(line.words[-1]["start"]) + shift
            if (0.06 <= abs(shift) <= 2.20
                    and target >= previous_end + 0.35
                    and (next_line_start is None
                         or target < next_line_start - 0.04)
                    and shifted_last_start < float(line.words[-1]["end"]) - 0.04):
                old_onset = onset
                for word_index, word in enumerate(line.words):
                    word["stage_vocal_phrase_original_start"] = round(
                        float(word["start"]), 3)
                    word["start"] = round(float(word["start"]) + shift, 3)
                    if word_index + 1 < len(line.words):
                        word["stage_vocal_phrase_original_end"] = round(
                            float(word["end"]), 3)
                        word["end"] = round(float(word["end"]) + shift, 3)
                    word["stage_vocal_phrase_shift_ms"] = round(shift * 1000, 1)
                line.timestamp = target
                first = line.words[0]
                onset = target
                phrase_onset_corrections.append({
                    "line": line_index + 1,
                    "word": first.get("word", ""),
                    "from": round(old_onset, 3),
                    "to": round(target, 3),
                    "shift_ms": round(shift * 1000, 1),
                    "retained_final_release": round(
                        float(line.words[-1]["end"]), 3),
                    "preceding_quiet_gap_ms": round(
                        (target - previous_end) * 1000, 1),
                    "source": "coherent-ipa-plus-stage-vocal-pickup-v1",
                })

        # Runs before the onset detector below so a recovered line is measured
        # in its corrected position and no longer produces a stale conflict.
        if silent_prefix_recovery:
            recovery = _recover_silently_placed_line(
                lines, line_index, line, regions,
                previous_end=previous_end,
                onset_search=onset_search,
                minimum_onset_conflict=minimum_onset_conflict,
                vocal_audio=vocal_audio,
                pending_voice_lanes=pending_voice_lanes)
            if recovery is not None:
                if recovery["status"] == "corrected":
                    silent_prefix_corrections.append(recovery)
                    first = line.words[0]
                    onset = float(first["start"])
                else:
                    silent_prefix_rejections.append(recovery)

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
            detail = {
                "line": line_index + 1,
                "word": first.get("word", ""),
                "aligned": round(onset, 3),
                "vocal_candidate": round(next_region[0], 3),
                "delta_ms": delta_ms,
                "reason": "dying-previous-island" if dying_previous
                          else "outside-stage-vocal-activity",
            }
            # Complementary-stem recovery is deliberately allowed to use the
            # original mix for text recognition.  Whisper can then attach the
            # first token to musical energy before the vocal which was later
            # restored into the exported hybrid stem.  Do not shift a complete
            # phrase: trim only that demonstrably weak first token to the first
            # activity island of the exact Stage stem.
            probability = first.get("stable_ts_probability")
            recovered_lead = (
                first.get("timing_source") == "instrumental-leakage-stable-ts"
                and probability is not None
                and float(probability) < 0.15
                and next_region[0] < first_end - 0.019
            )
            interpolated_prefix = []
            for word in line.words:
                if word.get("timing_source") != "stable-ts-source-anchor-interpolation":
                    break
                interpolated_prefix.append(word)
            prefix_end = (float(line.words[len(interpolated_prefix)]["start"])
                          if interpolated_prefix
                          and len(interpolated_prefix) < len(line.words) else first_end)
            recovered_interpolated_pickup = (
                len(interpolated_prefix) >= 1
                and next_region[0] < prefix_end - 0.039
            )
            if recovered_interpolated_pickup:
                cursor = float(next_region[0])
                weights = [max(1, len(str(word.get("word", ""))))
                           for word in interpolated_prefix]
                span = prefix_end - cursor
                total = sum(weights)
                for prefix_word, weight in zip(interpolated_prefix, weights):
                    prefix_word["start"] = round(cursor, 3)
                    cursor += span * weight / total
                    prefix_word["end"] = round(cursor, 3)
                    prefix_word["stage_vocal_pickup_reflow"] = True
                line.timestamp = float(interpolated_prefix[0]["start"])
                detail["status"] = "corrected-source-anchor-pickup"
                detail["prefix_words"] = len(interpolated_prefix)
                onset_corrections.append(detail)
                first.pop("stage_vocal_onset_conflict_ms", None)
            elif recovered_lead:
                old = onset
                first["start"] = round(next_region[0], 3)
                first["stage_vocal_onset_trim_ms"] = delta_ms
                line.timestamp = float(first["start"])
                detail["status"] = "corrected-low-confidence-recovered-lead"
                onset_corrections.append(detail)
                first.pop("stage_vocal_onset_conflict_ms", None)
            else:
                detail["status"] = "reported"
                onset_conflicts.append(detail)

    return {
        "method": "exact-stage-vocal-boundary-gate-v2",
        "release_corrections": len(release_corrections),
        "release_overrides": len(release_overrides),
        "onset_conflicts": len(onset_conflicts),
        "onset_corrections": len(onset_corrections)
                             + len(phrase_onset_corrections)
                             + len(silent_prefix_corrections),
        "phrase_onset_corrections": len(phrase_onset_corrections),
        "release_details": release_corrections,
        "release_override_details": release_overrides,
        "onset_details": onset_conflicts,
        "onset_correction_details": [
            *onset_corrections, *phrase_onset_corrections,
            *silent_prefix_corrections],
        "phrase_onset_correction_details": phrase_onset_corrections,
        "silent_prefix_recovery": {
            "enabled": silent_prefix_recovery,
            "method": "stage-vocal-silent-leading-run-v1",
            "corrected_lines": len(silent_prefix_corrections),
            "rejected_lines": len(silent_prefix_rejections),
            "corrections": silent_prefix_corrections,
            "rejections": silent_prefix_rejections,
        },
    }
