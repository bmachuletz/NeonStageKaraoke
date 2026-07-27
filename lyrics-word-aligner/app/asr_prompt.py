from __future__ import annotations

from collections import Counter
import os
import re


_TOKEN_RE = re.compile(r"[^\W_]+(?:['’][^\W_]+)?", re.UNICODE)
_COMMON = {
    "a", "aber", "alle", "als", "am", "an", "and", "are", "auf", "aus", "bei",
    "bin", "bis", "but", "da", "das", "dass", "dein", "dem", "den", "der", "die",
    "do", "du", "ein", "eine", "er", "es", "for", "für", "hab", "hat", "have",
    "ich", "im", "in", "is", "ist", "it", "ja", "kein", "keine", "man", "me",
    "mein", "mit", "my", "nicht", "no", "noch", "nur", "of", "on", "or", "sie",
    "so", "the", "to", "und", "uns", "von", "was", "we", "wenn", "wie", "wir",
    "with", "you", "zu", "zum",
}


def _enabled() -> bool:
    return os.getenv("LRC_ASR_PROMPT", "true").strip().lower() in {
        "1", "true", "yes", "on",
    }


def build_asr_prompt(
    lyric_texts,
    song_name: str,
    language: str,
    *,
    max_chars: int = 1600,
) -> tuple[str | None, dict]:
    """Build a bounded vocabulary hint without leaking lyric order to ASR.

    Supplying complete ordered lyrics to a generative transcriber can make it
    repeat or anticipate a chorus. Unique vocabulary is enough to help with
    names, compounds, slang and uncommon words while acoustic evidence remains
    authoritative.
    """
    if not _enabled() or max_chars <= 0:
        return None, {"enabled": False, "method": "distinct-vocabulary-v1"}

    occurrences: list[tuple[str, str, int]] = []
    counts: Counter[str] = Counter()
    first_seen: dict[str, int] = {}
    display: dict[str, str] = {}
    for text in lyric_texts:
        for token in _TOKEN_RE.findall(str(text)):
            key = token.casefold()
            counts[key] += 1
            if key not in first_seen:
                first_seen[key] = len(first_seen)
                display[key] = token

    # Rare and distinctive terms rank first. Frequency is deliberately only a
    # weak signal: repeated chorus vocabulary is useful, but never duplicated.
    for key, token in display.items():
        distinctive = (
            min(len(key), 14) * 2
            + (8 if key not in _COMMON else -12)
            + (5 if len(key) >= 8 else 0)
            + (4 if any(character.isdigit() for character in token) else 0)
            + (3 if any(character in token for character in "'’-") else 0)
            + min(counts[key], 4)
        )
        occurrences.append((key, token, distinctive))
    occurrences.sort(key=lambda item: (-item[2], first_seen[item[0]]))

    clean_song_name = " ".join(_TOKEN_RE.findall(song_name))[:180]
    language_hint = language if language.strip().lower() != "auto" else "auto-detect"
    prefix = (
        f"Karaoke song: {clean_song_name}. Language: {language_hint}. "
        "Vocabulary hints only; transcribe only words that are actually audible: "
    )
    extra = " ".join(_TOKEN_RE.findall(os.getenv("LRC_ASR_EXTRA_CONTEXT", "")))[:240]
    if extra:
        prefix += f"Additional context: {extra}. Vocabulary: "

    chosen: list[str] = []
    length = len(prefix)
    for _key, token, _score in occurrences:
        addition = len(token) + (2 if chosen else 0)
        if length + addition + 1 > max_chars:
            continue
        chosen.append(token)
        length += addition
    prompt = (prefix + ", ".join(chosen)).rstrip() if chosen or clean_song_name else None
    if prompt and len(prompt) > max_chars:
        prompt = prompt[:max_chars].rstrip(" ,")

    return prompt, {
        "enabled": bool(prompt),
        "method": "distinct-vocabulary-v1",
        "characters": len(prompt or ""),
        "vocabulary_terms": len(chosen),
        "available_unique_terms": len(display),
        "song_context": bool(clean_song_name),
        "extra_context": bool(extra),
        "ordered_lyrics_supplied": False,
    }
