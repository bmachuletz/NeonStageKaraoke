#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
output=${1:-"$repo_root/artifacts/NeonStage-Server-x86_64.AppImage"}
tool_dir="$repo_root/.tools/appimage"
project="$repo_root/src/Karaoke.Server/Karaoke.Server.csproj"
icon="$repo_root/src/Karaoke.App/Assets/neon-stage-icon.png"

[[ $(uname -m) == x86_64 ]] || { echo "The AppImage build currently supports x86_64 only." >&2; exit 2; }
for command in curl dotnet convert; do
  command -v "$command" >/dev/null || { echo "Missing command: $command" >&2; exit 1; }
done

mkdir -p "$tool_dir" "$(dirname "$output")"
appimagetool="$tool_dir/appimagetool-x86_64.AppImage"
if [[ ! -x "$appimagetool" ]]; then
  curl -fL --retry 3 -o "$appimagetool" \
    https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage
  chmod +x "$appimagetool"
fi

work=$(mktemp -d -t neon-stage-server-appimage.XXXXXXXX)
trap 'rm -rf -- "$work"' EXIT
appdir="$work/NeonStageServer.AppDir"
publish="$work/publish"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" \
  "$appdir/usr/share/icons/hicolor/256x256/apps" "$appdir/usr/share/doc/neon-stage/licenses"

dotnet publish "$project" -c Release -r linux-x64 --self-contained true \
  -p:DebugType=None -p:DebugSymbols=false -o "$publish"
cp -a "$publish/." "$appdir/usr/bin/"
rm -f "$appdir/usr/bin/libcoreclrtraceptprovider.so" \
  "$appdir/usr/bin/libmscordbi.so" "$appdir/usr/bin/libmscordaccore.so"
ln -sfn Karaoke.Server "$appdir/usr/bin/neon-stage-server"

cp "$repo_root/packaging/linux/AppRun.server" "$appdir/AppRun"
chmod +x "$appdir/AppRun" "$appdir/usr/bin/Karaoke.Server"
cp "$repo_root/packaging/linux/neon-stage-server.desktop" \
  "$appdir/neon-stage-server.desktop"
cp "$repo_root/packaging/linux/neon-stage-server.desktop" \
  "$appdir/usr/share/applications/neon-stage-server.desktop"
convert "$icon" -resize 256x256! "$appdir/neon-stage-server.png"
cp "$appdir/neon-stage-server.png" \
  "$appdir/usr/share/icons/hicolor/256x256/apps/neon-stage-server.png"
ln -sfn neon-stage-server.png "$appdir/.DirIcon"
cp "$repo_root/LICENSE" "$repo_root/NOTICE" "$repo_root/THIRD_PARTY_NOTICES.md" \
  "$repo_root/ACKNOWLEDGEMENTS.md" "$appdir/usr/share/doc/neon-stage/"
cp "$repo_root/packaging/linux/server.env.example" "$appdir/usr/share/doc/neon-stage/"
cp "$repo_root/packaging/licenses/AppImage-Type2-Runtime-LICENSE.txt" \
  "$appdir/usr/share/doc/neon-stage/licenses/"

rm -f "$output"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$appimagetool" "$appdir" "$output"
chmod +x "$output"
"$repo_root/scripts/release/verify-no-media.sh" "$(dirname "$output")"
echo "Server AppImage created: $output"
