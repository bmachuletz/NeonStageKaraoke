from __future__ import annotations

import re
from typing import Iterable

from .acoustic_boundaries import refine_syllable_boundaries

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
IPA_VOWELS = frozenset("aeiouyɑɐɒæəɚɛɜɞɪɨɔɵʊʌʉɯœøɶɤɘɝ")
ARPABET_VOWELS = frozenset({
    "aa", "ae", "ah", "ao", "aw", "ax", "ay", "eh", "er", "ey",
    "ih", "iy", "ow", "oy", "uh", "uw",
})
EDGE_RE = re.compile(r"^([^\wÀ-ɏ]*)(.*?)([^\wÀ-ɏ]*)$", re.UNICODE)


def enrich_lines_with_syllables(lines: Iterable, language: str, audio=None,
                                sample_rate: int = 16000) -> dict:
    """Add syllables constrained to GPU-aligned word windows.

    CTC character spans are preferred. Remaining internal boundaries start
    from an orthographic prior and may be refined by conservative local audio
    change points; the word window itself is never changed here.
    """
    dictionary = _dictionary(language)
    words = syllables = dictionary_splits = acoustic_splits = sustained_endings = 0
    acoustic_attempts = acoustic_refinements = phoneme_splits = 0
    acoustic_confidence_sum = 0.0
    confidence_sum = 0.0
    for line in lines:
        for word in line.words:
            parts, used_dictionary = _split_word(
                str(word.get("word", "")), dictionary, language)
            start = float(word["start"])
            end = max(start, float(word.get("end", start)))
            confidence = _confidence(parts, end - start, used_dictionary)
            phoneme_timing = _time_parts_from_phonemes(
                parts, word, start, end, confidence, language)
            acoustic = phoneme_timing or _time_parts_from_ctc(
                parts, word, start, end, confidence)
            core_end = min(end, max(start, float(word.get("acoustic_end", end))))
            timed_parts = acoustic or _time_parts(parts, start, core_end, confidence)
            timed_parts, acoustic_summary = refine_syllable_boundaries(
                audio, timed_parts, sample_rate=sample_rate,
                prior_is_acoustic=acoustic is not None)
            acoustic_attempts += acoustic_summary["attempted_boundaries"]
            acoustic_refinements += acoustic_summary["refined_boundaries"]
            acoustic_confidence_sum += (acoustic_summary["mean_confidence"]
                                        * acoustic_summary["refined_boundaries"])
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
            if acoustic_summary["refined_boundaries"]:
                word["syllable_method"] = (
                    "phoneme-onsets-plus-local-change-v1.1" if phoneme_timing else
                    "local-acoustic-change-point-v1")
            else:
                word["syllable_method"] = (
                    "phoneme-syllable-onsets-v1.1" if phoneme_timing else
                    "ctc-character-boundaries-v1" if acoustic else
                    "orthographic-duration-v2")
            words += 1
            syllables += len(parts)
            dictionary_splits += int(used_dictionary and len(parts) > 1)
            acoustic_splits += int(acoustic is not None and len(parts) > 1)
            phoneme_splits += int(phoneme_timing is not None and len(parts) > 1)
            confidence_sum += confidence
    return {
        "version": 5,
        "method": "ctc-phoneme-onsets-and-joint-acoustic-boundaries-v5",
        "acoustic_word_boundaries": True,
        "acoustic_syllable_boundaries": acoustic_splits > 0 or acoustic_refinements > 0,
        "language": language,
        "words": words,
        "syllables": syllables,
        "dictionary_splits": dictionary_splits,
        "acoustic_splits": acoustic_splits,
        "phoneme_nucleus_splits": phoneme_splits,
        "acoustic_change_point_enabled": audio is not None,
        "acoustic_change_point_attempts": acoustic_attempts,
        "acoustic_change_point_refinements": acoustic_refinements,
        "acoustic_change_point_mean_confidence": round(
            acoustic_confidence_sum / acoustic_refinements, 3)
            if acoustic_refinements else 0.0,
        "sustained_endings": sustained_endings,
        "mean_confidence": round(confidence_sum / words, 3) if words else 0.0,
    }


def _time_parts_from_phonemes(parts: list[str], word: dict, start: float, end: float,
                              confidence: float, language: str) -> list[dict] | None:
    phonemes = word.get("phonemes")
    if not isinstance(phonemes, list) or not phonemes or len(parts) < 2:
        return None
    nucleus_indices = [index for index, phone in enumerate(phonemes)
                       if _is_vowel_phone(str(phone.get("phone", "")))]
    if len(nucleus_indices) != len(parts):
        return None
    boundaries = [start]
    for syllable_index, nucleus_index in enumerate(nucleus_indices[1:], start=1):
        previous_nucleus = nucleus_indices[syllable_index - 1]
        consonant_indices = list(range(previous_nucleus + 1, nucleus_index))
        onset_units = _leading_onset_units(parts[syllable_index], language)
        onset_count = min(len(consonant_indices), onset_units)
        boundary_index = (nucleus_index - onset_count
                          if onset_count else nucleus_index)
        boundary = max(boundaries[-1], min(
            end, float(phonemes[boundary_index]["start"])))
        boundaries.append(boundary)
    boundaries.append(end)
    phone_confidence = float(word.get("phoneme_confidence", confidence))
    result = []
    for index, part in enumerate(parts):
        result.append({
            "text": part,
            "start": round(boundaries[index], 3),
            "end": round(max(boundaries[index], boundaries[index + 1]), 3),
            "confidence": round(min(confidence, max(0.25, phone_confidence)), 2),
            "index": index,
            "boundary_source": "phoneme-syllable-onset",
        })
    return result


def _leading_onset_units(part: str, language: str) -> int:
    """Approximate grapheme units before the syllable nucleus.

    Pyphen supplies the textual syllable split. Counting common multi-letter
    consonants as one lets the phone sequence place the boundary at the onset
    consonant instead of delaying highlighting until the following vowel.
    """
    core = "".join(character.lower() for character in part if character.isalpha())
    leading = ""
    for character in core:
        if character in VOWELS:
            break
        leading += character
    if not leading:
        return 0
    base_language = language.lower().split("-")[0]
    clusters = ({"sch", "ch", "ph", "th"} if base_language == "de"
                else {"sch", "sh", "ch", "ph", "th", "wh", "ng"})
    units = 0
    cursor = 0
    while cursor < len(leading):
        match = next((cluster for cluster in sorted(clusters, key=len, reverse=True)
                      if leading.startswith(cluster, cursor)), None)
        cursor += len(match) if match else 1
        units += 1
    return units


def _is_vowel_phone(phone: str) -> bool:
    normalized = phone.lower().strip("0123456789ˈˌ.'-_")
    return normalized in ARPABET_VOWELS or any(character in IPA_VOWELS
                                                for character in normalized)


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


def _split_word(value: str, dictionary, language: str = "") -> tuple[list[str], bool]:
    match = EDGE_RE.match(value)
    if not match:
        return [value], False
    prefix, core, suffix = match.groups()
    if not core or not any(character.isalpha() for character in core):
        return [value], False
    split = dictionary.inserted(core).split("-") if dictionary is not None else _heuristic_split(core)
    split = [part for part in split if part]
    split = _merge_language_nuclei(split, language)
    if not split:
        split = [core]
    split[0] = prefix + split[0]
    split[-1] += suffix
    return split, dictionary is not None


def _merge_language_nuclei(parts: list[str], language: str) -> list[str]:
    """Undo hyphenation boundaries which split one sung vowel nucleus.

    Pyphen provides typographic hyphenation, not phonological syllables. German
    may therefore yield ``Trä-um`` although ``äu`` is one diphthong. Karaoke
    must keep that nucleus together or the Stage highlights letters as if they
    were separately singable syllables.
    """
    if language.lower().split("-")[0] != "de" or len(parts) < 2:
        return parts
    nuclei = {"au", "ai", "ei", "eu", "äu", "ie"}
    merged: list[str] = []
    for part in parts:
        if (merged and merged[-1] and part
                and (merged[-1][-1] + part[0]).lower() in nuclei):
            merged[-1] += part
        else:
            merged.append(part)
    return merged


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
