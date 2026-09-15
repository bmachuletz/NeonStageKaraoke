from __future__ import annotations

import re
import subprocess
from functools import lru_cache
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
                                sample_rate: int = 16000,
                                enforce_minimum_geometry: bool = False) -> dict:
    """Add syllables constrained to GPU-aligned word windows.

    CTC character spans are preferred. Remaining internal boundaries start
    from an orthographic prior and may be refined by conservative local audio
    change points; the word window itself is never changed here.
    """
    dictionary = _dictionary(language)
    words = syllables = dictionary_splits = acoustic_splits = sustained_endings = 0
    phonological_splits = phoneme_nucleus_windows = 0
    acoustic_attempts = acoustic_refinements = phoneme_splits = 0
    acoustic_confidence_sum = 0.0
    confidence_sum = 0.0
    minimum_geometry_repairs = 0
    for line in lines:
        for word in line.words:
            parts, used_dictionary = _split_word(
                str(word.get("word", "")), dictionary, language)
            parts, phonological_split = _repair_split_from_phoneme_nuclei(
                str(word.get("word", "")), parts, word, language)
            phonological_splits += int(phonological_split)
            start = float(word["start"])
            end = max(start, float(word.get("end", start)))
            confidence = _confidence(
                parts, end - start, used_dictionary or phonological_split)
            nucleus_timing = _time_parts_from_phoneme_nuclei(
                parts, word, start, end, confidence)
            phoneme_timing = nucleus_timing or _time_parts_from_phonemes(
                parts, word, start, end, confidence, language)
            acoustic = phoneme_timing or _time_parts_from_ctc(
                parts, word, start, end, confidence)
            core_end = min(end, max(start, float(word.get("acoustic_end", end))))
            timed_parts = acoustic or _time_parts(parts, start, core_end, confidence)
            if nucleus_timing is not None:
                # Vowel-nucleus windows deliberately leave articulation gaps.
                # A shared-boundary refiner would join them again and erase
                # the crisp karaoke phrasing this stronger phone path proves.
                acoustic_summary = {
                    "attempted_boundaries": 0,
                    "refined_boundaries": 0,
                    "mean_confidence": 0.0,
                }
            else:
                timed_parts, acoustic_summary = refine_syllable_boundaries(
                    audio, timed_parts, sample_rate=sample_rate,
                    prior_is_acoustic=acoustic is not None)
            # A noisy release detector can place ``acoustic_end`` before the
            # word has enough time to articulate its known syllables. Keep the
            # measured release as evidence, but never use an impossible core
            # interval as the parent of the syllable geometry.
            geometry_repaired = False
            if enforce_minimum_geometry and nucleus_timing is None:
                geometry_end = max(
                    core_end,
                    min(end, start + len(timed_parts) * 0.055))
                timed_parts, geometry_repaired = _enforce_minimum_syllable_geometry(
                    timed_parts, start, geometry_end)
            minimum_geometry_repairs += int(geometry_repaired)
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
            if nucleus_timing is not None:
                word["syllable_method"] = "phoneme-vowel-articulation-windows-v1"
            elif acoustic_summary["refined_boundaries"]:
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
            phoneme_nucleus_windows += int(
                nucleus_timing is not None and len(parts) > 1)
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
        "phoneme_vowel_articulation_windows": phoneme_nucleus_windows,
        "phoneme_corrected_text_splits": phonological_splits,
        "acoustic_change_point_enabled": audio is not None,
        "acoustic_change_point_attempts": acoustic_attempts,
        "acoustic_change_point_refinements": acoustic_refinements,
        "acoustic_change_point_mean_confidence": round(
            acoustic_confidence_sum / acoustic_refinements, 3)
            if acoustic_refinements else 0.0,
        "sustained_endings": sustained_endings,
        "minimum_geometry_repairs": minimum_geometry_repairs,
        "minimum_geometry_safety_enabled": enforce_minimum_geometry,
        "mean_confidence": round(confidence_sum / words, 3) if words else 0.0,
    }


def _time_parts_from_phoneme_nuclei(
        parts: list[str], word: dict, start: float, end: float,
        confidence: float) -> list[dict] | None:
    """Create separated karaoke articulation windows from measured vowels.

    A written syllable is perceived mainly at its vowel nucleus.  In clipped
    singing, consonants and real rests between nuclei must not force one
    continuous highlight across the entire word.  The first syllable keeps
    the lexical word onset, internal syllables begin at their measured vowel,
    and non-final syllables end with that vowel.  The final syllable retains
    the word release so a genuine held ending still fills naturally.

    This path is available only for an attached IPA alignment with exactly one
    measured nucleus per textual syllable.  Orthographic and weak fallback
    splits retain the established contiguous geometry.
    """
    phonemes = word.get("phonemes") or word.get("syllable_phonemes")
    phone_confidence = float(word.get(
        "phoneme_confidence", word.get("syllable_phoneme_confidence", 0.0)))
    if (not isinstance(phonemes, list) or len(parts) < 2
            or phone_confidence < 0.25):
        return None
    nuclei = [phone for phone in phonemes
              if _is_vowel_phone(str(phone.get("phone", "")))]
    if len(nuclei) != len(parts):
        return None
    result = []
    previous_end = start
    for index, (part, nucleus) in enumerate(zip(parts, nuclei)):
        nucleus_start = max(start, min(end, float(nucleus["start"])))
        nucleus_end = max(nucleus_start, min(end, float(nucleus["end"])))
        part_start = start if index == 0 else nucleus_start
        part_end = end if index == len(parts) - 1 else nucleus_end
        # Frame-level vowel spans below 45 ms are too unstable for readable
        # Stage geometry. Fall back atomically rather than mixing window types
        # inside one word.
        if (part_end - part_start < 0.045
                or part_start < previous_end - 0.001):
            return None
        result.append({
            "text": part,
            "start": round(part_start, 3),
            "end": round(part_end, 3),
            "confidence": round(min(confidence, max(
                0.25, phone_confidence)), 2),
            "index": index,
            "boundary_source": "phoneme-vowel-articulation-window",
        })
        previous_end = part_end
    if not any(float(right["start"]) - float(left["end"]) >= 0.025
               for left, right in zip(result, result[1:])):
        # Connected legato gains nothing from a special representation and is
        # better served by the mature shared-boundary path.
        return None
    return result


def _enforce_minimum_syllable_geometry(
        parts: list[dict], start: float, end: float,
        *, maximum_floor: float = 0.055) -> tuple[list[dict], bool]:
    """Project impossible child boundaries back into their word window.

    Forced phoneme paths and local change points are independent observations,
    but both can occasionally leave a 0--30 ms edge syllable.  Keep their
    boundaries whenever possible and move only as far as needed to give every
    syllable a readable, physically plausible interval.  This never changes
    the owning word's timing.
    """
    if len(parts) < 2 or end <= start:
        return parts, False
    floor = min(maximum_floor, (end - start) / len(parts))
    if floor <= 0.0:
        return parts, False
    durations = [float(part["end"]) - float(part["start"]) for part in parts]
    if all(duration + 0.001 >= floor for duration in durations):
        return parts, False

    boundaries = []
    previous = start
    for index, part in enumerate(parts[:-1]):
        lower = max(start + (index + 1) * floor, previous + floor)
        upper = end - (len(parts) - index - 1) * floor
        boundary = min(upper, max(lower, float(part["end"])))
        boundaries.append(boundary)
        previous = boundary
    edges = [start, *boundaries, end]
    for index, part in enumerate(parts):
        part["start"] = round(edges[index], 3)
        part["end"] = round(edges[index + 1], 3)
        part["minimum_geometry_repair"] = True
    return parts, True


def _time_parts_from_phonemes(parts: list[str], word: dict, start: float, end: float,
                              confidence: float, language: str) -> list[dict] | None:
    phonemes = word.get("phonemes") or word.get("syllable_phonemes")
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
    phone_confidence = float(word.get(
        "phoneme_confidence", word.get("syllable_phoneme_confidence", confidence)))
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
    # A truncated or weak phone path can place the next vowel nucleus exactly
    # at the word end.  Such a path would collapse the final written syllable
    # to 0 ms and is less informative than the CTC or duration fallback.  Do
    # not clamp it to an invented nearby time; reject the whole phone prior so
    # the next independent source gets a chance.
    average_part = (end - start) / len(parts)
    minimum_part = min(0.055, max(0.025, average_part * 0.28))
    if any(float(part["end"]) - float(part["start"]) < minimum_part
           for part in result):
        return None
    return result


def _repair_split_from_phoneme_nuclei(value: str, parts: list[str], word: dict,
                                      language: str) -> tuple[list[str], bool]:
    """Use measured IPA nuclei to correct typographic hyphenation counts.

    Pyphen intentionally models written hyphenation.  English words such as
    ``away`` and ``apart`` may therefore remain unsplit even though the IPA
    path contains two separately sung vowel nuclei.  The IPA model determines
    only the expected count; grapheme text still comes from the conservative
    local splitter so the canonical spelling is never replaced.
    """
    if language.lower().split("-")[0] != "en":
        return parts, False
    phonemes = word.get("phonemes") or word.get("syllable_phonemes")
    expected = (sum(_is_vowel_phone(str(phone.get("phone", "")))
                    for phone in phonemes)
                if isinstance(phonemes, list) and phonemes else 0)
    source = "measured-ipa-vowel-nucleus-count"
    if expected <= len(parts):
        # A locally accepted acoustic phone path is deliberately optional:
        # difficult singing may reject CTC even though the canonical word's
        # pronunciation is unambiguous.  Query the same eSpeak pronunciation
        # used by the IPA aligner so textual syllable counts do not silently
        # fall back to typographic Pyphen hyphenation (away/apart are common
        # examples).  This changes text subdivision only, never word timing.
        expected = _pronunciation_syllable_count(value, language)
        source = "espeak-ipa-vowel-nucleus-count"
    if expected <= len(parts):
        return parts, False
    match = EDGE_RE.match(value)
    if not match:
        return parts, False
    prefix, core, suffix = match.groups()
    fallback = _heuristic_split(core)
    if len(fallback) != expected:
        return parts, False
    fallback[0] = prefix + fallback[0]
    fallback[-1] += suffix
    word["syllable_split_source"] = source
    return fallback, True


@lru_cache(maxsize=8192)
def _pronunciation_syllable_count(value: str, language: str) -> int:
    """Return a conservative eSpeak IPA nucleus count for one English word.

    eSpeak emits model-compatible phones separated by underscores.  Counting
    vowel-bearing phones rather than vowel characters keeps diphthongs such as
    /eɪ/ together and mirrors the representation used by the CTC aligner.
    Missing binaries or unknown words simply leave the existing split intact.
    """
    match = EDGE_RE.match(value)
    core = match.group(2) if match else value
    if not core or not any(character.isalpha() for character in core):
        return 0
    code = "en-us" if language.lower().split("-")[0] == "en" else language
    try:
        completed = subprocess.run(
            ["espeak-ng", "-q", "--ipa=1", "-v", code, core],
            check=True, capture_output=True, text=True, timeout=2)
    except (FileNotFoundError, subprocess.SubprocessError):
        return 0
    phones = [phone.replace("ˈ", "").replace("ˌ", "").replace("\u200d", "")
              for phone in completed.stdout.strip().split("_")]
    return sum(_is_vowel_phone(phone) for phone in phones if phone)


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
    # A dictionary-backed single nucleus is still a useful karaoke unit: its
    # acoustic word window tells the Stage when to attack and how long to hold.
    # The old sub-threshold value made every one-syllable EasyAligner word fall
    # back to a continuous whole-word sweep.
    value = 0.84 if dictionary_used else 0.58
    if duration < 0.09 * len(parts):
        value -= 0.2
    return round(max(0.25, min(0.9, value)), 2)
