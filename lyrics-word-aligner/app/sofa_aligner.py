from __future__ import annotations

import csv
import difflib
import os
import re
import subprocess
import tempfile
from pathlib import Path

import soundfile as sf

from .ctc_aligner import HEURISTIC_SOURCES


DIGIT_WORDS = {
    "0": "zero", "1": "one", "2": "two", "3": "three", "4": "four",
    "5": "five", "6": "six", "7": "seven", "8": "eight", "9": "nine",
}


def _word_groups(text: str) -> list[list[str]]:
    """Map each displayed lyric token to one or more SOFA dictionary tokens."""
    tokens = re.findall(r"[a-z]+(?:'[a-z]+)?|\d+", text.lower().replace("’", "'"))
    return [[DIGIT_WORDS[digit] for digit in token] if token.isdigit() else [token]
            for token in tokens]


def _words(text: str) -> list[str]:
    return [word for group in _word_groups(text) for word in group]


def _continuous_sections(lines: list) -> list[list]:
    sections, current = [], []
    for line in lines:
        if not line.words:
            continue
        if current and (float(line.words[0]["start"]) - float(current[-1].words[-1]["end"]) > 1.2
                        or float(line.words[-1]["end"]) - float(current[0].words[0]["start"]) > 30.0):
            sections.append(current)
            current = []
        current.append(line)
    if current:
        sections.append(current)
    return sections


def _karaoke_word_bounds(intervals: list, *, minimum_duration: float = 0.04) -> list[tuple[float, float]]:
    """Turn SOFA phone spans into stable karaoke word spans.

    SOFA's word tier can contain very short spans for reduced function words
    (for example an English ``the`` consisting almost entirely of a transition).
    Its onset is still acoustically located.  Preserve every measured onset and
    duration, but give sub-frame spans a small, next-onset-bounded display
    window so they remain visible and usable by the karaoke renderer.
    """
    result: list[tuple[float, float]] = []
    for index, interval in enumerate(intervals):
        start = float(interval.minTime)
        measured_end = float(interval.maxTime)
        next_start = (float(intervals[index + 1].minTime)
                      if index + 1 < len(intervals) else None)
        end = measured_end
        if end - start < minimum_duration:
            end = start + minimum_duration
            if next_start is not None:
                end = min(end, next_start)
            # Coincident model tokens cannot both own the same display time.
            # Keep them monotonic; the validator will honestly flag a token if
            # the following acoustic onset leaves less than one video frame.
            end = max(measured_end, end)
        result.append((start, end))
    return result


def _line_can_be_replaced(line, sofa_word_count: int) -> bool:
    """Never mix SOFA and another aligner's time axes inside one lyric line."""
    return bool(line.words) and len(line.words) == sofa_word_count


def _exact_sequence_mapping(expected: list[str], actual: list[str]) -> dict[int, int]:
    """Map unchanged SOFA tokens while leaving isolated OOV gaps untrusted."""
    matcher = difflib.SequenceMatcher(a=expected, b=actual, autojunk=False)
    return {
        expected_index + offset: actual_index + offset
        for expected_index, actual_index, size in matcher.get_matching_blocks()
        for offset in range(size)
    }


def realign_english_singing(audio, lines: list, *, minimum_confidence: float = 0.25) -> dict:
    sofa_root = Path(os.getenv("LRC_SOFA_ROOT", "/app/SOFA"))
    checkpoint = Path(os.getenv("LRC_SOFA_EN_CKPT", "/models/sofa/english/tgm_en_v100.ckpt"))
    dictionary = Path(os.getenv("LRC_SOFA_EN_DICT", "/models/sofa/english/tgm_sofa_dict.txt"))
    if not (sofa_root.exists() and checkpoint.exists() and dictionary.exists()):
        return {"enabled": False, "reason": "SOFA-Code oder englisches Modell fehlt"}
    candidates = [section for section in _continuous_sections(lines) if any(
        word.get("timing_source") in HEURISTIC_SOURCES
        for line in section for word in line.words
    )]
    if not candidates:
        return {"enabled": True, "model": "tgm_en_v100", "attempted_sections": 0,
                "accepted_sections": 0, "accepted_words": 0}

    duration = len(audio) / 16000
    jobs = []
    with tempfile.TemporaryDirectory(prefix="neonstage-sofa-") as temp_name:
        temp = Path(temp_name)
        for job_index, section in enumerate(candidates):
            start = max(0.0, float(section[0].words[0]["start"]) - 1.2)
            end = min(duration, float(section[-1].words[-1]["end"]) + 1.2)
            name = f"section-{job_index:03d}"
            sf.write(temp / f"{name}.wav", audio[int(start * 16000):int(end * 16000)], 16000)
            (temp / f"{name}.lab").write_text(
                " ".join(_words(" ".join(line.text for line in section))), encoding="utf-8"
            )
            jobs.append((name, section, start, end))
        command = [
            "python3", str(sofa_root / "infer.py"), "--ckpt", str(checkpoint),
            "--folder", str(temp), "--dictionary", str(dictionary),
            "--out_formats", "textgrid", "--save_confidence",
        ]
        completed = subprocess.run(command, cwd=sofa_root, capture_output=True, text=True,
                                   timeout=300, check=False)
        if completed.returncode != 0:
            return {"enabled": True, "model": "tgm_en_v100", "error": completed.stderr[-2000:],
                    "attempted_sections": len(jobs), "accepted_sections": 0, "accepted_words": 0}
        confidence_path = temp / "confidence" / "confidence.csv"
        confidences = {}
        if confidence_path.exists():
            with confidence_path.open(newline="", encoding="utf-8") as handle:
                confidences = {row["name"]: float(row["confidence"]) for row in csv.DictReader(handle)}
        import textgrid

        accepted_sections = accepted_words = 0
        diagnostics = []
        for name, section, base, _end in jobs:
            confidence = confidences.get(name, 0.0)
            grid_path = temp / "TextGrid" / f"{name}.TextGrid"
            if not grid_path.exists():
                diagnostics.append({"section": name, "status": "missing-output"})
                continue
            grid = textgrid.TextGrid.fromFile(str(grid_path))
            intervals = [item for item in grid.getFirst("words")
                         if item.mark not in {"SP", "AP", "", "<SP>", "<AP>"}]
            expected_words = [word for line in section for word in _words(line.text)]
            actual_words = [item.mark.lower() for item in intervals]
            if confidence < minimum_confidence:
                diagnostics.append({"section": name, "status": "low-confidence",
                                    "confidence": round(confidence, 4)})
                continue
            bounds = _karaoke_word_bounds(intervals)
            mapping = _exact_sequence_mapping(expected_words, actual_words)
            expected_cursor = 0
            section_applied_words = 0
            skipped_lines = 0
            for line in section:
                groups = _word_groups(line.text)
                token_count = sum(len(group) for group in groups)
                mapped = [mapping.get(index) for index in range(expected_cursor, expected_cursor + token_count)]
                expected_cursor += token_count
                if not _line_can_be_replaced(line, len(groups)):
                    skipped_lines += 1
                    continue
                if (any(index is None for index in mapped)
                        or any(right != left + 1 for left, right in zip(mapped, mapped[1:]))):
                    skipped_lines += 1
                    continue
                replacement_bounds = [bounds[index] for index in mapped if index is not None]
                grouped_bounds = []
                group_cursor = 0
                for group in groups:
                    group_spans = replacement_bounds[group_cursor:group_cursor + len(group)]
                    group_cursor += len(group)
                    grouped_bounds.append((group_spans[0][0], group_spans[-1][1]))
                for word, (word_start, word_end) in zip(line.words, grouped_bounds):
                    word["start"] = round(base + word_start, 3)
                    word["end"] = round(base + word_end, 3)
                    word["timing_source"] = "sofa-singing-alignment"
                    word["sofa_confidence"] = round(confidence, 4)
                line.timestamp = float(line.words[0]["start"])
                section_applied_words += len(groups)
            if section_applied_words:
                accepted_sections += 1
            accepted_words += section_applied_words
            diagnostics.append({"section": name,
                                "status": "accepted" if not skipped_lines else "accepted-partial",
                                "confidence": round(confidence, 4),
                                "words": section_applied_words,
                                "skipped_non_atomic_lines": skipped_lines,
                                "expected": len(expected_words), "actual": len(actual_words)})
        return {"enabled": True, "model": "tgm_en_v100", "attempted_sections": len(jobs),
                "accepted_sections": accepted_sections, "accepted_words": accepted_words,
                "minimum_confidence": minimum_confidence, "section_diagnostics": diagnostics}
