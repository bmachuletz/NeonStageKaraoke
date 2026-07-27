#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"

if ! command -v dotnet >/dev/null 2>&1; then
  echo "Fehler: Das .NET SDK wurde nicht gefunden." >&2
  exit 1
fi

echo "Neon Stage Karaoke wird wiederhergestellt …"
PROJECTS=(
  "LrcMatcher/LrcMatcher.csproj"
  "src/Karaoke.Contracts/Karaoke.Contracts.csproj"
  "src/Karaoke.Server/Karaoke.Server.csproj"
  "src/Karaoke.App/Karaoke.App.csproj"
  "src/Karaoke.App.Desktop/Karaoke.App.Desktop.csproj"
  "src/Karaoke.App.Android/Karaoke.App.Android.csproj"
)
for project in "${PROJECTS[@]}"; do
  echo "  Restore: ${project}"
  dotnet restore "${PROJECT_ROOT}/${project}" --disable-parallel
done

echo "Neon Stage Karaoke wird gebaut …"
# Der serielle Build umgeht einen beobachteten CLR-Fehler bei parallelen Builds des Android-Projekts.
dotnet build "${PROJECT_ROOT}/Karaoke.slnx" --no-restore --maxcpucount:1

echo "Build erfolgreich abgeschlossen."
