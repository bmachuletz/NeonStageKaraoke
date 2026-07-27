#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
server_url="${NEONSTAGE_SERVER_URL:-http://127.0.0.1:5274}"
aligner_url="${LRC_ALIGNER_URL:-http://127.0.0.1:8081}"
library="${KARAOKE_LIBRARY_PATH:-/home/benjamin/Karaoke/Sunnify}"
sunnify_dir="${SUNNIFY_SOURCE:-$repo_root/.tools/sunnify-spotify-downloader}"
python_bin="${SUNNIFY_PYTHON:-$repo_root/.tools/sunnify-venv/bin/python}"
qobuz_python="${QOBUZ_PYTHON:-python3}"
download_provider="${NEONSTAGE_DOWNLOAD_PROVIDER:-youtube}"
max_wishes=0
dry_run=0
event_token=""
wish_id=""
all_events=0

usage() {
  echo "Verwendung: $0 [--max ANZAHL] [--event-token TOKEN | --all-events] [--wish-id ID] [--dry-run] [--server URL] [--library PFAD] [--aligner-url URL]"
}

while (($#)); do
  case "$1" in
    --max) max_wishes=${2:?Anzahl fehlt}; shift 2 ;;
    --event-token) event_token=${2:?Event-Token fehlt}; shift 2 ;;
    --all-events) all_events=1; shift ;;
    --wish-id) wish_id=${2:?Wunsch-ID fehlt}; shift 2 ;;
    --dry-run) dry_run=1; shift ;;
    --server) server_url=${2:?URL fehlt}; shift 2 ;;
    --library) library=${2:?Pfad fehlt}; shift 2 ;;
    --aligner-url) aligner_url=${2:?URL fehlt}; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unbekannte Option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

for command in curl jq flock install dotnet; do
  command -v "$command" >/dev/null || { echo "Fehlendes Programm: $command" >&2; exit 1; }
done
[[ "$download_provider" == youtube || "$download_provider" == qobuz ]] || {
  echo "Unbekannter Download-Provider: $download_provider" >&2; exit 2;
}
if ((dry_run == 0)) && [[ "$download_provider" == youtube ]]; then
  [[ -x "$python_bin" && -f "$sunnify_dir/Spotify_Downloader.py" ]] || {
    echo "Sunnify Headless ist noch nicht eingerichtet." >&2
    echo "Zuerst ausführen: $repo_root/scripts/linux/setup-sunnify-headless.sh" >&2
    exit 1
  }
fi
if ((dry_run == 0)) && [[ "$download_provider" == qobuz ]]; then
  command -v "$qobuz_python" >/dev/null || { echo "Python für das Qobuz-Plugin fehlt: $qobuz_python" >&2; exit 1; }
  [[ -f "$repo_root/scripts/python/qobuz_headless.py" ]] || { echo "Qobuz-Plugin fehlt." >&2; exit 1; }
  for variable in QOBUZ_APP_ID QOBUZ_APP_SECRET QOBUZ_USER_AUTH_TOKEN; do
    [[ -n "${!variable:-}" ]] || { echo "Qobuz ist aktiviert, aber $variable fehlt." >&2; exit 1; }
  done
fi
[[ -d "$library" ]] || { echo "Bibliothek nicht gefunden: $library" >&2; exit 1; }
curl -fsS "$server_url/api/health" >/dev/null || { echo "Neon-Stage-Server nicht erreichbar: $server_url" >&2; exit 1; }
if ((dry_run == 0)); then
  curl -fsS "$aligner_url/health" >/dev/null || { echo "GPU-Aligner nicht erreichbar: $aligner_url" >&2; exit 1; }
fi

# Prevent cron/systemd/manual starts from processing the same wish twice.
exec 9>"/tmp/neonstage-wishlist-worker.lock"
flock -n 9 || { echo "Ein Wunschlisten-Worker läuft bereits."; exit 0; }

event_query=""
if ((all_events != 0)); then
  wishes='[]'
  while IFS= read -r event; do
    token=$(jq -r '.inviteToken' <<<"$event")
    event_wishes=$(curl -fsS "$server_url/api/wishlist?eventToken=$(printf '%s' "$token" | jq -sRr @uri)")
    event_wishes=$(jq --arg token "$token" '[.[] | . + {_eventToken:$token}]' <<<"$event_wishes")
    wishes=$(jq -c --argjson addition "$event_wishes" '. + $addition' <<<"$wishes")
  done < <(curl -fsS "$server_url/api/events" | jq -c '.[]')
else
  [[ -z "$event_token" ]] || event_query="?eventToken=$event_token"
  wishes=$(curl -fsS "$server_url/api/wishlist$event_query" | jq --arg token "$event_token" '[.[] | . + {_eventToken:$token}]')
fi
[[ -z "$wish_id" ]] || wishes=$(jq --arg id "$wish_id" '[.[] | select(.id == $id)]' <<<"$wishes")
count=$(jq 'length' <<<"$wishes")
((count > 0)) || { echo "Die Wunschliste ist leer."; exit 0; }
processed=0
failed=0

# API order is newest first; process the oldest request first.
while IFS= read -r wish; do
  ((max_wishes == 0 || processed + failed < max_wishes)) || break
  wish_id=$(jq -r '.id' <<<"$wish")
  wish_event_token=$(jq -r '._eventToken // empty' <<<"$wish")
  spotify_id=$(jq -r '.track.id' <<<"$wish")
  spotify_url=$(jq -r '.track.spotifyUrl // empty' <<<"$wish")
  catalog_source=$(jq -r '.track.sourceLabel // (if .track.source == 1 then "qobuz" else "spotify" end)' <<<"$wish" | tr '[:upper:]' '[:lower:]')
  qobuz_id=$(jq -r '.track.qobuzId // empty' <<<"$wish")
  title=$(jq -r '.track.title' <<<"$wish")
  artist=$(jq -r '.track.artist' <<<"$wish")
  if [[ "$catalog_source" == spotify ]]; then
    [[ -n "$spotify_url" ]] || spotify_url="https://open.spotify.com/track/$spotify_id"
  fi
  echo
  echo "Wunsch: $title · $artist"
  [[ -z "$spotify_url" ]] || echo "Spotify: $spotify_url"
  if ((dry_run != 0)); then
    ((processed+=1))
    continue
  fi

  staging=$(mktemp -d -p /tmp neonstage-wish.XXXXXXXX)
  result_file="$staging/sunnify-result.json"
  if [[ "$download_provider" == qobuz ]]; then
    echo "Download-Provider: Qobuz (autorisierter Kaufdownload)"
    download_command=("$qobuz_python" "$repo_root/scripts/python/qobuz_headless.py"
      --title "$title" --artist "$artist"
      --album "$(jq -r '.track.album // empty' <<<"$wish")"
      --duration-ms "$(jq -r '.track.durationMilliseconds // 0' <<<"$wish")"
      --output "$staging")
    [[ -z "$qobuz_id" ]] || download_command+=(--track-id "$qobuz_id")
  else
    if [[ "$catalog_source" == qobuz ]]; then
      echo "Dieser Wunsch stammt aus Qobuz, das Qobuz-Plugin ist aber nicht aktiv." >&2
      rm -rf -- "$staging"
      ((failed+=1))
      continue
    fi
    echo "Download-Provider: YouTube / Sunnify"
    download_command=("$python_bin" "$repo_root/scripts/python/sunnify_headless.py"
      --sunnify-source "$sunnify_dir" --format mp3 --quality 320
      "$spotify_url" "$staging")
  fi
  if ! "${download_command[@]}" >"$result_file"; then
    echo "Download fehlgeschlagen: $(jq -r '.errors // .messages // [] | join("; ")' "$result_file" 2>/dev/null || true)" >&2
    rm -rf -- "$staging"
    ((failed+=1))
    continue
  fi
  downloaded=$(jq -er '.track.file' "$result_file")
  [[ -f "$downloaded" ]] || { echo "Der Download-Provider meldete keine Audiodatei." >&2; rm -rf -- "$staging"; ((failed+=1)); continue; }

  # Keep artist folders readable while excluding path separators/control chars.
  safe_artist=$(jq -rn --arg value "$artist" '$value | gsub("[/\\\\]"; "_") | gsub("^[. ]+|[. ]+$"; "")')
  [[ -n "$safe_artist" ]] || safe_artist="Unbekannter Interpret"
  destination_dir="$library/$safe_artist"
  mkdir -p "$destination_dir"
  destination="$destination_dir/$(basename "$downloaded")"
  install -m 0644 "$downloaded" "$destination"
  rm -rf -- "$staging"

  echo "LRCLIB-Matching: $destination"
  if ! dotnet run --project "$repo_root/LrcMatcher/LrcMatcher.csproj" --no-build -- \
      "$destination" --plain-fallback --max-duration-difference 5 --aligner-url "$aligner_url"; then
    echo "LRC-Matching fehlgeschlagen; Wunsch bleibt erhalten." >&2
    ((failed+=1))
    continue
  fi
  lrc="${destination%.*}.lrc"
  if [[ ! -s "$lrc" ]]; then
    echo "Kein sicherer synchronisierter LRC-Treffer; Wunsch bleibt erhalten." >&2
    ((failed+=1))
    continue
  fi

  echo "GPU-Wort-/Silbenalignment: $destination"
  if ! "$repo_root/scripts/linux/align-library.sh" --force \
      --library "$destination_dir" --match "$(basename "$destination")" --url "$aligner_url"; then
    echo "Alignment fehlgeschlagen; Wunsch bleibt erhalten." >&2
    ((failed+=1))
    continue
  fi
  [[ -s "${destination%.*}.alignment.json" ]] || { echo "Alignment-Sidecar fehlt; Wunsch bleibt erhalten." >&2; ((failed+=1)); continue; }
  publishable=$(jq -r '.quality.publishable // false' "${destination%.*}.alignment.json")
  quality_score=$(jq -r '.quality.score // 0' "${destination%.*}.alignment.json")
  quality_grade=$(jq -r '.quality.grade // "unbekannt"' "${destination%.*}.alignment.json")
  if [[ "$publishable" != true ]]; then
    echo "Alignment vom Quality-Gate abgelehnt (Score $quality_score, Stufe $quality_grade); Song geht ins manuelle Review." >&2
  else
    echo "Quality-Gate akzeptiert: Score $quality_score, Stufe $quality_grade"
  fi
  for required in "${destination%.*}.pre-align.lrc" \
      "${destination%.*}.instrumental.ogg" "${destination%.*}.vocals.ogg" \
      "${destination%.*}.visuals.json"; do
    [[ -s "$required" ]] || { echo "Erforderliche Bibliotheksdatei fehlt: $required" >&2; ((failed+=1)); continue 2; }
  done

  # Keep the immutable matcher/import lyrics. They are required for a clean
  # future realignment and prevent generated word timings becoming new input.
  # Lossless stems are build intermediates; runtime uses the verified Ogg files.
  rm -f -- "${destination%.*}.instrumental.flac" \
    "${destination%.*}.vocals.flac" "${destination%.*}.stems.json"

  wish_event_query=""
  [[ -z "$wish_event_token" ]] || wish_event_query="?eventToken=$(printf '%s' "$wish_event_token" | jq -sRr @uri)"
  curl -fsS -X DELETE "$server_url/api/wishlist/$wish_id$wish_event_query" >/dev/null
  ((processed+=1))
  echo "Komplett importiert und aus der Wunschliste entfernt: $title"
done < <(jq -c 'reverse[]' <<<"$wishes")

if ((processed > 0 && dry_run == 0)); then
  curl -fsS -X POST "$server_url/api/library/reindex" >/dev/null ||
    echo "Hinweis: Bibliotheks-Reindex läuft bereits oder konnte nicht gestartet werden." >&2
fi
echo
echo "Abgeschlossen: $processed erfolgreich, $failed fehlgeschlagen."
((failed == 0))
