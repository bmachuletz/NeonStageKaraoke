"""Judge a transcript by what it covers and by what it invented.

Word count and similarity alone rank a transcript that silently drops a whole
passage above one that reaches the end of the song, because the missing words
simply never enter the comparison. A passage without recognised text is worse
than a passage recognised imperfectly: no later alignment stage has anything to
anchor to there.

The opposite failure needs its own measure. Autoregressive speech models answer
a repeated phrase with a repetition loop, emitting fluent text that corresponds
to no audio at all. Such a run is recognisable without knowing the language: it
repeats itself, and its words do not occur in the canonical lyrics.

Both measures are diagnostic first. They enter candidate selection so that a
transcript is preferred for covering the singing, not merely for looking tidy.
"""
from __future__ import annotations

import re
import unicodedata

def _tokens(text: str) -> list[str]:
    normalized = unicodedata.normalize("NFKC", str(text or "")).lower()
    return [token for token in re.findall(r"[^\W_]+", normalized, re.UNICODE)]


def _word_key(word: dict) -> str:
    """One comparable token per recognised word; punctuation is irrelevant."""
    parts = _tokens(word.get("word", ""))
    return parts[0] if len(parts) == 1 else "".join(parts)


def measure_transcript_coverage(words: list[dict],
                                activity: list[tuple[float, float]], *,
                                minimum_gap: float = 1.0) -> dict:
    """How much measured singing the transcript actually accounts for."""
    regions = sorted((float(begin), float(end)) for begin, end in activity
                     if end > begin)
    summary = {"method": "transcript-vocal-activity-coverage-v1",
               "sung_seconds": 0.0, "covered_seconds": 0.0,
               "covered_share": 1.0, "gaps": []}
    if not regions:
        return {**summary, "reason": "no-vocal-activity"}
    spans = sorted((float(word["start"]), float(word["end"]))
                   for word in words
                   if word.get("start") is not None and word.get("end") is not None)
    merged: list[list[float]] = []
    for start, end in spans:
        if merged and start <= merged[-1][1] + 0.12:
            merged[-1][1] = max(merged[-1][1], end)
        else:
            merged.append([start, end])

    sung = covered = 0.0
    gaps: list[dict] = []
    for region_start, region_end in regions:
        sung += region_end - region_start
        cursor = region_start
        for span_start, span_end in merged:
            overlap_start = max(span_start, region_start)
            overlap_end = min(span_end, region_end)
            if overlap_end <= overlap_start:
                continue
            covered += overlap_end - overlap_start
            if overlap_start - cursor >= minimum_gap:
                gaps.append({"start": round(cursor, 3),
                             "end": round(overlap_start, 3),
                             "seconds": round(overlap_start - cursor, 3)})
            cursor = max(cursor, overlap_end)
        if region_end - cursor >= minimum_gap:
            gaps.append({"start": round(cursor, 3), "end": round(region_end, 3),
                         "seconds": round(region_end - cursor, 3)})
    return {
        **summary,
        "sung_seconds": round(sung, 3),
        "covered_seconds": round(covered, 3),
        "covered_share": round(covered / sung, 4) if sung else 1.0,
        "uncovered_seconds": round(sung - covered, 3),
        "gaps": gaps,
    }


def detect_hallucinated_runs(words: list[dict], canonical_text: str, *,
                             minimum_run: int = 4,
                             repetition_length: int = 3,
                             maximum_probability: float = 0.60) -> dict:
    """Find runs of words that belong to no line of the canonical lyrics.

    A single unknown word is an ordinary recognition error. A run of them is
    the signature of a decoder that kept generating after the audio stopped
    supporting it, which is exactly what repeated phrases provoke.
    """
    vocabulary = set(_tokens(canonical_text))
    summary = {"method": "transcript-hallucination-run-v1",
               "runs": [], "hallucinated_words": 0,
               "repetition_loops": 0, "words": len(words)}
    if not vocabulary or not words:
        return {**summary, "reason": "no-canonical-vocabulary"}

    keys = [_word_key(word) for word in words]
    unknown = [bool(key) and key not in vocabulary for key in keys]
    index = 0
    while index < len(words):
        if not unknown[index]:
            index += 1
            continue
        end = index
        while end < len(words) and unknown[end]:
            end += 1
        if end - index >= minimum_run:
            block = words[index:end]
            probabilities = [float(word.get("probability", 1.0))
                             for word in block
                             if word.get("probability") is not None]
            mean = sum(probabilities) / len(probabilities) if probabilities else None
            summary["runs"].append({
                "start": round(float(block[0]["start"]), 3),
                "end": round(float(block[-1]["end"]), 3),
                "words": end - index,
                "mean_probability": round(mean, 4) if mean is not None else None,
                "text": " ".join(str(word.get("word", "")) for word in block)[:120],
                "reason": ("unknown-run-low-confidence"
                           if mean is not None and mean < maximum_probability
                           else "unknown-run"),
            })
            summary["hallucinated_words"] += end - index
        index = end

    # A decoder stuck in a loop repeats the same short token sequence.
    for size in range(1, repetition_length + 1):
        run = 1
        for position in range(size, len(keys) - size + 1, size):
            if keys[position:position + size] == keys[position - size:position]:
                run += 1
                if run == 4:
                    summary["repetition_loops"] += 1
            else:
                run = 1
    return summary


def score_transcript(comparison: dict, coverage: dict, hallucination: dict) -> dict:
    """Rank candidates by covered, trustworthy matches instead of raw matches.

    Matching words are discounted by the share of singing the transcript never
    reached, because those seconds cannot contribute to any later alignment.
    Invented words are removed outright: they are worse than silence, since a
    later stage may anchor lyrics to them.
    """
    matching = int(comparison.get("matching_words", 0))
    share = float(coverage.get("covered_share", 1.0))
    invented = int(hallucination.get("hallucinated_words", 0))
    effective = max(0.0, matching - invented) * share
    return {
        "matching_words": matching,
        "covered_share": round(share, 4),
        "hallucinated_words": invented,
        "effective_matches": round(effective, 2),
        "similarity": float(comparison.get("similarity", 0.0)),
    }


def selection_rank(score: dict) -> tuple[float, float]:
    return (score["effective_matches"], score["similarity"])
