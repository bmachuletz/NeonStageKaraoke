#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
version_file="$repo_root/release-version.env"
artifact_root="$repo_root/artifacts/releases"
requested_version=""
platform_args=()
reuse_build=0
skip_unity=0
skip_tests=0
allow_dirty=0
allow_debug_android_signing=0
dry_run=0
publish_mode=ask

usage() {
  cat <<'EOF'
Usage: ./scripts/release/build-release.sh [options]

Builds versioned Neon Stage release artifacts on the current host. A successful
run increments the shared build number in release-version.env exactly once.

Options:
  --platform LIST       Comma-separated: linux, android, macos, windows
                        May be repeated. Default: current desktop platform.
  --version X.Y.Z       Start or continue this product version. A new version
                        starts at build 1 (default state: 0.1.0, build 0).
  --reuse-build         Reuse the current build number for another build host.
  --skip-unity          Build Server and Lyrics Editor without the Unity Stage.
  --skip-tests          Skip .NET build and test suites.
  --allow-dirty         Allow a build from a modified Git working tree.
  --allow-debug-android-signing
                        Permit Unity's non-production Android signing fallback.
  --publish             Upload without asking (requires authenticated GitHub CLI).
  --no-publish          Never ask and keep the release local.
  --output DIR          Parent directory for versioned release folders.
  --dry-run             Validate and print the plan without building/incrementing.
  -h, --help            Show this help.

Android production signing environment:
  NEONSTAGE_ANDROID_KEYSTORE       Absolute path to the keystore
  NEONSTAGE_ANDROID_KEYALIAS       Key alias
  NEONSTAGE_ANDROID_KEYSTORE_PASS  Keystore password
  NEONSTAGE_ANDROID_KEYALIAS_PASS  Alias password

Examples:
  ./scripts/release/build-release.sh --platform linux,android
  ./scripts/release/build-release.sh --platform macos --reuse-build
  ./scripts/release/build-release.sh --version 0.2.0 --platform linux
EOF
}

while (($#)); do
  case "$1" in
    --platform)
      [[ $# -ge 2 ]] || { echo "--platform benötigt einen Wert." >&2; exit 2; }
      platform_args+=("$2"); shift 2 ;;
    --version)
      [[ $# -ge 2 ]] || { echo "--version benötigt einen Wert." >&2; exit 2; }
      requested_version=$2; shift 2 ;;
    --reuse-build) reuse_build=1; shift ;;
    --skip-unity) skip_unity=1; shift ;;
    --skip-tests) skip_tests=1; shift ;;
    --allow-dirty) allow_dirty=1; shift ;;
    --allow-debug-android-signing) allow_debug_android_signing=1; shift ;;
    --publish) publish_mode=yes; shift ;;
    --no-publish) publish_mode=no; shift ;;
    --output)
      [[ $# -ge 2 ]] || { echo "--output benötigt einen Wert." >&2; exit 2; }
      artifact_root=$2; shift 2 ;;
    --dry-run) dry_run=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unbekannte Option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ -f "$version_file" ]] || { echo "Fehlende Versionsdatei: $version_file" >&2; exit 2; }
stored_version=$(sed -n 's/^VERSION=//p' "$version_file" | tail -n 1)
stored_build=$(sed -n 's/^BUILD=//p' "$version_file" | tail -n 1)
semver_pattern='^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$'
[[ $stored_version =~ $semver_pattern ]] || { echo "Ungültige VERSION in $version_file" >&2; exit 2; }
[[ $stored_build =~ ^[0-9]+$ ]] || { echo "Ungültige BUILD-Nummer in $version_file" >&2; exit 2; }

version=${requested_version:-$stored_version}
[[ $version =~ $semver_pattern ]] || {
  echo "Version muss X.Y.Z oder X.Y.Z-suffix entsprechen: $version" >&2; exit 2;
}
if (( reuse_build )); then
  [[ $version == "$stored_version" && $stored_build -gt 0 ]] || {
    echo "--reuse-build benötigt die gespeicherte Version mit BUILD > 0." >&2; exit 2;
  }
  build=$stored_build
elif [[ $version == "$stored_version" ]]; then
  build=$((stored_build + 1))
else
  build=1
fi

case "$(uname -s)" in
  Linux*) host=linux; default_platform=linux ;;
  Darwin*) host=macos; default_platform=macos ;;
  MINGW*|MSYS*|CYGWIN*) host=windows; default_platform=windows ;;
  *) echo "Nicht unterstütztes Build-Betriebssystem: $(uname -s)" >&2; exit 2 ;;
esac

if ((${#platform_args[@]} == 0)); then platform_args=("$default_platform"); fi
platforms=()
for argument in "${platform_args[@]}"; do
  IFS=',' read -r -a split_platforms <<< "$argument"
  for platform in "${split_platforms[@]}"; do
    case "$platform" in
      linux|android|macos|windows) ;;
      *) echo "Unbekannte Plattform: $platform" >&2; exit 2 ;;
    esac
    if [[ " ${platforms[*]} " != *" $platform "* ]]; then platforms+=("$platform"); fi
  done
done

for platform in "${platforms[@]}"; do
  case "$platform:$host" in
    linux:linux|android:linux|macos:macos|windows:windows) ;;
    *) echo "$platform kann mit diesem Skript nicht auf dem Host $host gebaut werden." >&2
       echo "Baue auf dem passenden Host weiter und verwende dort --reuse-build." >&2
       exit 2 ;;
  esac
done
if (( skip_unity )) && [[ " ${platforms[*]} " == *" android "* ]]; then
  echo "--skip-unity ist nicht mit --platform android kombinierbar." >&2
  exit 2
fi

if [[ " ${platforms[*]} " == *" android "* ]] && (( ! allow_debug_android_signing )); then
  signing_vars=(NEONSTAGE_ANDROID_KEYSTORE NEONSTAGE_ANDROID_KEYALIAS \
    NEONSTAGE_ANDROID_KEYSTORE_PASS NEONSTAGE_ANDROID_KEYALIAS_PASS)
  for variable in "${signing_vars[@]}"; do
    [[ -n ${!variable:-} ]] || {
      echo "Für einen Android-Release fehlt $variable." >&2
      echo "Nur für Testpakete kann --allow-debug-android-signing verwendet werden." >&2
      exit 2
    }
  done
  [[ -f $NEONSTAGE_ANDROID_KEYSTORE ]] || {
    echo "Android-Keystore nicht gefunden: $NEONSTAGE_ANDROID_KEYSTORE" >&2; exit 2;
  }
fi

if (( ! allow_dirty )) && [[ -n $(git -C "$repo_root" status --porcelain) ]]; then
  echo "Der Git-Arbeitsbaum ist nicht sauber. Committe Änderungen oder verwende bewusst --allow-dirty." >&2
  exit 2
fi

release_name="v$version-build.$build"
target_dir="$artifact_root/$release_name"
echo "Neon Stage Release $version (Build $build)"
echo "Host: $host · Plattformen: ${platforms[*]}"
echo "Ziel: $target_dir"
if (( dry_run )); then
  echo "Dry-run abgeschlossen; Buildnummer bleibt unverändert."
  exit 0
fi
[[ ! -e "$target_dir" ]] || { echo "Release-Ziel existiert bereits: $target_dir" >&2; exit 2; }

mkdir -p "$artifact_root"
staging_dir=$(mktemp -d "$artifact_root/.${release_name}.partial.XXXXXXXX")
cleanup() { rm -rf -- "$staging_dir"; }
trap cleanup EXIT

"$repo_root/scripts/release/verify-no-media.sh" "$repo_root"
if (( ! skip_tests )); then
  dotnet build "$repo_root/src/Karaoke.Server/Karaoke.Server.csproj" -c Release --maxcpucount:1
  dotnet build "$repo_root/src/Karaoke.App.Desktop/Karaoke.App.Desktop.csproj" -c Release --maxcpucount:1
  dotnet build "$repo_root/tests/Karaoke.Editor.Core.Tests/Karaoke.Editor.Core.Tests.csproj" \
    -c Release --maxcpucount:1
  dotnet build "$repo_root/tests/Karaoke.Server.PlaybackTests/Karaoke.Server.PlaybackTests.csproj" \
    -c Release --maxcpucount:1
  dotnet run --project "$repo_root/tests/Karaoke.Editor.Core.Tests/Karaoke.Editor.Core.Tests.csproj" \
    -c Release --no-build
  dotnet run --project "$repo_root/tests/Karaoke.Server.PlaybackTests/Karaoke.Server.PlaybackTests.csproj" \
    -c Release --no-build
fi

export NEONSTAGE_VERSION=$version
export NEONSTAGE_BUILD_NUMBER=$build
export NEONSTAGE_RELEASE_BUILD=1
suffix="v$version-build.$build"

if [[ " ${platforms[*]} " == *" linux "* ]]; then
  "$repo_root/scripts/release/build-server-appimage.sh" \
    "$staging_dir/NeonStage-Server-$suffix-linux-x86_64.AppImage"
  "$repo_root/scripts/release/build-editor-appimage.sh" \
    "$staging_dir/NeonStage-LyricsEditor-$suffix-linux-x86_64.AppImage"
  if (( ! skip_unity )); then
    "$repo_root/scripts/release/build-stage-appimage.sh" \
      "$staging_dir/NeonStage-Stage-$suffix-linux-x86_64.AppImage"
  fi
fi

if [[ " ${platforms[*]} " == *" android "* ]]; then
  if (( allow_debug_android_signing )) && [[ -z ${NEONSTAGE_ANDROID_KEYSTORE:-} ]]; then
    echo "WARNUNG: Android-Paket wird nur mit Unitys Testsignatur gebaut." >&2
  fi
  "$repo_root/scripts/linux/build-unity-stage-android.sh"
  cp "$repo_root/src/Karaoke.Stage.Unity/Builds/Android/NeonStage-ShellS2-arm32.apk" \
    "$staging_dir/NeonStage-Stage-$suffix-android-armv7.apk"
fi

if [[ " ${platforms[*]} " == *" macos "* ]]; then
  "$repo_root/scripts/macos/build-unity-stage-macos.sh"
  mac_arch=arm64
  [[ ${NEONSTAGE_MACOS_UNIVERSAL:-0} == 1 ]] && mac_arch=universal
  ditto -c -k --sequesterRsrc --keepParent \
    "$repo_root/src/Karaoke.Stage.Unity/Builds/macOS/NeonStage Karaoke.app" \
    "$staging_dir/NeonStage-Stage-$suffix-macOS-$mac_arch.zip"
fi

if [[ " ${platforms[*]} " == *" windows "* ]]; then
  if command -v pwsh >/dev/null; then powershell_command=pwsh
  elif command -v powershell.exe >/dev/null; then powershell_command=powershell.exe
  else echo "PowerShell wurde nicht gefunden." >&2; exit 2
  fi
  windows_arguments=(-NoProfile -File "$repo_root/scripts/windows/build-release.ps1" \
    -OutputDirectory "$staging_dir" -Version "$version" -BuildNumber "$build")
  (( skip_unity )) && windows_arguments+=(-SkipUnity)
  "$powershell_command" "${windows_arguments[@]}"
fi

cp "$repo_root/LICENSE" "$repo_root/NOTICE" "$repo_root/THIRD_PARTY_NOTICES.md" \
  "$repo_root/ACKNOWLEDGEMENTS.md" "$staging_dir/"
commit=$(git -C "$repo_root" rev-parse HEAD)
cat > "$staging_dir/RELEASE-METADATA.txt" <<EOF
NEONSTAGE_VERSION=$version
NEONSTAGE_BUILD=$build
GIT_COMMIT=$commit
PLATFORMS=${platforms[*]}
CREATED_UTC=$(date -u +%Y-%m-%dT%H:%M:%SZ)
EOF

(
  cd "$staging_dir"
  checksum_files=()
  while IFS= read -r file; do checksum_files+=("${file#./}"); done \
    < <(find . -maxdepth 1 -type f ! -name SHA256SUMS -print | LC_ALL=C sort)
  if command -v sha256sum >/dev/null; then
    sha256sum "${checksum_files[@]}" > SHA256SUMS
  else
    shasum -a 256 "${checksum_files[@]}" > SHA256SUMS
  fi
)
"$repo_root/scripts/release/verify-no-media.sh" "$staging_dir"
mv "$staging_dir" "$target_dir"
trap - EXIT

if (( ! reuse_build )); then
  state_temp=$(mktemp "$repo_root/.release-version.XXXXXXXX")
  {
    echo '# Product version follows Semantic Versioning. BUILD is increased once after'
    echo '# every successful release-build run and is shared by all platform artifacts.'
    echo "VERSION=$version"
    echo "BUILD=$build"
  } > "$state_temp"
  chmod 644 "$state_temp"
  mv "$state_temp" "$version_file"
fi

echo "Release erfolgreich: $target_dir"
echo "Versionsstand: $version, Build $build"
if (( ! reuse_build )); then
  echo "Bitte release-version.env zusammen mit den Release-Notizen committen."
fi

publish_answer=no
case "$publish_mode" in
  yes) publish_answer=yes ;;
  no) ;;
  ask)
    if [[ -t 0 ]]; then
      read -r -p "Release $release_name jetzt zu GitHub hochladen? [j/N] " answer
      case "$answer" in j|J|ja|JA|y|Y|yes|YES) publish_answer=yes ;; esac
    else
      echo "Keine interaktive Eingabe: GitHub-Upload wird übersprungen."
    fi
    ;;
esac

if [[ $publish_answer == yes ]]; then
  command -v gh >/dev/null || {
    echo "GitHub CLI 'gh' fehlt. Release bleibt lokal unter: $target_dir" >&2
    echo "Nach Installation erneut mit 'gh release create $release_name ...' hochladen." >&2
    exit 3
  }
  gh auth status >/dev/null || {
    echo "GitHub CLI ist nicht angemeldet. Zuerst 'gh auth login' ausführen." >&2
    exit 3
  }
  git -C "$repo_root" fetch origin --quiet
  if ! git -C "$repo_root" branch -r --contains "$commit" | grep -q .; then
    echo "Der Build-Commit $commit ist noch auf keinem Remote-Branch vorhanden." >&2
    echo "Bitte zuerst den Quellstand pushen; danach kann das lokale Release mit gh hochgeladen werden." >&2
    exit 3
  fi
  release_title="Neon Stage $version (Build $build)"
  if gh release view "$release_name" >/dev/null 2>&1; then
    echo "GitHub Release $release_name existiert bereits; Assets werden aktualisiert."
    gh release upload "$release_name" "$target_dir"/* --clobber
  else
    gh release create "$release_name" "$target_dir"/* \
      --target "$commit" \
      --title "$release_title" \
      --generate-notes \
      --prerelease
  fi
  echo "GitHub Release veröffentlicht: $release_name"
else
  echo "GitHub-Upload übersprungen."
fi
