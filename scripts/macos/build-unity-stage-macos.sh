#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
unity_editor="${UNITY_EDITOR:-}"
if [[ -z "$unity_editor" ]]; then
  unity_editor="$(find /Applications/Unity/Hub/Editor -path '*/Unity.app/Contents/MacOS/Unity' -type f 2>/dev/null | sort -V | tail -1)"
fi
if [[ -z "$unity_editor" || ! -x "$unity_editor" ]]; then
  echo "Unity Editor nicht gefunden. UNITY_EDITOR auf .../Unity.app/Contents/MacOS/Unity setzen." >&2
  exit 2
fi

"$unity_editor" -batchmode -quit \
  -projectPath "$repo_root/src/Karaoke.Stage.Unity" \
  -executeMethod NeonStage.Stage.Editor.NeonStageAndroidBuild.BuildMacOS \
  -logFile "$repo_root/Builds/macos-unity.log"

echo "Build: $repo_root/src/Karaoke.Stage.Unity/Builds/macOS/NeonStage Karaoke.app"
