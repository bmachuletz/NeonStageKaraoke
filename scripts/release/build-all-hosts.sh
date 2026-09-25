#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
version_file="$repo_root/release-version.env"
artifact_root="$repo_root/artifacts/releases"
mac_host=${NEONSTAGE_MAC_HOST:-benjamin@192.168.178.48}
windows_host=${NEONSTAGE_WINDOWS_HOST:-benni@192.168.178.189}
mac_identity=${NEONSTAGE_MAC_SSH_IDENTITY:-${NEONSTAGE_SSH_IDENTITY:-}}
windows_identity=${NEONSTAGE_WINDOWS_SSH_IDENTITY:-${NEONSTAGE_SSH_IDENTITY:-}}
requested_version=""
reuse_build=0
skip_linux=0
skip_macos=0
skip_windows=0
skip_android=0
skip_unity=0
skip_tests=0
allow_debug_android_signing=0
commit_mode=ask
publish_mode=ask
keep_remote=0
dry_run=0

usage() {
  cat <<'EOF'
Usage: ./scripts/release/build-all-hosts.sh [options]

Creates one release from one committed source revision on three native hosts:
Linux locally, macOS and Windows through SSH. Artifacts are collected on Linux
and can be attached to one GitHub Release. Binaries are never committed to Git.

Host options:
  --mac-host USER@HOST          Default: benjamin@192.168.178.48
  --windows-host USER@HOST      Default: benni@192.168.178.189
  --mac-identity FILE           SSH private key for the Mac
  --windows-identity FILE       SSH private key for Windows

Release options:
  --version X.Y.Z               Start a new product version
  --reuse-build                 Rebuild the currently committed build number
  --commit                      Commit all reviewed changes, reserve the build,
                                and push before building
  --no-commit                   Only valid with --reuse-build and a clean tree
  --publish                     Create/update the GitHub prerelease afterwards
  --no-publish                  Keep the complete release local
  --output DIR                  Parent directory for release folders

Build options:
  --skip-linux | --skip-macos | --skip-windows
  --skip-android                Do not build the Android Stage on Linux
  --skip-unity                  Omit all Unity Stage players (implies Android skip)
  --skip-tests                  Skip the shared .NET tests on Linux
  --allow-debug-android-signing Permit a test-signed APK
  --keep-remote                 Keep remote temporary build directories
  --dry-run                     Validate local configuration and show the plan
EOF
}

while (($#)); do
  case "$1" in
    --mac-host) mac_host=${2:?--mac-host requires USER@HOST}; shift 2 ;;
    --windows-host) windows_host=${2:?--windows-host requires USER@HOST}; shift 2 ;;
    --mac-identity) mac_identity=${2:?--mac-identity requires a file}; shift 2 ;;
    --windows-identity) windows_identity=${2:?--windows-identity requires a file}; shift 2 ;;
    --version) requested_version=${2:?--version requires a value}; shift 2 ;;
    --reuse-build) reuse_build=1; shift ;;
    --commit) commit_mode=yes; shift ;;
    --no-commit) commit_mode=no; shift ;;
    --publish) publish_mode=yes; shift ;;
    --no-publish) publish_mode=no; shift ;;
    --output) artifact_root=${2:?--output requires a path}; shift 2 ;;
    --skip-linux) skip_linux=1; shift ;;
    --skip-macos) skip_macos=1; shift ;;
    --skip-windows) skip_windows=1; shift ;;
    --skip-android) skip_android=1; shift ;;
    --skip-unity) skip_unity=1; skip_android=1; shift ;;
    --skip-tests) skip_tests=1; shift ;;
    --allow-debug-android-signing) allow_debug_android_signing=1; shift ;;
    --keep-remote) keep_remote=1; shift ;;
    --dry-run) dry_run=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ $(uname -s) == Linux ]] || { echo "The coordinator must run on the Linux build host." >&2; exit 2; }
(( ! skip_linux || ! skip_macos || ! skip_windows )) || { echo "All build hosts were skipped." >&2; exit 2; }
[[ -f $version_file ]] || { echo "Missing version file: $version_file" >&2; exit 2; }
for command in git ssh scp tar sha256sum base64 iconv; do
  command -v "$command" >/dev/null || { echo "Required command is missing: $command" >&2; exit 2; }
done

stored_version=$(sed -n 's/^VERSION=//p' "$version_file" | tail -n 1)
stored_build=$(sed -n 's/^BUILD=//p' "$version_file" | tail -n 1)
semver_pattern='^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'
[[ $stored_version =~ $semver_pattern && $stored_build =~ ^[0-9]+$ ]] || {
  echo "Invalid release-version.env." >&2; exit 2;
}
version=${requested_version:-$stored_version}
[[ $version =~ $semver_pattern ]] || { echo "Invalid version: $version" >&2; exit 2; }
if (( reuse_build )); then
  [[ $version == "$stored_version" && $stored_build -gt 0 ]] || {
    echo "--reuse-build requires the stored version with BUILD > 0." >&2; exit 2;
  }
  build=$stored_build
else
  if [[ $version == "$stored_version" ]]; then build=$((stored_build + 1)); else build=1; fi
fi
release_name="v$version-build.$build"
target_dir="$artifact_root/$release_name"
run_id="$release_name-$(date -u +%Y%m%dT%H%M%SZ)-$$"

mac_ssh=(-o BatchMode=yes -o ConnectTimeout=10 -o ServerAliveInterval=15 -o ServerAliveCountMax=4)
windows_ssh=("${mac_ssh[@]}")
[[ -z $mac_identity ]] || mac_ssh+=(-i "$mac_identity" -o IdentitiesOnly=yes)
[[ -z $windows_identity ]] || windows_ssh+=(-i "$windows_identity" -o IdentitiesOnly=yes)

echo "Neon Stage distributed release $version (Build $build)"
echo "Linux:  $([[ $skip_linux == 1 ]] && echo skipped || echo local)"
echo "macOS:  $([[ $skip_macos == 1 ]] && echo skipped || echo "$mac_host")"
echo "Windows:$([[ $skip_windows == 1 ]] && echo skipped || echo " $windows_host")"
echo "Target:  $target_dir"
if (( dry_run )); then
  echo "Dry-run complete; no files, commits, hosts, or releases were changed."
  exit 0
fi

[[ ! -e $target_dir ]] || { echo "Release target already exists: $target_dir" >&2; exit 2; }
"$repo_root/scripts/release/verify-no-media.sh" "$repo_root"

if (( ! skip_macos )); then
  ssh "${mac_ssh[@]}" "$mac_host" 'test "$(uname -s)" = Darwin && command -v tar >/dev/null' || {
    echo "macOS SSH preflight failed: $mac_host" >&2; exit 2;
  }
fi

windows_ps() {
  local source=$1 encoded
  command -v iconv >/dev/null || { echo "iconv is required for Windows PowerShell remoting." >&2; exit 2; }
  encoded=$(printf '%s' "$source" | iconv -f UTF-8 -t UTF-16LE | base64 -w0)
  ssh "${windows_ssh[@]}" "$windows_host" \
    powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "$encoded"
}

if (( ! skip_windows )); then
  windows_ps '$ErrorActionPreference="Stop"; if (-not (Get-Command tar.exe -ErrorAction SilentlyContinue)) { throw "tar.exe fehlt" }; Write-Output ([Environment]::OSVersion.VersionString)' || {
    echo "Windows SSH/PowerShell preflight failed: $windows_host" >&2; exit 2;
  }
fi

if (( reuse_build )); then
  [[ -z $(git -C "$repo_root" status --porcelain) ]] || {
    echo "--reuse-build requires a clean working tree." >&2; exit 2;
  }
  [[ $commit_mode != yes ]] || echo "No new commit is needed for --reuse-build."
else
  if [[ $commit_mode == ask ]]; then
    [[ -t 0 ]] || { echo "Use --commit for a non-interactive release." >&2; exit 2; }
    read -r -p "Reserve $release_name, commit all reviewed changes, and push them? [j/N] " answer
    [[ $answer =~ ^[jJyY]$ ]] || { echo "Cancelled before changing the repository."; exit 0; }
    commit_mode=yes
  fi
  [[ $commit_mode == yes ]] || {
    echo "A new distributed build must reserve its version in a pushed commit. Use --commit." >&2
    exit 2
  }
  version_temp=$(mktemp "$repo_root/.release-version.XXXXXXXX")
  {
    echo '# Product version follows Semantic Versioning. BUILD is increased once after'
    echo '# every successful release-build run and is shared by all platform artifacts.'
    echo "VERSION=$version"
    echo "BUILD=$build"
  } > "$version_temp"
  chmod 644 "$version_temp"
  mv "$version_temp" "$version_file"
  "$repo_root/scripts/git/commit-and-push.sh" \
    --message "release: prepare $release_name" --push
fi

[[ -z $(git -C "$repo_root" status --porcelain) ]] || {
  echo "The source tree must be clean after release preparation." >&2; exit 2;
}
commit=$(git -C "$repo_root" rev-parse HEAD)
git -C "$repo_root" fetch origin --quiet
git -C "$repo_root" branch -r --contains "$commit" | grep -q . || {
  echo "Build commit $commit is not present on a remote branch." >&2; exit 2;
}

mkdir -p "$artifact_root"
staging_dir=$(mktemp -d "$artifact_root/.${release_name}.distributed.XXXXXXXX")
source_archive="$staging_dir/source.tar.gz"
git -C "$repo_root" archive --format=tar.gz -o "$source_archive" HEAD

cleanup() {
  rm -f -- "$source_archive"
  [[ ! -d $staging_dir ]] || rm -rf -- "$staging_dir"
  if (( ! keep_remote )); then
    if (( ! skip_macos )); then
      ssh "${mac_ssh[@]}" "$mac_host" "rm -rf -- \"\$HOME/.neonstage-build/$run_id\"" >/dev/null 2>&1 || true
    fi
    if (( ! skip_windows )); then
      windows_ps "\$path = Join-Path \$HOME '.neonstage-build\\$run_id'; if (Test-Path -LiteralPath \$path) { Remove-Item -LiteralPath \$path -Recurse -Force }" >/dev/null 2>&1 || true
    fi
  fi
}
trap cleanup EXIT

if (( ! skip_linux )); then
  linux_args=(--output "$staging_dir" --version "$version" --build "$build")
  (( skip_android )) && linux_args+=(--skip-android)
  (( skip_unity )) && linux_args+=(--skip-unity)
  (( skip_tests )) && linux_args+=(--skip-tests)
  (( allow_debug_android_signing )) && linux_args+=(--allow-debug-android-signing)
  "$repo_root/scripts/linux/build-release.sh" "${linux_args[@]}"
fi

if (( ! skip_macos )); then
  remote_root=".neonstage-build/$run_id"
  ssh "${mac_ssh[@]}" "$mac_host" \
    "mkdir -p \"\$HOME/$remote_root/source\" \"\$HOME/$remote_root/artifacts\""
  scp "${mac_ssh[@]}" "$source_archive" "$mac_host:$remote_root/source.tar.gz"
  mac_flags=""
  (( skip_unity )) && mac_flags=" --skip-unity"
  ssh "${mac_ssh[@]}" "$mac_host" \
    "set -e; tar -xzf \"\$HOME/$remote_root/source.tar.gz\" -C \"\$HOME/$remote_root/source\"; cd \"\$HOME/$remote_root/source\"; ./scripts/macos/build-release.sh --output \"\$HOME/$remote_root/artifacts\" --version '$version' --build '$build'$mac_flags; tar -czf \"\$HOME/$remote_root/artifacts.tar.gz\" -C \"\$HOME/$remote_root/artifacts\" ."
  scp "${mac_ssh[@]}" "$mac_host:$remote_root/artifacts.tar.gz" "$staging_dir/macos-artifacts.tar.gz"
  tar -xzf "$staging_dir/macos-artifacts.tar.gz" -C "$staging_dir"
  rm -f -- "$staging_dir/macos-artifacts.tar.gz"
fi

if (( ! skip_windows )); then
  remote_root=".neonstage-build/$run_id"
  windows_ps "\$root=Join-Path \$HOME '$remote_root'; New-Item -ItemType Directory -Force -Path (Join-Path \$root 'source'),(Join-Path \$root 'artifacts') | Out-Null"
  scp "${windows_ssh[@]}" "$source_archive" "$windows_host:$remote_root/source.tar.gz"
  windows_flags=""
  (( skip_unity )) && windows_flags=" -SkipUnity"
  windows_ps "\$ErrorActionPreference='Stop'; \$root=Join-Path \$HOME '$remote_root'; \$source=Join-Path \$root 'source'; \$artifacts=Join-Path \$root 'artifacts'; & tar.exe -xzf (Join-Path \$root 'source.tar.gz') -C \$source; if (\$LASTEXITCODE -ne 0) { throw 'Source extraction failed' }; & (Join-Path \$source 'scripts\\windows\\build-release.ps1') -OutputDirectory \$artifacts -Version '$version' -BuildNumber $build$windows_flags; & tar.exe -czf (Join-Path \$root 'artifacts.tar.gz') -C \$artifacts .; if (\$LASTEXITCODE -ne 0) { throw 'Artifact archive failed' }"
  scp "${windows_ssh[@]}" "$windows_host:$remote_root/artifacts.tar.gz" "$staging_dir/windows-artifacts.tar.gz"
  tar -xzf "$staging_dir/windows-artifacts.tar.gz" -C "$staging_dir"
  rm -f -- "$staging_dir/windows-artifacts.tar.gz"
fi

rm -f -- "$source_archive"
for notice in LICENSE NOTICE THIRD_PARTY_NOTICES.md ACKNOWLEDGEMENTS.md; do
  cp "$repo_root/$notice" "$staging_dir/"
done
platforms=()
if (( ! skip_linux )); then
  platforms+=(linux)
  (( skip_android )) || platforms+=(android)
fi
(( skip_macos )) || platforms+=(macos)
(( skip_windows )) || platforms+=(windows)
printf '%s\n' \
  "NEONSTAGE_VERSION=$version" \
  "NEONSTAGE_BUILD=$build" \
  "GIT_COMMIT=$commit" \
  "PLATFORMS=${platforms[*]}" \
  "CREATED_UTC=$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$staging_dir/RELEASE-METADATA.txt"

require_artifact() {
  compgen -G "$staging_dir/$1" >/dev/null || { echo "Missing release artifact: $1" >&2; exit 1; }
}
if (( ! skip_linux )); then
  require_artifact "NeonStage-Server-$release_name-linux-x86_64.AppImage"
  require_artifact "NeonStage-LyricsEditor-$release_name-linux-x86_64.AppImage"
  (( skip_unity )) || require_artifact "NeonStage-Stage-$release_name-linux-x86_64.AppImage"
  (( skip_android )) || require_artifact "NeonStage-Stage-$release_name-android-armv7.apk"
fi
if (( ! skip_macos )); then
  require_artifact "NeonStage-Server-$release_name-macOS-arm64.zip"
  require_artifact "NeonStage-LyricsEditor-$release_name-macOS-arm64.zip"
  (( skip_unity )) || require_artifact "NeonStage-Stage-$release_name-macOS-*.zip"
fi
if (( ! skip_windows )); then
  require_artifact "NeonStage-Server-$release_name-windows-x64.zip"
  require_artifact "NeonStage-Server-$release_name-windows-x64.exe"
  require_artifact "NeonStage-LyricsEditor-$release_name-windows-x64.zip"
  require_artifact "NeonStage-LyricsEditor-$release_name-windows-x64.exe"
  if (( ! skip_unity )); then
    require_artifact "NeonStage-Stage-$release_name-windows-x64.zip"
    require_artifact "NeonStage-Stage-$release_name-windows-x64.exe"
  fi
fi

(
  cd "$staging_dir"
  mapfile -d '' files < <(find . -maxdepth 1 -type f ! -name SHA256SUMS -print0 | sort -z)
  sha256sum "${files[@]}" > SHA256SUMS
)
"$repo_root/scripts/release/verify-no-media.sh" "$staging_dir"
mv "$staging_dir" "$target_dir"
trap - EXIT
cleanup

echo "Distributed release created: $target_dir"
publish_answer=no
case "$publish_mode" in
  yes) publish_answer=yes ;;
  no) ;;
  ask)
    if [[ -t 0 ]]; then
      read -r -p "Publish $release_name as a GitHub prerelease now? [j/N] " answer
      [[ $answer =~ ^[jJyY]$ ]] && publish_answer=yes
    fi
    ;;
esac
if [[ $publish_answer == yes ]]; then
  command -v gh >/dev/null || { echo "GitHub CLI gh is missing." >&2; exit 3; }
  gh auth status >/dev/null
  if gh release view "$release_name" >/dev/null 2>&1; then
    gh release upload "$release_name" "$target_dir"/* --clobber
  else
    gh release create "$release_name" "$target_dir"/* --target "$commit" \
      --title "Neon Stage $version (Build $build)" --generate-notes --prerelease
  fi
  echo "GitHub Release published: $release_name"
else
  echo "GitHub upload skipped."
fi
