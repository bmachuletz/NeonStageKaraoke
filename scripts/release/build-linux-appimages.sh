#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
artifact_dir=${1:-"$repo_root/artifacts"}
mkdir -p "$artifact_dir"

"$repo_root/scripts/release/build-server-appimage.sh" \
  "$artifact_dir/NeonStage-Server-x86_64.AppImage"
"$repo_root/scripts/release/build-editor-appimage.sh" \
  "$artifact_dir/NeonStage-LyricsEditor-x86_64.AppImage"
"$repo_root/scripts/release/build-stage-appimage.sh" \
  "$artifact_dir/NeonStage-Stage-x86_64.AppImage"

(cd "$artifact_dir" && sha256sum NeonStage-*-x86_64.AppImage > SHA256SUMS-AppImages)
echo "Linux AppImages created in: $artifact_dir"
