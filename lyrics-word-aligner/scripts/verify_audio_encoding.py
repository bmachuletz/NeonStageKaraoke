#!/usr/bin/env python3
"""Verify that a runtime encode preserves the source audio timeline."""

from __future__ import annotations

import argparse
import array
import json
import math
import subprocess
import sys
from pathlib import Path


RATE = 8000
FRAME = 80  # 10 ms energy envelope


def envelope(path: Path) -> list[float]:
    process = subprocess.Popen(
        ["ffmpeg", "-nostdin", "-v", "error", "-i", str(path), "-map_metadata", "-1",
         "-ac", "1", "-ar", str(RATE), "-f", "s16le", "-"],
        stdout=subprocess.PIPE,
    )
    assert process.stdout is not None
    pending = array.array("h")
    values: list[float] = []
    while chunk := process.stdout.read(FRAME * 2 * 256):
        decoded = array.array("h")
        decoded.frombytes(chunk)
        if sys.byteorder != "little":
            decoded.byteswap()
        pending.extend(decoded)
        complete = len(pending) // FRAME
        for index in range(complete):
            frame = pending[index * FRAME:(index + 1) * FRAME]
            values.append(math.sqrt(sum(sample * sample for sample in frame) / FRAME))
        del pending[:complete * FRAME]
    if process.wait() != 0:
        raise RuntimeError(f"ffmpeg konnte {path} nicht dekodieren")
    return values


def correlation(left: list[float], right: list[float], lag: int) -> float:
    if lag >= 0:
        a, b = left[lag:], right[:len(left) - lag]
    else:
        a, b = left[:len(left) + lag], right[-lag:]
    count = min(len(a), len(b))
    if count < 100:
        return 0.0
    # Normalize energy scale; Vorbis is lossy but its envelope must remain aligned.
    dot = left_energy = right_energy = 0.0
    for x, y in zip(a[:count], b[:count]):
        dot += x * y
        left_energy += x * x
        right_energy += y * y
    return dot / math.sqrt(max(1e-12, left_energy * right_energy))


def verify(source: Path, encoded: Path, maximum_lag_ms: int) -> dict:
    left, right = envelope(source), envelope(encoded)
    search = max(1, maximum_lag_ms // 10)
    candidates = [(lag, correlation(left, right, lag)) for lag in range(-search, search + 1)]
    lag, score = max(candidates, key=lambda item: item[1])
    duration_difference_ms = (len(right) - len(left)) * 10
    return {
        "source": source.name,
        "encoded": encoded.name,
        "lagMilliseconds": lag * 10,
        "durationDifferenceMilliseconds": duration_difference_ms,
        "envelopeCorrelation": round(score, 6),
        "verified": abs(lag * 10) <= 20 and abs(duration_difference_ms) <= 30 and score >= 0.97,
        "method": "decoded-energy-cross-correlation-v1",
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("encoded", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--maximum-lag-ms", type=int, default=250)
    args = parser.parse_args()
    result = verify(args.source, args.encoded, args.maximum_lag_ms)
    rendered = json.dumps(result, ensure_ascii=False, indent=2)
    if args.output:
        args.output.write_text(rendered, encoding="utf-8")
    else:
        print(rendered)
    return 0 if result["verified"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
