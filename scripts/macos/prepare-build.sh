#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
unity_project="$repo_root/src/Karaoke.Stage.Unity"
version_file="$unity_project/ProjectSettings/ProjectVersion.txt"
skip_unity=0

if [[ ${1:-} == --skip-unity ]]; then
  skip_unity=1
  shift
fi
if (($#)); then
  echo "Unbekannte Option: $1" >&2
  echo "Aufruf: $0 [--skip-unity]" >&2
  exit 2
fi

[[ $(uname -s) == Darwin ]] || {
  echo "macOS-Builds müssen auf einem Mac vorbereitet werden." >&2
  exit 2
}

for apple_tool in /usr/bin/codesign /usr/bin/ditto /usr/bin/lipo /usr/bin/sips \
  /usr/bin/iconutil /usr/libexec/PlistBuddy; do
  [[ -x $apple_tool ]] || {
    echo "Apple-Buildwerkzeug fehlt: $apple_tool" >&2
    echo "Bitte zuerst 'xcode-select --install' ausführen." >&2
    exit 2
  }
done

# Homebrew verwendet auf Apple Silicon und Intel unterschiedliche Präfixe.
# Beide werden explizit aufgenommen, damit ein nicht-interaktiver Build dieselben
# Werkzeuge sieht wie ein interaktives Terminal.
export PATH="$HOME/.dotnet:/opt/homebrew/bin:/opt/homebrew/sbin:/usr/local/bin:/usr/local/sbin:$PATH"

find_brew() {
  if command -v brew >/dev/null 2>&1; then command -v brew; return; fi
  [[ -x /opt/homebrew/bin/brew ]] && { echo /opt/homebrew/bin/brew; return; }
  [[ -x /usr/local/bin/brew ]] && { echo /usr/local/bin/brew; return; }
  return 1
}

ensure_homebrew() {
  local brew_path
  brew_path=$(find_brew || true)
  if [[ -z $brew_path ]]; then
    echo "Homebrew fehlt; installiere den offiziellen Paketmanager …"
    /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
    brew_path=$(find_brew || true)
  fi
  [[ -n $brew_path ]] || { echo "Homebrew konnte nicht installiert werden." >&2; exit 2; }
  eval "$("$brew_path" shellenv)"
  BREW=$brew_path
}

has_dotnet_10() {
  command -v dotnet >/dev/null 2>&1 &&
    dotnet --list-sdks 2>/dev/null | awk '$1 ~ /^10\./ { found=1 } END { exit !found }'
}

find_vlc_app() {
  for candidate in /Applications/VLC.app "$HOME/Applications/VLC.app"; do
    [[ -d $candidate ]] && { echo "$candidate"; return; }
  done
  return 1
}

missing_packages=()
has_dotnet_10 || missing_packages+=(dotnet)
command -v ffmpeg >/dev/null 2>&1 || missing_packages+=(ffmpeg)
command -v dylibbundler >/dev/null 2>&1 || missing_packages+=(dylibbundler)
vlc_app=$(find_vlc_app || true)
[[ -n $vlc_app ]] || missing_packages+=(vlc)
if ((${#missing_packages[@]})); then ensure_homebrew; fi

if ! has_dotnet_10; then
  echo "Installiere .NET SDK 10 benutzerlokal …"
  dotnet_installer=$(mktemp "${TMPDIR:-/tmp}/dotnet-install.XXXXXXXX.sh")
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$dotnet_installer"
  bash "$dotnet_installer" --channel 10.0 --install-dir "$HOME/.dotnet"
  rm -f -- "$dotnet_installer"
  hash -r
fi
has_dotnet_10 || { echo ".NET SDK 10 ist auch nach der Installation nicht verfügbar." >&2; exit 2; }

if ! command -v ffmpeg >/dev/null 2>&1; then
  echo "Installiere FFmpeg …"
  "$BREW" install ffmpeg
  hash -r
fi
command -v ffmpeg >/dev/null 2>&1 || { echo "FFmpeg ist nicht verfügbar." >&2; exit 2; }

if ! command -v dylibbundler >/dev/null 2>&1; then
  echo "Installiere dylibbundler für das portable FFmpeg-Paket …"
  "$BREW" install dylibbundler
  hash -r
fi
command -v dylibbundler >/dev/null 2>&1 || { echo "dylibbundler ist nicht verfügbar." >&2; exit 2; }

if [[ -z $vlc_app ]]; then
  echo "Installiere VLC/LibVLC benutzerlokal …"
  mkdir -p "$HOME/Applications"
  "$BREW" install --cask --appdir="$HOME/Applications" vlc
  vlc_app=$(find_vlc_app || true)
fi
[[ -n $vlc_app ]] || { echo "VLC wurde nicht gefunden." >&2; exit 2; }
vlc_root="$vlc_app/Contents/MacOS"
[[ -f "$vlc_root/lib/libvlc.dylib" ]] || {
  echo "VLC wurde gefunden, enthält aber kein lib/libvlc.dylib: $vlc_app" >&2
  exit 2
}
vlc_architectures=$(/usr/bin/lipo -archs "$vlc_root/lib/libvlc.dylib")
[[ " $vlc_architectures " == *" arm64 "* ]] || {
  echo "Die installierte VLC-Laufzeit enthält kein ARM64-LibVLC: $vlc_architectures" >&2
  exit 2
}
vlc_plugins=""
for candidate in "$vlc_root/plugins" "$vlc_root/lib/vlc/plugins"; do
  [[ -d $candidate ]] && { vlc_plugins=$candidate; break; }
done
[[ -n $vlc_plugins ]] || { echo "VLC-Pluginverzeichnis wurde nicht gefunden." >&2; exit 2; }

echo "Stelle .NET-Pakete wieder her …"
dotnet restore "$repo_root/src/Karaoke.Server/Karaoke.Server.csproj" --runtime osx-arm64
dotnet restore "$repo_root/src/Karaoke.App.Desktop/Karaoke.App.Desktop.csproj" --runtime osx-arm64
dotnet restore "$repo_root/tests/Karaoke.Editor.Core.Tests/Karaoke.Editor.Core.Tests.csproj"
dotnet restore "$repo_root/tests/Karaoke.Server.PlaybackTests/Karaoke.Server.PlaybackTests.csproj"

if (( skip_unity )); then
  echo "macOS-Prepare erfolgreich: .NET 10, FFmpeg, dylibbundler, VLC und Projektpakete sind bereit (Unity übersprungen)."
  exit 0
fi

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
  echo "Unity 6000.x fehlt. Unity wird aus Lizenzgründen nicht automatisch installiert." >&2
  echo "Bitte über Unity Hub installieren/aktivieren und UNITY_EDITOR bei Bedarf setzen." >&2
  exit 2
}

unity_installation=$(cd "$(dirname "$unity_editor")/../../.." && pwd)
[[ -d "$unity_installation/PlaybackEngines/MacStandaloneSupport" ]] || {
  echo "Unity-Modul 'Mac Build Support (Mono)' fehlt für $unity_editor." >&2
  exit 2
}

mkdir -p "$repo_root/Builds"
echo "Prepare macOS: Unity $unity_version · $unity_editor"
"$unity_editor" -batchmode -nographics -quit \
  -projectPath "$unity_project" \
  -logFile "$repo_root/Builds/macos-prepare.log"

livekit_package=""
for candidate in "$unity_project"/Library/PackageCache/io.livekit.livekit-sdk@*; do
  [[ -d $candidate ]] && { livekit_package=$candidate; break; }
done
[[ -n $livekit_package ]] || { echo "Das aufgelöste LiveKit-Unity-Paket fehlt." >&2; exit 2; }
for architecture in arm64 x86_64; do
  dylib="$livekit_package/Runtime/Plugins/ffi-macos-$architecture/liblivekit_ffi.dylib"
  [[ -f $dylib ]] || { echo "LiveKit-macOS-Bibliothek fehlt: $dylib" >&2; exit 2; }
  /usr/bin/lipo -archs "$dylib" | grep -qw "$architecture" || {
    echo "LiveKit-Bibliothek hat nicht die erwartete Architektur $architecture: $dylib" >&2
    exit 2
  }
done

echo "macOS-Prepare erfolgreich: .NET 10, FFmpeg, dylibbundler, VLC, Unity-Pakete und Stage-Skripte sind bereit."
