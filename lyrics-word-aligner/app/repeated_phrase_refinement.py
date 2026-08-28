from __future__ import annotations

import statistics
import subprocess
from functools import lru_cache

import numpy as np

from .micro_boundaries import _boundary_candidates
from .transcript_match import normalize_words


def refine_repeated_phrase_words(
        audio,
        lines: list,
        stable_words: list[dict],
        language: str,
        *,
        sample_rate: int = 16000,
        search_radius: float = 0.25,
        minimum_evidence: float = 0.58,
        minimum_improvement: float = 0.16,
) -> dict:
    """Realign complete repeated lines as individual acoustic calls.

    Long-context ASR is very good at identifying the correct occurrence of a
    chorus, but repeated identical tokens can borrow duration from their
    neighbours.  For a line made entirely from at least three repetitions of
    the same short unit, use the ASR timestamps only as occurrence priors and
    independently locate every sung onset in the exposed Stage vocal stem.
    The whole prefix is applied atomically; one weak onset rejects the line.
    """
    signal = _mono(audio)
    recognized = _normalized_stable_words(stable_words)
    summary = {
        "enabled": bool(len(signal) and recognized),
        "method": "stable-occurrence-plus-repeated-acoustic-onsets-v1.4",
        "attempted_lines": 0,
        "refined_lines": 0,
        "refined_words": 0,
        "diagnostics": [],
    }
    if not summary["enabled"]:
        summary["reason"] = "audio-or-stable-timestamps-unavailable"
        return summary
    for line_index, line in enumerate(lines):
        tokens = [_normal_word(word.get("word", "")) for word in line.words]
        repeated = _complete_repeated_unit(tokens)
        if repeated is None:
            continue
        unit_size, repeat_count = repeated
        summary["attempted_lines"] += 1
        stable = _find_local_stable_sequence(
            recognized, tokens, float(line.words[0]["start"]))
        if stable is None:
            summary["diagnostics"].append({
                "line": line_index + 1, "status": "stable-sequence-not-found",
                "unit_words": unit_size, "repetitions": repeat_count,
            })
            continue
        probabilities = [float(item.get("probability", 0.0)) for item in stable]
        if min(probabilities) < 0.60 or statistics.median(probabilities) < 0.78:
            summary["diagnostics"].append({
                "line": line_index + 1, "status": "weak-stable-occurrence",
                "minimum_probability": round(min(probabilities), 4),
                "median_probability": round(statistics.median(probabilities), 4),
            })
            continue
        proposed = []
        evidence_details = []
        for token, stable_word in zip(tokens, stable):
            prior = float(stable_word["start"])
            phone_class = _initial_phone_class(token, language)
            row = _boundary_candidates(
                signal, prior, phone_class, None, sample_rate, search_radius)
            baseline = next(item for item in row if item["is_prior"])
            candidate = max(
                (item for item in row if not item["is_prior"]),
                key=lambda item: float(item["objective"]), default=None)
            if candidate is None:
                proposed = []
                break
            evidence = float(candidate["evidence"])
            improvement = evidence - float(baseline["evidence"])
            snapped = _snap_to_activity_rise(
                signal, float(candidate["time"]), sample_rate)
            if (evidence < minimum_evidence
                    or improvement < minimum_improvement
                    or abs(snapped - prior) > search_radius + 0.03):
                proposed = []
                break
            proposed.append(snapped)
            evidence_details.append({
                "word": token, "stable_prior": round(prior, 3),
                "acoustic_start": round(snapped, 3),
                "delta_ms": round((snapped - prior) * 1000, 1),
                "phone_class": phone_class,
                "evidence": round(evidence, 4),
                "evidence_improvement": round(improvement, 4),
            })
        if (len(proposed) != len(tokens)
                or any(right - left < 0.055
                       for left, right in zip(proposed, proposed[1:]))):
            summary["diagnostics"].append({
                "line": line_index + 1, "status": "incomplete-acoustic-path",
                "unit_words": unit_size, "repetitions": repeat_count,
                "accepted_onsets": len(proposed), "required_onsets": len(tokens),
            })
            continue
        periodicity = _repetition_periodicity(proposed, unit_size, repeat_count)
        if not periodicity["verified"]:
            summary["diagnostics"].append({
                "line": line_index + 1,
                "status": "irregular-repetition-periodicity",
                "unit_words": unit_size,
                "repetitions": repeat_count,
                **periodicity,
            })
            continue
        previous_line_end = _previous_line_end(lines, line_index)
        next_line_start = _next_line_start(lines, line_index)
        if ((previous_line_end is not None and proposed[0] <= previous_line_end)
                or (next_line_start is not None and proposed[-1] >= next_line_start)):
            summary["diagnostics"].append({
                "line": line_index + 1, "status": "outside-line-lane",
            })
            continue
        proposed_ends = []
        for index, (stable_word, onset) in enumerate(zip(stable, proposed)):
            stable_end = float(stable_word["end"])
            if index + 1 < len(line.words):
                stable_next_start = float(stable[index + 1]["start"])
                if stable_next_start - stable_end <= 0.06:
                    end = proposed[index + 1]
                else:
                    end = min(stable_end, proposed[index + 1] - 0.02)
            else:
                end = stable_end
            if end - onset < 0.04:
                proposed_ends = []
                break
            proposed_ends.append(end)
        if len(proposed_ends) != len(proposed):
            summary["diagnostics"].append({
                "line": line_index + 1, "status": "invalid-word-duration",
            })
            continue
        old = [(float(word["start"]), float(word["end"])) for word in line.words]
        for index, (word, stable_word, onset, end) in enumerate(
                zip(line.words, stable, proposed, proposed_ends)):
            word["repeated_phrase_original_start"] = round(old[index][0], 3)
            word["repeated_phrase_original_end"] = round(old[index][1], 3)
            word["start"] = round(onset, 3)
            word["end"] = round(end, 3)
            word["timing_source"] = "stable-repetition-acoustic-onset"
            word["repeated_phrase_stable_probability"] = round(
                float(stable_word.get("probability", 0.0)), 4)
            word["repeated_phrase_periodicity_verified"] = True
        line.timestamp = float(line.words[0]["start"])
        summary["refined_lines"] += 1
        summary["refined_words"] += len(line.words)
        summary["diagnostics"].append({
            "line": line_index + 1, "status": "refined",
            "unit_words": unit_size, "repetitions": repeat_count,
            "old_start": round(old[0][0], 3),
            "new_start": round(proposed[0], 3),
            "words": evidence_details,
        })
    return summary


def _repetition_periodicity(onsets: list[float], unit_size: int,
                            repeat_count: int, *,
                            maximum_relative_spread: float = 0.35) -> dict:
    """Verify that repeated text follows one plausible musical pulse.

    Identical ASR tokens are ambiguous. A subsequence can be lexically exact
    while shifted by one occurrence. Compare the period between equivalent
    positions of the repeated unit; a wrong occurrence mapping produces a
    conspicuous short/long jump even when every isolated onset looks strong.
    """
    periods = []
    for position in range(unit_size):
        same_position = onsets[position:unit_size * repeat_count:unit_size]
        periods.extend(right - left
                       for left, right in zip(same_position, same_position[1:]))
    if not periods:
        return {"verified": False, "reason": "no-repeat-periods", "periods_ms": []}
    median = float(statistics.median(periods))
    if median <= 0.0:
        return {"verified": False, "reason": "nonpositive-period",
                "periods_ms": [round(value * 1000, 1) for value in periods]}
    spread = (max(periods) - min(periods)) / median
    verified = (0.30 <= median <= 4.0
                and min(periods) >= median * 0.62
                and max(periods) <= median * 1.38
                and spread <= maximum_relative_spread)
    return {
        "verified": verified,
        "reason": "consistent-musical-period" if verified else "period-outlier",
        "median_period_ms": round(median * 1000, 1),
        "relative_spread": round(spread, 4),
        "periods_ms": [round(value * 1000, 1) for value in periods],
    }


def _complete_repeated_unit(tokens: list[str]) -> tuple[int, int] | None:
    if len(tokens) < 3 or any(not token for token in tokens):
        return None
    for size in range(1, min(4, len(tokens) // 3) + 1):
        if len(tokens) % size:
            continue
        repetitions = len(tokens) // size
        unit = tokens[:size]
        if repetitions >= 3 and all(
                tokens[offset:offset + size] == unit
                for offset in range(0, len(tokens), size)):
            return size, repetitions
    return None


def _normalized_stable_words(words: list[dict]) -> list[tuple[str, dict]]:
    result = []
    for word in words:
        normalized = normalize_words(str(word.get("word", "")))
        if len(normalized) == 1:
            result.append((normalized[0], word))
    return result


def _find_local_stable_sequence(recognized: list[tuple[str, dict]],
                                tokens: list[str], reference_start: float,
                                maximum_start_delta: float = 1.25
                                ) -> list[dict] | None:
    candidates = []
    for index in range(len(recognized) - len(tokens) + 1):
        selected = recognized[index:index + len(tokens)]
        if [item[0] for item in selected] != tokens:
            continue
        words = [item[1] for item in selected]
        delta = abs(float(words[0]["start"]) - reference_start)
        if delta <= maximum_start_delta:
            candidates.append((delta, words))
    return min(candidates, key=lambda item: item[0])[1] if candidates else None


def _snap_to_activity_rise(signal: np.ndarray, prior: float,
                           sample_rate: int) -> float:
    """Move a multiresolution change score to the first 20 ms RMS rise."""
    hop = max(1, int(round(sample_rate * 0.005)))
    frame = max(64, int(round(sample_rate * 0.020)))
    first = max(0, int(round((prior - 0.06) * sample_rate)))
    last = min(len(signal), int(round((prior + 0.16) * sample_rate)))
    local = signal[first:last]
    if len(local) < frame * 2:
        return prior
    offsets = np.arange(0, len(local) - frame + 1, hop)
    rms = np.sqrt(np.asarray([
        np.mean(np.square(local[offset:offset + frame]))
        for offset in offsets
    ]) + 1e-12)
    times = (first + offsets + frame / 2) / sample_rate
    vicinity = np.flatnonzero((times >= prior - 0.05)
                              & (times <= prior + 0.07))
    if not len(vicinity):
        return prior
    minimum = int(vicinity[np.argmin(rms[vicinity])])
    upper = float(np.percentile(rms, 80))
    threshold = float(rms[minimum] + 0.35 * (upper - rms[minimum]))
    for index in range(minimum + 1, len(rms) - 1):
        if rms[index] >= threshold and rms[index + 1] >= threshold:
            return float(times[index])
    return prior


def _initial_phone_class(token: str, language: str) -> str:
    pronounced = _pronounced_initial_phone_class(token, language)
    if pronounced is not None:
        return pronounced
    value = token.lower()
    if not value:
        return "other"
    if value.startswith(("sch", "ch", "sh", "th", "ph")):
        return "fricative"
    first = value[0]
    if first in "pbtdkgcq":
        return "plosive"
    if first in "fvwßszxh":
        return "fricative"
    if first in "mn":
        return "nasal"
    if first in "lrjy":
        return "liquid"
    if first in "aeiouyäöü":
        return "vowel"
    return "other"


@lru_cache(maxsize=2048)
def _pronounced_initial_phone_class(token: str, language: str) -> str | None:
    """Classify the pronounced onset, falling back to orthography.

    This matters for languages with opaque spelling (for example English
    ``kn-`` or silent ``h``). eSpeak stays behind its command-line boundary;
    unsupported languages and local installation problems safely fall back to
    the established grapheme heuristic.
    """
    language_codes = {
        "de": "de", "en": "en-us", "fr": "fr-fr", "es": "es",
        "it": "it", "pt": "pt", "nl": "nl",
    }
    code = language_codes.get(language.lower().split("-")[0])
    if code is None:
        return None
    try:
        completed = subprocess.run(
            ["espeak-ng", "-q", "--ipa=1", "-v", code, token],
            check=True, capture_output=True, text=True, timeout=3)
    except (OSError, subprocess.SubprocessError):
        return None
    ipa = completed.stdout.strip().replace("ˈ", "").replace("ˌ", "")
    ipa = ipa.lstrip(" _-")
    if not ipa:
        return None
    if ipa.startswith(("tʃ", "dʒ", "ts", "dz")) or ipa[0] in "pbtdkɡqʔ":
        return "plosive"
    if ipa.startswith(("pf",)) or ipa[0] in "fvszʃʒçxɣhθðɸβ":
        return "fricative"
    if ipa[0] in "mnŋɲɳ":
        return "nasal"
    if ipa[0] in "lrɾɹjɥwʋ":
        return "liquid"
    if ipa[0] in "aeiouyɑɐɒæəɚɛɜɞɪɨɔɵʊʌʉɯœøɶɤɘɝ":
        return "vowel"
    return None


def _normal_word(value: str) -> str:
    normalized = normalize_words(str(value))
    return normalized[0] if len(normalized) == 1 else ""


def _previous_line_end(lines: list, index: int) -> float | None:
    for line in reversed(lines[:index]):
        if line.words:
            return float(line.words[-1]["end"])
    return None


def _next_line_start(lines: list, index: int) -> float | None:
    for line in lines[index + 1:]:
        if line.words:
            return float(line.words[0]["start"])
    return None


def _mono(audio) -> np.ndarray:
    signal = np.asarray(audio, dtype=np.float32)
    if signal.ndim > 1:
        signal = signal.mean(axis=tuple(range(1, signal.ndim)))
    return signal.reshape(-1)
