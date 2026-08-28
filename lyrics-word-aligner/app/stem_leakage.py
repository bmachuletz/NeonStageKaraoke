from __future__ import annotations

import os
import statistics

import numpy as np

from .stable_transcriber import StableTranscriberSession
from .transcript_match import compare_transcripts, normalize_words


SAMPLE_RATE = 16000


def _rms(audio, start: float, end: float) -> float:
    first = max(0, int(start * SAMPLE_RATE))
    last = min(len(audio), max(first + 1, int(end * SAMPLE_RATE)))
    values = np.asarray(audio[first:last], dtype=np.float64)
    if not values.size:
        return 0.0
    return float(np.sqrt(np.mean(values * values)))


def _invalid_model_geometry(line) -> bool:
    if not line.words:
        return True
    durations = [float(word["end"]) - float(word["start"]) for word in line.words]
    overlap = any(float(left["end"]) > float(right["start"]) + 0.005
                  for left, right in zip(line.words, line.words[1:]))
    nonmonotonic = any(float(right["start"]) + 0.015 < float(left["start"])
                       for left, right in zip(line.words, line.words[1:]))
    low_sofa = any(
        word.get("timing_source") == "sofa-singing-alignment"
        and float(word.get("sofa_confidence", 1.0)) < 0.55
        for word in line.words
    )
    return (any(duration <= 0.025 or duration > 6.0 for duration in durations)
            or overlap or nonmonotonic or low_sofa)


def _recognized_words(words: list[dict]) -> list[tuple[str, dict]]:
    result: list[tuple[str, dict]] = []
    for word in words:
        normalized = normalize_words(str(word.get("word", "")))
        if len(normalized) == 1:
            result.append((normalized[0], word))
    return result


def _best_contiguous_phrase(line, words: list[dict], *, window_start: float,
                            maximum_start_deviation: float) -> dict | None:
    expected = normalize_words(line.text)
    recognized = _recognized_words(words)
    if not expected or len(recognized) < len(expected):
        return None
    source = float(line.source_timestamp)
    best = None
    for offset in range(len(recognized) - len(expected) + 1):
        selected = recognized[offset:offset + len(expected)]
        candidate_words = [item[1] for item in selected]
        start = window_start + float(candidate_words[0]["start"])
        end = window_start + float(candidate_words[-1]["end"])
        if abs(start - source) > maximum_start_deviation or end <= start:
            continue
        recognized_text = " ".join(item[0] for item in selected)
        comparison = compare_transcripts(line.text, recognized_text)
        matching = (comparison["counts"]["match"]
                    + comparison["counts"]["approximate"])
        coverage = matching / len(expected)
        probabilities = [float(word.get("probability", 0.0))
                         for word in candidate_words]
        median_probability = statistics.median(probabilities)
        if (coverage < 0.80 or float(comparison["similarity"]) < 0.80
                or median_probability < 0.60):
            continue
        score = (coverage * 0.50 + float(comparison["similarity"]) * 0.30
                 + median_probability * 0.20
                 - min(0.15, abs(start - source) * 0.05))
        candidate = {
            "words": candidate_words,
            "start": start,
            "end": end,
            "coverage": coverage,
            "similarity": float(comparison["similarity"]),
            "median_probability": median_probability,
            "score": score,
            "transcript": recognized_text,
        }
        if best is None or candidate["score"] > best["score"]:
            best = candidate
    return best


def _best_loose_near_phrase(line, words: list[dict], *, window_start: float,
                            maximum_start_deviation: float) -> dict | None:
    """Find a similar local phrase without letting neighbour lines dilute it."""
    expected = normalize_words(line.text)
    recognized = _recognized_words(words)
    if not expected or not recognized:
        return None
    best = None
    minimum_length = max(1, len(expected) - 2)
    maximum_length = min(len(recognized), len(expected) + 2)
    for length in range(minimum_length, maximum_length + 1):
        for offset in range(len(recognized) - length + 1):
            selected = recognized[offset:offset + length]
            start = window_start + float(selected[0][1]["start"])
            if abs(start - float(line.source_timestamp)) > maximum_start_deviation:
                continue
            transcript = " ".join(item[0] for item in selected)
            comparison = compare_transcripts(line.text, transcript)
            coverage = (int(comparison.get("matching_words", 0))
                        / max(1, len(expected)))
            similarity = float(comparison.get("similarity", 0.0))
            score = similarity * 0.70 + coverage * 0.30
            candidate = {
                "transcript": transcript,
                "similarity": similarity,
                "matching_coverage": coverage,
                "start": start,
                "end": window_start + float(selected[-1][1]["end"]),
                "words": [item[1] for item in selected],
                "score": score,
            }
            if best is None or candidate["score"] > best["score"]:
                best = candidate
    return best


def _aligned_expected_words(line, words: list[dict], *, window_start: float) -> dict[int, dict]:
    """Map independently recognized exact/near words to canonical token indices."""
    recognized = _recognized_words(words)
    comparison = compare_transcripts(
        line.text, " ".join(token for token, _word in recognized))
    result: dict[int, dict] = {}
    for operation in comparison.get("operations", []):
        if operation.get("type") not in {"match", "approximate"}:
            continue
        expected_index = int(operation["expected_index"])
        recognized_index = int(operation["recognized_index"])
        if not 0 <= recognized_index < len(recognized):
            continue
        measured = dict(recognized[recognized_index][1])
        measured["start"] = window_start + float(measured["start"])
        measured["end"] = window_start + float(measured["end"])
        result[expected_index] = measured
    return result


def _fuse_complementary_recognitions(
    line,
    loose_words: list[dict],
    *,
    loose_window_start: float,
    prompted_words: list[dict],
    prompted_window_start: float,
    maximum_start_deviation: float,
) -> dict | None:
    """Fuse two acoustic passes only when together they cover every lyric word.

    A tight prompted pass can drop a short edge word while improving the
    distorted middle of a phrase.  The earlier unprompted near-match is allowed
    to supply that edge, including their common boundary word so timestamps do
    not jump between recognizers mid-word.  No timestamp is synthesized.
    """
    expected = normalize_words(line.text)
    loose = _aligned_expected_words(
        line, loose_words, window_start=loose_window_start)
    prompted = _aligned_expected_words(
        line, prompted_words, window_start=prompted_window_start)
    if not expected or not loose or not prompted:
        return None

    selected = dict(prompted)
    selected.update({index: word for index, word in loose.items()
                     if index not in selected})
    missing = [index for index in range(len(expected)) if index not in selected]
    if missing:
        return None

    # Keep one shared anchor from the unprompted pass when it supplied a missing
    # prefix/suffix. This prevents the two recognizers from overlapping at the
    # splice even if the tight crop rounded the first/last phoneme differently.
    first_prompted = min(prompted)
    last_prompted = max(prompted)
    if any(index < first_prompted for index in loose if index not in prompted):
        for index in range(0, first_prompted + 1):
            if index in loose:
                selected[index] = loose[index]
    if any(index > last_prompted for index in loose if index not in prompted):
        for index in range(last_prompted, len(expected)):
            if index in loose:
                selected[index] = loose[index]

    fused = [selected[index] for index in range(len(expected))]
    if (abs(float(fused[0]["start"]) - float(line.source_timestamp))
            > maximum_start_deviation):
        return None
    if any(float(word["end"]) <= float(word["start"]) for word in fused):
        return None
    # Independent recognizers can disagree by a few frames at the splice. Snap
    # only small overlaps to the measured onset of the following word. Larger
    # conflicts remain unsafe and reject the fusion.
    for left, right in zip(fused, fused[1:]):
        overlap = float(left["end"]) - float(right["start"])
        if overlap <= 0.005:
            continue
        if overlap > 0.12 or float(right["start"]) - float(left["start"]) < 0.025:
            return None
        left["end"] = float(right["start"])
        left["stem_leakage_boundary_snap_ms"] = round(overlap * 1000)
    loose_probabilities = [float(word.get("probability", 0.0))
                           for word in loose_words]
    corroborating_probability = statistics.median(loose_probabilities)
    if corroborating_probability < 0.60:
        return None
    fused_probability = statistics.median(
        [float(word.get("probability", 0.0)) for word in fused])
    return {
        "words": fused,
        "start": float(fused[0]["start"]),
        "end": float(fused[-1]["end"]),
        "coverage": 1.0,
        "similarity": 1.0,
        "median_probability": corroborating_probability,
        "fused_median_probability": fused_probability,
        "score": 0.80 + corroborating_probability * 0.20,
        "transcript": " ".join(expected),
        "fused_recognitions": True,
    }


def recover_complementary_stem_lines(
    vocal_audio,
    instrumental_audio,
    lines: list,
    language: str,
    device: str,
    *,
    transcribe_fn=None,
    prompted_transcribe_fn=None,
    recognition_audio=None,
    recognition_audio_source: str = "instrumental",
    recognition_candidates: list[tuple[str, np.ndarray]] | None = None,
) -> dict:
    """Recover known lyrics which a separator placed in the accompaniment.

    The fallback is intentionally conservative. It is eligible only for a
    damaged/low-confidence model path near a trusted line cue, a nearly empty
    vocal stem, a materially stronger complementary stem and a complete local
    Stable-TS text match. The canonical lyric spelling always wins over ASR.
    """
    if instrumental_audio is None:
        return {"enabled": False, "reason": "no-complementary-stem",
                "candidate_lines": 0, "recovered_lines": 0}

    # Zero means unlimited. The acoustic eligibility checks remain strict; a
    # fixed default of four silently skipped later leaked phrases in longer
    # songs. Deployments may still set an explicit operational budget.
    maximum_lines = max(0, int(os.getenv("LRC_STEM_LEAKAGE_MAX_LINES", "0")))
    has_capacity = lambda: maximum_lines == 0 or len(candidates) < maximum_lines
    maximum_start_deviation = float(os.getenv(
        "LRC_STEM_LEAKAGE_MAX_START_DEVIATION", "1.25"))
    maximum_vocal_rms = float(os.getenv("LRC_STEM_LEAKAGE_MAX_VOCAL_RMS", "0.0025"))
    minimum_instrumental_rms = float(os.getenv(
        "LRC_STEM_LEAKAGE_MIN_COMPLEMENT_RMS", "0.004"))
    maximum_ratio = float(os.getenv("LRC_STEM_LEAKAGE_MAX_RMS_RATIO", "0.16"))
    diagnostics: list[dict] = []
    candidates: list[tuple[int, object, float, float, float, float, str]] = []
    recognition_audio = (instrumental_audio
                         if recognition_audio is None else recognition_audio)
    recognition_sources = (recognition_candidates if recognition_candidates else
                           [(recognition_audio_source, recognition_audio)])
    duration = min(len(vocal_audio), len(instrumental_audio),
                   len(recognition_audio)) / SAMPLE_RATE
    candidate_indices: set[int] = set()

    for index, line in enumerate(lines):
        if (not has_capacity() or line.source_timestamp is None
                or not line.words or not _invalid_model_geometry(line)):
            continue
        source = float(line.source_timestamp)
        probe_end = min(duration, source + 2.8)
        vocal_rms = _rms(vocal_audio, max(0.0, source - 0.05), probe_end)
        complement_rms = _rms(instrumental_audio, max(0.0, source - 0.05), probe_end)
        ratio = vocal_rms / max(complement_rms, 1e-9)
        if (vocal_rms > maximum_vocal_rms or complement_rms < minimum_instrumental_rms
                or ratio > maximum_ratio):
            continue
        next_source = (float(lines[index + 1].source_timestamp)
                       if index + 1 < len(lines)
                       and lines[index + 1].source_timestamp is not None else source + 6.0)
        window_start = max(0.0, source - 0.38)
        window_end = min(duration, source + 8.0, next_source + 0.40)
        if window_end - window_start < 1.0:
            continue
        candidates.append((index, line, window_start, window_end, vocal_rms,
                           complement_rms, "silent-vocal-stem"))
        candidate_indices.add(index)

    # A false alignment on the missing stem often pulls the following line
    # several seconds before its own trusted cue. Once such a block begins,
    # inspect its prematurely placed successors on the complete recognition
    # signal as well. They still need the same complete local text match, so a
    # merely nearby line is never moved on timestamp evidence alone.
    base_indices = sorted(candidate_indices)
    for first in base_indices:
        index = first + 1
        while index < len(lines) and has_capacity():
            line = lines[index]
            if line.source_timestamp is None or not line.words:
                break
            source = float(line.source_timestamp)
            current_start = float(line.words[0]["start"])
            premature = current_start < source - 0.50
            # A missing/backing-vocal phrase can also make a section aligner
            # collapse the successor *after* its trusted editor/LRC cue.  The
            # old recovery followed only premature successors, so low-
            # confidence singing-model lines could escape the local
            # original-mix check. Admit a delayed successor only when its
            # model geometry is independently suspect;
            # the complete local Stable-TS match below is still mandatory.
            displaced_low_confidence = (
                _invalid_model_geometry(line)
                and abs(current_start - source) >= 0.35
            )
            if not premature and not displaced_low_confidence:
                break
            if index in candidate_indices:
                index += 1
                continue
            probe_end = min(duration, source + 2.8)
            vocal_rms = _rms(vocal_audio, max(0.0, source - 0.05), probe_end)
            complement_rms = _rms(
                instrumental_audio, max(0.0, source - 0.05), probe_end)
            next_source = (float(lines[index + 1].source_timestamp)
                           if index + 1 < len(lines)
                           and lines[index + 1].source_timestamp is not None else source + 6.0)
            window_start = max(0.0, source - 0.38)
            window_end = min(duration, source + 8.0, next_source + 0.40)
            candidates.append((
                index, line, window_start, window_end, vocal_rms,
                complement_rms,
                ("premature-overlap-successor" if premature
                 else "low-confidence-displaced-successor"),
            ))
            candidate_indices.add(index)
            index += 1

    # Successor recovery is discovered in a second pass. Restore musical order
    # before ASR and before the resulting hybrid intervals are emitted.
    candidates.sort(key=lambda item: item[0])

    stable_session = None

    def session():
        nonlocal stable_session
        if stable_session is None:
            stable_session = StableTranscriberSession(device)
        return stable_session

    if transcribe_fn is None:
        transcribe_fn = lambda chunk, lang, _target_device: session().transcribe(
            chunk, lang, vad=False, initial_prompt=None)
    if prompted_transcribe_fn is None:
        prompted_transcribe_fn = lambda chunk, lang, _target_device, prompt: session().transcribe(
            chunk, lang, vad=False, initial_prompt=prompt)

    recovered = 0
    try:
        for (index, line, window_start, window_end, vocal_rms, complement_rms,
             candidate_reason) in candidates:
            recovered += _recover_candidate(
                index, line, window_start, window_end, vocal_rms, complement_rms,
                candidate_reason, recognition_sources, language, device,
                maximum_start_deviation, transcribe_fn, prompted_transcribe_fn,
                diagnostics)
    finally:
        if stable_session is not None:
            stable_session.close()

    return {
        "enabled": True,
        "method": "trusted-lrc-complementary-stem-stable-ts-v1",
        "candidate_lines": len(candidates),
        "recovered_lines": recovered,
        "candidate_limit": maximum_lines or None,
        "shared_stable_ts_session": stable_session is not None,
        "diagnostics": diagnostics,
    }


def _recover_candidate(
    index, line, window_start: float, window_end: float,
    vocal_rms: float, complement_rms: float, candidate_reason: str,
    recognition_sources, language: str, device: str,
    maximum_start_deviation: float, transcribe_fn, prompted_transcribe_fn,
    diagnostics: list[dict],
) -> int:
        entry = {
            "line": index + 1,
            "text": line.text,
            "source_timestamp": round(float(line.source_timestamp), 3),
            "window_start": round(window_start, 3),
            "window_end": round(window_end, 3),
            "vocal_rms": round(vocal_rms, 7),
            "complement_rms": round(complement_rms, 7),
            "vocal_to_complement_ratio": round(vocal_rms / max(complement_rms, 1e-9), 4),
            "candidate_reason": candidate_reason,
            "recognition_audio_sources": [source for source, _audio in recognition_sources],
        }
        source_attempts = []
        best = None
        try:
            for source_name, source_audio in recognition_sources:
                windows = [("local", window_start, window_end)]
                if source_name != "original-mix":
                    context_start = max(0.0, window_start - 2.8)
                    context_end = min(len(source_audio) / SAMPLE_RATE, window_end + 6.0)
                    if (context_start < window_start - 0.5
                            or context_end > window_end + 0.5):
                        windows.append(("expanded-context", context_start, context_end))
                for window_mode, recognition_start, recognition_end in windows:
                    chunk = source_audio[
                        int(recognition_start * SAMPLE_RATE):
                        int(recognition_end * SAMPLE_RATE)]
                    result = transcribe_fn(chunk, language, device)
                    match = _best_contiguous_phrase(
                        line, result.get("words", []), window_start=recognition_start,
                        maximum_start_deviation=maximum_start_deviation)
                    attempt = {
                        "source": source_name,
                        "mode": f"unprompted-{window_mode}",
                        "window_start": round(recognition_start, 3),
                        "window_end": round(recognition_end, 3),
                        "recognized_text": str(result.get("text", ""))[:500],
                        "complete_match": match is not None,
                    }
                    source_attempts.append(attempt)
                    if match is not None:
                        candidate = (match["score"], match, result, source_name,
                                     f"unprompted-{window_mode}", recognition_start)
                        if best is None or candidate[0] > best[0]:
                            best = candidate
                        break

                    # A canonical prompt may disambiguate distorted vocals, but it
                    # must never manufacture lyrics from silence. Require an
                    # independent unprompted near-match on this exact source first.
                    loose = _best_loose_near_phrase(
                        line, result.get("words", []), window_start=recognition_start,
                        maximum_start_deviation=maximum_start_deviation)
                    attempt["near_phrase"] = (loose or {}).get("transcript", "")
                    attempt["similarity"] = round(float(
                        (loose or {}).get("similarity", 0.0)), 4)
                    attempt["matching_coverage"] = round(float(
                        (loose or {}).get("matching_coverage", 0.0)), 4)
                    if (loose is None or loose["similarity"] < 0.65
                            or loose["matching_coverage"] < 0.60):
                        continue
                    # Do not feed the complete expanded context to the prompted
                    # retry.  Once the unprompted pass has located a credible
                    # local phrase, crop tightly around that acoustic phrase.
                    # Otherwise Whisper may follow the clearly separated next
                    # lyric instead of disambiguating the distorted target.
                    prompt_start = max(
                        0.0, float(loose["start"]) - 0.40)
                    prompt_end = min(
                        len(source_audio) / SAMPLE_RATE,
                        max(float(loose["end"]) + 0.55,
                            float(loose["start"]) + 1.0))
                    prompt_chunk = source_audio[
                        int(prompt_start * SAMPLE_RATE):
                        int(prompt_end * SAMPLE_RATE)]
                    prompted = prompted_transcribe_fn(
                        prompt_chunk, language, device, line.text)
                    prompted_match = _best_contiguous_phrase(
                        line, prompted.get("words", []), window_start=prompt_start,
                        maximum_start_deviation=maximum_start_deviation)
                    if prompted_match is None:
                        prompted_match = _fuse_complementary_recognitions(
                            line,
                            loose["words"],
                            loose_window_start=recognition_start,
                            prompted_words=prompted.get("words", []),
                            prompted_window_start=prompt_start,
                            maximum_start_deviation=maximum_start_deviation)
                    source_attempts.append({
                        "source": source_name,
                        "mode": f"canonical-prompt-target-crop-after-near-match-{window_mode}",
                        "window_start": round(prompt_start, 3),
                        "window_end": round(prompt_end, 3),
                        "recognized_text": str(prompted.get("text", ""))[:500],
                        "complete_match": prompted_match is not None,
                        "fused_with_unprompted_near_match": bool(
                            (prompted_match or {}).get("fused_recognitions")),
                        "unprompted_similarity": attempt["similarity"],
                        "unprompted_matching_coverage": attempt["matching_coverage"],
                    })
                    if prompted_match is not None:
                        candidate = (
                            prompted_match["score"], prompted_match, prompted,
                            source_name,
                            f"canonical-prompt-target-crop-after-near-match-{window_mode}",
                            (0.0 if prompted_match.get("fused_recognitions")
                             else prompt_start))
                        if best is None or candidate[0] > best[0]:
                            best = candidate
                        break
        except (RuntimeError, ValueError, OSError) as error:
            entry.update(status="transcription-error", error=str(error)[:1000])
            diagnostics.append(entry)
            return 0
        entry["source_attempts"] = source_attempts
        if best is None:
            entry["status"] = "no-complete-local-match"
            diagnostics.append(entry)
            return 0
        (_score, match, result, selected_source, selected_mode,
         selected_window_start) = best
        entry["recognition_audio_source"] = selected_source
        entry["recognition_mode"] = selected_mode
        entry["recognized_text"] = str(result.get("text", ""))[:500]
        displayed = ([str(word.get("word", "")) for word in line.words]
                     if len(line.words) == len(match["words"]) else
                     normalize_words(line.text))
        replacement = []
        for text, measured in zip(displayed, match["words"]):
            replacement.append({
                "word": text,
                "start": round(selected_window_start + float(measured["start"]), 3),
                "end": round(selected_window_start + float(measured["end"]), 3),
                "timing_source": "instrumental-leakage-stable-ts",
                "stable_ts_probability": float(measured.get("probability", 0.0)),
                "stem_leakage_audio_source": selected_source,
                "stem_leakage_recognition_mode": selected_mode,
            })
        if (any(float(word["end"]) <= float(word["start"]) for word in replacement)
                or any(float(left["end"]) > float(right["start"]) + 0.005
                       for left, right in zip(replacement, replacement[1:]))):
            entry["status"] = "invalid-recognized-geometry"
            diagnostics.append(entry)
            return 0
        line.words = replacement
        line.timestamp = float(replacement[0]["start"])
        line.status = "review"
        line.reason = "Gesang aus dem komplementären Stem am LRC-Anker rekonstruiert"
        entry.update(
            status="recovered-complementary-stem-line",
            detected_start=round(match["start"], 3),
            detected_end=round(match["end"], 3),
            coverage=round(match["coverage"], 4),
            similarity=round(match["similarity"], 4),
            median_probability=round(match["median_probability"], 4),
            fused_median_probability=(
                round(float(match["fused_median_probability"]), 4)
                if match.get("fused_median_probability") is not None else None),
        )
        diagnostics.append(entry)
        return 1
