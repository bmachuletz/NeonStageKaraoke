from __future__ import annotations

import re
import unicodedata
from difflib import SequenceMatcher


def normalize_words(text: str) -> list[str]:
    text = unicodedata.normalize("NFKC", text).casefold()
    return re.findall(r"[^\W_]+(?:['’][^\W_]+)?", text, flags=re.UNICODE)


def _phonetic_form(word: str) -> str:
    value = word.replace("ä", "e").replace("ö", "e").replace("ü", "i").replace("ß", "s")
    for source, target in (("ph", "f"), ("sch", "s"), ("ch", "h"), ("ck", "k"),
                           ("tz", "z"), ("th", "t"), ("qu", "kw")):
        value = value.replace(source, target)
    value = re.sub(r"(.)\1+", r"\1", value)
    return value


def word_similarity(left: str, right: str) -> float:
    if left == right:
        return 1.0
    plain = SequenceMatcher(None, left, right).ratio()
    phonetic = SequenceMatcher(None, _phonetic_form(left), _phonetic_form(right)).ratio()
    return max(plain, phonetic)


def repeated_blocks(words: list[str], *, min_repetitions: int = 3) -> list[dict]:
    """Find consecutive repeated phrases, preferring the shortest repeating unit."""
    blocks: list[dict] = []
    cursor = 0
    while cursor < len(words):
        best = None
        for width in range(1, min(5, (len(words) - cursor) // min_repetitions + 1)):
            unit = words[cursor:cursor + width]
            repetitions = 1
            while words[cursor + repetitions * width:cursor + (repetitions + 1) * width] == unit:
                repetitions += 1
            if repetitions >= min_repetitions:
                best = {"start": cursor, "end": cursor + repetitions * width,
                        "unit": " ".join(unit), "unit_words": width, "repetitions": repetitions}
                break
        if best:
            blocks.append(best)
            cursor = best["end"]
        else:
            cursor += 1
    return blocks


def pair_repeated_blocks(expected: list[dict], recognized: list[dict], expected_count: int,
                         recognized_count: int) -> list[dict]:
    """Pair repetition runs by order, shape and relative transcript position."""
    pairs: list[dict] = []
    used: set[int] = set()
    for expected_block in expected:
        expected_position = expected_block["start"] / max(1, expected_count)
        candidates = []
        for index, recognized_block in enumerate(recognized):
            if index in used or expected_block["unit_words"] != recognized_block["unit_words"]:
                continue
            recognized_position = recognized_block["start"] / max(1, recognized_count)
            position_delta = abs(expected_position - recognized_position)
            repetition_delta = abs(expected_block["repetitions"] - recognized_block["repetitions"])
            if position_delta <= 0.12 and repetition_delta <= 2:
                candidates.append((position_delta + repetition_delta * 0.03, index, recognized_block))
        if candidates:
            _, index, recognized_block = min(candidates)
            used.add(index)
            pairs.append({
                "expected": expected_block,
                "recognized": recognized_block,
                "repetition_delta": recognized_block["repetitions"] - expected_block["repetitions"],
                "confidence": round(max(0.0, 1.0 - abs(expected_position -
                    recognized_block["start"] / max(1, recognized_count)) * 3.0 -
                    abs(recognized_block["repetitions"] - expected_block["repetitions"]) * 0.08), 3),
            })
    return pairs


def compare_transcripts(expected_text: str, recognized_text: str, *, min_repetitions: int = 3) -> dict:
    """Return a deterministic Levenshtein alignment and useful ASR diagnostics."""
    expected = normalize_words(expected_text)
    recognized = normalize_words(recognized_text)
    rows, cols = len(expected) + 1, len(recognized) + 1
    cost = [[0] * cols for _ in range(rows)]
    step = [[""] * cols for _ in range(rows)]
    for i in range(1, rows):
        cost[i][0], step[i][0] = i, "delete"
    for j in range(1, cols):
        cost[0][j], step[0][j] = j, "insert"
    for i in range(1, rows):
        for j in range(1, cols):
            similarity = word_similarity(expected[i - 1], recognized[j - 1])
            equal = similarity == 1.0
            approximate = not equal and similarity >= 0.72
            choices = [
                (cost[i - 1][j - 1] + (0 if equal else 0.35 if approximate else 1),
                 "match" if equal else "approximate" if approximate else "replace"),
                (cost[i - 1][j] + 1, "delete"),
                (cost[i][j - 1] + 1, "insert"),
            ]
            cost[i][j], step[i][j] = min(choices, key=lambda item: (item[0], item[1] != "match"))

    operations: list[dict] = []
    i, j = len(expected), len(recognized)
    while i or j:
        operation = step[i][j]
        if operation in {"match", "approximate", "replace"}:
            operations.append({"type": operation, "expected_index": i - 1, "recognized_index": j - 1,
                               "expected": expected[i - 1], "recognized": recognized[j - 1],
                               "similarity": round(word_similarity(expected[i - 1], recognized[j - 1]), 3)})
            i -= 1
            j -= 1
        elif operation == "delete":
            operations.append({"type": operation, "expected_index": i - 1, "expected": expected[i - 1]})
            i -= 1
        else:
            operations.append({"type": operation, "recognized_index": j - 1, "recognized": recognized[j - 1]})
            j -= 1
    operations.reverse()
    counts = {name: sum(op["type"] == name for op in operations)
              for name in ("match", "approximate", "replace", "insert", "delete")}
    similarity = (counts["match"] + 0.65 * counts["approximate"]) / max(1, max(len(expected), len(recognized)))
    differences = [op for op in operations if op["type"] != "match"]
    expected_repetitions = repeated_blocks(expected, min_repetitions=min_repetitions)
    recognized_repetitions = repeated_blocks(recognized, min_repetitions=min_repetitions)
    repetition_pairs = pair_repeated_blocks(
        expected_repetitions, recognized_repetitions, len(expected), len(recognized)
    )
    return {
        "expected_words": len(expected),
        "recognized_words": len(recognized),
        "matching_words": counts["match"],
        "similarity": round(similarity, 4),
        "edit_distance": round(cost[-1][-1], 2),
        "counts": counts,
        "repeated_blocks": {
            "expected": expected_repetitions,
            "recognized": recognized_repetitions,
            "paired": repetition_pairs,
        },
        "differences": differences[:100],
        "differences_truncated": len(differences) > 100,
        "operations": operations,
    }
