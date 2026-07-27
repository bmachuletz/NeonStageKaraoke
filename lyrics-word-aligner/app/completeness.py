from __future__ import annotations


def _merge(intervals: list[tuple[float, float]], gap: float = 0.45) -> list[tuple[float, float]]:
    merged: list[list[float]] = []
    for start, end in sorted(intervals):
        if end <= start:
            continue
        if not merged or start - merged[-1][1] > gap:
            merged.append([start, end])
        else:
            merged[-1][1] = max(merged[-1][1], end)
    return [(start, end) for start, end in merged]


def _overlap_duration(intervals: list[tuple[float, float]], start: float, end: float) -> float:
    return sum(max(0.0, min(stop, end) - max(begin, start)) for begin, stop in intervals)


def _activity_clusters(intervals: list[tuple[float, float]], start: float,
                       end: float) -> list[tuple[float, float]]:
    clipped = [(max(start, begin), min(end, stop)) for begin, stop in intervals
               if stop > start and begin < end]
    return _merge(clipped, gap=0.28)


def assess_lyric_completeness(lines: list, vocal_activity: list[tuple[float, float]],
                              recognized_words: list[dict], duration: float) -> dict:
    """Find acoustically populated gaps that are not represented by any lyric line.

    Alignment quality alone cannot detect absent source text.  This check uses
    independent ASR timestamps plus vocal-stem activity and therefore catches a
    missing verse/refrain without treating an ordinary instrumental break as a
    lyric defect.
    """
    lyric_spans = _merge([
        (max(0.0, float(line.words[0]["start"]) - 0.45),
         min(duration, float(line.words[-1]["end"]) + 0.45))
        for line in lines if line.words
    ])
    boundaries = [(0.0, lyric_spans[0][0])] if lyric_spans else [(0.0, duration)]
    boundaries += [(left[1], right[0]) for left, right in zip(lyric_spans, lyric_spans[1:])]
    if lyric_spans:
        boundaries.append((lyric_spans[-1][1], duration))

    suspicious: list[dict] = []
    investigation_regions: list[dict] = []
    for start, end in boundaries:
        if end - start < 0.65:
            continue
        for first, last in _activity_clusters(vocal_activity, start, end):
            active = _overlap_duration(vocal_activity, first, last)
            span = last - first
            # Short separator clicks and faint instrumental bleed must not
            # trigger an expensive ASR pass. A sustained island in an already
            # isolated vocal stem must, even when whole-song ASR missed it.
            if span < 0.65 or active < 0.5 or active / max(span, 0.001) < 0.3:
                continue
            words = [word for word in recognized_words
                     if float(word.get("start", -1)) >= first - 0.12
                     and float(word.get("end", -1)) <= last + 0.12]
            region = {
                "start": round(first, 3),
                "end": round(last, 3),
                "recognized_words": len(words),
                "vocal_activity_seconds": round(active, 3),
                "activity_ratio": round(active / max(span, 0.001), 3),
                "text_preview": " ".join(
                    str(word.get("word", "")).strip() for word in words[:12]
                ).strip(),
                "reason": "vocal-activity-without-lyrics",
            }
            investigation_regions.append(region)
            if len(words) >= 3:
                suspicious.append(dict(region))

    return {
        "complete": not suspicious,
        "suspicious_gaps": suspicious,
        "investigation_regions": investigation_regions,
        "requires_targeted_reanalysis": bool(investigation_regions),
        "method": "vocal-energy-plus-independent-asr-gap-v2",
    }


def apply_targeted_gap_results(completeness: dict, reanalysis: dict) -> None:
    """Turn unresolved targeted checks into an explicit review gate."""
    unresolved = reanalysis.get("unresolved_regions", [])
    completeness["targeted_reanalysis"] = reanalysis
    if not unresolved:
        completeness["complete"] = not completeness.get("suspicious_gaps", [])
        return
    known = {(item.get("start"), item.get("end"))
             for item in completeness.get("suspicious_gaps", [])}
    for item in unresolved:
        key = (item.get("start"), item.get("end"))
        if key not in known:
            completeness.setdefault("suspicious_gaps", []).append(item)
            known.add(key)
    completeness["complete"] = False


def apply_completeness_gate(summary: dict, completeness: dict) -> None:
    if completeness.get("complete", True):
        return
    quality = summary["quality"]
    quality["publishable"] = False
    quality["grade"] = "review" if quality.get("score", 0) >= 65 else "reject"
    quality["score"] = min(float(quality.get("score", 0)), 79.9)
    quality["lyrics_complete"] = False
    quality["missing_lyric_regions"] = len(completeness.get("suspicious_gaps", []))
