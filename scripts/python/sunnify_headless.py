#!/usr/bin/env python3
"""Minimal headless bridge for sunnypatell/sunnify-spotify-downloader.

Sunnify is a GUI application, but its MusicScraper is synchronous and can be
used without constructing a window. Keep this adapter deliberately small so a
future Sunnify update has one obvious compatibility boundary.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("spotify_url")
    parser.add_argument("output_directory", type=Path)
    parser.add_argument("--sunnify-source", type=Path, required=True)
    parser.add_argument("--format", default="mp3", choices=("mp3", "m4a", "flac", "opus", "wav"))
    parser.add_argument("--quality", default="320")
    args = parser.parse_args()

    source = args.sunnify_source.resolve()
    if not (source / "Spotify_Downloader.py").is_file():
        parser.error(f"Sunnify source not found: {source}")
    sys.path.insert(0, str(source))
    # Qt must not attempt to connect to Wayland/X11 on a server.
    os.environ.setdefault("QT_QPA_PLATFORM", "offscreen")

    from PyQt6.QtCore import QCoreApplication
    from Spotify_Downloader import MusicScraper, _METADATA_WRITERS, _fetch_cover_bytes

    app = QCoreApplication.instance() or QCoreApplication([])
    del app  # Keep Qt's signal implementation initialized; no event loop is needed.
    args.output_directory.mkdir(parents=True, exist_ok=True)
    scraper = MusicScraper(audio_format=args.format, audio_quality=args.quality)
    completed: list[str] = []
    errors: list[str] = []
    landed: list[dict] = []

    def receive_metadata(metadata: dict) -> None:
        path = Path(metadata.get("file", ""))
        if not path.is_file():
            return
        writer = _METADATA_WRITERS.get(path.suffix.lower())
        if writer is not None:
            writer(str(path), metadata, _fetch_cover_bytes(metadata.get("cover", "")))
        landed.append({**metadata, "file": str(path.resolve())})

    scraper.add_song_meta.connect(receive_metadata)
    scraper.PlaylistCompleted.connect(completed.append)
    scraper.error_signal.connect(errors.append)
    scraper.scrape_track(args.spotify_url, str(args.output_directory))

    if not landed:
        print(json.dumps({"ok": False, "messages": completed, "errors": errors}, ensure_ascii=False))
        return 1
    print(json.dumps({"ok": True, "track": landed[-1], "messages": completed}, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
