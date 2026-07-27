#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
output=${1:-"$repo_root/artifacts/NeonStage-LyricsEditor-x86_64.AppImage"}
tool_dir="$repo_root/.tools/appimage"
project="$repo_root/src/Karaoke.App.Desktop/Karaoke.App.Desktop.csproj"
icon="$repo_root/src/Karaoke.App/Assets/neon-stage-icon.png"

[[ $(uname -m) == x86_64 ]] || { echo "The AppImage build currently supports x86_64 only." >&2; exit 2; }
for command in curl dotnet find ldd convert; do
  command -v "$command" >/dev/null || { echo "Missing command: $command" >&2; exit 1; }
done
libvlc=$(ldconfig -p | awk '/libvlc\.so\.5 / && !found { value=$NF; found=1 } END { print value }')
libvlccore=$(ldconfig -p | awk '/libvlccore\.so\.[0-9]+ / && !found { value=$NF; found=1 } END { print value }')
plugin_dir=$(find /usr/lib -type d -path '*/vlc/plugins' -print -quit 2>/dev/null)
[[ -n "$libvlc" && -n "$libvlccore" && -n "$plugin_dir" ]] || {
  echo "LibVLC including its plugin directory is required (install the vlc package)." >&2
  exit 1
}

mkdir -p "$tool_dir" "$(dirname "$output")"
linuxdeploy="$tool_dir/linuxdeploy-x86_64.AppImage"
appimagetool="$tool_dir/appimagetool-x86_64.AppImage"
if [[ ! -x "$linuxdeploy" ]]; then
  curl -fL --retry 3 -o "$linuxdeploy" \
    https://github.com/linuxdeploy/linuxdeploy/releases/download/continuous/linuxdeploy-x86_64.AppImage
  chmod +x "$linuxdeploy"
fi
if [[ ! -x "$appimagetool" ]]; then
  curl -fL --retry 3 -o "$appimagetool" \
    https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$appimagetool"
fi

work=$(mktemp -d -t neon-stage-appimage.XXXXXXXX)
trap 'rm -rf -- "$work"' EXIT
appdir="$work/NeonStageEditor.AppDir"
publish="$work/publish"
mkdir -p "$appdir/usr/bin" "$appdir/usr/lib/vlc" "$appdir/usr/share/applications" \
  "$appdir/usr/share/icons/hicolor/256x256/apps" "$appdir/usr/share/doc/neon-stage/licenses"

dotnet publish "$project" -c Release -r linux-x64 --self-contained true \
  -p:DebugType=None -p:DebugSymbols=false -o "$publish"
cp -a "$publish/." "$appdir/usr/bin/"
# These self-contained runtime files are debugger/tracing helpers, not editor
# runtime dependencies. Keeping them makes linuxdeploy require optional LTTng.
rm -f "$appdir/usr/bin/libcoreclrtraceptprovider.so" \
  "$appdir/usr/bin/libmscordbi.so" "$appdir/usr/bin/libmscordaccore.so"
ln -sfn Karaoke.App.Desktop "$appdir/usr/bin/neon-stage-editor"
cp -L "$libvlc" "$libvlccore" "$appdir/usr/lib/"
cp "$repo_root/packaging/linux/neon-stage-editor.desktop" \
  "$appdir/usr/share/applications/neon-stage-editor.desktop"
convert "$icon" -resize 256x256! \
  "$appdir/usr/share/icons/hicolor/256x256/apps/neon-stage-editor.png"
cp "$repo_root/LICENSE" "$repo_root/NOTICE" "$repo_root/THIRD_PARTY_NOTICES.md" \
  "$repo_root/ACKNOWLEDGEMENTS.md" \
  "$appdir/usr/share/doc/neon-stage/"

# The editor redistributes libVLC and VLC plugins. Preserve the full copyleft
# license texts and the distributor's package-level copyright inventories in
# the artifact, not merely links in the project notice.
for license_file in LGPL-2.1 LGPL-3 GPL-2 GPL-3; do
  source_file="/usr/share/common-licenses/$license_file"
  [[ -f "$source_file" ]] || { echo "Missing system license text: $source_file" >&2; exit 1; }
  cp "$source_file" "$appdir/usr/share/doc/neon-stage/licenses/$license_file.txt"
done
for package_name in libvlc5 vlc vlc-plugin-base; do
  copyright_file="/usr/share/doc/$package_name/copyright"
  [[ -f "$copyright_file" ]] || { echo "Missing VLC copyright inventory: $copyright_file" >&2; exit 1; }
  cp "$copyright_file" "$appdir/usr/share/doc/neon-stage/licenses/$package_name-copyright.txt"
done

# Analyze the editor and LibVLC. VLC plugins are copied afterwards: asking
# linuxdeploy to inspect every optional video/output plugin pulls an entire
# desktop multimedia distribution into an audio-editor AppImage.
deploy_args=(--appdir "$appdir" --executable "$appdir/usr/bin/Karaoke.App.Desktop" \
  --desktop-file "$appdir/usr/share/applications/neon-stage-editor.desktop" \
  --icon-file "$appdir/usr/share/icons/hicolor/256x256/apps/neon-stage-editor.png" \
  --library "$libvlc" --library "$libvlccore")
APPIMAGE_EXTRACT_AND_RUN=1 "$linuxdeploy" "${deploy_args[@]}"
mkdir -p "$appdir/usr/lib/vlc"
cp -aL "$plugin_dir" "$appdir/usr/lib/vlc/plugins"

rm -f "$appdir/AppRun"
cp "$repo_root/packaging/linux/AppRun" "$appdir/AppRun"
chmod +x "$appdir/AppRun"
ln -sfn usr/share/icons/hicolor/256x256/apps/neon-stage-editor.png "$appdir/.DirIcon"
rm -f "$output"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$appimagetool" "$appdir" "$output"
chmod +x "$output"
"$repo_root/scripts/release/verify-no-media.sh" "$(dirname "$output")"
echo "AppImage created: $output"
