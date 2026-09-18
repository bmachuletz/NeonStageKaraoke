#!/usr/bin/env bash
set -Eeuo pipefail

project_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
unity_project=${1:-"$project_root/src/Karaoke.Stage.Unity"}
package_cache="$unity_project/Library/PackageCache"
patch_file="$project_root/patches/livekit-unity-2.0.0-android-context.patch"

package_dir=$(find "$package_cache" -mindepth 1 -maxdepth 1 -type d \
  -name 'io.livekit.livekit-sdk@*' -print -quit 2>/dev/null || true)

if [[ -z "$package_dir" ]]; then
  echo "LiveKit-Paketcache fehlt. Öffne das Unity-Projekt einmal, damit die Pakete aufgelöst werden." >&2
  exit 1
fi

client_file="$package_dir/Runtime/Scripts/Internal/FFI/FFIClient.cs"
if [[ ! -f "$client_file" ]]; then
  echo "Nicht unterstützte LiveKit-Paketstruktur: $client_file fehlt." >&2
  exit 1
fi

if grep -Fq 'ContextUtils initialized via managed fallback' "$client_file"; then
  exit 0
fi

if ! grep -Fq 'Android context init failed; PlatformAudio will not work' "$client_file"; then
  echo "LiveKit Android-Kontext-Patch passt nicht zur installierten SDK-Version." >&2
  exit 1
fi

patch --directory="$package_dir" --strip=1 --forward --input="$patch_file"
echo "LiveKit Android-Kontext-Fallback wurde in den Unity-Paketcache eingespielt."
