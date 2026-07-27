#!/usr/bin/env python3
"""Rebuild an interval-enhanced LRC from an existing alignment report."""

from __future__ import annotations

import argparse
import json
from pathlib import Path


def timestamp(value: float, brackets: str) -> str:
    value = max(0.0, value)
    minutes = int(value // 60)
    seconds = value - minutes * 60
    return f"{brackets[0]}{minutes:02d}:{seconds:05.2f}{brackets[1]}"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("report", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    report = json.loads(args.report.read_text(encoding="utf-8"))
    lines: list[str] = []
    for detail in report["details"]:
        line = timestamp(float(detail["timestamp"]), "[]")
        words = detail.get("words") or []
        if detail.get("status") == "ok" and words:
            parts = []
            for word in words:
                start = timestamp(float(word["start"]), "<>")[1:-1]
                end = timestamp(float(word.get("end", word["start"])), "<>")[1:-1]
                parts.append(f"<{start},{end}>{word['word']}")
            line += " ".join(parts)
        else:
            line += detail["text"]
        lines.append(line)
    args.output.write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
