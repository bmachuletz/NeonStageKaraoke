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
if [[ -z "$unity_editor" || ! -x "$unity_editor" ]]; then
  echo "Unity $unity_version nicht gefunden. UNITY_EDITOR auf .../Unity.app/Contents/MacOS/Unity setzen." >&2
  exit 2
fi

mkdir -p "$repo_root/Builds"
"$unity_editor" -batchmode -quit \
  -projectPath "$repo_root/src/Karaoke.Stage.Unity" \
  -executeMethod NeonStage.Stage.Editor.NeonStageAndroidBuild.BuildMacOS \
  -logFile "$repo_root/Builds/macos-unity.log"

echo "Build: $repo_root/src/Karaoke.Stage.Unity/Builds/macOS/NeonStage Karaoke.app"
