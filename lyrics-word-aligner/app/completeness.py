from __future__ import annotations

import re


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


def is_nonlexical_vocalization(text: str) -> bool:
    """Recognize ad-libs which are audible vocals but not missing lyric prose."""
    tokens = re.findall(r"[^\W_]+", str(text).casefold(), flags=re.UNICODE)
    if not tokens:
        return False
    normalized = [re.sub(r"(.)\1+", r"\1", token) for token in tokens]
    vocalizations = {
        "a", "ah", "eh", "ha", "hey", "hm", "m", "na", "la", "oh", "o",
        "uh", "um", "whoa", "woah", "woo", "wow", "yeah", "yea", "yo",
    }
    # Written karaoke lyrics often spell a melodic call as ``Wohohohohoh`` or
    # ``lalala``.  Consecutive-character collapsing cannot recognize those
    # alternating syllables.  Accept only repeated, closed interjection
    # syllables; ordinary lexical words (including held-vowel words such as
    # ``away``) must never become structural separators.
    repeated_call = re.compile(
        r"(?:(?:wo|ho|oh|ha|ah|uh|la|na|yo|ye|yeah)){2,}h?\Z")
    return all(
        token in vocalizations
        or (len(token) >= 4 and repeated_call.fullmatch(token) is not None)
        for token in normalized
    )


# Kept as a private compatibility alias for older callers and reports.
_is_nonlexical_vocalization = is_nonlexical_vocalization


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
    vocalizations = []
    semantic_unresolved = []
    for item in unresolved:
        transcript = str(item.get("targeted_transcript") or item.get("text_preview") or "")
        if is_nonlexical_vocalization(transcript):
            vocalizations.append({**item, "reason": "non-lexical-vocalization"})
        else:
            semantic_unresolved.append(item)
    completeness["vocalization_regions"] = vocalizations
    vocalization_keys = {(item.get("start"), item.get("end")) for item in vocalizations}
    if vocalization_keys:
        completeness["suspicious_gaps"] = [
            item for item in completeness.get("suspicious_gaps", [])
            if (item.get("start"), item.get("end")) not in vocalization_keys
        ]
    known = {(item.get("start"), item.get("end"))
             for item in completeness.get("suspicious_gaps", [])}
    for item in semantic_unresolved:
        key = (item.get("start"), item.get("end"))
        if key not in known:
            completeness.setdefault("suspicious_gaps", []).append(item)
            known.add(key)
    completeness["complete"] = not completeness.get("suspicious_gaps", [])


def constrain_lyrics_before_nonlexical_vocalizations(
        lines: list, reanalysis: dict, *, release_padding: float = 0.065) -> dict:
    """Keep a following ad-lib out of the preceding lyric word.

    A tonal sustain detector cannot decide whether a long /ah/ is the release
    of the written word or a separate, deliberately uncaptioned vocalization.
    Targeted ASR already makes that semantic distinction.  When it identifies
    a non-lexical region beginning inside an acoustically extended final word,
    end the lyric at the region boundary (with a tiny display release) instead
    of highlighting the word through the complete ad-lib.
    """
    regions = []
    for item in reanalysis.get("unresolved_regions", []):
        transcript = str(item.get("targeted_transcript") or "")
        if is_nonlexical_vocalization(transcript):
            regions.append((float(item["start"]), float(item["end"]), transcript))
    adjustments = []
    for line_index, line in enumerate(lines):
        if not line.words:
            continue
        word = line.words[-1]
        start, end = float(word["start"]), float(word["end"])
        acoustic_end = float(word.get("acoustic_end", end))
        for region_start, region_end, transcript in regions:
            if (not start < region_start < end - 0.20
                    or acoustic_end > region_start + 0.12):
                continue
            target = min(end, region_start + release_padding)
            if target <= start + 0.04:
                continue
            word["end"] = round(target, 3)
            word["nonlexical_vocalization_trim_ms"] = round((end - target) * 1000)
            word["nonlexical_vocalization_start"] = round(region_start, 3)
            adjustments.append({
                "line": line_index + 1,
                "word": word.get("word", ""),
                "from": round(end, 3),
                "to": round(target, 3),
                "vocalization_start": round(region_start, 3),
                "vocalization_end": round(region_end, 3),
                "transcript": transcript,
            })
            break
    return {
        "method": "targeted-asr-nonlexical-boundary-v1",
        "vocalization_regions": len(regions),
        "adjusted_words": len(adjustments),
        "release_padding_ms": round(release_padding * 1000),
        "adjustments": adjustments,
    }


def apply_completeness_gate(summary: dict, completeness: dict) -> None:
    if completeness.get("complete", True):
        return
    quality = summary["quality"]
    quality["publishable"] = False
    quality["grade"] = "review" if quality.get("score", 0) >= 65 else "reject"
    quality["score"] = min(float(quality.get("score", 0)), 79.9)
    quality["lyrics_complete"] = False
    quality["missing_lyric_regions"] = len(completeness.get("suspicious_gaps", []))
