#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
"$repo_root/scripts/macos/prepare-build.sh"
unity_version=$(sed -n 's/^m_EditorVersion: //p' \
  "$repo_root/src/Karaoke.Stage.Unity/ProjectSettings/ProjectVersion.txt" | head -n 1)
unity_editor="${UNITY_EDITOR:-}"
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
if [[ -z "$unity_editor" || ! -x "$unity_editor" ]]; then
  echo "Keine Unity-6000.x-Version gefunden. UNITY_EDITOR auf .../Unity.app/Contents/MacOS/Unity setzen." >&2
  exit 2
fi

mkdir -p "$repo_root/Builds"
"$unity_editor" -batchmode -quit \
  -projectPath "$repo_root/src/Karaoke.Stage.Unity" \
  -executeMethod NeonStage.Stage.Editor.NeonStageAndroidBuild.BuildMacOS \
  -logFile "$repo_root/Builds/macos-unity.log"

echo "Build: $repo_root/src/Karaoke.Stage.Unity/Builds/macOS/NeonStage Karaoke.app"
