#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
DESKTOP_PROJECT="${PROJECT_ROOT}/src/Karaoke.App.Desktop/Karaoke.App.Desktop.csproj"
DESKTOP_DLL="${PROJECT_ROOT}/src/Karaoke.App.Desktop/bin/Debug/net10.0/Karaoke.App.Desktop.dll"

if [[ ! -f "${DESKTOP_DLL}" ]]; then
  echo "Fehler: Die Desktop-App wurde noch nicht gebaut." >&2
  echo "Bitte zuerst ausführen: ${SCRIPT_DIR}/build.sh" >&2
  exit 1
fi

if ! command -v vlc >/dev/null 2>&1; then
  echo "Fehler: VLC/LibVLC wurde nicht gefunden." >&2
  echo "Bitte VLC über die Paketverwaltung deiner Distribution installieren." >&2
  exit 1
fi

# Priorität: erstes Argument, KARAOKE_SERVER, danach http://localhost:5274.
export KARAOKE_SERVER="${1:-${KARAOKE_SERVER:-http://localhost:5274}}"

echo "Neon Stage Karaoke Desktop wird gestartet …"
echo "Serveradresse: ${KARAOKE_SERVER}"

exec dotnet run --project "${DESKTOP_PROJECT}" --no-build
