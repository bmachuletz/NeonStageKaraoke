#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
artifact_dir="$repo_root/artifacts"
build_server=1
build_editor=1
build_stage=1

usage() {
  cat <<'EOF'
Usage: ./scripts/build-appimages.sh [options] [output-directory]

Options:
  --server-only       Build only the server AppImage
  --editor-only       Build only the lyrics editor AppImage
  --stage-only        Build only the Unity stage AppImage
  --skip-stage        Build server and editor without requiring Unity
  --skip-unity-build  Package an existing Builds/Linux Unity player
  --no-auto-install   Do not install/download missing build dependencies
  -h, --help          Show this help
EOF
}

while (($#)); do
  case "$1" in
    --server-only) build_server=1; build_editor=0; build_stage=0 ;;
    --editor-only) build_server=0; build_editor=1; build_stage=0 ;;
    --stage-only) build_server=0; build_editor=0; build_stage=1 ;;
    --skip-stage) build_stage=0 ;;
    --skip-unity-build) export NEONSTAGE_SKIP_UNITY_BUILD=1 ;;
    --no-auto-install) export NEONSTAGE_AUTO_INSTALL_DEPS=0 ;;
    -h|--help) usage; exit 0 ;;
    --*) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    *) artifact_dir=$1 ;;
  esac
  shift
done

mkdir -p "$artifact_dir"
outputs=()

if (( build_server )); then
  output="$artifact_dir/NeonStage-Server-x86_64.AppImage"
  "$repo_root/scripts/release/build-server-appimage.sh" "$output"
  outputs+=("$output")
fi
if (( build_editor )); then
  output="$artifact_dir/NeonStage-LyricsEditor-x86_64.AppImage"
  "$repo_root/scripts/release/build-editor-appimage.sh" "$output"
  outputs+=("$output")
fi
if (( build_stage )); then
  output="$artifact_dir/NeonStage-Stage-x86_64.AppImage"
  "$repo_root/scripts/release/build-stage-appimage.sh" "$output"
  outputs+=("$output")
fi
(( ${#outputs[@]} > 0 )) || { echo 'No AppImage component selected.' >&2; exit 2; }

(
  cd "$artifact_dir"
  names=()
  for output in "${outputs[@]}"; do names+=("$(basename "$output")"); done
  sha256sum "${names[@]}" > SHA256SUMS-AppImages
)
echo "Linux AppImages created in: $artifact_dir"
