#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
# shellcheck source=scripts/release/lib/appimage-common.sh
source "$repo_root/scripts/release/lib/appimage-common.sh"

profile=${1:-all}
case "$profile" in server|editor|stage|all) ;; *) ns_die 'Usage: setup-appimage-build-deps.sh [server|editor|stage|all]' ;; esac
ns_prepare_system_dependencies "$profile"
if [[ $profile != stage ]]; then ns_ensure_dotnet_10 "$repo_root"; fi
ns_ensure_appimage_tool "$repo_root" >/dev/null
if [[ $profile == server || $profile == editor || $profile == all ]]; then
  ns_ensure_linuxdeploy "$repo_root" >/dev/null
fi
ns_log "Dependency setup completed for profile: $profile"
