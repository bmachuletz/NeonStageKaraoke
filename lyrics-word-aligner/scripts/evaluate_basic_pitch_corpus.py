#!/usr/bin/env python3
"""Run reproducible Basic-Pitch treatment diagnostics over existing jobs.

The source jobs are read-only. Every treatment is written to a temporary
directory and discarded after its aggregate metrics have been collected.
This is not a ground-truth accuracy test; it detects unstable candidate volume,
large movements, weak stem evidence and genre-dependent behavior before select
mode is enabled.
"""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from app.basic_pitch_postprocess import run  # noqa: E402


def job_files(directory: Path) -> dict[str, Path] | None:
    lrcs = sorted(directory.glob("*.word-synced.lrc"))
    if not lrcs:
        return None
    lrc = lrcs[0]
    stem = lrc.name.removesuffix(".word-synced.lrc")

    def preferred(suffix: str) -> Path | None:
        exact = [directory / f"{stem}{suffix}.flac", directory / f"{stem}{suffix}.ogg"]
        return next((path for path in exact if path.exists()),
                    next(iter(sorted(directory.glob(f"*{suffix}.flac"))
                              + sorted(directory.glob(f"*{suffix}.ogg"))), None))

    audio = next((directory / f"{stem}{extension}" for extension in
                  (".flac", ".mp3", ".ogg", ".wav")
                  if (directory / f"{stem}{extension}").exists()), None)
    report = directory / f"{stem}.alignment.json"
    vocals, instrumental = preferred(".vocals"), preferred(".instrumental")
    if not all((audio, report.exists(), vocals, instrumental)):
        return None
    return {"audio": audio, "lrc": lrc, "report": report,
            "vocals": vocals, "instrumental": instrumental}


def evaluate(directory: Path, files: dict[str, Path]) -> dict:
    baseline = json.loads(files["report"].read_text(encoding="utf-8"))
    with tempfile.TemporaryDirectory(prefix="neon-stage-basic-pitch-") as output:
        result = run(
            files["audio"], files["lrc"], Path(output), language="auto",
            separator=True, device="cpu", provided_vocals=files["vocals"],
            provided_instrumental=files["instrumental"], baseline_report=files["report"])
    evidence = result["basic_pitch_evidence"]
    onset_shifts = [abs(float(item["shift_ms"])) for item in evidence["details"]
                    if item.get("accepted")]
    release_shifts = [abs(float(item["shift_ms"])) for item in evidence["release_details"]
                      if item.get("accepted")]
    confidence = evidence.get("alignment_confidence", {})
    word_pitch = evidence.get("word_pitch_evidence", {})
    repetitions = evidence.get("repetition_fingerprints", {})
    return {
        "job": directory.name, "song": files["audio"].stem,
        "lines": result.get("lines"),
        "baseline_quality": baseline.get("quality", {}).get("score"),
        "treatment_quality": result.get("quality", {}).get("score"),
        "note_events": evidence.get("note_events", 0),
        "vocal_pitch_range": evidence.get("vocal_pitch_range"),
        "raw_onsets_outside_vocal_range": evidence.get(
            "raw_onsets_outside_vocal_range", 0),
        "line_onsets_applied": evidence.get("applied_onsets", 0),
        "releases_applied": evidence.get("applied_releases", 0),
        "internal_supported": evidence.get("supported_internal_boundaries", 0),
        "internal_confirmed": evidence.get("confirmed_internal_boundaries", 0),
        "internal_applied": evidence.get("applied_internal_boundaries", 0),
        "maximum_onset_shift_ms": max(onset_shifts, default=0),
        "maximum_release_shift_ms": max(release_shifts, default=0),
        "large_shift_review_lines": len(confidence.get("review_lines", [])),
        "strongly_supported_lines": confidence.get("strongly_supported_lines", 0),
        "words_with_pitch": word_pitch.get("words_with_pitch", 0),
        "sustained_words": word_pitch.get("sustained_words", 0),
        "repetition_groups": repetitions.get("repeated_groups", 0),
        "repetition_outliers": sum(len(group.get("outlier_lines", []))
                                   for group in repetitions.get("groups", [])),
        "drift_supported": evidence.get("drift", {}).get("supported", False),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path, help="Aligner data/output directory")
    parser.add_argument("--limit", type=int, default=5)
    parser.add_argument("--match", action="append", default=[],
                        help="Case-insensitive song substring; may be repeated")
    args = parser.parse_args()
    jobs = []
    seen_songs = set()
    matches = [value.casefold() for value in args.match]
    for directory in sorted(args.root.iterdir()):
        if not directory.is_dir() or (files := job_files(directory)) is None:
            continue
        song = files["audio"].stem.casefold()
        if matches and not any(value in song for value in matches):
            continue
        if song in seen_songs:
            continue
        seen_songs.add(song)
        jobs.append((directory, files))
        if len(jobs) >= max(1, args.limit):
            break
    if not jobs:
        parser.error("no complete alignment jobs matched")
    results = [evaluate(directory, files) for directory, files in jobs]
    print(json.dumps({"method": "basic-pitch-corpus-shadow-evaluation-v1",
                      "jobs": results}, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
