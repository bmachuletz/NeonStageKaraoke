#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
# shellcheck source=scripts/release/lib/appimage-common.sh
source "$repo_root/scripts/release/lib/appimage-common.sh"
output=${1:-"$repo_root/artifacts/NeonStage-Server-x86_64.AppImage"}
project="$repo_root/src/Karaoke.Server/Karaoke.Server.csproj"
icon="$repo_root/src/Karaoke.App/Assets/neon-stage-icon.png"

ns_prepare_system_dependencies server
ns_ensure_dotnet_10 "$repo_root"
"$repo_root/scripts/linux/install-yt-dlp.sh"
"$repo_root/scripts/linux/install-deno.sh"
appimagetool=$(ns_ensure_appimage_tool "$repo_root")
linuxdeploy=$(ns_ensure_linuxdeploy "$repo_root")
ffmpeg=$(command -v ffmpeg)
mkdir -p "$(dirname "$output")"

work=$(mktemp -d -t neon-stage-server-appimage.XXXXXXXX)
trap 'rm -rf -- "$work"' EXIT
appdir="$work/NeonStageServer.AppDir"
publish="$work/publish"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" \
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
matcher_publish="$work/lrcmatcher"
dotnet publish "$repo_root/LrcMatcher/LrcMatcher.csproj" -c Release -r linux-x64 \
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:DebugType=None -p:DebugSymbols=false "${version_args[@]}" -o "$matcher_publish"
cp -a "$publish/." "$appdir/usr/bin/"
cp -L "$ffmpeg" "$appdir/usr/bin/ffmpeg"
cp "$repo_root/.tools/yt-dlp" "$appdir/usr/bin/yt-dlp"
cp "$repo_root/.tools/deno" "$appdir/usr/bin/deno"
cp "$matcher_publish/LrcMatcher" "$appdir/usr/bin/LrcMatcher"
rm -f "$appdir/usr/bin/libcoreclrtraceptprovider.so" \
  "$appdir/usr/bin/libmscordbi.so" "$appdir/usr/bin/libmscordaccore.so"
ln -sfn Karaoke.Server "$appdir/usr/bin/neon-stage-server"
chmod +x "$appdir/usr/bin/Karaoke.Server" "$appdir/usr/bin/ffmpeg" \
  "$appdir/usr/bin/yt-dlp" "$appdir/usr/bin/deno" "$appdir/usr/bin/LrcMatcher"
cp "$repo_root/packaging/linux/neon-stage-server.desktop" \
  "$appdir/neon-stage-server.desktop"
cp "$repo_root/packaging/linux/neon-stage-server.desktop" \
  "$appdir/usr/share/applications/neon-stage-server.desktop"
ns_convert_icon "$icon" "$appdir/neon-stage-server.png"
cp "$appdir/neon-stage-server.png" \
  "$appdir/usr/share/icons/hicolor/256x256/apps/neon-stage-server.png"
ln -sfn neon-stage-server.png "$appdir/.DirIcon"
cp "$repo_root/LICENSE" "$repo_root/NOTICE" "$repo_root/THIRD_PARTY_NOTICES.md" \
  "$repo_root/ACKNOWLEDGEMENTS.md" "$appdir/usr/share/doc/neon-stage/"
cp "$repo_root/.tools/yt-dlp-LICENSE" "$repo_root/.tools/deno-LICENSE.md" \
  "$appdir/usr/share/doc/neon-stage/licenses/"
cp "$repo_root/packaging/linux/server.env.example" "$appdir/usr/share/doc/neon-stage/"
cp "$repo_root/packaging/licenses/AppImage-Type2-Runtime-LICENSE.txt" \
  "$appdir/usr/share/doc/neon-stage/licenses/"
ns_copy_ffmpeg_licenses "$appdir"

deploy_args=(--appdir "$appdir" --executable "$appdir/usr/bin/Karaoke.Server" \
  --executable "$appdir/usr/bin/ffmpeg" \
  --executable "$appdir/usr/bin/yt-dlp" --executable "$appdir/usr/bin/deno" \
  --executable "$appdir/usr/bin/LrcMatcher" \
  --desktop-file "$appdir/usr/share/applications/neon-stage-server.desktop" \
  --icon-file "$appdir/usr/share/icons/hicolor/256x256/apps/neon-stage-server.png")
NO_STRIP=1 APPIMAGE_EXTRACT_AND_RUN=1 "$linuxdeploy" "${deploy_args[@]}"
rm -f "$appdir/AppRun"
cp "$repo_root/packaging/linux/AppRun.server" "$appdir/AppRun"
chmod +x "$appdir/AppRun"
ln -sfn neon-stage-server.png "$appdir/.DirIcon"

rm -f "$output"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$appimagetool" "$appdir" "$output"
chmod +x "$output"
ns_verify_appimage "$output"
"$repo_root/scripts/release/verify-no-media.sh" "$(dirname "$output")"
echo "Server AppImage created: $output"
