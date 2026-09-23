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
if [[ ! -x "$unity_editor" ]]; then
  unity_editor=""
  best_minor=-1
  best_patch=-1
  for candidate in /Applications/Unity/Hub/Editor/6000.*/Unity.app/Contents/MacOS/Unity; do
    [[ -x "$candidate" ]] || continue
    installation=${candidate#/Applications/Unity/Hub/Editor/}
    installation=${installation%%/*}
    if [[ $installation =~ ^6000\.([0-9]+)\.([0-9]+) ]]; then
      minor=${BASH_REMATCH[1]}
      patch=${BASH_REMATCH[2]}
      if (( minor > best_minor || (minor == best_minor && patch > best_patch) )); then
        unity_editor=$candidate
        best_minor=$minor
        best_patch=$patch
      fi
    fi
  done
fi
[[ -n "$unity_editor" && -x "$unity_editor" ]] || {
  echo "Keine Unity-6000.x-Version wurde gefunden. UNITY_EDITOR auf Unity.app/Contents/MacOS/Unity setzen." >&2
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
