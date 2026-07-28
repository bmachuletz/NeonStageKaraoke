#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
aligner_url="${LRC_ALIGNER_URL:-http://127.0.0.1:8081}"
audio=""
language="auto"

usage() {
  echo "Usage: $0 --audio FILE [--url URL] [--language auto|de|en|...]"
}

while (($#)); do
  case "$1" in
    --audio) audio=${2:?Audio file missing}; shift 2 ;;
    --url) aligner_url=${2:?URL missing}; shift 2 ;;
    --language) language=${2:?Language missing}; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

for command in curl jq install; do
  command -v "$command" >/dev/null || { echo "Missing command: $command" >&2; exit 1; }
done
[[ -n "$audio" && -f "$audio" ]] || { echo "Audio file not found: $audio" >&2; exit 1; }
curl -fsS "$aligner_url/health" >/dev/null || { echo "Aligner is unavailable: $aligner_url" >&2; exit 1; }

base=${audio%.*}
echo "[2%] Uploading audio for complete lyrics recognition"
response=$(curl -fsS -X POST "$aligner_url/api/transcription-jobs" \
  -F "audio=@$audio" -F "language=$language" -F "separate=true" \
  -F "alignment_device=cuda")
job_id=$(jq -er '.job_id' <<<"$response")

while :; do
  status=$(curl -fsS "$aligner_url/api/jobs/$job_id") || { sleep 2; continue; }
  state=$(jq -r '.state' <<<"$status")
  percent=$(jq -r '.percent // 0' <<<"$status")
  message=$(jq -r '.message // ""' <<<"$status")
  overall=$((2 + percent * 48 / 100))
  echo "[$overall%] $message"
  [[ "$state" == completed || "$state" == failed ]] && break
  sleep 2
done
if [[ "$state" == failed ]]; then
  echo "Complete lyrics recognition failed: $(jq -r '.error // .message' <<<"$status")" >&2
  exit 1
fi

download_output() {
  local name=$1 target=$2 encoded temporary
  [[ -n "$name" && "$name" != null && "$name" != */* && "$name" != *\\* ]] || {
    echo "Invalid output filename: $name" >&2; return 1;
  }
  encoded=$(jq -rn --arg name "$name" '$name | @uri')
  temporary=$(mktemp)
  trap 'rm -f -- "$temporary"' RETURN
  curl -fsS "$aligner_url/jobs/$job_id/$encoded" -o "$temporary"
  install -m 0644 "$temporary" "$target"
  rm -f -- "$temporary"
  trap - RETURN
}

output_lrc=$(jq -r '.output_lrc' <<<"$status")
output_report=$(jq -r '.output_report' <<<"$status")
download_output "$output_lrc" "$base.pre-align.lrc"
download_output "$output_lrc" "$base.lrc"
download_output "$output_report" "$base.transcription.json"

echo "[55%] Complete transcript stored; starting the regular alignment pipeline"
"$repo_root/scripts/linux/align-library.sh" --force --library "$(dirname "$audio")" \
  --match "$(basename "$audio")" --url "$aligner_url" --language "$language"
echo "[100%] Complete lyrics recognition and alignment finished"
