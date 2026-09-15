#!/usr/bin/env bash
set -Eeuo pipefail

library="/home/benjamin/Karaoke/Sunnify"
aligner_url="http://127.0.0.1:8081"
language="auto"
force=0
separate=true
match=""
lyrics_source=""
output_dir=""
reindex=1
reuse_stems=0
current_job_id=""
current_job_finished=1

cancel_current_job() {
  if [[ -n "$current_job_id" && "$current_job_finished" == 0 ]]; then
    echo >&2
    echo "Breche Aligner-Job $current_job_id ab …" >&2
    curl -fsS -X DELETE "$aligner_url/api/jobs/$current_job_id" >/dev/null 2>&1 || true
    current_job_finished=1
  fi
}

on_cancel() {
  cancel_current_job
  exit 130
}

trap on_cancel INT TERM HUP

usage() {
  echo "Verwendung: $0 [--force] [--library PFAD] [--url URL] [--language SPRACHE] [--no-separate] [--reuse-stems] [--match TEXT] [--lyrics-source DATEI] [--output-dir PFAD] [--no-reindex]"
}

while (($#)); do
  case "$1" in
    --force) force=1; shift ;;
    --library) library=${2:?Pfad fehlt}; shift 2 ;;
    --url) aligner_url=${2:?URL fehlt}; shift 2 ;;
    --language) language=${2:?Sprache fehlt}; shift 2 ;;
    --no-separate) separate=false; shift ;;
    --reuse-stems) reuse_stems=1; shift ;;
    --match) match=${2:?Suchtext fehlt}; shift 2 ;;
    --lyrics-source) lyrics_source=${2:?Lyrics-Datei fehlt}; shift 2 ;;
    --output-dir) output_dir=${2:?Ausgabeordner fehlt}; shift 2 ;;
    --no-reindex) reindex=0; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unbekannte Option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

if ((reuse_stems != 0)) && [[ "$separate" != true ]]; then
  echo "--reuse-stems kann nicht mit --no-separate kombiniert werden." >&2
  exit 2
fi

for command in curl jq find install realpath; do
  command -v "$command" >/dev/null || { echo "Fehlendes Programm: $command" >&2; exit 1; }
done
[[ -d "$library" ]] || { echo "Bibliothek nicht gefunden: $library" >&2; exit 1; }
[[ -z "$lyrics_source" || -f "$lyrics_source" ]] || { echo "Lyrics-Quelle nicht gefunden: $lyrics_source" >&2; exit 1; }
if [[ -n "$lyrics_source" && -z "$match" ]]; then
  echo "--lyrics-source erfordert --match, damit die Quelle genau einem Song zugeordnet wird." >&2
  exit 2
fi
if [[ -n "$output_dir" ]]; then
  mkdir -p "$output_dir"
  output_dir=$(realpath "$output_dir")
fi
curl -fsS "$aligner_url/health" >/dev/null || {
  echo "Aligner ist unter $aligner_url nicht erreichbar." >&2
  echo "Start: (cd lyrics-word-aligner && docker compose up -d)" >&2
  exit 1
}

processed=0
skipped=0
failed=0

while IFS= read -r -d '' audio; do
  case "$audio" in
    *.vocals.flac|*.instrumental.flac|*.vocals.ogg|*.instrumental.ogg) continue ;;
  esac
  if [[ -n "$match" && "${audio,,}" != *"${match,,}"* ]]; then
    continue
  fi
  base=${audio%.*}
  lrc="$base.lrc"
  if [[ ! -f "$lrc" ]]; then
    ((skipped+=1))
    echo "Übersprungen (keine LRC): $audio"
    continue
  fi

  # Preserve the original line-synchronised lyrics before an enhanced LRC can
  # replace them.  Forced re-alignment must start from these stable line
  # anchors; feeding a previous word alignment back into the aligner can make
  # weakly recognised chorus entries drift further on every run.
  [[ -f "$base.pre-align.lrc" ]] || install -m 0644 "$lrc" "$base.pre-align.lrc"
  # The immutable matcher/import source is always authoritative.  Reusing an
  # enhanced output LRC as input compounds timing errors on every later run.
  input_lrc=${lyrics_source:-"$base.pre-align.lrc"}
  destination_base="$base"
  if [[ -n "$output_dir" ]]; then
    destination_base="$output_dir/$(basename "$base")"
  fi

  if ((force == 0)) && grep -Eq '<[0-9]{1,3}:[0-9]{2}([.:][0-9]{1,3})?,[0-9]{1,3}:[0-9]{2}([.:][0-9]{1,3})?>' "$lrc"; then
    if [[ "$separate" == false || ( -f "$base.vocals.flac" && -f "$base.instrumental.flac" ) ]]; then
      ((skipped+=1))
      echo "Bereits wortgenau: $audio"
      continue
    fi
  fi

  echo
  echo "Sende an EasyAligner: $audio"
  echo "Lyrics-Quelle: $input_lrc"
  request=(
    -fsS -X POST "$aligner_url/api/jobs"
    -F "audio=@$audio"
    -F "lyrics=@$input_lrc"
    -F "language=$language"
    -F "separate=$separate"
    -F "alignment_device=cuda"
  )
  if ((reuse_stems != 0)); then
    vocals_source=""
    instrumental_source=""
    for extension in flac ogg; do
      [[ -n "$vocals_source" || ! -f "$base.vocals.$extension" ]] || vocals_source="$base.vocals.$extension"
      [[ -n "$instrumental_source" || ! -f "$base.instrumental.$extension" ]] || instrumental_source="$base.instrumental.$extension"
    done
    if [[ -z "$vocals_source" || -z "$instrumental_source" ]]; then
      echo "Alignment abgebrochen: gespeicherte Vocal- und Instrumentalspur fehlen für $audio" >&2
      ((failed+=1))
      continue
    fi
    echo "Feste Audioreferenz: $vocals_source + $instrumental_source"
    request+=(
      -F "vocals=@$vocals_source"
      -F "instrumental=@$instrumental_source"
    )
  fi
  response=$(curl "${request[@]}") || {
      echo "Upload fehlgeschlagen: $audio" >&2
      ((failed+=1))
      continue
    }
  job_id=$(jq -er '.job_id' <<<"$response")
  current_job_id=$job_id
  current_job_finished=0

  while :; do
    status=$(curl -fsS "$aligner_url/api/jobs/$job_id") || { sleep 2; continue; }
    state=$(jq -r '.state' <<<"$status")
    percent=$(jq -r '.percent // 0' <<<"$status")
    message=$(jq -r '.message // ""' <<<"$status")
    printf '\r%3s%%  %-70s' "$percent" "$message"
    [[ "$state" == completed || "$state" == failed || "$state" == cancelled ]] && break
    sleep 2
  done
  echo

  if [[ "$state" == cancelled ]]; then
    current_job_finished=1
    echo "Alignment wurde abgebrochen: $audio" >&2
    exit 130
  fi

  if [[ "$state" == failed ]]; then
    current_job_finished=1
    echo "Alignment fehlgeschlagen: $(jq -r '.error // .message' <<<"$status")" >&2
    ((failed+=1))
    continue
  fi
  current_job_finished=1

  publishable=$(jq -r '.quality.publishable // false' <<<"$status")
  quality_score=$(jq -r '.quality.score // 0' <<<"$status")
  quality_grade=$(jq -r '.quality.grade // "unbekannt"' <<<"$status")
  if [[ "$publishable" != true ]]; then
    echo "Alignment vom Quality-Gate abgelehnt (Score $quality_score, Stufe $quality_grade): manuelles Review" >&2
  else
    echo "Quality-Gate akzeptiert: Score $quality_score, Stufe $quality_grade"
  fi

  download() {
    local remote_name=$1 destination=$2 temporary encoded_name
    if [[ -z "$remote_name" || "$remote_name" == "null" || "$remote_name" == */* || "$remote_name" == *\\* ]]; then
      echo "Ungültiger Ausgabename vom Aligner: $remote_name" >&2
      return 1
    fi
    # Der API-Endpunkt liefert Dateinamen, keine bereits URL-codierten URLs.
    # Deshalb muss genau dieses einzelne Pfadsegment codiert werden; insbesondere
    # Leerzeichen, Umlaute und typografische Bindestriche sind sonst für curl ungültig.
    encoded_name=$(jq -rn --arg name "$remote_name" '$name | @uri')
    temporary=$(mktemp)
    if curl -fsS "$aligner_url/jobs/$job_id/$encoded_name" -o "$temporary"; then
      install -m 0644 "$temporary" "$destination"
    else
      echo "Ausgabe fehlt: $remote_name" >&2
      rm -f "$temporary"
      return 1
    fi
    rm -f "$temporary"
  }

  output_lrc=$(jq -er '.output_lrc | strings | select(length > 0)' <<<"$status") || output_lrc=""
  output_report=$(jq -er '.output_report | strings | select(length > 0)' <<<"$status") || output_report=""
  download_failed=0
  download "$output_lrc" "$destination_base.lrc" || download_failed=1
  download "$output_report" "$destination_base.alignment.json" || download_failed=1

  if [[ "$separate" == true && "$reuse_stems" == 0 ]]; then
    vocals=$(jq -r '.stems.vocals // empty' <<<"$status")
    instrumental=$(jq -r '.stems.instrumental // empty' <<<"$status")
    manifest=$(jq -r '.stems_manifest // empty' <<<"$status")
    [[ -z "$vocals" ]] || download "$vocals" "$destination_base.vocals.flac" || download_failed=1
    [[ -z "$instrumental" ]] || download "$instrumental" "$destination_base.instrumental.flac" || download_failed=1
    [[ -z "$manifest" ]] || download "$manifest" "$destination_base.stems.json" || download_failed=1
  fi

  if ((download_failed != 0)); then
    echo "Mindestens eine Aligner-Ausgabe konnte nicht geladen werden: $audio" >&2
    ((failed+=1))
    continue
  fi

  for stem_kind in vocals instrumental; do
    stem_flac="$destination_base.$stem_kind.flac"
    stem_ogg="$destination_base.$stem_kind.ogg"
    if [[ -f "$stem_flac" && ( ! -f "$stem_ogg" || "$stem_flac" -nt "$stem_ogg" ) ]]; then
      ffmpeg -nostdin -hide_banner -loglevel error -y -i "$stem_flac" -map_metadata -1 \
        -c:a libvorbis -q:a 6 "$stem_ogg" || {
        rm -f "$stem_ogg"
        echo "Warnung: Ogg-Konvertierung fehlgeschlagen: $stem_flac" >&2
      }
    fi
  done

  encoding_checks=$(mktemp)
  echo '[]' >"$encoding_checks"
  for stem_kind in vocals instrumental; do
    stem_flac="$destination_base.$stem_kind.flac"
    stem_ogg="$destination_base.$stem_kind.ogg"
    [[ -f "$stem_flac" && -f "$stem_ogg" ]] || continue
    check=$(mktemp)
    if ! python3 "$(dirname "$0")/../../lyrics-word-aligner/scripts/verify_audio_encoding.py" \
        "$stem_flac" "$stem_ogg" --output "$check"; then
      echo "Stem-Encoding hat die Timeline verändert: $stem_ogg" >&2
      download_failed=1
    fi
    jq --slurpfile item "$check" '. + $item' "$encoding_checks" >"$encoding_checks.next"
    mv "$encoding_checks.next" "$encoding_checks"
    rm -f "$check"
  done
  if [[ -s "$destination_base.alignment.json" ]]; then
    report_temp=$(mktemp)
    jq --slurpfile checks "$encoding_checks" '.encoding_validation = $checks[0]' \
      "$destination_base.alignment.json" >"$report_temp" && install -m 0644 "$report_temp" "$destination_base.alignment.json"
    rm -f "$report_temp"
  fi
  rm -f "$encoding_checks"
  if ((download_failed != 0)); then
    echo "Audio-Timeline-Prüfung fehlgeschlagen: $audio" >&2
    ((failed+=1))
    continue
  fi

  if [[ -f "$destination_base.instrumental.flac" ]]; then
    python3 "$(dirname "$0")/../../lyrics-word-aligner/scripts/analyze_visuals.py" \
      "$destination_base.instrumental.flac" "$destination_base.visuals.json" ||
      echo "Warnung: Visualisierungsanalyse fehlgeschlagen: $audio" >&2
  fi
  ((processed+=1))
  if [[ "$publishable" == true ]]; then
    echo "Fertig: $audio"
  else
    echo "Technisch vollständig und für manuelles Review bereit: $audio"
  fi
done < <(find "$library" -type f \( -iname '*.mp3' -o -iname '*.m4a' -o -iname '*.aac' -o -iname '*.ogg' -o -iname '*.opus' -o -iname '*.wav' -o -iname '*.wma' -o -iname '*.flac' \) -print0)

echo
echo "Abgeschlossen: $processed verarbeitet, $skipped übersprungen, $failed fehlgeschlagen."
if ((processed > 0 && reindex != 0)); then
  server_url=${NEONSTAGE_SERVER_URL:-http://127.0.0.1:5274}
  if curl -fsS -X POST "$server_url/api/library/reindex" >/dev/null; then
    echo "Server-Bibliothek aktualisiert: $server_url"
  else
    echo "Hinweis: Server-Reindex nicht erreichbar; laufende Clients aktualisieren sich beim nächsten Server-Scan." >&2
  fi
fi
((failed == 0))
