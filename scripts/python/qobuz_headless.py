#!/usr/bin/env python3
"""Authorized Qobuz purchase-download bridge for the Neon Stage worker.

This client deliberately requests ``intent=download``. It does not use the
streaming intent, extract credentials from Qobuz applications, or bypass an
account entitlement. Qobuz must authorize the configured account for the
matched track or the request fails and the wishlist entry remains untouched.
"""

from __future__ import annotations

import argparse
import difflib
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import socket
import sys
import time
import unicodedata
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urljoin, urlparse
from urllib.request import HTTPRedirectHandler, Request, build_opener


class QobuzError(RuntimeError):
    pass


def normalize(value: str) -> str:
    value = unicodedata.normalize("NFKD", value).encode("ascii", "ignore").decode("ascii")
    return " ".join(re.sub(r"[^a-z0-9]+", " ", value.lower()).split())


def similarity(left: str, right: str) -> float:
    return difflib.SequenceMatcher(None, normalize(left), normalize(right)).ratio()


def artist_name(track: dict) -> str:
    performer = track.get("performer") or {}
    album = track.get("album") or {}
    album_artist = album.get("artist") or {}
    return str(performer.get("name") or album_artist.get("name") or track.get("artist") or "")


def album_name(track: dict) -> str:
    album = track.get("album") or {}
    return str(album.get("title") or "")


def match_score(track: dict, title: str, artist: str, album: str, duration_ms: int) -> tuple[float, float, float]:
    title_score = similarity(str(track.get("title") or ""), title)
    artist_score = similarity(artist_name(track), artist)
    album_score = similarity(album_name(track), album) if album else 1.0
    candidate_seconds = float(track.get("duration") or 0)
    wanted_seconds = duration_ms / 1000 if duration_ms > 0 else 0
    duration_score = 1.0 if not candidate_seconds or not wanted_seconds else max(
        0.0, 1.0 - abs(candidate_seconds - wanted_seconds) / 12.0
    )
    return 0.5 * title_score + 0.3 * artist_score + 0.08 * album_score + 0.12 * duration_score, title_score, artist_score


def checked_https_url(value: str, label: str) -> str:
    parsed = urlparse(value)
    if parsed.scheme != "https" or not parsed.hostname:
        raise QobuzError(f"{label} must be an absolute HTTPS URL")
    try:
        addresses = {item[4][0] for item in socket.getaddrinfo(parsed.hostname, parsed.port or 443)}
    except socket.gaierror as error:
        raise QobuzError(f"Could not resolve {label}: {error}") from error
    for address in addresses:
        ip = ipaddress.ip_address(address)
        if ip.is_private or ip.is_loopback or ip.is_link_local or ip.is_reserved or ip.is_unspecified:
            raise QobuzError(f"{label} resolves to a non-public address")
    return value


class SafeRedirects(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, new_url):  # noqa: ANN001
        checked_https_url(new_url, "Qobuz download redirect")
        return super().redirect_request(request, fp, code, message, headers, new_url)


def request_json(base_url: str, endpoint: str, parameters: dict[str, str], headers: dict[str, str]) -> dict:
    url = checked_https_url(urljoin(base_url.rstrip("/") + "/", endpoint), "Qobuz API URL")
    request = Request(url + "?" + urlencode(parameters), headers=headers)
    try:
        with build_opener(SafeRedirects()).open(request, timeout=45) as response:
            return json.load(response)
    except HTTPError as error:
        detail = error.read(2048).decode("utf-8", "replace")
        raise QobuzError(f"Qobuz API returned HTTP {error.code}: {detail}") from error
    except (URLError, TimeoutError, json.JSONDecodeError) as error:
        raise QobuzError(f"Qobuz API request failed: {error}") from error


def safe_filename(title: str, artist: str, extension: str) -> str:
    value = re.sub(r"[\\/:*?\"<>|\x00-\x1f]", "_", f"{title} - {artist}").strip(" .")
    return (value or "Qobuz track")[:180] + extension


def download(url: str, destination: Path, expected_flac: bool) -> None:
    checked_https_url(url, "Qobuz download URL")
    temporary = destination.with_suffix(destination.suffix + ".part")
    request = Request(url, headers={"User-Agent": "NeonStageKaraoke/1.0"})
    try:
        with build_opener(SafeRedirects()).open(request, timeout=90) as source, temporary.open("wb") as target:
            while chunk := source.read(1024 * 1024):
                target.write(chunk)
    except (HTTPError, URLError, TimeoutError, OSError) as error:
        temporary.unlink(missing_ok=True)
        raise QobuzError(f"Qobuz audio download failed: {error}") from error
    if temporary.stat().st_size < 64 * 1024:
        temporary.unlink(missing_ok=True)
        raise QobuzError("Qobuz returned an implausibly small audio file")
    with temporary.open("rb") as downloaded:
        magic = downloaded.read(4)
    if expected_flac and magic != b"fLaC":
        temporary.unlink(missing_ok=True)
        raise QobuzError("Qobuz did not return the requested FLAC file")
    temporary.replace(destination)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--title", required=True)
    parser.add_argument("--artist", required=True)
    parser.add_argument("--album", default="")
    parser.add_argument("--duration-ms", type=int, default=0)
    parser.add_argument("--track-id", default="")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    app_id = os.environ.get("QOBUZ_APP_ID", "").strip()
    app_secret = os.environ.get("QOBUZ_APP_SECRET", "").strip()
    user_token = os.environ.get("QOBUZ_USER_AUTH_TOKEN", "").strip()
    base_url = os.environ.get("QOBUZ_API_BASE_URL", "https://www.qobuz.com/api.json/0.2").strip()
    format_id = int(os.environ.get("QOBUZ_FORMAT_ID", "6"))
    if not app_id or not app_secret or not user_token:
        raise QobuzError("Qobuz app credentials or authorized user token are missing")
    if format_id not in {5, 6, 7, 27}:
        raise QobuzError("Unsupported Qobuz format ID")

    headers = {"X-App-Id": app_id, "X-User-Auth-Token": user_token, "User-Agent": "NeonStageKaraoke/1.0"}
    if args.track_id:
        track_id = args.track_id.strip()
        if not re.fullmatch(r"[0-9]+", track_id):
            raise QobuzError("Invalid Qobuz track ID")
        track = {"id": track_id, "title": args.title, "performer": {"name": args.artist}}
        score = 1.0
    else:
        search = request_json(base_url, "track/search", {
            "app_id": app_id, "query": f"{args.artist} {args.title}", "limit": "25"
        }, headers)
        items = (search.get("tracks") or {}).get("items") or search.get("items") or []
        ranked = sorted(((*match_score(item, args.title, args.artist, args.album, args.duration_ms), item)
                         for item in items), key=lambda value: value[0], reverse=True)
        if not ranked:
            raise QobuzError("No Qobuz catalog match was found")
        score, title_score, artist_score, track = ranked[0]
        if score < 0.76 or title_score < 0.78 or artist_score < 0.62:
            raise QobuzError(f"Best Qobuz match is uncertain (score {score:.0%})")
        track_id = str(track.get("id") or "")
        if not track_id:
            raise QobuzError("Matched Qobuz track has no ID")

    request_ts = str(int(time.time()))
    signature_payload = (
        f"trackgetFileUrlformat_id{format_id}intentdownloadtrack_id{track_id}{request_ts}{app_secret}"
    )
    request_sig = hashlib.md5(signature_payload.encode("utf-8"), usedforsecurity=False).hexdigest()
    file_response = request_json(base_url, "track/getFileUrl", {
        "app_id": app_id,
        "format_id": str(format_id),
        "intent": "download",
        "request_ts": request_ts,
        "request_sig": request_sig,
        "track_id": track_id,
    }, headers)
    file_url = str(file_response.get("url") or "")
    if not file_url:
        raise QobuzError("Qobuz did not authorize a purchase download for this track")

    extension = ".mp3" if format_id == 5 else ".flac"
    args.output.mkdir(parents=True, exist_ok=True)
    destination = args.output / safe_filename(args.title, args.artist, extension)
    download(file_url, destination, expected_flac=format_id != 5)
    print(json.dumps({"track": {"file": str(destination), "qobuzId": track_id,
                                "matchScore": round(score, 4)}, "errors": []}, ensure_ascii=False))
    print(f"Qobuz: {track.get('title')} · {artist_name(track)} ({score:.0%})", file=sys.stderr)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (QobuzError, ValueError) as error:
        print(json.dumps({"errors": [str(error)]}, ensure_ascii=False))
        raise SystemExit(1)
