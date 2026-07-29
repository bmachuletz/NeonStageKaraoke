#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
aligner_url="${LRC_ALIGNER_URL:-http://127.0.0.1:8081}"
audio=""
language="auto"
canonical_source=""
force_no_canonical=0
output_dir=""
reindex=1

usage() {
  echo "Usage: $0 --audio FILE [--url URL] [--language auto|de|en|...] [--canonical FILE | --no-canonical] [--output-dir DIR] [--no-reindex]"
}

while (($#)); do
  case "$1" in
    --audio) audio=${2:?Audio file missing}; shift 2 ;;
    --url) aligner_url=${2:?URL missing}; shift 2 ;;
    --language) language=${2:?Language missing}; shift 2 ;;
    --canonical) canonical_source=${2:?Canonical lyrics file missing}; shift 2 ;;
    --no-canonical) force_no_canonical=1; shift ;;
    --output-dir) output_dir=${2:?Output directory missing}; shift 2 ;;
    --no-reindex) reindex=0; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

for command in curl jq install realpath; do
  command -v "$command" >/dev/null || { echo "Missing command: $command" >&2; exit 1; }
done
[[ -n "$audio" && -f "$audio" ]] || { echo "Audio file not found: $audio" >&2; exit 1; }
((force_no_canonical == 0)) || [[ -z "$canonical_source" ]] || {
  echo "--canonical and --no-canonical cannot be used together" >&2; exit 2;
}
[[ -z "$canonical_source" || -f "$canonical_source" ]] || { echo "Canonical lyrics file not found: $canonical_source" >&2; exit 1; }
curl -fsS "$aligner_url/health" >/dev/null || { echo "Aligner is unavailable: $aligner_url" >&2; exit 1; }

base=${audio%.*}
work_dir=$(mktemp -d)
trap 'rm -rf -- "$work_dir"' EXIT
canonical_lrc=""
if [[ -n "$canonical_source" ]]; then
  canonical_lrc="$work_dir/canonical.lrc"
  install -m 0600 "$canonical_source" "$canonical_lrc"
elif ((force_no_canonical == 0)) && [[ -s "$base.lrc" ]]; then
  canonical_lrc="$work_dir/canonical.lrc"
  install -m 0600 "$base.lrc" "$canonical_lrc"
elif ((force_no_canonical == 0)) && [[ -s "$base.pre-align.lrc" ]]; then
  canonical_lrc="$work_dir/canonical.lrc"
  install -m 0600 "$base.pre-align.lrc" "$canonical_lrc"
fi
destination_base="$base"
if [[ -n "$output_dir" ]]; then
  mkdir -p "$output_dir"
  output_dir=$(realpath "$output_dir")
  destination_base="$output_dir/$(basename "$base")"
fi

echo "[2%] Uploading audio for complete lyrics recognition"
upload=(-F "audio=@$audio" -F "language=$language" -F "separate=true" \
  -F "alignment_device=cuda")
[[ -z "$canonical_lrc" ]] || upload+=(-F "lyrics=@$canonical_lrc;filename=$(basename "$base").lrc")
response=$(curl -fsS -X POST "$aligner_url/api/transcription-jobs" "${upload[@]}")
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
result_lrc="$work_dir/result.lrc"
download_output "$output_lrc" "$result_lrc"
download_output "$output_report" "$destination_base.transcription.json"
if [[ -z "$output_dir" ]]; then
  install -m 0644 "$result_lrc" "$base.pre-align.lrc"
  install -m 0644 "$result_lrc" "$base.lrc"
fi
if [[ $(jq -r '.canonical_transfer.applied // false' <<<"$status") == true ]]; then
  coverage=$(jq -r '.canonical_transfer.mapping_coverage' <<<"$status")
  echo "[54%] Canonical spelling and line structure retained (coverage: $coverage)"
fi

echo "[55%] Complete transcript stored; starting the regular alignment pipeline"
alignment_args=(--force --library "$(dirname "$audio")" --match "$(basename "$audio")" \
  --url "$aligner_url" --language "$language" --lyrics-source "$result_lrc")
[[ -z "$output_dir" ]] || alignment_args+=(--output-dir "$output_dir")
((reindex != 0)) || alignment_args+=(--no-reindex)
"$repo_root/scripts/linux/align-library.sh" "${alignment_args[@]}"
echo "[100%] Complete lyrics recognition and alignment finished"
