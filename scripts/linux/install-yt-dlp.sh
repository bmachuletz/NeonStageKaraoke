#!/usr/bin/env bash
set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(cd -- "${SCRIPT_DIR}/../.." && pwd)"
TOOLS_DIR="${PROJECT_ROOT}/.tools"
TARGET="${TOOLS_DIR}/yt-dlp"
RELEASE_BASE="https://github.com/yt-dlp/yt-dlp/releases/latest/download"

case "$(uname -s):$(uname -m)" in
  Linux:x86_64|Linux:amd64) ASSET="yt-dlp_linux" ;;
  Linux:aarch64|Linux:arm64) ASSET="yt-dlp_linux_aarch64" ;;
  Darwin:arm64) ASSET="yt-dlp_macos" ;;
  *) echo "Fehler: Nicht unterstützte Plattform: $(uname -s) $(uname -m)" >&2; exit 1 ;;
esac

for command_name in curl awk; do
  if ! command -v "${command_name}" >/dev/null 2>&1; then
    echo "Fehler: ${command_name} wurde nicht gefunden." >&2
    exit 1
  fi
done
if command -v sha256sum >/dev/null 2>&1; then SHA256=(sha256sum)
elif command -v shasum >/dev/null 2>&1; then SHA256=(shasum -a 256)
else echo "Fehler: sha256sum oder shasum wurde nicht gefunden." >&2; exit 1
fi

mkdir -p "${TOOLS_DIR}"
TEMP_DIR="$(mktemp -d)"
trap 'rm -rf -- "${TEMP_DIR}"' EXIT

echo "Aktuelle offizielle yt-dlp-Version wird geladen …"
curl --fail --location --retry 3 --silent --show-error \
  "${RELEASE_BASE}/${ASSET}" --output "${TEMP_DIR}/${ASSET}"
curl --fail --location --retry 3 --silent --show-error \
  "${RELEASE_BASE}/SHA2-256SUMS" --output "${TEMP_DIR}/SHA2-256SUMS"

EXPECTED_SUM="$(awk -v asset="${ASSET}" '$2 == asset { print $1; exit }' "${TEMP_DIR}/SHA2-256SUMS")"
if [[ -z "${EXPECTED_SUM}" ]]; then
  echo "Fehler: Für ${ASSET} wurde keine offizielle Prüfsumme gefunden." >&2
  exit 1
fi

ACTUAL_SUM="$("${SHA256[@]}" "${TEMP_DIR}/${ASSET}" | awk '{ print $1 }')"
if [[ "${ACTUAL_SUM}" != "${EXPECTED_SUM}" ]]; then
  echo "Fehler: Die SHA-256-Prüfsumme von yt-dlp stimmt nicht überein." >&2
  exit 1
fi

chmod 0755 "${TEMP_DIR}/${ASSET}"
mv -- "${TEMP_DIR}/${ASSET}" "${TARGET}"
curl --fail --location --retry 3 --silent --show-error \
  "https://raw.githubusercontent.com/yt-dlp/yt-dlp/master/LICENSE" --output "${TOOLS_DIR}/yt-dlp-LICENSE"

echo "Installiert: ${TARGET}"
"${TARGET}" --version
