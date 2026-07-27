from __future__ import annotations

from .transcript_match import normalize_words


def local_repetition_requests(lines: list, comparison: dict, total_duration: float,
                              *, pre_roll: float = 2.0, post_roll: float = 4.0) -> list[dict]:
    """Build bounded audio searches from original LRC positions for every paired block."""
    word_lines: list[int] = []
    for line_index, line in enumerate(lines):
        word_lines.extend([line_index] * len(normalize_words(line.text)))
    requests: list[dict] = []
    for pair in comparison.get("repeated_blocks", {}).get("paired", []):
        expected = pair["expected"]
        start_index, end_index = int(expected["start"]), int(expected["end"])
        if start_index >= len(word_lines) or end_index <= start_index or end_index > len(word_lines):
            continue
        first_line = word_lines[start_index]
        last_line = word_lines[end_index - 1]
        start = max(0.0, float(lines[first_line].timestamp) - pre_roll)
        if last_line + 1 < len(lines):
            end = min(total_duration, float(lines[last_line + 1].timestamp) + post_roll)
        else:
            end = min(total_duration, float(lines[last_line].timestamp) + 15.0)
        recognized = pair["recognized"]
        transcript = " ".join([recognized["unit"]] * int(recognized["repetitions"]))
        pair["expected_lrc_start"] = round(float(lines[first_line].timestamp), 3)
        if last_line + 1 < len(lines):
            pair["expected_lrc_end"] = round(float(lines[last_line + 1].timestamp), 3)
        words_through_line = sum(len(normalize_words(line.text)) for line in lines[:last_line + 1])
        pair["trailing_words_in_line"] = max(0, words_through_line - end_index)
        requests.append({"pair": pair, "start": start, "end": end, "transcript": transcript})
    return requests


def apply_local_timestamp(pair: dict, words: list[dict], base: float) -> bool:
    if not words:
        return False
    start = float(words[0]["start"]) + base
    end = float(words[-1]["end"]) + base
    lrc_end = pair.get("expected_lrc_end")
    if lrc_end is not None:
        reserved = max(0.25, int(pair.get("trailing_words_in_line", 0)) * 0.25)
        end = min(end, float(lrc_end) - reserved)
    if end <= start:
        return False
    absolute_words = [{
        "start": round(float(word["start"]) + base, 3),
        "end": round(min(float(word["end"]) + base, end), 3),
    } for word in words]
    if (any(word["end"] <= word["start"] for word in absolute_words)
            or any(right["start"] < left["start"]
                   for left, right in zip(absolute_words, absolute_words[1:]))):
        absolute_words = []
    pair["audio_start"] = round(start, 3)
    pair["audio_end"] = round(end, 3)
    pair["audio_duration"] = round(end - start, 3)
    if absolute_words:
        pair["audio_words"] = absolute_words
    pair["timestamp_method"] = "local-lrc-bounded-asr-alignment"
    return True


def transition_requests(lines: list, timed_pairs: list[dict], total_duration: float,
                        *, following_lines: int = 3) -> list[dict]:
    """Plan bounded anchor+context realignments around repeated lyric blocks."""
    word_lines: list[int] = []
    for line_index, line in enumerate(lines):
        word_lines.extend([line_index] * len(normalize_words(line.text)))
    requests: list[dict] = []
    for pair in timed_pairs:
        expected = pair["expected"]
        first_word, end_word = int(expected["start"]), int(expected["end"])
        if first_word >= len(word_lines) or end_word <= first_word or end_word > len(word_lines):
            continue
        first_line = word_lines[first_word]
        block_last_line = word_lines[end_word - 1]
        end_line = min(len(lines), block_last_line + 1 + following_lines)
        start = max(0.0, float(pair["audio_start"]) - 1.0)
        if end_line < len(lines) and lines[end_line].source_timestamp is not None:
            desired_end = float(lines[end_line].source_timestamp) + 2.0
        else:
            desired_end = float(pair["audio_end"]) + 18.0
        end = min(total_duration, start + 35.0, max(float(pair["audio_end"]) + 8.0, desired_end))
        requests.append({
            "first_line": first_line,
            "end_line": end_line,
            "start": start,
            "end": end,
            "transcript": "\n".join(line.text for line in lines[first_line:end_line]),
        })
    return requests


def apply_transition_words(lines: list, request: dict, aligned_words: list[dict]) -> int:
    """Apply a contextual alignment while preserving fixed ASR anchor words."""
    selected = lines[request["first_line"]:request["end_line"]]
    counts = [len(line.words) for line in selected]
    if sum(counts) != len(aligned_words):
        return 0
    changed = cursor = 0
    for line, count in zip(selected, counts):
        replacements = aligned_words[cursor:cursor + count]
        cursor += count
        proposal: list[tuple[float, float, bool]] = []
        valid = True
        previous_end: float | None = None
        for word, replacement in zip(line.words, replacements):
            if word.get("timing_source") == "asr-repetition-anchor":
                start, end, replace = float(word["start"]), float(word["end"]), False
            else:
                start, end, replace = float(replacement["start"]), float(replacement["end"]), True
            if end - start < 0.03 or (previous_end is not None and start + 0.015 < previous_end):
                valid = False
                break
            proposal.append((start, end, replace))
            previous_end = end
        # A partly applied line mixes two unrelated time axes. Keep the original
        # line unless the complete contextual proposal is chronologically sound.
        if not valid or len(proposal) != len(line.words):
            continue
        for word, (start, end, replace) in zip(line.words, proposal):
            if not replace:
                continue
            word["start"] = round(start, 3)
            word["end"] = round(end, 3)
            word["timing_source"] = "transition-block-qwen"
            changed += 1
        if line.words:
            line.timestamp = float(line.words[0]["start"])
    return changed


def timestamp_repetition_pairs(comparison: dict, recognized_words: list[dict]) -> list[dict]:
    """Add acoustic bounds to structurally paired ASR repetition blocks."""
    normalized: list[tuple[int, dict]] = []
    for token_index, token in enumerate(recognized_words):
        for _ in normalize_words(str(token["word"])):
            normalized.append((token_index, token))
    timed: list[dict] = []
    for pair in comparison.get("repeated_blocks", {}).get("paired", []):
        block = pair["recognized"]
        start_index, end_index = int(block["start"]), int(block["end"])
        if start_index >= len(normalized) or end_index <= start_index or end_index > len(normalized):
            continue
        first = normalized[start_index][1]
        last = normalized[end_index - 1][1]
        start, end = float(first["start"]), float(last["end"])
        if end <= start:
            continue
        pair["audio_start"] = round(start, 3)
        pair["audio_end"] = round(end, 3)
        pair["audio_duration"] = round(end - start, 3)
        timed.append(pair)
    return timed


def apply_repetition_anchors(lines: list, timed_pairs: list[dict], *, max_start_deviation: float = 3.0) -> dict:
    """Replace collapsed repeated lyric words with ASR-confirmed acoustic windows."""
    flattened: list[dict] = []
    for line in lines:
        for word in line.words:
            for _ in normalize_words(str(word["word"])):
                flattened.append(word)
    anchored_words = anchored_blocks = rejected_blocks = 0
    for pair in timed_pairs:
        expected = pair["expected"]
        start_index, end_index = int(expected["start"]), int(expected["end"])
        if end_index > len(flattened) or end_index <= start_index:
            continue
        words = flattened[start_index:end_index]
        # Avoid assigning the same token twice if punctuation normalization ever
        # expands a forced-aligner token.
        unique_words = list(dict.fromkeys(id(word) for word in words))
        by_id = {id(word): word for word in words}
        words = [by_id[word_id] for word_id in unique_words]
        if not words:
            continue
        start, end = float(pair["audio_start"]), float(pair["audio_end"])
        reference_start = float(pair.get("expected_lrc_start", words[0].get("start", start)))
        if abs(start - reference_start) > max_start_deviation:
            pair["anchor_status"] = "rejected"
            pair["anchor_reason"] = "ASR-Zeitanker zu weit vom lokalen LRC/Alignment-Fenster entfernt"
            rejected_blocks += 1
            continue
        measured_words = pair.get("audio_words", [])
        original = [
            (word, word.get("start"), word.get("end"), word.get("timing_source"))
            for word in words
        ]
        if len(measured_words) == len(words):
            for word, measured in zip(words, measured_words):
                word["start"] = round(float(measured["start"]), 3)
                word["end"] = round(float(measured["end"]), 3)
                word["timing_source"] = "asr-repetition-anchor"
        else:
            weights = [max(1, len(normalize_words(str(word["word"]))[0])) for word in words]
            total = sum(weights)
            cursor = start
            for index, (word, weight) in enumerate(zip(words, weights)):
                word_end = end if index == len(words) - 1 else cursor + (end - start) * weight / total
                word["start"] = round(cursor, 3)
                word["end"] = round(word_end, 3)
                word["timing_source"] = "asr-repetition-anchor"
                cursor = word_end
        # Structural repetition matching can legitimately pair the correct
        # words with the wrong occurrence. Never keep an anchor when it makes
        # the emitted word stream run backwards; fall back to the previous
        # acoustic candidate instead.
        affected_lines = {id(line): line for line in lines if any(id(word) in unique_words for word in line.words)}
        monotonic = True
        for line in affected_lines.values():
            starts = [float(word["start"]) for word in line.words]
            ends = [float(word["end"]) for word in line.words]
            if (any(right + 0.001 < left for left, right in zip(starts, starts[1:]))
                    or any(end <= start for start, end in zip(starts, ends))):
                monotonic = False
                break
        if not monotonic:
            for word, old_start, old_end, old_source in original:
                word["start"] = old_start
                word["end"] = old_end
                if old_source is None:
                    word.pop("timing_source", None)
                else:
                    word["timing_source"] = old_source
            pair["anchor_status"] = "rejected"
            pair["anchor_reason"] = "ASR-Zeitanker erzeugt keine monotone Wortfolge"
            rejected_blocks += 1
            continue
        anchored_words += len(words)
        anchored_blocks += 1
        pair["anchor_status"] = "applied"
        pair.pop("anchor_reason", None)
    if anchored_blocks:
        for line in lines:
            if line.words:
                line.timestamp = float(line.words[0]["start"])
    return {"blocks": anchored_blocks, "words": anchored_words,
            "rejected_blocks": rejected_blocks,
            "method": "asr-structural-repetition-v1"}


def refine_stretched_repetition_anchors(lines: list, activity: list[tuple[float, float]],
                                        *, max_word_duration: float = 6.0,
                                        cluster_gap: float = 1.0) -> dict:
    """Map stretched repeated calls onto distinct measured vocal islands."""
    flattened = [(line, index, word) for line in lines for index, word in enumerate(line.words)]
    groups: list[list[tuple]] = []
    current: list[tuple] = []
    for item in flattened:
        word = item[2]
        if word.get("timing_source") != "asr-repetition-anchor":
            if current:
                groups.append(current)
                current = []
            continue
        if current and float(word["start"]) - float(current[-1][2]["end"]) > 0.15:
            groups.append(current)
            current = []
        current.append(item)
    if current:
        groups.append(current)

    refined_words = refined_blocks = 0
    diagnostics = []
    for group in groups:
        if not any(float(item[2]["end"]) - float(item[2]["start"]) > max_word_duration
                   for item in group):
            continue
        first_line, first_index, first_word = group[0]
        previous_end = (float(first_line.words[first_index - 1]["end"])
                        if first_index > 0 else float(first_word["start"]) - 1.0)
        window_start = max(previous_end, float(first_word["start"]) - 1.0)
        window_end = float(group[-1][2]["end"]) + 5.0
        local = [(max(start, window_start), min(end, window_end)) for start, end in activity
                 if end > window_start and start < window_end]
        local = [(start, end) for start, end in local if end - start >= 0.08]
        clusters: list[list[tuple[float, float]]] = []
        for interval in local:
            if not clusters or interval[0] - clusters[-1][-1][1] > cluster_gap:
                clusters.append([interval])
            else:
                clusters[-1].append(interval)
        islands = [(cluster[0][0], cluster[-1][1]) for cluster in clusters]
        if len(islands) != len(group):
            diagnostics.append({"status": "island-count-mismatch", "words": len(group),
                                "islands": len(islands)})
            continue
        for (_line, _index, word), (start, end) in zip(group, islands):
            word["start"] = round(start, 3)
            word["end"] = round(end, 3)
            word["timing_source"] = "asr-repetition-activity"
        for line, _index, _word in group:
            line.timestamp = float(line.words[0]["start"])
        refined_blocks += 1
        refined_words += len(group)
        diagnostics.append({"status": "accepted", "words": len(group),
                            "islands": [[round(a, 3), round(b, 3)] for a, b in islands]})
    return {"method": "repetition-vocal-islands-v1", "refined_blocks": refined_blocks,
            "refined_words": refined_words, "diagnostics": diagnostics}
