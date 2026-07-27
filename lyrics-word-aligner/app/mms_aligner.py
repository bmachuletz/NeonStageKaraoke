from __future__ import annotations

import gc
import re
import statistics
import unicodedata

from .ctc_aligner import HEURISTIC_SOURCES


NUMBER_WORDS = {
    "0": "null", "1": "eins", "2": "zwei", "3": "drei", "4": "vier",
    "5": "funf", "6": "sechs", "7": "sieben", "8": "acht", "9": "neun",
    "100": "hundert", "1000": "tausend", "2000": "zweitausend",
}


def normalize_words(text: str) -> list[str]:
    result = []
    for raw in re.findall(r"[^\W_]+(?:['’][^\W_]+)?", text.lower(), re.UNICODE):
        raw = NUMBER_WORDS.get(raw, raw).replace("ß", "ss").replace("’", "'")
        word = unicodedata.normalize("NFKD", raw).encode("ascii", "ignore").decode("ascii")
        word = re.sub(r"[^a-z']", "", word)
        if word:
            result.append(word)
    return result


class MmsPhraseAligner:
    """Independent multilingual MMS forced aligner used as a strict second vote."""

    def __init__(self, device: str):
        import torch
        import torchaudio

        if device == "auto":
            device = "cuda" if torch.cuda.is_available() else "cpu"
        self.torch = torch
        self.device = device
        self.bundle = torchaudio.pipelines.MMS_FA
        self.model = self.bundle.get_model().to(device).eval()
        self.tokenizer = self.bundle.get_tokenizer()
        self.forced_aligner = self.bundle.get_aligner()

    def close(self):
        del self.model
        gc.collect()
        if self.torch.cuda.is_available():
            self.torch.cuda.empty_cache()

    def align(self, audio, text: str, base_seconds: float) -> list[dict]:
        words = normalize_words(text)
        if not words:
            return []
        waveform = self.torch.from_numpy(audio).float().unsqueeze(0).to(self.device)
        with self.torch.inference_mode():
            emission, _ = self.model(waveform)
            spans = self.forced_aligner(emission[0], self.tokenizer(words))
        if len(spans) != len(words) or any(not item for item in spans):
            return []
        frame_seconds = (len(audio) / 16000) / max(1, emission.shape[1])
        result = []
        for word, word_spans in zip(words, spans):
            total_frames = sum(len(span) for span in word_spans)
            confidence = sum(float(span.score) * len(span) for span in word_spans) / max(1, total_frames)
            result.append({
                "word": word,
                "start": round(base_seconds + float(word_spans[0].start) * frame_seconds, 3),
                "end": round(base_seconds + float(word_spans[-1].end) * frame_seconds, 3),
                "confidence": round(confidence, 4),
            })
        return result


def realign_remaining_lines(audio, lines: list, device: str, *, minimum_confidence: float = 0.35) -> dict:
    candidates = [line for line in lines if line.words and any(
        word.get("timing_source") in HEURISTIC_SOURCES for word in line.words
    )]
    if not candidates:
        return {"enabled": True, "model": "MMS_FA", "attempted_lines": 0,
                "accepted_lines": 0, "accepted_words": 0, "line_diagnostics": []}
    aligner = MmsPhraseAligner(device)
    duration = len(audio) / 16000
    accepted_lines = accepted_words = 0
    diagnostics = []
    calibration = []
    try:
        # Establish the score scale on this singer/song from lines already
        # located by an independent acoustic aligner. MMS confidence is highly
        # domain-dependent and must not be calibrated from speech defaults.
        for index, line in enumerate(lines):
            if line in candidates or not line.words:
                continue
            original_start = float(line.words[0]["start"])
            original_end = float(line.words[-1]["end"])
            start = max(0.0, original_start - 0.75)
            end = min(duration, original_end + 0.75)
            aligned = aligner.align(audio[int(start * 16000):int(end * 16000)], line.text, start)
            if len(aligned) != len(line.words):
                continue
            confidence = sum(word["confidence"] for word in aligned) / len(aligned)
            calibration.append({
                "line": index + 1,
                "mean_confidence": round(confidence, 4),
                "start_delta_ms": round((aligned[0]["start"] - original_start) * 1000),
                "end_delta_ms": round((aligned[-1]["end"] - original_end) * 1000),
            })
        trusted_calibration = [item["mean_confidence"] for item in calibration
                               if abs(item["start_delta_ms"]) <= 180 and abs(item["end_delta_ms"]) <= 250]
        calibrated_minimum = minimum_confidence
        if len(trusted_calibration) >= 5:
            calibrated_minimum = max(0.16, min(minimum_confidence,
                                                statistics.median(trusted_calibration) * 0.72))
        for line in candidates:
            index = lines.index(line)
            original_start = float(line.words[0]["start"])
            original_end = float(line.words[-1]["end"])
            start = max(0.0, original_start - 1.2)
            end = min(duration, original_end + 1.2)
            aligned = aligner.align(audio[int(start * 16000):int(end * 16000)], line.text, start)
            if len(aligned) != len(line.words):
                diagnostics.append({"line": index + 1, "status": "word-count-mismatch"})
                continue
            confidence = sum(word["confidence"] for word in aligned) / len(aligned)
            if confidence < calibrated_minimum:
                diagnostics.append({"line": index + 1, "status": "low-confidence",
                                    "mean_confidence": round(confidence, 4),
                                    "aligned_start": aligned[0]["start"],
                                    "aligned_end": aligned[-1]["end"]})
                continue
            if aligned[0]["start"] < original_start - 0.65 or aligned[-1]["end"] > original_end + 0.65:
                diagnostics.append({"line": index + 1, "status": "outside-phrase-window",
                                    "mean_confidence": round(confidence, 4),
                                    "aligned_start": aligned[0]["start"],
                                    "aligned_end": aligned[-1]["end"],
                                    "original_start": original_start,
                                    "original_end": original_end})
                continue
            if (index > 0 and lines[index - 1].words
                    and aligned[0]["start"] + 0.08 < float(lines[index - 1].words[-1]["end"])):
                diagnostics.append({"line": index + 1, "status": "overlaps-previous"})
                continue
            if (index + 1 < len(lines) and lines[index + 1].words
                    and aligned[-1]["end"] > float(lines[index + 1].words[0]["start"]) + 0.08):
                diagnostics.append({"line": index + 1, "status": "overlaps-next"})
                continue
            for word, replacement in zip(line.words, aligned):
                word["start"] = replacement["start"]
                word["end"] = replacement["end"]
                word["timing_source"] = "mms-forced-alignment"
                word["mms_confidence"] = replacement["confidence"]
            line.timestamp = float(line.words[0]["start"])
            accepted_lines += 1
            accepted_words += len(line.words)
            diagnostics.append({"line": index + 1, "status": "accepted",
                                "mean_confidence": round(confidence, 4)})
    finally:
        aligner.close()
    return {"enabled": True, "model": "MMS_FA", "attempted_lines": len(candidates),
            "accepted_lines": accepted_lines, "accepted_words": accepted_words,
            "minimum_confidence": minimum_confidence,
            "calibrated_minimum_confidence": round(calibrated_minimum, 4),
            "calibration_lines": calibration,
            "line_diagnostics": diagnostics}
