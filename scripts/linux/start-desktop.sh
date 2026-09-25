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

# Ein explizites Argument bleibt eine verbindliche Vorgabe. Ohne Argument ist
# localhost nur der Erstwert; danach gewinnt die im Editor gespeicherte URL.
if [[ -n ${1:-} ]]; then
  export NEONSTAGE_SERVER_URL=$1
else
  export NEONSTAGE_DEFAULT_SERVER_URL="${NEONSTAGE_DEFAULT_SERVER_URL:-http://localhost:5274}"
fi

echo "Neon Stage Karaoke Desktop wird gestartet …"
echo "Serveradresse: ${NEONSTAGE_SERVER_URL:-${KARAOKE_SERVER:-gespeicherte Einstellung oder $NEONSTAGE_DEFAULT_SERVER_URL}}"

exec dotnet run --project "${DESKTOP_PROJECT}" --no-build
