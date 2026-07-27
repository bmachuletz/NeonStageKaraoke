from __future__ import annotations


def plan_sections(lines: list, total_duration: float, *, pause_gap: float = 7.0,
                  max_duration: float = 45.0, pre_roll: float = 0.75,
                  post_roll: float = 0.5, last_line_duration: float = 12.0) -> list[dict]:
    """Group consecutive lyric lines into bounded local singing sections."""
    if not lines:
        return []
    ranges: list[tuple[int, int]] = []
    start = 0
    for index in range(1, len(lines)):
        gap = float(lines[index].timestamp) - float(lines[index - 1].timestamp)
        span = float(lines[index].timestamp) - float(lines[start].timestamp)
        if gap >= pause_gap or span >= max_duration:
            ranges.append((start, index))
            start = index
    ranges.append((start, len(lines)))

    sections: list[dict] = []
    for range_index, (first, end) in enumerate(ranges):
        audio_start = max(0.0, float(lines[first].timestamp) - pre_roll)
        if range_index + 1 < len(ranges):
            next_first = ranges[range_index + 1][0]
            gap = float(lines[next_first].timestamp) - float(lines[end - 1].timestamp)
            if gap >= pause_gap:
                audio_end = float(lines[next_first].timestamp) - min(0.75, gap / 3)
            else:
                audio_end = float(lines[next_first].timestamp) + post_roll
        else:
            audio_end = float(lines[end - 1].timestamp) + last_line_duration
        audio_end = min(total_duration, max(audio_start + 0.25, audio_end))
        sections.append({"first": first, "end": end, "audio_start": audio_start,
                         "audio_end": audio_end})
    return sections
