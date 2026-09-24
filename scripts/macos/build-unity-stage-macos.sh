#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
skip_prepare=0
if [[ ${1:-} == --skip-prepare ]]; then skip_prepare=1; shift; fi
if (($#)); then echo "Aufruf: $0 [--skip-prepare]" >&2; exit 2; fi
(( skip_prepare )) || "$repo_root/scripts/macos/prepare-build.sh"
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
build_log="$repo_root/Builds/macos-unity.log"
if ! "$unity_editor" -batchmode -nographics -quit \
  -projectPath "$repo_root/src/Karaoke.Stage.Unity" \
  -executeMethod NeonStage.Stage.Editor.NeonStageAndroidBuild.BuildMacOS \
  -logFile "$build_log"; then
  echo "Unity-macOS-Build fehlgeschlagen. Letzte Logzeilen aus $build_log:" >&2
  tail -n 100 "$build_log" >&2 || true
  exit 1
fi

app="$repo_root/src/Karaoke.Stage.Unity/Builds/macOS/NeonStage Karaoke.app"
plist="$app/Contents/Info.plist"
[[ -d "$app" && -f "$plist" ]] || {
  echo "Unity meldete Erfolg, aber das App-Bundle fehlt: $app" >&2
  exit 1
}

bundle_executable=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$plist")
executable="$app/Contents/MacOS/$bundle_executable"
[[ -x "$executable" ]] || {
  echo "Ausführbare Datei im App-Bundle fehlt: $executable" >&2
  exit 1
}

microphone_text=$(/usr/libexec/PlistBuddy -c 'Print :NSMicrophoneUsageDescription' "$plist" 2>/dev/null || true)
[[ -n "$microphone_text" ]] || {
  echo "NSMicrophoneUsageDescription fehlt im erzeugten App-Bundle." >&2
  exit 1
}

executable_architectures=$(/usr/bin/lipo -archs "$executable")
[[ " $executable_architectures " == *" arm64 "* ]] || {
  echo "Der macOS-Player enthält kein ARM64-Binary: $executable_architectures" >&2
  exit 1
}
if [[ ${NEONSTAGE_MACOS_UNIVERSAL:-0} == 1 ]] && \
   [[ " $executable_architectures " != *" x86_64 "* ]]; then
  echo "Der angeforderte Universal-Build enthält kein x86_64-Binary: $executable_architectures" >&2
  exit 1
fi

livekit_dylib=""
while IFS= read -r candidate; do
  livekit_dylib=$candidate
  break
done < <(find "$app" -name liblivekit_ffi.dylib -type f -print)
[[ -n "$livekit_dylib" ]] || {
  echo "Die LiveKit-Nativebibliothek fehlt im erzeugten App-Bundle." >&2
  exit 1
}
livekit_architectures=$(/usr/bin/lipo -archs "$livekit_dylib")
[[ " $livekit_architectures " == *" arm64 "* ]] || {
  echo "LiveKit im App-Bundle enthält kein ARM64-Binary: $livekit_architectures" >&2
  exit 1
}
if [[ ${NEONSTAGE_MACOS_UNIVERSAL:-0} == 1 ]] && \
   [[ " $livekit_architectures " != *" x86_64 "* ]]; then
  echo "LiveKit im Universal-Build enthält kein x86_64-Binary: $livekit_architectures" >&2
  exit 1
fi

if ! /usr/bin/codesign --verify --deep --strict "$app"; then
  echo "Die ad-hoc-Signatur des erzeugten App-Bundles ist ungültig." >&2
  exit 1
fi

echo "Build: $app ($executable_architectures, LiveKit: $livekit_architectures)"
