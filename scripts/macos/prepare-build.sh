#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
unity_project="$repo_root/src/Karaoke.Stage.Unity"
version_file="$unity_project/ProjectSettings/ProjectVersion.txt"

[[ $(uname -s) == Darwin ]] || {
  echo "macOS-Builds müssen auf einem Mac vorbereitet werden." >&2
  exit 2
}
[[ -f "$version_file" ]] || { echo "Unity-Projektversion fehlt: $version_file" >&2; exit 2; }
unity_version=$(sed -n 's/^m_EditorVersion: //p' "$version_file" | head -n 1)
[[ -n "$unity_version" ]] || { echo "Unity-Version konnte nicht gelesen werden." >&2; exit 2; }

unity_editor=${UNITY_EDITOR:-}
if [[ -z "$unity_editor" ]]; then
  unity_editor="/Applications/Unity/Hub/Editor/$unity_version/Unity.app/Contents/MacOS/Unity"
fi
[[ -n "$unity_editor" && -x "$unity_editor" ]] || {
  echo "Unity $unity_version wurde nicht gefunden. UNITY_EDITOR auf Unity.app/Contents/MacOS/Unity setzen." >&2
  exit 2
}

unity_contents=$(cd "$(dirname "$unity_editor")/.." && pwd)
[[ -d "$unity_contents/PlaybackEngines/MacStandaloneSupport" ]] || {
  echo "Unity-Modul 'Mac Build Support (Mono)' fehlt für $unity_editor." >&2
  exit 2
}

mkdir -p "$repo_root/Builds"
echo "Prepare macOS: Unity $unity_version · $unity_editor"
"$unity_editor" -batchmode -nographics -quit \
  -projectPath "$unity_project" \
  -logFile "$repo_root/Builds/macos-prepare.log"

echo "macOS-Prepare erfolgreich: Pakete aufgelöst und Stage-Skripte kompiliert."
