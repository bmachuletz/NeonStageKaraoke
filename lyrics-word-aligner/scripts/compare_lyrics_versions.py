#!/usr/bin/env python3
"""Compare an automatic lyrics version against a manually corrected reference.

Only manually corrected segments carry ground truth.  A reference version that
was itself derived from an earlier automatic run repeats that run's timings
everywhere else, so an average over all words mostly measures how reproducible
the pipeline is - improvements stay invisible and regressions get masked.  This
script therefore reports three nested populations:

  * words the reviewer edited individually (the tightest ground truth),
  * all words inside lines the reviewer touched,
  * every word in the window, for context only.

Usage:
    compare_lyrics_versions.py REFERENCE.json CANDIDATE.json [LIMIT_SECONDS]

Both files are the JSON returned by
``GET /api/songs/{id}/lyrics/versions/{versionId}``.  LIMIT_SECONDS restricts
the evaluation to the part of the song the reference is trusted for.
"""
from __future__ import annotations

import json
import re
import sys
import unicodedata

import numpy as np


def load_lines(path: str) -> list:
    document = json.loads(json.load(open(path, encoding="utf-8"))["documentJson"])
    return document.get("Lines") or document.get("lines") or []


def seconds(value) -> float:
    if isinstance(value, (int, float)):
        return float(value)
    text = str(value)
    sign = -1.0 if text.startswith("-") else 1.0
    total = 0.0
    for part in text.lstrip("-").split(":"):
        total = total * 60.0 + float(part)
    return sign * total


def field(node: dict, name: str):
    return node.get(name) or node.get(name[0].lower() + name[1:])


def normalized(text: str) -> str:
    return re.sub(r"[^\w]", "",
                  unicodedata.normalize("NFKC", str(text)).lower(), flags=re.UNICODE)


def words_of(lines: list) -> list[dict]:
    out = []
    for index, line in enumerate(lines):
        for word in (field(line, "Children") or []):
            out.append({
                "line": index + 1,
                "text": field(word, "Text") or "",
                "start": seconds(field(word, "Start")),
                "end": seconds(field(word, "End")),
                "line_manual": bool(line.get("IsManuallyAdjusted")
                                    or line.get("isManuallyAdjusted")),
                "word_manual": str(field(word, "Origin")) == "ManuallyAdjusted",
            })
    return out


def report(title: str, pairs: list[tuple[dict, dict]]) -> None:
    if not pairs:
        print(f"{title}: keine Wörter")
        return
    start = np.abs(np.array([b["start"] - a["start"] for a, b in pairs]))
    end = np.abs(np.array([b["end"] - a["end"] for a, b in pairs]))
    print(f"{title} ({len(pairs)} Wörter)")
    print(f"  Wortstart  Mittel {start.mean() * 1000:7.1f} ms   "
          f"Median {np.median(start) * 1000:7.1f} ms   "
          f"p90 {np.percentile(start, 90) * 1000:7.1f} ms")
    print(f"  Wortende   Mittel {end.mean() * 1000:7.1f} ms   "
          f"Median {np.median(end) * 1000:7.1f} ms   "
          f"p90 {np.percentile(end, 90) * 1000:7.1f} ms")
    shares = "   ".join(
        f"<={int(tolerance * 1000)}ms {float((start <= tolerance).mean()) * 100:5.1f}%"
        for tolerance in (0.05, 0.10, 0.20, 0.30))
    print(f"  Wortstart  {shares}")
    print()


def main() -> int:
    if not 3 <= len(sys.argv) <= 4:
        print(__doc__)
        return 2
    reference_path, candidate_path = sys.argv[1], sys.argv[2]
    limit = float(sys.argv[3]) if len(sys.argv) == 4 else float("inf")

    reference = words_of(load_lines(reference_path))
    candidate = words_of(load_lines(candidate_path))
    if len(reference) != len(candidate):
        print(f"Abbruch: Wortanzahl unterschiedlich "
              f"({len(reference)} vs {len(candidate)}).")
        return 1
    mismatched = [index for index, (a, b) in enumerate(zip(reference, candidate))
                  if a["start"] <= limit
                  and normalized(a["text"]) != normalized(b["text"])]
    if mismatched:
        print(f"Abbruch: {len(mismatched)} Wörter im Messfenster haben "
              f"abweichenden Text, erste Indizes {mismatched[:5]}.")
        return 1

    pairs = [(a, b) for a, b in zip(reference, candidate) if a["start"] <= limit]
    horizon = "gesamter Song" if limit == float("inf") else f"bis {limit:.0f} s"
    print(f"Referenz {reference_path}\nKandidat {candidate_path}\nFenster: {horizon}\n")
    report("Wortgenau manuell korrigierte Wörter",
           [(a, b) for a, b in pairs if a["word_manual"]])
    report("Alle Wörter in manuell korrigierten Zeilen",
           [(a, b) for a, b in pairs if a["line_manual"]])
    report("Alle Wörter im Fenster (ohne Ground-Truth-Filter)", pairs)

    # A rigid line shift and a distorted line need different repairs, so keep
    # them apart instead of averaging them into one number.
    print("Zeilen mit Abweichung (Versatz in ms, positiv = Kandidat zu spät):")
    print(f"{'Zeile':>5} {'Start':>9} {'Ende':>9} {'Streuung':>9}  Art")
    grouped: dict[int, list] = {}
    for a, b in pairs:
        grouped.setdefault(a["line"], []).append((a, b))
    for index in sorted(grouped):
        items = grouped[index]
        shifts = np.array([b["start"] - a["start"] for a, b in items])
        if np.max(np.abs(shifts)) <= 0.002:
            continue
        first = (items[0][1]["start"] - items[0][0]["start"]) * 1000
        last = (items[-1][1]["end"] - items[-1][0]["end"]) * 1000
        spread = float(shifts.std()) * 1000
        kind = "starre Verschiebung" if spread < 20 else "innere Verzerrung"
        print(f"{index:>5} {first:>9.0f} {last:>9.0f} {spread:>9.0f}  {kind}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
