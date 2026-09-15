#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
SERVER_PROJECT="${PROJECT_ROOT}/src/Karaoke.Server/Karaoke.Server.csproj"
SERVER_DLL="${PROJECT_ROOT}/src/Karaoke.Server/bin/Debug/net10.0/Karaoke.Server.dll"

if [[ ! -f "${SERVER_DLL}" ]]; then
  echo "Fehler: Der Server wurde noch nicht gebaut." >&2
  echo "Bitte zuerst ausführen: ${SCRIPT_DIR}/build.sh" >&2
  exit 1
fi

# Priorität: erstes Argument, KARAOKE_LIBRARY_PATH, danach appsettings.json.
if [[ -n "${1:-}" ]]; then
  export Karaoke__LibraryPath="$1"
elif [[ -n "${KARAOKE_LIBRARY_PATH:-}" ]]; then
  export Karaoke__LibraryPath="${KARAOKE_LIBRARY_PATH}"
fi

if [[ -n "${KARAOKE_DATABASE_PATH:-}" ]]; then
  export Karaoke__DatabasePath="${KARAOKE_DATABASE_PATH}"
else
  # Relative Datenbankpfade hängen sonst vom Startverzeichnis ab und können
  # unbemerkt eine zweite, leere karaoke.db erzeugen.
  export Karaoke__DatabasePath="${PROJECT_ROOT}/data/karaoke.db"
fi

export Karaoke__PublicBaseUrl="${NEONSTAGE_PUBLIC_URL:-http://cloud.hdvtec.de:5274}"
export NEONSTAGE_YT_DLP_PATH="${NEONSTAGE_YT_DLP_PATH:-${PROJECT_ROOT}/.tools/yt-dlp}"

echo "Karaoke-Server wird gestartet …"
if [[ -n "${Karaoke__LibraryPath:-}" ]]; then
  echo "Musikbibliothek: ${Karaoke__LibraryPath}"
fi
echo "Adresse: http://localhost:5274"
if [[ -n "${Karaoke__PublicBaseUrl:-}" ]]; then
  echo "Einladungsadresse: ${Karaoke__PublicBaseUrl}"
else
  echo "Einladungsadresse: wird automatisch aus der LAN-IP ermittelt"
fi
echo "Datenbank: ${Karaoke__DatabasePath}"
echo "yt-dlp: ${NEONSTAGE_YT_DLP_PATH}"

exec dotnet run --project "${SERVER_PROJECT}" --no-build
