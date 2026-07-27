#!/usr/bin/env bash

# Shared dependency bootstrap for the AppImage builders. Callers are expected
# to enable `set -Eeuo pipefail` before sourcing this file.

ns_log() { printf '[AppImage] %s\n' "$*" >&2; }
ns_die() { printf '[AppImage] ERROR: %s\n' "$*" >&2; exit 1; }

ns_require_linux_x64() {
  [[ $(uname -s) == Linux ]] || ns_die 'AppImages can only be built on Linux.'
  [[ $(uname -m) == x86_64 ]] || ns_die 'The AppImage build currently supports x86_64 only.'
}

ns_root_run() {
  if [[ ${EUID:-$(id -u)} -eq 0 ]]; then "$@"; return; fi
  command -v sudo >/dev/null || ns_die "Administrative rights are required. Install sudo or install the listed packages manually: $*"
  sudo "$@"
}

ns_install_system_packages() {
  local profile=$1
  [[ ${NEONSTAGE_AUTO_INSTALL_DEPS:-1} != 0 ]] ||
    ns_die 'Build dependencies are missing and NEONSTAGE_AUTO_INSTALL_DEPS=0 disables automatic installation.'

  if command -v apt-get >/dev/null; then
    local packages=(ca-certificates curl file findutils binutils imagemagick)
    [[ $profile == server || $profile == editor || $profile == all ]] && packages+=(ffmpeg)
    [[ $profile == editor || $profile == all ]] && packages+=(vlc)
    ns_log "Installing missing build dependencies with apt: ${packages[*]}"
    ns_root_run apt-get update
    ns_root_run env DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "${packages[@]}"
  elif command -v dnf >/dev/null; then
    local packages=(ca-certificates curl file findutils binutils ImageMagick)
    [[ $profile == server || $profile == editor || $profile == all ]] && packages+=(ffmpeg)
    [[ $profile == editor || $profile == all ]] && packages+=(vlc vlc-devel)
    ns_log "Installing missing build dependencies with dnf: ${packages[*]}"
    ns_root_run dnf install -y "${packages[@]}"
  elif command -v pacman >/dev/null; then
    local packages=(ca-certificates curl file findutils binutils imagemagick)
    [[ $profile == server || $profile == editor || $profile == all ]] && packages+=(ffmpeg)
    [[ $profile == editor || $profile == all ]] && packages+=(vlc)
    ns_log "Installing missing build dependencies with pacman: ${packages[*]}"
    ns_root_run pacman -Sy --needed --noconfirm "${packages[@]}"
  elif command -v zypper >/dev/null; then
    local packages=(ca-certificates curl file findutils binutils ImageMagick)
    [[ $profile == server || $profile == editor || $profile == all ]] && packages+=(ffmpeg)
    [[ $profile == editor || $profile == all ]] && packages+=(vlc vlc-devel)
    ns_log "Installing missing build dependencies with zypper: ${packages[*]}"
    ns_root_run zypper --non-interactive install --no-recommends "${packages[@]}"
  else
    ns_die 'No supported package manager found (apt, dnf, pacman, or zypper). Install curl, file, findutils, ImageMagick, FFmpeg, and VLC manually.'
  fi
}

ns_has_image_converter() { command -v magick >/dev/null || command -v convert >/dev/null; }

ns_has_libvlc() {
  command -v ldconfig >/dev/null || return 1
  ldconfig -p 2>/dev/null | awk '/libvlc\.so\.[0-9]+ / { found=1 } END { exit !found }' || return 1
  find /usr/lib /usr/lib64 -type d -path '*/vlc/plugins' -print -quit 2>/dev/null | grep -q .
}

ns_prepare_system_dependencies() {
  local profile=${1:-all}
  ns_require_linux_x64
  local missing=0 command
  for command in curl file find awk sha256sum ldd ldconfig; do
    command -v "$command" >/dev/null || missing=1
  done
  ns_has_image_converter || missing=1
  if [[ $profile == server || $profile == editor || $profile == all ]]; then
    command -v ffmpeg >/dev/null || missing=1
  fi
  if [[ $profile == editor || $profile == all ]]; then ns_has_libvlc || missing=1; fi
  (( missing == 0 )) || ns_install_system_packages "$profile"

  for command in curl file find awk sha256sum ldd ldconfig; do
    command -v "$command" >/dev/null || ns_die "Missing command after dependency setup: $command"
  done
  ns_has_image_converter || ns_die 'ImageMagick is unavailable after dependency setup (magick/convert).'
  if [[ $profile == server || $profile == editor || $profile == all ]]; then
    command -v ffmpeg >/dev/null || ns_die 'FFmpeg is unavailable after dependency setup.'
  fi
  if [[ $profile == editor || $profile == all ]]; then
    ns_has_libvlc || ns_die 'LibVLC or its plugin directory is unavailable after dependency setup.'
  fi
}

ns_ensure_dotnet_10() {
  local repo_root=$1 local_root="$1/.tools/dotnet"
  if [[ -x "$local_root/dotnet" ]]; then
    export DOTNET_ROOT="$local_root"
    export PATH="$local_root:$PATH"
  fi
  if command -v dotnet >/dev/null && dotnet --list-sdks 2>/dev/null | awk '$1 ~ /^10\./ { found=1 } END { exit !found }'; then
    return
  fi
  [[ ${NEONSTAGE_AUTO_INSTALL_DEPS:-1} != 0 ]] || ns_die '.NET SDK 10 is missing and automatic installation is disabled.'
  mkdir -p "$local_root" "$repo_root/.tools/downloads"
  local installer="$repo_root/.tools/downloads/dotnet-install.sh"
  ns_download_file "$installer" 'https://dot.net/v1/dotnet-install.sh'
  ns_log "Installing .NET SDK 10 locally into $local_root"
  bash "$installer" --channel 10.0 --quality GA --install-dir "$local_root" --no-path
  export DOTNET_ROOT="$local_root"
  export PATH="$local_root:$PATH"
  dotnet --list-sdks | awk '$1 ~ /^10\./ { found=1 } END { exit !found }' || ns_die '.NET SDK 10 installation did not complete successfully.'
}

ns_download_file() {
  local target=$1 url=$2 expected_sha=${3:-}
  if [[ -s "$target" ]]; then
    if [[ -z $expected_sha ]] || printf '%s  %s\n' "$expected_sha" "$target" | sha256sum --check --status; then return; fi
    [[ ${NEONSTAGE_AUTO_INSTALL_DEPS:-1} != 0 ]] ||
      ns_die "Cached download does not match its configured checksum: $target"
    rm -f -- "$target"
  fi
  [[ ${NEONSTAGE_AUTO_INSTALL_DEPS:-1} != 0 ]] ||
    ns_die "Required build tool is not cached and automatic downloads are disabled: $target"
  mkdir -p "$(dirname "$target")"
  local temporary="$target.part"
  rm -f -- "$temporary"
  ns_log "Downloading $url"
  curl --fail --location --retry 3 --retry-all-errors --connect-timeout 20 -o "$temporary" "$url"
  if [[ -n $expected_sha ]]; then
    printf '%s  %s\n' "$expected_sha" "$temporary" | sha256sum --check --status || {
      rm -f -- "$temporary"; ns_die "Checksum verification failed for $url";
    }
  fi
  mv -- "$temporary" "$target"
}

ns_ensure_appimage_tool() {
  local repo_root=$1 target="$1/.tools/appimage/appimagetool-x86_64.AppImage"
  ns_download_file "$target" \
    "${APPIMAGETOOL_URL:-https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage}" \
    "${APPIMAGETOOL_SHA256:-}"
  chmod +x "$target"
  printf '%s\n' "$target"
}

ns_ensure_linuxdeploy() {
  local repo_root=$1 target="$1/.tools/appimage/linuxdeploy-x86_64.AppImage"
  ns_download_file "$target" \
    "${LINUXDEPLOY_URL:-https://github.com/linuxdeploy/linuxdeploy/releases/download/continuous/linuxdeploy-x86_64.AppImage}" \
    "${LINUXDEPLOY_SHA256:-}"
  chmod +x "$target"
  printf '%s\n' "$target"
}

ns_convert_icon() {
  local source=$1 target=$2
  if command -v magick >/dev/null; then magick "$source" -resize 256x256! "$target"
  else convert "$source" -resize 256x256! "$target"
  fi
}

ns_find_libvlc() {
  NS_LIBVLC=$(ldconfig -p | awk '/libvlc\.so\.[0-9]+ / && !found { value=$NF; found=1 } END { print value }')
  NS_LIBVLCCORE=$(ldconfig -p | awk '/libvlccore\.so\.[0-9]+ / && !found { value=$NF; found=1 } END { print value }')
  NS_VLC_PLUGIN_DIR=$(find /usr/lib /usr/lib64 -type d -path '*/vlc/plugins' -print -quit 2>/dev/null)
  [[ -n $NS_LIBVLC && -n $NS_LIBVLCCORE && -n $NS_VLC_PLUGIN_DIR ]] || ns_die 'Could not resolve LibVLC runtime files.'
  export NS_LIBVLC NS_LIBVLCCORE NS_VLC_PLUGIN_DIR
}

ns_copy_ffmpeg_licenses() {
  local appdir=$1 destination="$1/usr/share/doc/neon-stage/licenses"
  mkdir -p "$destination"
  ffmpeg -version > "$destination/FFmpeg-build.txt"
  local copied=0 source license_file license_source
  for source in /usr/share/doc/ffmpeg/copyright /usr/share/licenses/ffmpeg/COPYING* /usr/share/licenses/ffmpeg/LICENSE*; do
    if [[ -f "$source" ]]; then cp "$source" "$destination/ffmpeg-$(basename "$source")"; copied=1; fi
  done
  (( copied == 1 )) || ns_die 'FFmpeg license/copyright metadata was not found on this system.'

  # Distributor copyright files commonly refer to these canonical texts by
  # path. Include them in the portable artifact so the reference never points
  # only to a file installed on the build host.
  for license_file in LGPL-2.1 LGPL-3 GPL-2 GPL-3; do
    license_source="/usr/share/common-licenses/$license_file"
    if [[ ! -f "$license_source" ]]; then
      license_source=$(find /usr/share/licenses -type f \
        \( -iname "*$license_file*" -o -iname "*${license_file/./}*" \) \
        -print -quit 2>/dev/null || true)
    fi
    [[ -f "$license_source" ]] && cp "$license_source" "$destination/$license_file.txt"
  done
}

ns_verify_appimage() {
  local artifact=$1
  [[ -s "$artifact" && -x "$artifact" ]] || ns_die "AppImage was not created correctly: $artifact"
  file "$artifact" | grep -q 'ELF 64-bit' || ns_die "Artifact is not a 64-bit ELF AppImage: $artifact"
  ns_log "Verified $(basename "$artifact") ($(du -h "$artifact" | awk '{print $1}'))"
}
