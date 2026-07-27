#!/usr/bin/env python3
"""Create compact, playback-synchronous music features without runtime DSP."""

from __future__ import annotations

import argparse
import json
import math
import struct
import subprocess
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("audio", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    rate, frame_size = 8000, 800  # 10 frames/s
    process = subprocess.run(
        ["ffmpeg", "-v", "error", "-i", str(args.audio), "-ac", "1", "-ar", str(rate), "-f", "s16le", "-"],
        check=True, stdout=subprocess.PIPE)
    samples = struct.unpack(f"<{len(process.stdout) // 2}h", process.stdout)
    raw = []
    low = 0.0
    alpha = math.exp(-2 * math.pi * 180 / rate)
    for offset in range(0, len(samples) - frame_size + 1, frame_size):
        chunk = samples[offset:offset + frame_size]
        total = bass = high = 0.0
        previous = float(chunk[0])
        for sample in chunk:
            value = float(sample)
            low = alpha * low + (1 - alpha) * value
            total += value * value
            bass += low * low
            delta = value - previous
            high += delta * delta
            previous = value
        energy = math.sqrt(total / frame_size)
        bass_value = math.sqrt(bass / frame_size)
        high_value = math.sqrt(high / frame_size) * 0.55
        mid_value = max(0.0, energy - bass_value * 0.45 - high_value * 0.2)
        raw.append((energy, bass_value, mid_value, high_value))
    scales = []
    for band in range(4):
        values = sorted(row[band] for row in raw)
        scales.append(max(values[min(len(values) - 1, int(len(values) * 0.95))], 1.0))
    frames = []
    recent = []
    for index, row in enumerate(raw):
        values = [min(1.0, math.sqrt(value / scales[band])) for band, value in enumerate(row)]
        recent.append(values[1])
        if len(recent) > 12:
            recent.pop(0)
        baseline = sum(recent) / len(recent)
        beat = len(recent) > 4 and values[1] > max(0.28, baseline * 1.32)
        frames.append({"timeSeconds": index / 10, "energy": values[0], "bass": values[1],
                       "mid": values[2], "high": values[3], "beat": beat})
    args.output.write_text(json.dumps(frames, separators=(",", ":")), encoding="utf-8")


if __name__ == "__main__":
    main()
