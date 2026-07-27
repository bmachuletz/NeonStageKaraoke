#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
tools_dir="$repo_root/.tools"
sunnify_dir="$tools_dir/sunnify-spotify-downloader"
venv_dir="$tools_dir/sunnify-venv"
version="${SUNNIFY_VERSION:-v2.1.1}"

command -v git >/dev/null || { echo "git fehlt." >&2; exit 1; }
command -v python3 >/dev/null || { echo "python3 fehlt." >&2; exit 1; }
command -v ffmpeg >/dev/null || { echo "ffmpeg fehlt." >&2; exit 1; }
mkdir -p "$tools_dir"

if [[ ! -d "$sunnify_dir/.git" ]]; then
  git clone --branch "$version" --depth 1 https://github.com/sunnypatell/sunnify-spotify-downloader.git "$sunnify_dir"
else
  git -C "$sunnify_dir" fetch --depth 1 origin "refs/tags/$version:refs/tags/$version"
  git -C "$sunnify_dir" checkout --detach "$version"
fi

python3 -m venv "$venv_dir"
"$venv_dir/bin/python" -m pip install --upgrade pip
"$venv_dir/bin/python" -m pip install -r "$sunnify_dir/req.txt"

echo "Sunnify Headless $version ist eingerichtet."
echo "Worker: $repo_root/scripts/linux/process-wishlist.sh"
