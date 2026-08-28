from __future__ import annotations
import base64
import json
import re
from pathlib import Path
from .models import LrcLine

TIME_RE = re.compile(r"\[(\d{1,3}):(\d{2})(?:[.:](\d+))?\]")
WORD_TIME_RE = re.compile(
    r"<(?P<start>\d{1,3}:\d{2}(?:[.:]\d+)?)"
    r"(?:,(?P<end>\d{1,3}:\d{2}(?:[.:]\d+)?))?>"
    r"(?P<word>.*?)(?=\s*<\d{1,3}:\d{2}|$)"
)
METADATA_RE = re.compile(r"^\[(ar|al|ti|au|by|offset|re|ve|length):", re.I)
EDITOR_SYLLABLE_RE = re.compile(
    r"^\[neon-editor-syllables:(?P<payload>[A-Za-z0-9_-]+)\]$")
VOICE_LANE_RE = re.compile(
    r"^\[neon-voice:(?P<lane>\d+)(?::(?P<label>[A-Za-z0-9_-]+))?\]$")
STRUCTURE_MARKER_RE = re.compile(
    r"^(?:(?:pre|post)[\s_-]*chorus|(?:vor|nach)[\s_-]*refrain|chorus|refrain|"
    r"verse|strophe|couplet|part|teil|bridge|intro|outro|instrumental|interlude|"
    r"zwischenspiel|hook|solo|breakdown|spoken|rap)"
    r"(?:\s+(?:\d+|x\s*\d+|\d+\s*x|[ivxlcdm]+|one|two|three|four|five|"
    r"eins|zwei|drei|vier|fünf))?$",
    re.IGNORECASE,
)


def is_structure_marker(text: str) -> bool:
    candidate = text.strip().strip("♪♫").strip()
    was_wrapped = False
    if len(candidate) >= 2 and (candidate[0], candidate[-1]) in {
            ("[", "]"), ("(", ")"), ("{", "}")}:
        was_wrapped = True
        candidate = candidate[1:-1].strip()
    if was_wrapped and ":" in candidate:
        candidate = candidate.split(":", 1)[0].strip()
    return STRUCTURE_MARKER_RE.fullmatch(candidate.rstrip(":").strip()) is not None


def parse_timestamp(match: re.Match[str]) -> float:
    minutes = int(match.group(1))
    seconds = int(match.group(2))
    fraction_raw = match.group(3) or "0"
    fraction = int(fraction_raw) / (10 ** len(fraction_raw))
    return minutes * 60 + seconds + fraction


def parse_timestamp_token(token: str) -> float:
    match = re.fullmatch(r"(\d{1,3}):(\d{2})(?:[.:](\d+))?", token)
    if match is None:
        raise ValueError(f"Ungültiger LRC-Zeitstempel: {token}")
    return parse_timestamp(match)


def parse_lrc(path: str | Path) -> tuple[list[str], list[LrcLine]]:
    headers: list[str] = []
    lines: list[LrcLine] = []
    plain_lines: list[str] = []
    empty_timestamps: list[float] = []
    editor_syllable_payloads: list[dict] = []
    pending_voice_lane = 0
    pending_voice_label: str | None = None
    for raw in Path(path).read_text(encoding="utf-8-sig").splitlines():
        voice_match = VOICE_LANE_RE.fullmatch(raw.strip())
        if voice_match is not None:
            pending_voice_lane = max(0, int(voice_match.group("lane")))
            pending_voice_label = _decode_label(voice_match.group("label"))
            continue
        syllable_match = EDITOR_SYLLABLE_RE.fullmatch(raw.strip())
        if syllable_match is not None:
            headers.append(raw)
            try:
                token = syllable_match.group("payload")
                padding = "=" * (-len(token) % 4)
                payload = json.loads(base64.urlsafe_b64decode(token + padding))
                if isinstance(payload, dict):
                    editor_syllable_payloads.append(payload)
            except (ValueError, TypeError, json.JSONDecodeError):
                # Optional editor metadata must never make otherwise valid
                # lyrics unusable. The pipeline will simply rebuild syllables.
                pass
            continue
        matches = list(TIME_RE.finditer(raw))
        if not matches:
            if raw.strip() and (METADATA_RE.match(raw.strip()) or raw.lstrip().startswith("[")):
                headers.append(raw)
            elif raw.strip():
                if not is_structure_marker(raw):
                    plain_lines.append(raw.strip())
            continue
        body = TIME_RE.sub("", raw).strip()
        word_matches = list(WORD_TIME_RE.finditer(body))
        words = []
        for word_match in word_matches:
            word_text = word_match.group("word").strip()
            if not word_text:
                continue
            start = parse_timestamp_token(word_match.group("start"))
            end_token = word_match.group("end")
            end = parse_timestamp_token(end_token) if end_token else start
            words.append({
                "word": word_text,
                "start": start,
                "end": max(start, end),
                "timing_source": "input-enhanced-lrc",
            })
        text = " ".join(word["word"] for word in words) if words else body.strip()
        if is_structure_marker(text):
            # Its position is still useful as a phrase-end boundary, but the
            # annotation must never enter canonical matching as a sung token.
            empty_timestamps.extend(parse_timestamp(match) for match in matches)
            pending_voice_lane = 0
            pending_voice_label = None
            continue
        if not text:
            empty_timestamps.extend(parse_timestamp(match) for match in matches)
            continue
        for match in matches:
            timestamp = parse_timestamp(match)
            lines.append(LrcLine(timestamp, text, raw, words=[dict(word) for word in words],
                                 source_timestamp=timestamp,
                                 voice_lane=pending_voice_lane,
                                 voice_label=pending_voice_label))
        pending_voice_lane = 0
        pending_voice_label = None
    lines.sort(key=lambda x: x.timestamp)
    # LRCLIB commonly uses an empty timestamp to say "the singing stops here".
    # Attach it to the preceding line instead of silently dropping it.  Only a
    # marker before the next text line can bound that line.
    for boundary in sorted(empty_timestamps):
        preceding = next((line for line in reversed(lines) if line.timestamp < boundary), None)
        if preceding is None:
            continue
        next_line = next((line for line in lines if line.timestamp > preceding.timestamp), None)
        if next_line is not None and boundary >= next_line.timestamp:
            continue
        if preceding.source_end_boundary is None or boundary < preceding.source_end_boundary:
            preceding.source_end_boundary = boundary
    if not lines:
        lines = [LrcLine(0.0, text, text, timed_input=False) for text in plain_lines]
    if not lines:
        raise ValueError("Die Lyrics-Datei enthält keinen verwertbaren Text.")
    _apply_editor_syllable_payloads(lines, editor_syllable_payloads)
    return headers, lines


def _apply_editor_syllable_payloads(lines: list[LrcLine], payloads: list[dict]) -> None:
    """Attach editor syllable references to their exact line and word.

    Indices identify the hierarchy for the same exported editor revision. Text
    and word windows are verified as a guard against stale or hand-edited
    metadata being applied to a different lyric line. Automatically generated
    syllables remain a reference only; explicitly edited syllables additionally
    receive the authoritative ``editor_syllables`` marker.
    """
    for payload in payloads:
        try:
            line_index = int(payload["Line"])
            word_index = int(payload["Word"])
            line = lines[line_index]
            word = line.words[word_index]
            if str(word.get("word", "")).strip().casefold() != str(payload["Text"]).strip().casefold():
                continue
            editor_word_start = float(payload["Start"])
            editor_word_end = float(payload["End"])
            if abs(float(word["start"]) - editor_word_start) > 0.002:
                continue
            if abs(float(word.get("end", word["start"])) - editor_word_end) > 0.002:
                continue
            raw_syllables = payload.get("Syllables")
            word["editor_word_manual_adjusted"] = bool(
                payload.get("WordManuallyAdjusted", False))
            word["editor_word_start"] = editor_word_start
            word["editor_word_end"] = editor_word_end
            if not isinstance(raw_syllables, list) or not raw_syllables:
                continue
            syllables = []
            previous_end = editor_word_start
            for index, item in enumerate(raw_syllables):
                start = max(editor_word_start, float(item["Start"]))
                end = min(editor_word_end, max(start, float(item["End"])))
                if start + 0.002 < previous_end:
                    raise ValueError("overlapping editor syllables")
                syllables.append({
                    "text": str(item["Text"]),
                    "start": start,
                    "end": end,
                    "confidence": float(word.get("syllable_confidence", 1.0)),
                    "index": index,
                    "manual_adjusted": bool(item.get("ManuallyAdjusted", True)),
                    "boundary_source": "manual-editor",
                })
                previous_end = end
            word["editor_reference_syllables"] = syllables
            if (word["editor_word_manual_adjusted"]
                    or any(item.get("manual_adjusted") for item in syllables)):
                word["editor_syllables"] = syllables
        except (KeyError, IndexError, TypeError, ValueError):
            continue


def fmt_time(value: float, angle: bool = False) -> str:
    value = max(0.0, value)
    minutes = int(value // 60)
    seconds = value - minutes * 60
    # Millisecond precision is required: the alignment sidecar and editor use
    # 1 ms coordinates. Centisecond LRC rounding could move a syllable 2–5 ms
    # outside its word and even create an apparent line overlap.
    token = f"{minutes:02d}:{seconds:06.3f}"
    return f"<{token}>" if angle else f"[{token}]"


def fmt_word_time(start: float, end: float) -> str:
    """Neon Stage extension: preserve the aligner's real word interval."""
    start_token = fmt_time(start, angle=True)[1:-1]
    end_token = fmt_time(max(start, end), angle=True)[1:-1]
    return f"<{start_token},{end_token}>"


def _decode_label(token: str | None) -> str | None:
    if not token:
        return None
    try:
        padding = "=" * (-len(token) % 4)
        return base64.urlsafe_b64decode(token + padding).decode("utf-8") or None
    except (ValueError, UnicodeDecodeError):
        return None


def _encode_label(value: str | None) -> str | None:
    if not value:
        return None
    return base64.urlsafe_b64encode(value.encode("utf-8")).decode("ascii").rstrip("=")


def render_enhanced_lrc(headers: list[str], lines: list[LrcLine]) -> str:
    # Voice metadata belongs to one exact following line, never to the global
    # header block. Re-emit it from the parsed line model.
    out = [header for header in headers
           if VOICE_LANE_RE.fullmatch(header.strip()) is None]
    if out:
        out.append("")
    for index, line in enumerate(lines):
        if line.voice_lane > 0 or line.voice_label:
            label = _encode_label(line.voice_label)
            suffix = f":{label}" if label else ""
            out.append(f"[neon-voice:{max(0, int(line.voice_lane))}{suffix}]")
        base = fmt_time(line.timestamp)
        # The quality state belongs in the alignment report. Even uncertain
        # acoustic timings are essential review material for the editor and
        # must never be degraded back to an uneditable plain-text line.
        if line.words:
            body = " ".join(
                f"{fmt_word_time(float(w['start']), float(w.get('end', w['start'])))}{w['word']}"
                for w in line.words
            )
            out.append(f"{base}{body}")
        else:
            out.append(f"{base}{line.text}")
        boundary = line.source_end_boundary
        next_start = lines[index + 1].timestamp if index + 1 < len(lines) else None
        if (boundary is not None and boundary > line.timestamp
                and (next_start is None or boundary < next_start)):
            out.append(fmt_time(boundary))
    return "\n".join(out) + "\n"
