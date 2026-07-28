from __future__ import annotations

import argparse
import json
from pathlib import Path

from .full_transcription import run


def _write_status(output: Path, **changes) -> None:
    path = output / "status.json"
    current = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
    current.update(changes)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(current, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(path)


def main() -> int:
    parser = argparse.ArgumentParser(description="Isolated Neon Stage full-lyrics transcription worker")
    parser.add_argument("--job-id", required=True)
    parser.add_argument("--audio", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--language", required=True)
    parser.add_argument("--device", required=True)
    parser.add_argument("--canonical", type=Path)
    parser.add_argument("--separate", action="store_true")
    args = parser.parse_args()
    try:
        result = run(
            args.audio, args.output, language=args.language, separate=args.separate,
            device=args.device, canonical_path=args.canonical,
            progress=lambda percent, message: _write_status(
                args.output, state="processing", percent=percent, message=message),
        )
        result.update({
            "job_id": args.job_id, "state": "completed", "percent": 100,
            "message": "Vollständige Lyrics erkannt",
            "download_lrc": f"/jobs/{args.job_id}/{result['output_lrc']}",
            "download_report": f"/jobs/{args.job_id}/{result['output_report']}",
            "download_text": f"/jobs/{args.job_id}/{result['output_text']}",
        })
        _write_status(args.output, **result)
        return 0
    except BaseException as error:
        _write_status(args.output, state="failed", percent=100,
                      message=str(error), error=str(error))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
