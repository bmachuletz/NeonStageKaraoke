#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
output_dir=""
version=""
build_number=""
skip_android=0
skip_unity=0
skip_tests=0
allow_debug_android_signing=0

usage() {
  cat <<'EOF'
Usage: ./scripts/linux/build-release.sh --output DIR --version X.Y.Z --build N [options]

Builds the native Linux release artifacts and, by default, the Android Stage.
This is a component builder: it never changes release-version.env, commits,
pushes, tags, or publishes a GitHub Release.

Options:
  --skip-android                 Do not build the Android APK.
  --skip-unity                   Build Server and Editor without either Unity player.
  --skip-tests                   Skip .NET builds and executable test suites.
  --allow-debug-android-signing  Permit Unity's test key for a local test APK.
EOF
}

while (($#)); do
  case "$1" in
    --output) output_dir=${2:?--output requires a path}; shift 2 ;;
    --version) version=${2:?--version requires a value}; shift 2 ;;
    --build) build_number=${2:?--build requires a value}; shift 2 ;;
    --skip-android) skip_android=1; shift ;;
    --skip-unity) skip_unity=1; shift ;;
    --skip-tests) skip_tests=1; shift ;;
    --allow-debug-android-signing) allow_debug_android_signing=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ $(uname -s) == Linux ]] || { echo "Linux packages must be built on Linux." >&2; exit 2; }
[[ -n $output_dir && $version =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ &&
   $build_number =~ ^[1-9][0-9]*$ ]] || {
  usage >&2
  exit 2
}
(( ! skip_unity || skip_android )) || {
  echo "--skip-unity requires --skip-android." >&2
  exit 2
}

if (( ! skip_android && ! allow_debug_android_signing )); then
  signing_vars=(NEONSTAGE_ANDROID_KEYSTORE NEONSTAGE_ANDROID_KEYALIAS
    NEONSTAGE_ANDROID_KEYSTORE_PASS NEONSTAGE_ANDROID_KEYALIAS_PASS)
  for variable in "${signing_vars[@]}"; do
    [[ -n ${!variable:-} ]] || {
      echo "Android production signing is missing $variable." >&2
      echo "For a test-only package use --allow-debug-android-signing." >&2
      exit 2
    }
  done
  [[ -f $NEONSTAGE_ANDROID_KEYSTORE ]] || {
    echo "Android keystore not found: $NEONSTAGE_ANDROID_KEYSTORE" >&2
    exit 2
  }
fi

output_dir=$(mkdir -p "$output_dir" && cd "$output_dir" && pwd)
"$repo_root/scripts/release/verify-no-media.sh" "$repo_root"

if (( ! skip_tests )); then
  projects=(
    src/Karaoke.Server/Karaoke.Server.csproj
    src/Karaoke.App.Desktop/Karaoke.App.Desktop.csproj
    tests/Karaoke.Editor.Core.Tests/Karaoke.Editor.Core.Tests.csproj
    tests/Karaoke.Server.PlaybackTests/Karaoke.Server.PlaybackTests.csproj
  )
  for project in "${projects[@]}"; do
    dotnet build "$repo_root/$project" -c Release --maxcpucount:1
  done
  dotnet run --project "$repo_root/tests/Karaoke.Editor.Core.Tests/Karaoke.Editor.Core.Tests.csproj" \
    -c Release --no-build
  dotnet run --project "$repo_root/tests/Karaoke.Server.PlaybackTests/Karaoke.Server.PlaybackTests.csproj" \
    -c Release --no-build
fi

export NEONSTAGE_VERSION=$version
export NEONSTAGE_BUILD_NUMBER=$build_number
export NEONSTAGE_RELEASE_BUILD=1
suffix="v$version-build.$build_number"

"$repo_root/scripts/release/build-server-appimage.sh" \
  "$output_dir/NeonStage-Server-$suffix-linux-x86_64.AppImage"
"$repo_root/scripts/release/build-editor-appimage.sh" \
  "$output_dir/NeonStage-LyricsEditor-$suffix-linux-x86_64.AppImage"
if (( ! skip_unity )); then
  "$repo_root/scripts/release/build-stage-appimage.sh" \
    "$output_dir/NeonStage-Stage-$suffix-linux-x86_64.AppImage"
fi

if (( ! skip_android )); then
  if (( allow_debug_android_signing )) && [[ -z ${NEONSTAGE_ANDROID_KEYSTORE:-} ]]; then
    echo "WARNING: Android package uses Unity's test signing key." >&2
  fi
  "$repo_root/scripts/linux/build-unity-stage-android.sh"
  cp "$repo_root/src/Karaoke.Stage.Unity/Builds/Android/NeonStage-ShellS2-arm32.apk" \
    "$output_dir/NeonStage-Stage-$suffix-android-armv7.apk"
fi

"$repo_root/scripts/release/verify-no-media.sh" "$output_dir"
echo "Linux/Android release packages created in: $output_dir"
