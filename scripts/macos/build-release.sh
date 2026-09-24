#!/usr/bin/env bash
set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
output_dir=""
version=""
build_number=""
skip_unity=0
skip_prepare=0

while (($#)); do
  case "$1" in
    --output) output_dir=${2:?--output benötigt einen Pfad}; shift 2 ;;
    --version) version=${2:?--version benötigt einen Wert}; shift 2 ;;
    --build) build_number=${2:?--build benötigt einen Wert}; shift 2 ;;
    --skip-unity) skip_unity=1; shift ;;
    --skip-prepare) skip_prepare=1; shift ;;
    *) echo "Unbekannte Option: $1" >&2; exit 2 ;;
  esac
done

[[ $(uname -s) == Darwin ]] || { echo "macOS-Pakete müssen auf einem Mac gebaut werden." >&2; exit 2; }
[[ -n $output_dir && -n $version && $build_number =~ ^[1-9][0-9]*$ ]] || {
  echo "Aufruf: $0 --output DIR --version X.Y.Z --build N [--skip-unity] [--skip-prepare]" >&2
  exit 2
}
[[ $version =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || {
  echo "Ungültige Version: $version" >&2; exit 2;
}

export PATH="/opt/homebrew/bin:/opt/homebrew/sbin:/usr/local/bin:/usr/local/sbin:$PATH"
if (( ! skip_prepare )); then
  prepare_args=()
  (( skip_unity )) && prepare_args+=(--skip-unity)
  "$repo_root/scripts/macos/prepare-build.sh" "${prepare_args[@]}"
fi

for command in dotnet ffmpeg dylibbundler ditto codesign sips iconutil; do
  command -v "$command" >/dev/null 2>&1 || { echo "Benötigtes Werkzeug fehlt: $command" >&2; exit 2; }
done

output_dir=$(mkdir -p "$output_dir" && cd "$output_dir" && pwd)
suffix="v$version-build.$build_number"
export NEONSTAGE_VERSION=$version
export NEONSTAGE_BUILD_NUMBER=$build_number
export NEONSTAGE_RELEASE_BUILD=1
work_root=$(mktemp -d "${TMPDIR:-/tmp}/neonstage-macos-release.XXXXXXXX")
cleanup() { rm -rf -- "$work_root"; }
trap cleanup EXIT
package_root="$work_root/packages"
mkdir -p "$package_root"

common_publish=(
  -c Release -r osx-arm64 --self-contained true
  -p:DebugType=None -p:DebugSymbols=false
  "-p:Version=$version" "-p:InformationalVersion=$version+build.$build_number"
)

server_dir="$work_root/NeonStage Server"
editor_app="$work_root/Neon Stage Lyrics Editor.app"
editor_runtime="$editor_app/Contents/Resources/app"
mkdir -p "$server_dir" "$editor_app/Contents/MacOS" "$editor_app/Contents/Resources"

dotnet publish "$repo_root/src/Karaoke.Server/Karaoke.Server.csproj" \
  "${common_publish[@]}" -o "$server_dir"
dotnet publish "$repo_root/src/Karaoke.App.Desktop/Karaoke.App.Desktop.csproj" \
  "${common_publish[@]}" -o "$editor_runtime"

# Homebrews FFmpeg ist dynamisch gelinkt. dylibbundler kopiert alle nicht zum
# Betriebssystem gehörenden Bibliotheken und schreibt portable @executable_path-Routen.
cp "$(command -v ffmpeg)" "$server_dir/ffmpeg"
dylibbundler -od -b -x "$server_dir/ffmpeg" -d "$server_dir/ffmpeg-libs" \
  -p @executable_path/ffmpeg-libs/
while IFS= read -r dylib; do codesign --force --sign - "$dylib"; done \
  < <(find "$server_dir/ffmpeg-libs" -type f -name '*.dylib' -print)
codesign --force --sign - "$server_dir/ffmpeg"
cp -R "$server_dir/ffmpeg-libs" "$editor_runtime/ffmpeg-libs"
cp "$server_dir/ffmpeg" "$editor_runtime/ffmpeg"
ffmpeg -version > "$server_dir/FFmpeg-build.txt"
cp "$server_dir/FFmpeg-build.txt" "$editor_runtime/FFmpeg-build.txt"
ffmpeg_prefix=""
if command -v brew >/dev/null 2>&1 && brew --prefix ffmpeg >/dev/null 2>&1; then
  ffmpeg_prefix=$(brew --prefix ffmpeg)
fi
if [[ -n $ffmpeg_prefix ]]; then
  for license in "$ffmpeg_prefix"/LICENSE* "$ffmpeg_prefix"/COPYING* \
    "$ffmpeg_prefix"/share/doc/ffmpeg/LICENSE* "$ffmpeg_prefix"/share/doc/ffmpeg/COPYING*; do
    [[ -f $license ]] || continue
    cp "$license" "$server_dir/FFmpeg-$(basename "$license")"
    cp "$license" "$editor_runtime/FFmpeg-$(basename "$license")"
  done
fi

vlc_root=/Applications/VLC.app/Contents/MacOS
vlc_plugins=""
for candidate in "$vlc_root/plugins" "$vlc_root/lib/vlc/plugins"; do
  [[ -d $candidate ]] && { vlc_plugins=$candidate; break; }
done
[[ -f "$vlc_root/lib/libvlc.dylib" && -n $vlc_plugins ]] || {
  echo "Die vorbereitete VLC-Laufzeit ist unvollständig." >&2; exit 2;
}
mkdir -p "$editor_runtime/vlc"
ditto "$vlc_root/lib" "$editor_runtime/vlc/lib"
ditto "$vlc_plugins" "$editor_runtime/vlc/plugins"
for license in /Applications/VLC.app/Contents/MacOS/COPYING \
  /Applications/VLC.app/Contents/Resources/COPYING; do
  [[ -f $license ]] && { cp "$license" "$editor_runtime/VLC-COPYING"; break; }
done

cp "$repo_root/packaging/macos/NeonStage Lyrics Editor" \
  "$editor_app/Contents/MacOS/NeonStage Lyrics Editor"
chmod +x "$editor_app/Contents/MacOS/NeonStage Lyrics Editor" \
  "$editor_runtime/Karaoke.App.Desktop" "$editor_runtime/ffmpeg"
sed -e "s/__VERSION__/$version/g" -e "s/__BUILD__/$build_number/g" \
  "$repo_root/packaging/macos/Editor-Info.plist" > "$editor_app/Contents/Info.plist"

iconset="$work_root/NeonStage.iconset"
mkdir -p "$iconset"
icon_source="$repo_root/src/Karaoke.App/Assets/neon-stage-icon.png"
for specification in "16 icon_16x16" "32 icon_16x16@2x" "32 icon_32x32" \
  "64 icon_32x32@2x" "128 icon_128x128" "256 icon_128x128@2x" \
  "256 icon_256x256" "512 icon_256x256@2x" "512 icon_512x512" \
  "1024 icon_512x512@2x"; do
  read -r pixels name <<< "$specification"
  sips -z "$pixels" "$pixels" "$icon_source" --out "$iconset/$name.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$editor_app/Contents/Resources/NeonStage.icns"

cp "$repo_root/packaging/macos/Start-NeonStage-Server.command" "$server_dir/"
chmod +x "$server_dir/Start-NeonStage-Server.command" "$server_dir/Karaoke.Server" "$server_dir/ffmpeg"
for notice in LICENSE NOTICE THIRD_PARTY_NOTICES.md ACKNOWLEDGEMENTS.md; do
  cp "$repo_root/$notice" "$server_dir/"
  cp "$repo_root/$notice" "$editor_app/Contents/Resources/"
done

codesign --force --deep --sign - "$editor_app"
codesign --verify --deep --strict "$editor_app"
/usr/bin/lipo -archs "$editor_runtime/Karaoke.App.Desktop" | grep -qw arm64 || {
  echo "Der Editor-Apphost enthält kein ARM64-Binary." >&2; exit 2;
}
/usr/bin/lipo -archs "$editor_runtime/vlc/lib/libvlc.dylib" | grep -qw arm64 || {
  echo "Das gebündelte LibVLC enthält kein ARM64-Binary." >&2; exit 2;
}
[[ -n $(find "$editor_runtime/vlc/plugins" -type f -print -quit) ]] || {
  echo "Das Editor-Paket enthält keine VLC-Plugins." >&2; exit 2;
}
ditto -c -k --sequesterRsrc --keepParent "$server_dir" \
  "$package_root/NeonStage-Server-$suffix-macOS-arm64.zip"
ditto -c -k --sequesterRsrc --keepParent "$editor_app" \
  "$package_root/NeonStage-LyricsEditor-$suffix-macOS-arm64.zip"

if (( ! skip_unity )); then
  "$repo_root/scripts/macos/build-unity-stage-macos.sh" --skip-prepare
  stage_app="$repo_root/src/Karaoke.Stage.Unity/Builds/macOS/NeonStage Karaoke.app"
  [[ -d $stage_app ]] || { echo "Unity-Stage fehlt nach erfolgreichem Build: $stage_app" >&2; exit 2; }
  stage_arch=arm64
  [[ ${NEONSTAGE_MACOS_UNIVERSAL:-0} == 1 ]] && stage_arch=universal
  ditto -c -k --sequesterRsrc --keepParent "$stage_app" \
    "$package_root/NeonStage-Stage-$suffix-macOS-$stage_arch.zip"
fi

mv "$package_root"/*.zip "$output_dir/"
echo "macOS-Pakete erstellt: $output_dir"
