#!/usr/bin/env bash
set -Eeuo pipefail

project_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
unity_project="$project_root/src/Karaoke.Stage.Unity"
unity_editor=${UNITY_EDITOR:-}

if [[ -z "$unity_editor" ]]; then
  unity_editor=$(find "$HOME/Unity/Hub/Editor" -mindepth 3 -maxdepth 3 -type f -path '*/Editor/Unity' -perm -111 \
    -print -quit 2>/dev/null || true)
fi

if [[ -z "$unity_editor" || ! -x "$unity_editor" ]]; then
  echo "Unity 6 Editor nicht gefunden. Setze UNITY_EDITOR=/pfad/zu/Unity." >&2
  exit 1
fi

"$unity_editor" \
  -batchmode \
  -nographics \
  -quit \
  -projectPath "$unity_project" \
  -executeMethod NeonStage.Stage.Editor.NeonStageAndroidBuild.BuildLinux \
  -logFile -

echo "Linux Stage: $unity_project/Builds/Linux/NeonStage"
