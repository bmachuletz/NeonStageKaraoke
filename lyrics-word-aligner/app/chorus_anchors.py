from __future__ import annotations

from collections import Counter
import os
import re

from .models import LrcLine


_TOKEN_RE = re.compile(r"[^\W_]+(?:['’][^\W_]+)?", re.UNICODE)


def _prefix(line: LrcLine, size: int) -> tuple[str, ...]:
    return tuple(token.casefold() for token in _TOKEN_RE.findall(line.text)[:size])


def recover_missing_initial_chorus(
    vocal_audio,
    instrumental_audio,
    lines: list[LrcLine],
    language: str,
    device: str,
    *,
    transcribe_fn=None,
    initial_prompt: str | None = None,
) -> dict:
    """Recover a backing-vocal intro that the separator put in Instrumental.

    This intentionally handles only the first occurrence of a repeated prefix.
    A silent vocal stem plus a timestamped singing segment in Instrumental is
    strong evidence for the common karaoke-separator failure where backing
    vocals are retained in the accompaniment.
    """
    import numpy as np

    prefix_size = int(os.getenv("LRC_CHORUS_ANCHOR_PREFIX_WORDS", "3"))
    minimum_repetitions = int(os.getenv("LRC_CHORUS_ANCHOR_MIN_REPETITIONS", "3"))
    prefixes = [_prefix(line, prefix_size) for line in lines]
    counts = Counter(prefix for prefix in prefixes if len(prefix) == prefix_size)
    repeated = {prefix for prefix, count in counts.items() if count >= minimum_repetitions}
    if not repeated or instrumental_audio is None:
        return {"enabled": True, "recovered_lines": 0, "reason": "no-repeated-prefix"}

    first_index = next((index for index, prefix in enumerate(prefixes) if prefix in repeated), None)
    if first_index is None or lines[first_index].source_timestamp is None or not lines[first_index].words:
        return {"enabled": True, "recovered_lines": 0, "reason": "no-eligible-first-line"}
    line = lines[first_index]
    source = float(line.source_timestamp)
    sample_rate = 16000
    check_start = max(0, int((source - 1.5) * sample_rate))
    check_end = min(len(vocal_audio), int((source + 0.25) * sample_rate))
    vocal_window = np.asarray(vocal_audio[check_start:check_end], dtype=np.float32)
    vocal_rms = float(np.sqrt(np.mean(vocal_window * vocal_window))) if vocal_window.size else 0.0
    silence_threshold = float(os.getenv("LRC_MISSING_CHORUS_VOCAL_RMS", "0.0005"))
    if vocal_rms > silence_threshold:
        return {"enabled": True, "recovered_lines": 0, "reason": "vocal-stem-not-silent",
                "vocal_rms": round(vocal_rms, 7)}

    matching_indices = [i for i, prefix in enumerate(prefixes) if prefix == prefixes[first_index]]
    last_nearby = max((i for i in matching_indices if i <= first_index + 4), default=first_index)
    window_start = max(0.0, source - 3.0)
    window_end = min(len(instrumental_audio) / sample_rate,
                     float(lines[last_nearby].source_timestamp or source) + 6.0)
    chunk = instrumental_audio[int(window_start * sample_rate):int(window_end * sample_rate)]
    if transcribe_fn is None:
        from .stable_transcriber import transcribe_stable
        result = transcribe_stable(
            chunk, language, device, vad=False, initial_prompt=initial_prompt)
    else:
        result = transcribe_fn(chunk, language, device)
    measured = result.get("words", [])
    if len(measured) < prefix_size:
        return {"enabled": True, "recovered_lines": 0,
                "reason": "instrumental-no-timestamped-phrase", "transcript": result.get("text", "")}

    measured = measured[:prefix_size]
    start = window_start + float(measured[0]["start"])
    end = window_start + float(measured[-1]["end"])
    if not (0.25 <= end - start <= 4.0 and source - 3.0 <= start < source - 0.08):
        return {"enabled": True, "recovered_lines": 0, "reason": "instrumental-phrase-not-earlier",
                "detected_start": round(start, 3), "detected_end": round(end, 3),
                "transcript": result.get("text", "")}

    # Whisper often hears the correct backing-vocal island but merges or
    # substitutes its words under dense music. Preserve its measured outer
    # bounds and distribute the known lyric tokens by phonetic text weight.
    prefix_words = line.words[:prefix_size]
    weights = [max(1, sum(len(token) for token in _TOKEN_RE.findall(str(word["word"]))))
               for word in prefix_words]
    total_weight = sum(weights)
    cursor = start
    for index, (word, weight) in enumerate(zip(prefix_words, weights)):
        word_end = end if index == len(prefix_words) - 1 else cursor + (end - start) * weight / total_weight
        word["start"] = round(cursor, 3)
        word["end"] = round(word_end, 3)
        word["timing_source"] = "instrumental-chorus-stable-ts"
        word["instrumental_recognized_phrase"] = result.get("text", "")
        cursor = word_end
    line.timestamp = float(line.words[0]["start"])
    return {
        "enabled": True,
        "recovered_lines": 1,
        "line": first_index + 1,
        "prefix": " ".join(prefixes[first_index]),
        "source_timestamp": round(source, 3),
        "detected_start": round(start, 3),
        "detected_end": round(end, 3),
        "vocal_rms": round(vocal_rms, 7),
        "audio_source": "instrumental",
        "method": "missing-backing-vocal-stable-ts-v1",
        "transcript": result.get("text", ""),
    }


def apply_trusted_chorus_anchors(lines: list[LrcLine]) -> dict:
    """Keep repeated chorus entries on their original line-synchronised cue.

    Singing ASR commonly misses the first words when lead and backing vocals
    enter together.  The acoustic result is still used for word durations, but
    a clearly late repeated phrase is translated back to its LRC line cue.
    Only repeated prefixes are eligible, so ordinary LRCLIB timestamps remain
    search hints rather than being treated as universal ground truth.
    """
    prefix_size = int(os.getenv("LRC_CHORUS_ANCHOR_PREFIX_WORDS", "3"))
    minimum_repetitions = int(os.getenv("LRC_CHORUS_ANCHOR_MIN_REPETITIONS", "3"))
    minimum_delay = float(os.getenv("LRC_CHORUS_ANCHOR_MIN_DELAY", "0.25"))
    maximum_shift = float(os.getenv("LRC_CHORUS_ANCHOR_MAX_SHIFT", "2.0"))

    prefixes = [_prefix(line, prefix_size) for line in lines]
    counts = Counter(prefix for prefix in prefixes if len(prefix) == prefix_size)
    repeated = {prefix for prefix, count in counts.items() if count >= minimum_repetitions}
    shifted: list[dict] = []

    for line, prefix in zip(lines, prefixes):
        if prefix not in repeated or line.source_timestamp is None or not line.words:
            continue
        aligned_start = float(line.words[0]["start"])
        delay = aligned_start - float(line.source_timestamp)
        if delay < minimum_delay or delay > maximum_shift:
            continue

        # Only the repeated backing-vocal prefix is moved. The following words
        # are commonly sung by the lead voice later (and may overlap the tail
        # of the chorus); translating the complete line would destroy those
        # otherwise reliable acoustic timings.
        anchored_words = line.words[:prefix_size]
        for word in anchored_words:
            word["start"] = max(0.0, float(word["start"]) - delay)
            word["end"] = max(float(word["start"]), float(word["end"]) - delay)
            word["timing_source"] = "lrc-chorus-anchor"
            word["chorus_anchor_shift_ms"] = round(-delay * 1000.0, 1)
        line.timestamp = float(line.source_timestamp)
        shifted.append({
            "text": line.text,
            "prefix": " ".join(prefix),
            "source_timestamp": round(float(line.source_timestamp), 3),
            "aligned_start": round(aligned_start, 3),
            "shift_ms": round(-delay * 1000.0, 1),
            "anchored_words": len(anchored_words),
        })

    return {
        "enabled": True,
        "repeated_prefixes": [" ".join(prefix) for prefix in sorted(repeated)],
        "shifted_lines": len(shifted),
        "adjustments": shifted,
        "minimum_delay_ms": round(minimum_delay * 1000.0),
        "maximum_shift_ms": round(maximum_shift * 1000.0),
    }
