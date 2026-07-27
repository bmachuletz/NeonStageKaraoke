#!/usr/bin/env python3
from __future__ import annotations
import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from app.lrc import render_enhanced_lrc
from app.models import AlignmentConfig, LrcLine
from app.repair import repair_collapsed_timings
from app.syllables import enrich_lines_with_syllables
from app.validator import validate

parser = argparse.ArgumentParser(description="Repariert kollabierte Wortzeiten eines bestehenden Alignments.")
parser.add_argument("lrc", type=Path)
parser.add_argument("alignment", type=Path)
args = parser.parse_args()

report = json.loads(args.alignment.read_text(encoding="utf-8"))
lines = []
for detail in report.get("details", []):
    line = LrcLine(float(detail["timestamp"]), str(detail["text"]), "")
    line.words = detail.get("words", [])
    lines.append(line)
cfg = AlignmentConfig(language=report.get("language", "de"))
repaired = repair_collapsed_timings(lines, cfg)
if repaired == 0:
    print("Keine kollabierten Zeilen gefunden.")
    raise SystemExit(0)
syllables = enrich_lines_with_syllables(lines, cfg.language)
summary = validate(lines, cfg)
report.update(summary)
report["repaired_collapsed_lines"] = int(report.get("repaired_collapsed_lines", 0)) + repaired
report["syllable_alignment"] = syllables
report["details"] = [{
    "timestamp": line.timestamp, "text": line.text, "status": line.status,
    "reason": line.reason, "words": line.words,
} for line in lines]
headers = [line.rstrip() for line in args.lrc.read_text(encoding="utf-8").splitlines()
           if line.startswith(("[ar:", "[al:", "[ti:", "[by:", "[offset:", "[re:"))]
args.lrc.write_text(render_enhanced_lrc(headers, lines), encoding="utf-8")
args.alignment.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
print(f"{repaired} kollabierte Zeile(n) repariert; {summary['uncertain']} weiterhin unsicher.")
