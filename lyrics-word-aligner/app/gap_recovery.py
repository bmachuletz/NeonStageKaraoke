from __future__ import annotations

import math
import os

from .models import LrcLine
from .transcript_match import compare_transcripts, normalize_words


def best_lyric_block(lines: list[LrcLine], transcript: str, *, max_lines: int = 2) -> dict | None:
    """Find a short known lyric block for a bounded vocal-gap transcript."""
    recognized = normalize_words(transcript)
    if len(recognized) < 2:
        return None
    best: dict | None = None
    for first in range(len(lines)):
        for count in range(1, min(max_lines, len(lines) - first) + 1):
            selected = lines[first:first + count]
            text = "\n".join(line.text for line in selected)
            expected = normalize_words(text)
            if not expected or len(expected) > max(4, len(recognized) * 1.65):
                continue
            comparison = compare_transcripts(text, transcript)
            matching = comparison["counts"]["match"] + comparison["counts"]["approximate"]
            coverage = matching / max(1, max(len(expected), len(recognized)))
            score = float(comparison["similarity"]) * 0.7 + coverage * 0.3
            candidate = {
                "first_line": first,
                "line_count": count,
                "text": text,
                "expected_words": len(expected),
                "recognized_words": len(recognized),
                "matching_words": matching,
                "coverage": round(coverage, 4),
                "similarity": comparison["similarity"],
                "score": round(score, 4),
            }
            if best is None or candidate["score"] > best["score"]:
                best = candidate
    return best


def _new_lines(source: list[LrcLine], aligned: list[dict], region: dict) -> list[LrcLine]:
    counts = [len(normalize_words(line.text)) for line in source]
    if sum(counts) != len(aligned):
        return []
    result: list[LrcLine] = []
    cursor = 0
    for source_line, count in zip(source, counts):
        words = [dict(word) for word in aligned[cursor:cursor + count]]
        cursor += count
        for word in words:
            word["timing_source"] = "targeted-vocal-gap-recovery"
            word["gap_asr_text"] = region.get("targeted_transcript", "")
        result.append(LrcLine(
            timestamp=float(words[0]["start"]),
            text=source_line.text,
            original="",
            words=words,
            status="review",
            reason="Aus Vocal-Ausschlag ohne Text als bekannte Zeile rekonstruiert",
            timed_input=False,
            source_timestamp=None,
        ))
    return result


def _delayed_source_line(lines: list[LrcLine], region: dict) -> int | None:
    start, end = float(region["start"]), float(region["end"])
    candidates: list[tuple[float, int]] = []
    for index, line in enumerate(lines):
        if line.source_timestamp is None or not line.words:
            continue
        source = float(line.source_timestamp)
        aligned = float(line.words[0]["start"])
        if start - 0.45 <= source <= end + 0.55 and aligned - start >= 0.35:
            candidates.append((abs(source - start), index))
    return min(candidates)[1] if candidates else None


def recover_vocal_gap_lines(audio, lines: list[LrcLine], regions: list[dict], language: str,
                            device: str, *, prompt: str | None = None,
                            transcribe_session=None, align_session=None) -> dict:
    """Inspect uncovered vocal islands in bounded GPU windows.

    Only a high-confidence match to one or two already known lyric lines is
    inserted automatically. Unique or unclear speech is reported for review;
    ASR text is never accepted as authoritative lyrics on its own.
    """
    minimum_score = float(os.getenv("LRC_GAP_RECOVERY_MIN_SCORE", "0.74"))
    minimum_coverage = float(os.getenv("LRC_GAP_RECOVERY_MIN_COVERAGE", "0.68"))
    maximum_regions = int(os.getenv("LRC_GAP_RECOVERY_MAX_REGIONS", "8"))
    duration = len(audio) / 16000
    selected = sorted(regions, key=lambda item: item.get("start", 0))[:maximum_regions]
    diagnostics: list[dict] = []

    owns_transcriber = transcribe_session is None
    if owns_transcriber:
        from .transcriber import QwenTranscriber
        transcribe_session = QwenTranscriber(device)
    try:
        for region in selected:
            start = max(0.0, float(region["start"]) - 0.22)
            end = min(duration, float(region["end"]) + 0.22)
            if end - start > 14.0:
                end = start + 14.0
            chunk = audio[int(start * 16000):int(end * 16000)]
            # A full-song vocabulary prompt is larger than many gap windows
            # and can be echoed as a hallucinated transcript. These bounded
            # diagnostics deliberately receive audio + language only.
            result = transcribe_session.transcribe(chunk, language, prompt=None)
            transcript = str(result.get("text", "")).strip()
            match = best_lyric_block(lines, transcript, max_lines=2)
            source_line = _delayed_source_line(lines, region)
            diagnostics.append({
                **region,
                "analysis_start": round(start, 3),
                "analysis_end": round(end, 3),
                "targeted_transcript": transcript,
                "match": match,
                "delayed_source_line": source_line,
                "model": result.get("model"),
            })
    finally:
        if owns_transcriber:
            transcribe_session.close()

    recoverable = [item for item in diagnostics
                   if item.get("match")
                   and item["match"]["score"] >= minimum_score
                   and item["match"]["coverage"] >= minimum_coverage
                   and item["match"]["matching_words"] >= max(
                       2, math.ceil(item["match"]["expected_words"] * 0.6))]
    owns_aligner = align_session is None
    source_realignments = [item for item in diagnostics
                           if item.get("delayed_source_line") is not None]
    if (recoverable or source_realignments) and owns_aligner:
        from .aligner import QwenWordAligner
        align_session = QwenWordAligner(device=device)
    recovered: list[dict] = []
    try:
        for item in source_realignments:
            index = int(item["delayed_source_line"])
            target = lines[index]
            source = float(target.source_timestamp)
            previous_end = (float(lines[index - 1].words[-1]["end"])
                            if index > 0 and lines[index - 1].words else 0.0)
            next_start = (float(lines[index + 1].words[0]["start"])
                          if index + 1 < len(lines) and lines[index + 1].words else duration)
            start = max(previous_end, source - 0.55)
            end = min(duration, next_start, max(source + 1.0,
                      float(target.words[-1]["end"]) + 0.25))
            if end - start < 0.45:
                item["source_realign_status"] = "source-window-too-short"
                continue
            chunk = audio[int(start * 16000):int(end * 16000)]
            old_start = float(target.words[0]["start"])
            aligned = align_session.align_text(chunk, target.text, language)
            for word in aligned:
                word["start"] = round(float(word["start"]) + start, 3)
                word["end"] = round(float(word["end"]) + start, 3)
                word["timing_source"] = "targeted-vocal-gap-source-line"
                word["gap_asr_text"] = item.get("targeted_transcript", "")
            # Dense punk vocals often make the complete sentence miss a short
            # pickup such as "And". Re-run only that first word inside the
            # measured vocal island and splice it into the otherwise useful
            # sentence alignment.
            if aligned and float(aligned[0]["start"]) > float(item["end"]) + 0.1:
                first_text = str(target.words[0].get("word", "")).strip()
                prefix_start = max(previous_end, min(source, float(item["start"])) - 0.18)
                prefix_end = min(end, float(item["end"]) + 0.35)
                if first_text and prefix_end - prefix_start >= 0.35:
                    prefix_audio = audio[int(prefix_start * 16000):int(prefix_end * 16000)]
                    prefix = align_session.align_text(prefix_audio, first_text, language)
                    if prefix:
                        pickup = dict(prefix[0])
                        pickup["start"] = round(float(pickup["start"]) + prefix_start, 3)
                        pickup["end"] = round(float(pickup["end"]) + prefix_start, 3)
                        pickup["timing_source"] = "targeted-vocal-gap-pickup"
                        pickup["gap_asr_text"] = item.get("targeted_transcript", "")
                        next_word_start = (float(aligned[1]["start"])
                                           if len(aligned) > 1 else float("inf"))
                        if (float(item["start"]) - 0.25 <= float(pickup["start"])
                                <= float(item["end"]) + 0.15
                                and float(pickup["end"]) <= next_word_start + 0.03):
                            aligned[0] = pickup
                            item["pickup_word_recovered"] = first_text
            if (not aligned
                    or float(aligned[0]["start"]) < float(item["start"]) - 0.45
                    or float(aligned[0]["start"]) > float(item["end"]) + 0.15
                    or old_start - float(aligned[0]["start"]) < 0.2
                    or float(aligned[-1]["end"]) > next_start + 0.05):
                item["source_realign_status"] = "forced-alignment-outside-source-window"
                continue
            target.words = aligned
            target.timestamp = float(aligned[0]["start"])
            target.status = "review"
            target.reason = "Verspäteten Zeilenbeginn anhand Vocal-Lücke und LRC-Anker neu ausgerichtet"
            item["source_realign_status"] = "realigned-delayed-existing-line"
            item["recovered_lines"] = 1
            recovered.append(item)

        for item in recoverable:
            if item in recovered:
                continue
            match = item["match"]
            source = lines[match["first_line"]:match["first_line"] + match["line_count"]]
            start, end = float(item["analysis_start"]), float(item["analysis_end"])
            chunk = audio[int(start * 16000):int(end * 16000)]
            aligned = align_session.align_text(chunk, match["text"], language)
            for word in aligned:
                word["start"] = round(float(word["start"]) + start, 3)
                word["end"] = round(float(word["end"]) + start, 3)
            if not aligned or float(aligned[0]["start"]) < float(item["start"]) - 0.35 \
                    or float(aligned[-1]["end"]) > float(item["end"]) + 0.35:
                item["recovery_status"] = "forced-alignment-outside-vocal-gap"
                continue
            clones = _new_lines(source, aligned, item)
            if not clones:
                item["recovery_status"] = "word-count-mismatch"
                continue
            lines.extend(clones)
            lines.sort(key=lambda line: line.timestamp)
            item["recovery_status"] = "recovered-known-lyric-block"
            item["recovered_lines"] = len(clones)
            recovered.append(item)
    finally:
        if owns_aligner and align_session is not None:
            align_session.close()

    unresolved = [{**item, "reason": item.get("recovery_status", "no-safe-known-lyric-match")}
                  for item in diagnostics if item not in recovered]
    return {
        "enabled": True,
        "method": "bounded-vocal-gap-asr-and-known-line-recovery-v1",
        "investigated_regions": len(diagnostics),
        "recovered_regions": len(recovered),
        "recovered_lines": sum(item.get("recovered_lines", 0) for item in recovered),
        "realigned_existing_lines": sum(
            item.get("source_realign_status") == "realigned-delayed-existing-line"
            for item in recovered),
        "regions": diagnostics,
        "unresolved_regions": unresolved,
        "minimum_score": minimum_score,
        "minimum_coverage": minimum_coverage,
    }
