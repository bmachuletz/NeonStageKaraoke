#!/usr/bin/env bash
set -Eeuo pipefail

project_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
unity_project="$project_root/src/Karaoke.Stage.Unity"
unity_editor=${UNITY_EDITOR:-}

if [[ -z "$unity_editor" ]]; then
  for candidate in \
    "$HOME"/Unity/Hub/Editor/*/Editor/Unity \
    "$HOME"/.local/share/unity3d/Hub/Editor/*/Editor/Unity \
    /opt/unityhub/Editor/*/Editor/Unity \
    /opt/Unity/Hub/Editor/*/Editor/Unity \
    /opt/unity/Editor/Unity; do
    if [[ -x "$candidate" ]]; then unity_editor=$candidate; break; fi
  done
fi

if [[ -z "$unity_editor" || ! -x "$unity_editor" ]]; then
  echo "Unity 6 Editor nicht gefunden." >&2
  echo "Installiere in Unity Hub das Modul 'Linux Build Support' oder setze UNITY_EDITOR=/pfad/zu/Editor/Unity." >&2
  echo "Server und Editor können unabhängig mit ./scripts/build-appimages.sh --skip-stage gebaut werden." >&2
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
