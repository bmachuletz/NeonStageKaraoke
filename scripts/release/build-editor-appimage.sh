#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
# shellcheck source=scripts/release/lib/appimage-common.sh
source "$repo_root/scripts/release/lib/appimage-common.sh"
output=${1:-"$repo_root/artifacts/NeonStage-LyricsEditor-x86_64.AppImage"}
project="$repo_root/src/Karaoke.App.Desktop/Karaoke.App.Desktop.csproj"
icon="$repo_root/src/Karaoke.App/Assets/neon-stage-icon.png"

ns_prepare_system_dependencies editor
ns_ensure_dotnet_10 "$repo_root"
ns_find_libvlc
libvlc=$NS_LIBVLC
libvlccore=$NS_LIBVLCCORE
plugin_dir=$NS_VLC_PLUGIN_DIR
ffmpeg=$(command -v ffmpeg)
linuxdeploy=$(ns_ensure_linuxdeploy "$repo_root")
appimagetool=$(ns_ensure_appimage_tool "$repo_root")
mkdir -p "$(dirname "$output")"

work=$(mktemp -d -t neon-stage-appimage.XXXXXXXX)
trap 'rm -rf -- "$work"' EXIT
appdir="$work/NeonStageEditor.AppDir"
publish="$work/publish"
mkdir -p "$appdir/usr/bin" "$appdir/usr/lib/vlc" "$appdir/usr/share/applications" \
  "$appdir/usr/share/icons/hicolor/256x256/apps" "$appdir/usr/share/doc/neon-stage/licenses"

version_args=()
if [[ -n ${NEONSTAGE_VERSION:-} ]]; then
  version_args+=("-p:Version=$NEONSTAGE_VERSION")
  if [[ -n ${NEONSTAGE_BUILD_NUMBER:-} ]]; then
    version_args+=("-p:InformationalVersion=$NEONSTAGE_VERSION+build.$NEONSTAGE_BUILD_NUMBER")
  fi
fi
dotnet publish "$project" -c Release -r linux-x64 --self-contained true \
  -p:DebugType=None -p:DebugSymbols=false "${version_args[@]}" -o "$publish"
cp -a "$publish/." "$appdir/usr/bin/"
cp -L "$ffmpeg" "$appdir/usr/bin/ffmpeg"
# These self-contained runtime files are debugger/tracing helpers, not editor
# runtime dependencies. Keeping them makes linuxdeploy require optional LTTng.
rm -f "$appdir/usr/bin/libcoreclrtraceptprovider.so" \
  "$appdir/usr/bin/libmscordbi.so" "$appdir/usr/bin/libmscordaccore.so"
ln -sfn Karaoke.App.Desktop "$appdir/usr/bin/neon-stage-editor"
cp -L "$libvlc" "$libvlccore" "$appdir/usr/lib/"
cp "$repo_root/packaging/linux/neon-stage-editor.desktop" \
  "$appdir/usr/share/applications/neon-stage-editor.desktop"
ns_convert_icon "$icon" "$appdir/usr/share/icons/hicolor/256x256/apps/neon-stage-editor.png"
cp "$repo_root/LICENSE" "$repo_root/NOTICE" "$repo_root/THIRD_PARTY_NOTICES.md" \
  "$repo_root/ACKNOWLEDGEMENTS.md" \
  "$appdir/usr/share/doc/neon-stage/"
cp "$repo_root/packaging/licenses/AppImage-Type2-Runtime-LICENSE.txt" \
  "$appdir/usr/share/doc/neon-stage/licenses/"

# The editor redistributes libVLC and VLC plugins. Preserve the full copyleft
# license texts and the distributor's package-level copyright inventories in
# the artifact, not merely links in the project notice.
for license_file in LGPL-2.1 LGPL-3 GPL-2 GPL-3; do
  source_file="/usr/share/common-licenses/$license_file"
  if [[ ! -f "$source_file" ]]; then
    source_file=$(find /usr/share/licenses -type f \( -iname "*$license_file*" -o -iname "*${license_file/./}*" \) \
      -print -quit 2>/dev/null || true)
  fi
  [[ -f "$source_file" ]] || { echo "Missing system license text: $license_file" >&2; exit 1; }
  cp "$source_file" "$appdir/usr/share/doc/neon-stage/licenses/$license_file.txt"
done
for package_name in libvlc5 vlc vlc-plugin-base; do
  copyright_file="/usr/share/doc/$package_name/copyright"
  [[ -f "$copyright_file" ]] && cp "$copyright_file" \
    "$appdir/usr/share/doc/neon-stage/licenses/$package_name-copyright.txt"
done
if ! find "$appdir/usr/share/doc/neon-stage/licenses" -name '*vlc*-copyright.txt' -print -quit | grep -q .; then
  while IFS= read -r -d '' license_file; do
    cp "$license_file" "$appdir/usr/share/doc/neon-stage/licenses/vlc-$(basename "$license_file")"
  done < <(find /usr/share/licenses -maxdepth 3 -type f -ipath '*vlc*' -print0 2>/dev/null || true)
fi
find "$appdir/usr/share/doc/neon-stage/licenses" -iname '*vlc*' -print -quit | grep -q . || {
  echo "Missing VLC copyright/license inventory." >&2; exit 1;
}
ns_copy_ffmpeg_licenses "$appdir"

# Analyze the editor and LibVLC. VLC plugins are copied afterwards: asking
# linuxdeploy to inspect every optional video/output plugin pulls an entire
# desktop multimedia distribution into an audio-editor AppImage.
deploy_args=(--appdir "$appdir" --executable "$appdir/usr/bin/Karaoke.App.Desktop" \
  --executable "$appdir/usr/bin/ffmpeg" \
  --desktop-file "$appdir/usr/share/applications/neon-stage-editor.desktop" \
  --icon-file "$appdir/usr/share/icons/hicolor/256x256/apps/neon-stage-editor.png" \
  --library "$libvlc" --library "$libvlccore")
NO_STRIP=1 APPIMAGE_EXTRACT_AND_RUN=1 "$linuxdeploy" "${deploy_args[@]}"
mkdir -p "$appdir/usr/lib/vlc"
cp -aL "$plugin_dir" "$appdir/usr/lib/vlc/plugins"

rm -f "$appdir/AppRun"
cp "$repo_root/packaging/linux/AppRun" "$appdir/AppRun"
chmod +x "$appdir/AppRun"
ln -sfn usr/share/icons/hicolor/256x256/apps/neon-stage-editor.png "$appdir/.DirIcon"
rm -f "$output"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$appimagetool" "$appdir" "$output"
chmod +x "$output"
ns_verify_appimage "$output"
"$repo_root/scripts/release/verify-no-media.sh" "$(dirname "$output")"
echo "AppImage created: $output"
