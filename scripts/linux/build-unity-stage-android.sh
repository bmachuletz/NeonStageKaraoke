#!/usr/bin/env bash
set -Eeuo pipefail

project_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
unity_project="$project_root/src/Karaoke.Stage.Unity"
unity_editor=${UNITY_EDITOR:-}

if [[ -z "$unity_editor" ]]; then
  for candidate in \
    /opt/unity/Editor/Unity \
    "$HOME/Unity/Hub/Editor/6000.5.4f1/Editor/Unity"; do
    if [[ -x "$candidate" ]]; then
      unity_editor=$candidate
      break
    fi
  done
fi

if [[ -z "$unity_editor" && -d "$HOME/Unity/Hub/Editor" ]]; then
  unity_editor=$(find "$HOME/Unity/Hub/Editor" -mindepth 3 -maxdepth 3 -type f -path '*/Editor/Unity' -perm -111 \
    -print -quit)
fi

if [[ -z "$unity_editor" || ! -x "$unity_editor" ]]; then
  echo "Unity 6 Editor nicht gefunden." >&2
  echo "Installiere Unity mit Android Build Support oder setze UNITY_EDITOR=/pfad/zu/Unity." >&2
  exit 1
fi

# LiveKit Unity 2.0.0 cannot resolve its shaded ContextUtils class through
# JNI FindClass on Android. Apply LiveKit's upstream managed fallback before
# Unity compiles the player; the patch is idempotent.
if ! find "$unity_project/Library/PackageCache" -mindepth 1 -maxdepth 1 -type d \
  -name 'io.livekit.livekit-sdk@*' -print -quit 2>/dev/null | grep -q .; then
  echo "Unity-Pakete werden vor dem Android-Build einmalig aufgelöst ..."
  DOTNET_USE_POLLING_FILE_WATCHER=${DOTNET_USE_POLLING_FILE_WATCHER:-1} \
  DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=${DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE:-false} \
  "$unity_editor" -batchmode -nographics -quit -projectPath "$unity_project" -logFile -
fi
"$project_root/scripts/unity/patch-livekit-android-context.sh" "$unity_project"

# Unity's IL post-processor uses the .NET physical file provider. Polling keeps
# builds reliable on development machines that already exhausted their inotify
# watcher quota through editors, containers and language servers.
DOTNET_USE_POLLING_FILE_WATCHER=${DOTNET_USE_POLLING_FILE_WATCHER:-1} \
DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE=${DOTNET_HOSTBUILDER__RELOADCONFIGONCHANGE:-false} \
"$unity_editor" \
  -batchmode \
  -nographics \
  -quit \
  -projectPath "$unity_project" \
  -executeMethod NeonStage.Stage.Editor.NeonStageAndroidBuild.BuildShellS2 \
  -logFile -

echo "APK: $unity_project/Builds/Android/NeonStage-ShellS2-arm32.apk"
