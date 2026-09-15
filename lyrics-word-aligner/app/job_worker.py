from __future__ import annotations

import argparse
import json
from pathlib import Path

from .easyaligner_profile import run


def _write_status(job_dir: Path, **changes) -> dict:
    path = job_dir / "status.json"
    current = {}
    if path.exists():
        try:
            current = json.loads(path.read_text(encoding="utf-8"))
        except Exception:
            current = {}
    current.update(changes)
    temporary = path.with_suffix(".tmp")
    temporary.write_text(json.dumps(
        current, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    temporary.replace(path)
    return current


def main() -> int:
    parser = argparse.ArgumentParser(description="Isolated Neon Stage alignment worker")
    parser.add_argument("--job-id", required=True)
    parser.add_argument("--audio", type=Path, required=True)
    parser.add_argument("--lyrics", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--language", required=True)
    parser.add_argument("--device", required=True)
    parser.add_argument("--separate", action="store_true")
    parser.add_argument("--provided-vocals", type=Path)
    parser.add_argument("--provided-instrumental", type=Path)
    args = parser.parse_args()
    try:
        def progress(percent: int, message: str) -> None:
            _write_status(args.output, state="processing", percent=percent, message=message)

        result = run(
            args.audio, args.lyrics, args.output,
            language=args.language, separator=args.separate,
            device=args.device, progress=progress,
            provided_vocals=args.provided_vocals,
            provided_instrumental=args.provided_instrumental,
        )
        result.update({
            "job_id": args.job_id,
            "state": "completed",
            "percent": 100,
            "message": "Fertig",
            "download_lrc": f"/jobs/{args.job_id}/{result['output_lrc']}",
            "download_report": f"/jobs/{args.job_id}/{result['output_report']}",
        })
        _write_status(args.output, **result)
        return 0
    except BaseException as error:
        # OOMs and native model exceptions must still result in a useful job
        # status.  Process exit then guarantees release of all CUDA allocations.
        _write_status(
            args.output, state="failed", percent=100,
            message=str(error), error=str(error),
        )
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
