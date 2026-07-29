from __future__ import annotations

from .transcript_match import normalize_words


def _timed_tokens(words: list[dict]) -> list[dict]:
    result: list[dict] = []
    for word in words:
        for _token in normalize_words(str(word.get("word", ""))):
            result.append(word)
    return result


def recover_deleted_fragments(audio, lines: list, stable_words: list[dict], comparison: dict,
                              aligner, language: str, *, minimum_words: int = 2,
                              maximum_words: int = 8) -> dict:
    """Re-align a short lyric fragment that whole-song ASR skipped.

    Dense backing vocals are sometimes timestamped as the neighbouring phrase
    by Whisper. Forcing the whole lyric line then compresses the missing words
    into the old boundary. This pass gives only the deleted text and its bounded
    local audio window to the word aligner.
    """
    timed = _timed_tokens(stable_words)
    operations = {
        int(item["expected_index"]): item
        for item in comparison.get("operations", [])
        if "expected_index" in item
    }
    expected_cursor = 0
    diagnostics: list[dict] = []
    recovered_fragments = recovered_words = 0
    duration = len(audio) / 16000

    for line_index, line in enumerate(lines):
        tokens = normalize_words(line.text)
        first_expected = expected_cursor
        expected_cursor += len(tokens)
        if not line.words or len(line.words) != len(tokens):
            continue
        deleted_offsets = [offset for offset in range(len(tokens))
                           if operations.get(first_expected + offset, {}).get("type") == "delete"]
        runs: list[tuple[int, int]] = []
        for offset in deleted_offsets:
            if not runs or offset != runs[-1][1]:
                runs.append((offset, offset + 1))
            else:
                runs[-1] = (runs[-1][0], offset + 1)

        for first, end in runs:
            count = end - first
            if not minimum_words <= count <= maximum_words:
                continue
            left_operation = operations.get(first_expected + first - 1) if first > 0 else None
            right_operation = operations.get(first_expected + end) if end < len(tokens) else None
            left_word = _recognized_word(left_operation, timed)
            right_word = _recognized_word(right_operation, timed)
            lower = (float(left_word["end"]) - 0.12 if left_word is not None else
                     max(0.0, float(line.source_timestamp or line.timestamp) - 0.2))
            if right_word is not None and float(right_word["start"]) > lower + 0.35:
                upper = float(right_word["start"]) + 0.12
            elif line_index + 1 < len(lines) and lines[line_index + 1].source_timestamp is not None:
                # A right-hand ASR word starting at the left boundary usually
                # swallowed this chorus fragment. The next trusted LRC line is
                # the safer ownership boundary.
                upper = float(lines[line_index + 1].source_timestamp) + 0.25
            else:
                upper = min(duration, max(float(line.words[-1]["end"]) + 0.5,
                                          lower + count * 0.45))
            lower, upper = max(0.0, lower), min(duration, upper)
            target_text = " ".join(str(word.get("word", "")) for word in line.words[first:end])
            entry = {"line": line_index + 1, "first_word": first + 1, "words": count,
                     "text": target_text, "window_start": round(lower, 3),
                     "window_end": round(upper, 3)}
            if upper - lower < max(0.45, count * 0.09) or upper - lower > 8.0:
                diagnostics.append({**entry, "status": "invalid-local-window"})
                continue
            chunk = audio[int(lower * 16000):int(upper * 16000)]
            aligned = aligner.align_text(chunk, target_text, language)
            if len(aligned) != count:
                diagnostics.append({**entry, "status": "word-count-mismatch",
                                    "aligned_words": len(aligned)})
                continue
            replacements = []
            for replacement in aligned:
                value = dict(replacement)
                value["start"] = round(float(value["start"]) + lower, 3)
                value["end"] = round(float(value["end"]) + lower, 3)
                replacements.append(value)
            valid = (all(float(word["end"]) > float(word["start"]) for word in replacements)
                     and all(float(right["start"]) >= float(left["end"]) - 0.03
                             for left, right in zip(replacements, replacements[1:]))
                     and float(replacements[0]["start"]) >= lower - 0.05
                     and float(replacements[-1]["end"]) <= upper + 0.05)
            if first > 0:
                valid = valid and float(replacements[0]["start"]) >= float(line.words[first - 1]["end"]) - 0.08
            if end < len(line.words):
                valid = valid and float(replacements[-1]["end"]) <= float(line.words[end]["start"]) + 0.08
            if not valid:
                diagnostics.append({**entry, "status": "implausible-local-alignment",
                                    "aligned_start": replacements[0]["start"],
                                    "aligned_end": replacements[-1]["end"]})
                continue
            old_start = float(line.words[first]["start"])
            old_end = float(line.words[end - 1]["end"])
            if max(abs(float(replacements[0]["start"]) - old_start),
                   abs(float(replacements[-1]["end"]) - old_end)) < 0.08:
                diagnostics.append({**entry, "status": "no-material-change"})
                continue
            for word, replacement in zip(line.words[first:end], replacements):
                word["start"] = replacement["start"]
                word["end"] = replacement["end"]
                word["timing_source"] = "targeted-deleted-fragment-qwen"
                word["fragment_window_start"] = round(lower, 3)
                word["fragment_window_end"] = round(upper, 3)
            line.timestamp = float(line.words[0]["start"])
            recovered_fragments += 1
            recovered_words += count
            diagnostics.append({**entry, "status": "recovered",
                                "old_start": round(old_start, 3), "old_end": round(old_end, 3),
                                "aligned_start": replacements[0]["start"],
                                "aligned_end": replacements[-1]["end"]})

    return {"enabled": True, "method": "bounded-deleted-fragment-qwen-v1",
            "recovered_fragments": recovered_fragments, "recovered_words": recovered_words,
            "diagnostics": diagnostics}


def _recognized_word(operation: dict | None, timed: list[dict]) -> dict | None:
    if not operation or operation.get("type") not in {"match", "approximate"}:
        return None
    index = int(operation.get("recognized_index", -1))
    return timed[index] if 0 <= index < len(timed) else None
