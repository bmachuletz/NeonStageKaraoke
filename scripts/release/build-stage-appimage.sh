#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
# shellcheck source=scripts/release/lib/appimage-common.sh
source "$repo_root/scripts/release/lib/appimage-common.sh"
output=${1:-"$repo_root/artifacts/NeonStage-Stage-x86_64.AppImage"}
build_dir="$repo_root/src/Karaoke.Stage.Unity/Builds/Linux"
icon="$repo_root/src/Karaoke.App/Assets/neon-stage-icon.png"

ns_prepare_system_dependencies stage

if [[ ${NEONSTAGE_SKIP_UNITY_BUILD:-0} != 1 ]]; then
  "$repo_root/scripts/linux/build-unity-stage-linux.sh"
fi
[[ -x "$build_dir/NeonStage" && -f "$build_dir/UnityPlayer.so" && -d "$build_dir/NeonStage_Data" ]] || {
  echo "Unity Linux build is incomplete: $build_dir" >&2
  exit 1
}

"$repo_root/scripts/release/verify-no-media.sh" "$repo_root"
appimagetool=$(ns_ensure_appimage_tool "$repo_root")
mkdir -p "$(dirname "$output")"

work=$(mktemp -d -t neon-stage-stage-appimage.XXXXXXXX)
trap 'rm -rf -- "$work"' EXIT
appdir="$work/NeonStageStage.AppDir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" \
  "$appdir/usr/share/icons/hicolor/256x256/apps" "$appdir/usr/share/doc/neon-stage/licenses"

cp "$build_dir/NeonStage" "$build_dir/UnityPlayer.so" "$appdir/usr/bin/"
cp -a "$build_dir/NeonStage_Data" "$appdir/usr/bin/"
while IFS= read -r -d '' library; do cp "$library" "$appdir/usr/bin/"; done \
  < <(find "$build_dir" -maxdepth 1 -type f -name '*.so*' ! -name 'UnityPlayer.so' -print0)
chmod +x "$appdir/usr/bin/NeonStage"

cp "$repo_root/packaging/linux/AppRun.stage" "$appdir/AppRun"
chmod +x "$appdir/AppRun"
cp "$repo_root/packaging/linux/neon-stage-stage.desktop" "$appdir/neon-stage-stage.desktop"
cp "$repo_root/packaging/linux/neon-stage-stage.desktop" \
  "$appdir/usr/share/applications/neon-stage-stage.desktop"
ns_convert_icon "$icon" "$appdir/neon-stage-stage.png"
cp "$appdir/neon-stage-stage.png" \
  "$appdir/usr/share/icons/hicolor/256x256/apps/neon-stage-stage.png"
ln -sfn neon-stage-stage.png "$appdir/.DirIcon"
cp "$repo_root/LICENSE" "$repo_root/NOTICE" "$repo_root/THIRD_PARTY_NOTICES.md" \
  "$repo_root/ACKNOWLEDGEMENTS.md" "$appdir/usr/share/doc/neon-stage/"
cp "$repo_root/packaging/licenses/AppImage-Type2-Runtime-LICENSE.txt" \
  "$appdir/usr/share/doc/neon-stage/licenses/"

rm -f "$output"
ARCH=x86_64 APPIMAGE_EXTRACT_AND_RUN=1 "$appimagetool" "$appdir" "$output"
chmod +x "$output"
ns_verify_appimage "$output"
"$repo_root/scripts/release/verify-no-media.sh" "$(dirname "$output")"
echo "Stage AppImage created: $output"
