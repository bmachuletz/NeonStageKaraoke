from __future__ import annotations
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
    for raw in Path(path).read_text(encoding="utf-8-sig").splitlines():
        matches = list(TIME_RE.finditer(raw))
        if not matches:
            if raw.strip() and (METADATA_RE.match(raw.strip()) or raw.lstrip().startswith("[")):
                headers.append(raw)
            elif raw.strip():
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
        if not text:
            continue
        for match in matches:
            timestamp = parse_timestamp(match)
            lines.append(LrcLine(timestamp, text, raw, words=[dict(word) for word in words],
                                 source_timestamp=timestamp))
    lines.sort(key=lambda x: x.timestamp)
    if not lines:
        lines = [LrcLine(0.0, text, text, timed_input=False) for text in plain_lines]
    if not lines:
        raise ValueError("Die Lyrics-Datei enthält keinen verwertbaren Text.")
    return headers, lines


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


def render_enhanced_lrc(headers: list[str], lines: list[LrcLine]) -> str:
    out = list(headers)
    if out:
        out.append("")
    for line in lines:
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
    return "\n".join(out) + "\n"
