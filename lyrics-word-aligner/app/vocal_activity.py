from __future__ import annotations

import numpy as np

from .models import AlignmentConfig


def detect_vocal_activity(audio: np.ndarray, sample_rate: int = 16000, *, frame_ms: int = 30,
                          hop_ms: int = 10, min_active_ms: int = 80,
                          bridge_gap_ms: int = 140) -> list[tuple[float, float]]:
    """Detect adaptive energy regions on an already isolated vocal stem."""
    frame = max(1, sample_rate * frame_ms // 1000)
    hop = max(1, sample_rate * hop_ms // 1000)
    if len(audio) < frame:
        return []
    starts = np.arange(0, len(audio) - frame + 1, hop)
    rms = np.sqrt(np.array([np.mean(audio[start:start + frame] ** 2) for start in starts]) + 1e-12)
    noise = float(np.percentile(rms, 30))
    strong = float(np.percentile(rms, 90))
    threshold = max(noise * 2.5, strong * 0.08, 1e-4)
    active = rms >= threshold
    bridge_frames = max(0, bridge_gap_ms // hop_ms)
    if bridge_frames:
        inactive = np.flatnonzero(~active)
        # Fill only bounded short holes; leading/trailing silence stays silent.
        for index in inactive:
            left = max(0, index - bridge_frames)
            right = min(len(active), index + bridge_frames + 1)
            if active[left:index].any() and active[index + 1:right].any():
                active[index] = True
    intervals: list[tuple[float, float]] = []
    index = 0
    min_frames = max(1, min_active_ms // hop_ms)
    while index < len(active):
        if not active[index]:
            index += 1
            continue
        end = index + 1
        while end < len(active) and active[end]:
            end += 1
        if end - index >= min_frames:
            intervals.append((starts[index] / sample_rate,
                              min(len(audio) / sample_rate, (starts[end - 1] + frame) / sample_rate)))
        index = end
    return intervals


def _active_time_to_song_time(intervals: list[tuple[float, float]], position: float) -> float:
    remaining = max(0.0, position)
    for start, end in intervals:
        duration = end - start
        if remaining <= duration:
            return start + remaining
        remaining -= duration
    return intervals[-1][1]


def _place_words(words: list[dict], intervals: list[tuple[float, float]], source: str) -> bool:
    active_duration = sum(end - start for start, end in intervals)
    if not intervals or active_duration < len(words) * 0.045:
        return False
    weights = [max(1, len(str(word["word"]))) for word in words]
    total_weight = sum(weights)
    cursor = 0.0
    for word, weight in zip(words, weights):
        word_start = _active_time_to_song_time(intervals, cursor)
        cursor += active_duration * weight / total_weight
        word_end = _active_time_to_song_time(intervals, cursor)
        word["start"] = round(word_start, 3)
        word["end"] = round(max(word_start + 0.03, word_end), 3)
        word["timing_source"] = source
    return True


def _select_activity_cluster(intervals: list[tuple[float, float]], source_start: float,
                             minimum_duration: float, *, max_gap: float = 0.75
                             ) -> list[tuple[float, float]]:
    """Choose one nearby phrase instead of distributing words across an instrumental gap."""
    if not intervals:
        return []
    clusters: list[list[tuple[float, float]]] = [[intervals[0]]]
    for interval in intervals[1:]:
        if interval[0] - clusters[-1][-1][1] > max_gap:
            clusters.append([interval])
        else:
            clusters[-1].append(interval)
    viable = [cluster for cluster in clusters
              if sum(end - start for start, end in cluster) >= minimum_duration]
    if not viable:
        return []
    return min(viable, key=lambda cluster: (abs(cluster[0][0] - source_start), cluster[0][0]))


def repair_anchor_context(lines: list, cfg: AlignmentConfig,
                          activity: list[tuple[float, float]], *, tail_seconds: float = 5.0) -> int:
    """Repair only unanchored runs surrounding fixed ASR repetition words."""
    repaired_runs = 0
    for line_index, line in enumerate(lines):
        anchored = [index for index, word in enumerate(line.words)
                    if word.get("timing_source") == "asr-repetition-anchor"]
        if not anchored or len(anchored) == len(line.words):
            continue
        runs: list[tuple[int, int]] = []
        cursor = 0
        while cursor < len(line.words):
            if cursor in anchored:
                cursor += 1
                continue
            end = cursor + 1
            while end < len(line.words) and end not in anchored:
                end += 1
            runs.append((cursor, end))
            cursor = end
        for first, end in runs:
            left = (float(line.words[first - 1]["end"]) if first > 0 else
                    float(line.source_timestamp if line.source_timestamp is not None else line.timestamp) - cfg.pre_roll)
            if end < len(line.words):
                right = float(line.words[end]["start"])
            else:
                next_anchor = None
                for following in lines[line_index + 1:]:
                    anchor_words = [word for word in following.words
                                    if word.get("timing_source") == "asr-repetition-anchor"]
                    if anchor_words:
                        next_anchor = float(anchor_words[0]["start"])
                        break
                    if following.source_timestamp is not None and float(following.source_timestamp) > left + tail_seconds:
                        break
                right = min(left + tail_seconds, next_anchor) if next_anchor is not None else left + tail_seconds
            if right <= left:
                continue
            local = [(max(start, left), min(stop, right)) for start, stop in activity
                     if stop > left and start < right]
            local = [(start, stop) for start, stop in local if stop - start >= 0.04]
            if _place_words(line.words[first:end], local, "anchor-context-vocal-activity"):
                repaired_runs += 1
        if line.words:
            line.timestamp = float(line.words[0]["start"])
    return repaired_runs


def repair_anchor_line_tails(lines: list, activity: list[tuple[float, float]],
                             *, max_tail_seconds: float = 2.5) -> int:
    """Place trailing words from an anchor line in the immediate vocal region after it."""
    repaired = 0
    for index, line in enumerate(lines):
        anchor_indices = [word_index for word_index, word in enumerate(line.words)
                          if word.get("timing_source") == "asr-repetition-anchor"]
        if not anchor_indices:
            continue
        last_anchor = max(anchor_indices)
        tail = line.words[last_anchor + 1:]
        if not tail:
            continue
        left = float(line.words[last_anchor]["end"])
        right = left + max_tail_seconds
        if index + 1 < len(lines) and lines[index + 1].source_timestamp is not None:
            next_source = float(lines[index + 1].source_timestamp)
            if next_source > left:
                right = min(right, next_source)
        local = [(max(start, left), min(end, right)) for start, end in activity
                 if end > left and start < right]
        local = [(start, end) for start, end in local if end - start >= 0.04]
        if _place_words(tail, local, "anchor-tail-vocal-activity"):
            repaired += 1
        line.timestamp = float(line.words[0]["start"])
    return repaired


def repair_with_vocal_activity(lines: list, cfg: AlignmentConfig,
                               activity: list[tuple[float, float]]) -> int:
    """Repair collapsed words over measured vocal energy instead of wall-clock geometry."""
    repaired = 0
    previous_end = 0.0
    for index, line in enumerate(lines):
        if not line.words:
            continue
        if any(word.get("timing_source") == "asr-repetition-anchor" for word in line.words):
            previous_end = max(previous_end, float(line.words[-1]["end"]))
            continue
        durations = [float(word["end"]) - float(word["start"]) for word in line.words]
        collapsed = sum(duration < 0.03 for duration in durations)
        rewind = float(line.words[0]["start"]) + 0.08 < previous_end
        starts = [float(word["start"]) for word in line.words]
        nonmonotonic = any(right + 0.015 < left for left, right in zip(starts, starts[1:]))
        phrase_span = float(line.words[-1]["end"]) - float(line.words[0]["start"])
        compressed_phrase = bool(durations) and sum(durations) / len(durations) < 0.10
        sparse_phrase = phrase_span > max(4.0, len(line.words) * 1.6)
        if (collapsed < max(1, len(line.words) // 4) and not rewind and not nonmonotonic
                and not compressed_phrase and not sparse_phrase):
            previous_end = max(previous_end, float(line.words[-1]["end"]))
            continue
        source_start = line.source_timestamp if line.source_timestamp is not None else line.timestamp
        next_start = (lines[index + 1].source_timestamp if index + 1 < len(lines)
                      else source_start + cfg.last_line_duration)
        window_start = max(previous_end, float(source_start) - cfg.pre_roll)
        window_end = float(next_start if next_start is not None else source_start + cfg.last_line_duration) + cfg.post_roll
        if index + 1 < len(lines):
            next_anchors = [word for word in lines[index + 1].words
                            if word.get("timing_source") == "asr-repetition-anchor"]
            if next_anchors:
                window_end = min(window_end, float(next_anchors[0]["start"]))
        local = [(max(start, window_start), min(end, window_end)) for start, end in activity
                 if end > window_start and start < window_end]
        local = [(start, end) for start, end in local if end - start >= 0.04]
        local = _select_activity_cluster(local, float(source_start), len(line.words) * 0.045)
        if not _place_words(line.words, local, "vocal-activity-repair"):
            continue
        line.timestamp = float(line.words[0]["start"])
        previous_end = float(line.words[-1]["end"])
        repaired += 1
    return repaired
