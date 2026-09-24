#!/usr/bin/env bash
set -euo pipefail

runtime=$(cd "$(dirname "$0")" && pwd)
data_root="$HOME/Library/Application Support/NeonStage/server"
library_root="$HOME/Music/NeonStage"
mkdir -p "$data_root/usdb-cache" "$library_root"

export PATH="$runtime:$PATH"
export Karaoke__LibraryPath=${Karaoke__LibraryPath:-$library_root}
export Karaoke__DatabasePath=${Karaoke__DatabasePath:-$data_root/karaoke.db}
export Usdb__CachePath=${Usdb__CachePath:-$data_root/usdb-cache}
export Karaoke__PublicBaseUrl=${Karaoke__PublicBaseUrl:-http://127.0.0.1:5274}
export ASPNETCORE_URLS=${ASPNETCORE_URLS:-http://0.0.0.0:5274}
export ASPNETCORE_ENVIRONMENT=${ASPNETCORE_ENVIRONMENT:-Production}
exec "$runtime/Karaoke.Server" "$@"
