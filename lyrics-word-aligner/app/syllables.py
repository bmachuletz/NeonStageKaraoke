from __future__ import annotations

import re
from typing import Iterable

try:
    import pyphen
except ImportError:  # The small heuristic keeps old/local installations usable.
    pyphen = None


LANGUAGES = {
    "de": "de_DE", "de-de": "de_DE",
    "en": "en_US", "en-us": "en_US", "en-gb": "en_GB",
    "fr": "fr_FR", "es": "es_ES", "it": "it_IT",
    "pt": "pt_PT", "nl": "nl_NL",
}
VOWELS = frozenset("aeiouyäöüàáâãåæèéêëìíîïòóôõùúûýÿœ")
EDGE_RE = re.compile(r"^([^\wÀ-ɏ]*)(.*?)([^\wÀ-ɏ]*)$", re.UNICODE)


def enrich_lines_with_syllables(lines: Iterable, language: str) -> dict:
    """Add orthographic syllables constrained to GPU-aligned word windows.

    These are deliberately marked non-acoustic: Qwen determines each word's
    acoustic interval on the GPU; syllable boundaries are estimated inside it.
    """
    dictionary = _dictionary(language)
    words = syllables = dictionary_splits = acoustic_splits = sustained_endings = 0
    confidence_sum = 0.0
    for line in lines:
        for word in line.words:
            parts, used_dictionary = _split_word(str(word.get("word", "")), dictionary)
            start = float(word["start"])
            end = max(start, float(word.get("end", start)))
            confidence = _confidence(parts, end - start, used_dictionary)
            acoustic = _time_parts_from_ctc(parts, word, start, end, confidence)
            core_end = min(end, max(start, float(word.get("acoustic_end", end))))
            timed_parts = acoustic or _time_parts(parts, start, core_end, confidence)
            # ASR/CTC usually timestamps the lexical core. If vocal activity
            # proves that the singer holds the release, only the final syllable
            # owns that sustain; stretching every syllable makes karaoke
            # highlighting visibly early in the middle of long words.
            if timed_parts and end > core_end + 0.001:
                timed_parts[-1]["end"] = round(end, 3)
                timed_parts[-1]["sustain_extension_ms"] = round((end - core_end) * 1000)
                sustained_endings += 1
            word["syllables"] = timed_parts
            word["syllable_confidence"] = confidence
            word["syllable_method"] = "ctc-character-boundaries-v1" if acoustic else "orthographic-duration-v2"
            words += 1
            syllables += len(parts)
            dictionary_splits += int(used_dictionary and len(parts) > 1)
            acoustic_splits += int(acoustic is not None and len(parts) > 1)
            confidence_sum += confidence
    return {
        "version": 2,
        "method": "ctc-character-boundaries-with-orthographic-fallback-v2",
        "acoustic_word_boundaries": True,
        "acoustic_syllable_boundaries": acoustic_splits > 0,
        "language": language,
        "words": words,
        "syllables": syllables,
        "dictionary_splits": dictionary_splits,
        "acoustic_splits": acoustic_splits,
        "sustained_endings": sustained_endings,
        "mean_confidence": round(confidence_sum / words, 3) if words else 0.0,
    }


def _time_parts_from_ctc(parts: list[str], word: dict, start: float, end: float,
                         confidence: float) -> list[dict] | None:
    characters = word.get("ctc_characters")
    if not isinstance(characters, list) or not characters or len(parts) < 2:
        return None
    counts = [sum(character.isalpha() or character.isdigit() for character in part) for part in parts]
    if any(count <= 0 for count in counts) or sum(counts) != len(characters):
        return None
    boundaries = [start]
    cursor = 0
    for count in counts[:-1]:
        cursor += count
        left = float(characters[cursor - 1]["end"])
        right = float(characters[cursor]["start"])
        boundaries.append(max(boundaries[-1], min(end, (left + right) / 2)))
    boundaries.append(end)
    result = []
    for index, part in enumerate(parts):
        result.append({
            "text": part,
            "start": round(boundaries[index], 3),
            "end": round(max(boundaries[index], boundaries[index + 1]), 3),
            "confidence": confidence,
            "index": index,
            "boundary_source": "ctc-character",
        })
    return result


def _dictionary(language: str):
    if pyphen is None:
        return None
    code = LANGUAGES.get(language.lower(), language.replace("-", "_"))
    try:
        return pyphen.Pyphen(lang=code)
    except KeyError:
        return None


def _split_word(value: str, dictionary) -> tuple[list[str], bool]:
    match = EDGE_RE.match(value)
    if not match:
        return [value], False
    prefix, core, suffix = match.groups()
    if not core or not any(character.isalpha() for character in core):
        return [value], False
    split = dictionary.inserted(core).split("-") if dictionary is not None else _heuristic_split(core)
    split = [part for part in split if part]
    if not split:
        split = [core]
    split[0] = prefix + split[0]
    split[-1] += suffix
    return split, dictionary is not None


def _heuristic_split(core: str) -> list[str]:
    """Low-confidence fallback used only when no Pyphen dictionary is present."""
    nuclei = []
    for index, character in enumerate(core.lower()):
        if character in VOWELS and (not nuclei or index > nuclei[-1][-1] + 1):
            nuclei.append([index])
        elif character in VOWELS and nuclei:
            nuclei[-1].append(index)
    if len(nuclei) < 2:
        return [core]
    boundaries = []
    for left, right in zip(nuclei, nuclei[1:]):
        consonants = right[0] - left[-1] - 1
        boundaries.append(left[-1] + 1 + (1 if consonants > 1 else 0))
    return [core[start:end] for start, end in zip([0, *boundaries], [*boundaries, len(core)]) if end > start]


def _time_parts(parts: list[str], start: float, end: float, confidence: float) -> list[dict]:
    weights = [_weight(part) for part in parts]
    total = sum(weights) or 1.0
    duration = max(0.0, end - start)
    cursor = start
    result = []
    for index, (part, weight) in enumerate(zip(parts, weights)):
        part_end = end if index == len(parts) - 1 else cursor + duration * weight / total
        result.append({
            "text": part,
            "start": round(cursor, 3),
            "end": round(max(cursor, part_end), 3),
            "confidence": confidence,
            "index": index,
        })
        cursor = part_end
    return result


def _weight(part: str) -> float:
    letters = [character.lower() for character in part if character.isalpha()]
    return max(1.0, len(letters) + sum(character in VOWELS for character in letters) * 0.45)


def _confidence(parts: list[str], duration: float, dictionary_used: bool) -> float:
    value = 0.84 if dictionary_used and len(parts) > 1 else 0.58
    if duration < 0.09 * len(parts):
        value -= 0.2
    return round(max(0.25, min(0.9, value)), 2)
