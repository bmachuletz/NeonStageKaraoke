from __future__ import annotations

import gc
import math
import os
import re
import statistics

import numpy as np


HEURISTIC_SOURCES = {"vocal-activity-repair", "anchor-context-vocal-activity",
                     "anchor-tail-vocal-activity", "geometric-repair"}
MODEL_BUNDLES = {
    "de": "VOXPOPULI_ASR_BASE_10K_DE",
    "en": "WAV2VEC2_ASR_BASE_960H",
}


def _section_allows_atomic_replacement(section: list, replaceable_ids: set[int]) -> bool:
    """An atomic pass may replace only a section made entirely of weak lines.

    The combined transcript is useful for resolving several neighbouring
    heuristic lines. It must not use one weak neighbour as permission to rewrite
    an otherwise plausible line merely to make the global CTC path fit.
    """
    return (all(id(line) in replaceable_ids for line in section)
            and not any(word.get("timing_source") == "stable-ts-whisper"
                        for line in section for word in line.words))


def _rms_db(audio, start: float, end: float) -> float:
    first = max(0, int(start * 16000))
    last = min(len(audio), max(first + 1, int(end * 16000)))
    if first >= len(audio) or last <= first:
        return -180.0
    values = np.asarray(audio[first:last], dtype=np.float64)
    if values.ndim > 1:
        values = values.mean(axis=tuple(range(1, values.ndim)))
    rms = float(np.sqrt(np.mean(values * values))) if len(values) else 0.0
    return 20.0 * math.log10(max(rms, 1e-9))


def _candidate_rejection_reason(
    words: list[dict],
    audio,
    *,
    minimum_word_confidence: float = 0.10,
    maximum_internal_gap: float = 0.75,
    weak_word_confidence: float = 0.20,
    acoustic_margin_db: float = 12.0,
) -> str | None:
    """Reject a forced path that is geometrically or acoustically implausible."""
    if not words:
        return "empty-alignment"
    if any(float(word["end"]) <= float(word["start"]) for word in words):
        return "non-positive-word-duration"
    gaps = [float(right["start"]) - float(left["end"])
            for left, right in zip(words, words[1:])]
    if gaps and max(gaps) > maximum_internal_gap:
        return "implausible-internal-gap"
    confidences = [float(word.get("confidence", 0.0)) for word in words]
    if min(confidences) < minimum_word_confidence:
        return "low-word-confidence"

    levels = [_rms_db(audio, float(word["start"]) - 0.02,
                      float(word["end"]) + 0.02) for word in words]
    reference = max(levels)
    if any(confidence < weak_word_confidence and level < reference - acoustic_margin_db
           for confidence, level in zip(confidences, levels)):
        return "weak-word-without-acoustic-support"
    return None


def _clean_words(text: str, dictionary: dict[str, int]) -> list[str]:
    words = re.findall(r"[^\W_]+(?:['’][^\W_]+)?", text.lower(), re.UNICODE)
    result = []
    for word in words:
        # CTC character dictionaries commonly contain no digits although they
        # are sung as ordinary words. Keep the displayed-token cardinality.
        number_words = {
            "0": "null", "1": "eins", "2": "zwei", "3": "drei", "4": "vier",
            "5": "fünf", "6": "sechs", "7": "sieben", "8": "acht", "9": "neun",
            "100": "hundert", "1000": "tausend", "2000": "zweitausend",
        }
        word = number_words.get(word, word)
        cleaned = "".join(character for character in word.replace("’", "'")
                          if character in dictionary and character != "|")
        if cleaned:
            result.append(cleaned)
    return result


class CtcPhraseAligner:
    """Independent wav2vec2/CTC alignment for phrases rejected by Qwen."""

    def __init__(self, language: str, device: str):
        import torch
        import torchaudio

        language = language.lower().split("-")[0]
        bundle_name = os.getenv(f"LRC_CTC_MODEL_{language.upper()}", MODEL_BUNDLES.get(language, ""))
        if not bundle_name or not hasattr(torchaudio.pipelines, bundle_name):
            raise ValueError(f"Kein CTC-Alignmentmodell für '{language}' konfiguriert.")
        if device == "auto":
            device = "cuda" if torch.cuda.is_available() else "cpu"
        os.environ.setdefault("TORCH_HOME", "/models/torch")
        self.torch = torch
        self.torchaudio = torchaudio
        self.device = device
        self.bundle_name = bundle_name
        bundle = getattr(torchaudio.pipelines, bundle_name)
        self.model = bundle.get_model().to(device).eval()
        self.labels = bundle.get_labels()
        self.dictionary = {label.lower(): index for index, label in enumerate(self.labels)}
        self.blank = self.dictionary.get("-", 0)

    def close(self) -> None:
        del self.model
        gc.collect()
        if self.torch.cuda.is_available():
            self.torch.cuda.empty_cache()

    def align(self, audio, text: str, base_seconds: float) -> list[dict]:
        torch = self.torch
        words = _clean_words(text, self.dictionary)
        if not words:
            return []
        separator = self.dictionary.get("|")
        target: list[int] = []
        word_ranges: list[tuple[int, int]] = []
        for index, word in enumerate(words):
            if index and separator is not None:
                target.append(separator)
            first = len(target)
            target.extend(self.dictionary[character] for character in word)
            word_ranges.append((first, len(target)))
        waveform = torch.from_numpy(audio).float().unsqueeze(0).to(self.device)
        with torch.inference_mode():
            emissions, _ = self.model(waveform)
            log_probs = torch.log_softmax(emissions, dim=-1)
            targets = torch.tensor([target], dtype=torch.int32, device=self.device)
            paths, scores = self.torchaudio.functional.forced_align(
                log_probs, targets, blank=self.blank
            )
        spans = self.torchaudio.functional.merge_tokens(paths[0].cpu(), scores[0].cpu(), self.blank)
        if len(spans) != len(target):
            return []
        frame_seconds = (len(audio) / 16000) / max(1, log_probs.shape[1])
        aligned = []
        for word, (first, end) in zip(words, word_ranges):
            selected = spans[first:end]
            if not selected:
                return []
            # forced_align/merge_tokens expose mean log-probabilities.
            confidence = sum(math.exp(float(span.score)) for span in selected) / len(selected)
            aligned.append({
                "word": word,
                "start": round(base_seconds + float(selected[0].start) * frame_seconds, 3),
                "end": round(base_seconds + float(selected[-1].end) * frame_seconds, 3),
                "confidence": round(confidence, 4),
            })
        return aligned


def realign_heuristic_lines(audio, lines: list, language: str, device: str,
                            *, padding: float = 0.35, minimum_confidence: float = 0.25,
                            minimum_word_confidence: float = 0.10,
                            maximum_internal_gap: float = 0.75) -> dict:
    candidates = [line for line in lines if line.words and any(
        word.get("timing_source") in HEURISTIC_SOURCES for word in line.words
    )]
    if not candidates:
        return {"enabled": True, "model": None, "attempted_lines": 0,
                "accepted_lines": 0, "accepted_words": 0}
    aligner = CtcPhraseAligner(language, device)
    attempted = accepted_lines = accepted_words = contextual_pairs = section_passes = 0
    diagnostics = []
    section_diagnostics = []
    candidate_ids = {id(line) for line in candidates}
    original_ranges = {
        id(line): (float(line.words[0]["start"]), float(line.words[-1]["end"]))
        for line in lines if line.words
    }
    try:
        for line in candidates:
            attempted += 1
            line_index = lines.index(line)
            original_start, original_end = original_ranges[id(line)]
            # The model needs acoustic lead-in/out even when LRC lines touch.
            # We therefore widen the inference window but reject a path that
            # lands implausibly far outside the original phrase ownership.
            lower = 0.0
            upper = len(audio) / 16000
            attempts = []
            aligned = []
            confidence = 0.0
            accepted_padding = None
            for candidate_padding in dict.fromkeys((padding, 0.75, 1.2)):
                start = max(lower, original_start - candidate_padding)
                end = min(upper, original_end + candidate_padding)
                if end - start < 0.15:
                    attempts.append({"padding": candidate_padding, "status": "window-too-short"})
                    continue
                chunk = audio[int(start * 16000):int(end * 16000)]
                trial = aligner.align(chunk, line.text, start)
                if len(trial) != len(line.words):
                    attempts.append({"padding": candidate_padding, "status": "word-count-mismatch",
                                     "aligned_words": len(trial), "expected_words": len(line.words)})
                    continue
                trial_confidence = sum(word["confidence"] for word in trial) / len(trial)
                if any(right["start"] < left["end"] for left, right in zip(trial, trial[1:])):
                    attempts.append({"padding": candidate_padding, "status": "nonmonotonic",
                                     "mean_confidence": round(trial_confidence, 4)})
                    continue
                rejection = _candidate_rejection_reason(
                    trial, audio, minimum_word_confidence=minimum_word_confidence,
                    maximum_internal_gap=maximum_internal_gap)
                if rejection:
                    attempts.append({"padding": candidate_padding, "status": rejection,
                                     "mean_confidence": round(trial_confidence, 4),
                                     "minimum_word_confidence": round(min(
                                         float(word["confidence"]) for word in trial), 4),
                                     "maximum_internal_gap": round(max([
                                         float(right["start"]) - float(left["end"])
                                         for left, right in zip(trial, trial[1:])
                                     ] or [0.0]), 3)})
                    continue
                ownership_slack = min(0.45, candidate_padding)
                if (trial[0]["start"] < original_start - ownership_slack
                        or trial[-1]["end"] > original_end + ownership_slack):
                    attempts.append({"padding": candidate_padding, "status": "outside-phrase-window",
                                     "mean_confidence": round(trial_confidence, 4),
                                     "aligned_start": trial[0]["start"], "aligned_end": trial[-1]["end"]})
                    continue
                attempts.append({"padding": candidate_padding,
                                 "status": "accepted" if trial_confidence >= minimum_confidence else "low-confidence",
                                 "mean_confidence": round(trial_confidence, 4),
                                 "window_start": round(start, 3), "window_end": round(end, 3)})
                if trial_confidence >= minimum_confidence and trial_confidence > confidence:
                    aligned, confidence, accepted_padding = trial, trial_confidence, candidate_padding
            if not aligned:
                diagnostics.append({"line": line_index + 1, "text": line.text,
                                    "status": attempts[-1]["status"] if attempts else "not-attempted",
                                    "attempts": attempts})
                continue
            for word, replacement in zip(line.words, aligned):
                word["start"] = replacement["start"]
                word["end"] = replacement["end"]
                word["timing_source"] = "ctc-phoneme-alignment"
                word["ctc_confidence"] = replacement["confidence"]
            line.timestamp = float(line.words[0]["start"])
            accepted_lines += 1
            accepted_words += len(line.words)
            diagnostics.append({"line": line_index + 1, "text": line.text, "status": "accepted",
                                "padding": accepted_padding, "mean_confidence": round(confidence, 4),
                                "attempts": attempts})
        # Resolve repetitions globally inside continuous singing sections. The
        # complete transcript gives CTC a single monotonic path; only previously
        # rejected heuristic lines are promoted, and only on their own confidence.
        sections = []
        current = []
        for line in lines:
            if not line.words:
                continue
            if (current and (float(line.words[0]["start"]) - float(current[-1].words[-1]["end"]) > 1.2
                             or float(line.words[-1]["end"]) - float(current[0].words[0]["start"]) > 30.0)):
                sections.append(current)
                current = []
            current.append(line)
        if current:
            sections.append(current)
        diagnostics_by_line = {entry["line"]: entry for entry in diagnostics}
        for section in sections:
            rejected = [line for line in section if id(line) in candidate_ids
                        and any(word.get("timing_source") in HEURISTIC_SOURCES for word in line.words)]
            if not rejected or len(section) < 2:
                continue
            start = max(0.0, original_ranges[id(section[0])][0] - 0.75)
            end = min(len(audio) / 16000, original_ranges[id(section[-1])][1] + 0.75)
            aligned = aligner.align(audio[int(start * 16000):int(end * 16000)],
                                    " ".join(line.text for line in section), start)
            expected = sum(len(line.words) for line in section)
            if len(aligned) != expected:
                continue
            cursor = 0
            promoted = 0
            section_line_results = []
            section_replacements = []
            rejected_ids = {id(line) for line in rejected}
            for line in section:
                replacements = aligned[cursor:cursor + len(line.words)]
                cursor += len(line.words)
                section_replacements.append(replacements)
                line_confidence = sum(word["confidence"] for word in replacements) / len(replacements)
                rejection = _candidate_rejection_reason(
                    replacements, audio, minimum_word_confidence=minimum_word_confidence,
                    maximum_internal_gap=maximum_internal_gap)
                section_line_results.append({"line": lines.index(line) + 1,
                                             "mean_confidence": round(line_confidence, 4),
                                             "rejection_reason": rejection,
                                             "start": replacements[0]["start"],
                                             "end": replacements[-1]["end"],
                                             "was_heuristic": id(line) in rejected_ids})
            section_confidences = [item["mean_confidence"] for item in section_line_results]
            atomic = (min(section_confidences) >= 0.10
                      and statistics.median(section_confidences) >= 0.20
                      and not any(item["rejection_reason"] for item in section_line_results)
                      and _section_allows_atomic_replacement(section, rejected_ids))
            if atomic:
                # One transcript, one CTC path, one atomic update. This is the
                # duration-aware equivalent of a left-to-right phrase HMM and
                # prevents repeated lines from competing in isolated windows.
                for line, replacements in zip(section, section_replacements):
                    for word, replacement in zip(line.words, replacements):
                        word["start"] = replacement["start"]
                        word["end"] = replacement["end"]
                        word["timing_source"] = "ctc-section-alignment"
                        word["ctc_confidence"] = replacement["confidence"]
                    line.timestamp = float(line.words[0]["start"])
                    if id(line) in rejected_ids:
                        accepted_lines += 1
                        accepted_words += len(line.words)
                        promoted += 1
                        diagnostic = diagnostics_by_line.get(lines.index(line) + 1)
                        if diagnostic is not None:
                            diagnostic["status"] = "accepted-section-atomic"
                section_passes += 1
                section_diagnostics.append({"first_line": lines.index(section[0]) + 1,
                                            "last_line": lines.index(section[-1]) + 1,
                                            "promoted_lines": promoted,
                                            "atomic_replacement": True,
                                            "median_confidence": round(statistics.median(section_confidences), 4),
                                            "lines": section_line_results})
                continue
            for line, replacements, line_result in zip(section, section_replacements,
                                                       section_line_results):
                if id(line) not in rejected_ids:
                    continue
                confidence = line_result["mean_confidence"]
                if confidence < minimum_confidence or line_result["rejection_reason"]:
                    continue
                global_index = lines.index(line)
                if (global_index > 0 and lines[global_index - 1].words
                        and replacements[0]["start"] + 0.08
                        < float(lines[global_index - 1].words[-1]["end"])):
                    continue
                if (global_index + 1 < len(lines) and lines[global_index + 1].words
                        and replacements[-1]["end"]
                        > float(lines[global_index + 1].words[0]["start"]) + 0.08):
                    continue
                for word, replacement in zip(line.words, replacements):
                    word["start"] = replacement["start"]
                    word["end"] = replacement["end"]
                    word["timing_source"] = "ctc-section-alignment"
                    word["ctc_confidence"] = replacement["confidence"]
                line.timestamp = float(line.words[0]["start"])
                accepted_lines += 1
                accepted_words += len(line.words)
                promoted += 1
                diagnostic = diagnostics_by_line.get(global_index + 1)
                if diagnostic is not None:
                    diagnostic["status"] = "accepted-section"
                    diagnostic["section_mean_confidence"] = round(confidence, 4)
            if promoted:
                section_passes += 1
            section_diagnostics.append({"first_line": lines.index(section[0]) + 1,
                                        "last_line": lines.index(section[-1]) + 1,
                                        "promoted_lines": promoted,
                                        "atomic_replacement": False,
                                        "lines": section_line_results})
        # Independent line windows can both claim the same coarticulated sound.
        # Re-run only such neighbours as one transcript so CTC must choose one
        # globally monotonic path across the phrase boundary.
        for index in range(1, len(lines)):
            previous, current = lines[index - 1], lines[index]
            if not previous.words or not current.words:
                continue
            if float(previous.words[-1]["end"]) <= float(current.words[0]["start"]) + 0.08:
                continue
            sources = {word.get("timing_source") for word in [*previous.words, *current.words]}
            if not sources or not sources.issubset({"ctc-phoneme-alignment", "ctc-context-alignment",
                                                   "ctc-section-alignment"}):
                continue
            start = max(0.0, float(previous.words[0]["start"]) - padding)
            end = min(len(audio) / 16000, float(current.words[-1]["end"]) + padding)
            chunk = audio[int(start * 16000):int(end * 16000)]
            aligned = aligner.align(chunk, previous.text + " " + current.text, start)
            expected = len(previous.words) + len(current.words)
            if len(aligned) != expected:
                continue
            confidence = sum(word["confidence"] for word in aligned) / len(aligned)
            if (confidence < minimum_confidence or _candidate_rejection_reason(
                    aligned, audio, minimum_word_confidence=minimum_word_confidence,
                    maximum_internal_gap=maximum_internal_gap)):
                continue
            split = len(previous.words)
            if aligned[split - 1]["end"] > aligned[split]["start"]:
                continue
            for word, replacement in zip([*previous.words, *current.words], aligned):
                word["start"] = replacement["start"]
                word["end"] = replacement["end"]
                word["timing_source"] = "ctc-context-alignment"
                word["ctc_confidence"] = replacement["confidence"]
            previous.timestamp = float(previous.words[0]["start"])
            current.timestamp = float(current.words[0]["start"])
            contextual_pairs += 1
    finally:
        model = aligner.bundle_name
        aligner.close()
    return {"enabled": True, "model": model, "attempted_lines": attempted,
            "accepted_lines": accepted_lines, "accepted_words": accepted_words,
            "contextual_boundary_pairs": contextual_pairs,
            "section_alignment_passes": section_passes,
            "minimum_confidence": minimum_confidence,
            "minimum_word_confidence": minimum_word_confidence,
            "maximum_internal_gap": maximum_internal_gap,
            "section_diagnostics": section_diagnostics,
            "line_diagnostics": diagnostics}


def realign_overlapping_line_pairs(audio, lines: list, language: str, device: str,
                                   *, padding: float = 0.45,
                                   minimum_confidence: float = 0.25,
                                   minimum_word_confidence: float = 0.10,
                                   maximum_internal_gap: float = 0.75) -> dict:
    """Re-align conflicting neighbours as one monotonic transcript.

    Independent line windows can claim the same vocal frames. A combined CTC
    pass forces one path across the boundary and therefore gives the transition
    a second, acoustically constrained analysis before the hard overlap gate.
    """
    initial = [index for index in range(1, len(lines))
               if lines[index - 1].words and lines[index].words and
               float(lines[index - 1].words[-1]["end"]) >
               float(lines[index].words[0]["start"]) + 0.001]
    if not initial:
        return {"enabled": True, "model": None, "detected_pairs": 0,
                "realigned_pairs": 0, "unresolved_pairs": []}
    aligner = CtcPhraseAligner(language, device)
    accepted = 0
    diagnostics = []
    try:
        duration = len(audio) / 16000
        for index in initial:
            previous, current = lines[index - 1], lines[index]
            if float(previous.words[-1]["end"]) <= float(current.words[0]["start"]) + 0.001:
                continue
            start = max(0.0, float(previous.words[0]["start"]) - padding)
            end = min(duration, float(current.words[-1]["end"]) + padding)
            aligned = aligner.align(audio[int(start * 16000):int(end * 16000)],
                                    previous.text + " " + current.text, start)
            expected = len(previous.words) + len(current.words)
            confidence = (sum(word["confidence"] for word in aligned) / len(aligned)
                          if aligned else 0.0)
            rejection = (_candidate_rejection_reason(
                aligned, audio, minimum_word_confidence=minimum_word_confidence,
                maximum_internal_gap=maximum_internal_gap) if aligned else "empty-alignment")
            if len(aligned) != expected or confidence < minimum_confidence or rejection:
                diagnostics.append({"previous_line": index, "next_line": index + 1,
                                    "status": "rejected", "returned_words": len(aligned),
                                    "expected_words": expected,
                                    "mean_confidence": round(confidence, 4),
                                    "rejection_reason": rejection})
                continue
            split = len(previous.words)
            if aligned[split - 1]["end"] > aligned[split]["start"] + 0.001:
                diagnostics.append({"previous_line": index, "next_line": index + 1,
                                    "status": "ctc-path-overlaps",
                                    "mean_confidence": round(confidence, 4)})
                continue
            for word, replacement in zip([*previous.words, *current.words], aligned):
                word["start"] = replacement["start"]
                word["end"] = replacement["end"]
                word["timing_source"] = "ctc-overlap-reanalysis"
                word["ctc_confidence"] = replacement["confidence"]
            previous.timestamp = float(previous.words[0]["start"])
            current.timestamp = float(current.words[0]["start"])
            accepted += 1
            diagnostics.append({"previous_line": index, "next_line": index + 1,
                                "status": "accepted",
                                "mean_confidence": round(confidence, 4),
                                "boundary": aligned[split]["start"]})
    finally:
        model = aligner.bundle_name
        aligner.close()
    unresolved = [{"previous_line": index, "next_line": index + 1,
                   "overlap_ms": round((float(lines[index - 1].words[-1]["end"]) -
                                        float(lines[index].words[0]["start"])) * 1000, 1)}
                  for index in range(1, len(lines))
                  if lines[index - 1].words and lines[index].words and
                  float(lines[index - 1].words[-1]["end"]) >
                  float(lines[index].words[0]["start"]) + 0.001]
    return {"enabled": True, "model": model, "detected_pairs": len(initial),
            "realigned_pairs": accepted, "unresolved_pairs": unresolved,
            "minimum_confidence": minimum_confidence,
            "minimum_word_confidence": minimum_word_confidence,
            "maximum_internal_gap": maximum_internal_gap,
            "diagnostics": diagnostics}
