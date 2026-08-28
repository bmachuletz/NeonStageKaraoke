#!/usr/bin/env bash
set -Eeuo pipefail

project_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
stage="$project_root/src/Karaoke.Stage.Unity/Builds/Linux/NeonStage"
export NEONSTAGE_SERVER_URL="${NEONSTAGE_SERVER_URL:-http://cloud.hdvtec.de:5274}"

if [[ ! -x "$stage" ]]; then
  echo "Unity-Linux-Build fehlt. Zuerst ausführen:" >&2
  echo "  $project_root/scripts/linux/build-unity-stage-linux.sh" >&2
  exit 1
fi

if [[ ${1:-} == http://* || ${1:-} == https://* ]]; then
  export NEONSTAGE_SERVER_URL=$1
  shift
fi

exec "$stage" "$@"
