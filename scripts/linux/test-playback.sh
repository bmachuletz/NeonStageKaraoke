#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROJECT="$ROOT_DIR/tests/Karaoke.Server.PlaybackTests/Karaoke.Server.PlaybackTests.csproj"
DLL="$ROOT_DIR/tests/Karaoke.Server.PlaybackTests/bin/Debug/net10.0/Karaoke.Server.PlaybackTests.dll"

dotnet restore "$PROJECT" --disable-parallel
dotnet build "$PROJECT" --no-restore -m:1
dotnet "$DLL"
