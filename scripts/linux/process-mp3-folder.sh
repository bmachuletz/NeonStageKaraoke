#!/usr/bin/env bash
set -Eeuo pipefail

server_url="${NEONSTAGE_SERVER_URL:-http://127.0.0.1:5274}"
recursive=true
source_path=""

usage() {
  echo "Verwendung: process-audio-folder.sh ORDNER [--no-recursive] [--server URL]"
}

while (($#)); do
  case "$1" in
    --no-recursive) recursive=false; shift ;;
    --server) server_url=${2:?URL fehlt}; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    -*) echo "Unbekannte Option: $1" >&2; usage >&2; exit 2 ;;
    *) [[ -z "$source_path" ]] || { echo "Es darf nur ein Importordner angegeben werden." >&2; exit 2; }
       source_path=$1; shift ;;
  esac
done

[[ -n "$source_path" ]] || { usage >&2; exit 2; }
source_path=$(realpath "$source_path")
[[ -d "$source_path" ]] || { echo "Ordner nicht gefunden: $source_path" >&2; exit 1; }
for command in curl jq; do
  command -v "$command" >/dev/null || { echo "Fehlendes Programm: $command" >&2; exit 1; }
done

payload=$(jq -cn --arg path "$source_path" --argjson recursive "$recursive" \
  '{sourcePath:$path,recursive:$recursive}')
response=$(curl -fsS -X POST "$server_url/api/admin/folder-import" \
  -H 'Content-Type: application/json' --data "$payload") || {
  echo "Ordnerimport konnte nicht gestartet werden. Läuft bereits ein Auftrag?" >&2
  exit 1
}
job_id=$(jq -er '.jobId' <<<"$response")
echo "Audio-Ordnerimport (MP3/FLAC) gestartet: $job_id"

last_message=""
while :; do
  status=$(curl -fsS "$server_url/api/admin/folder-import")
  message=$(jq -r '.message' <<<"$status")
  percent=$(jq -r '.percent' <<<"$status")
  if [[ "$message" != "$last_message" ]]; then
    printf '%3s%%  %s\n' "$percent" "$message"
    last_message=$message
  fi
  [[ $(jq -r '.isRunning' <<<"$status") == true ]] || break
  sleep 2
done

jq -r '"Fertig: \(.succeeded), Review: \(.review), Fehler: \(.failed), übersprungen: \(.skipped)"' <<<"$status"
(( $(jq -r '.failed' <<<"$status") == 0 ))
