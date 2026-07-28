#!/usr/bin/env bash
set -Eeuo pipefail

library="${KARAOKE_LIBRARY_PATH:-/home/benjamin/Karaoke/Sunnify}"
quarantine=""
execute=0

usage() {
  echo "Verwendung: $0 [--library PFAD] [--quarantine PFAD] [--execute]"
  echo "Ohne --execute wird ausschließlich eine Vorschau ausgegeben."
}

while (($#)); do
  case "$1" in
    --library) library=${2:?Pfad fehlt}; shift 2 ;;
    --quarantine) quarantine=${2:?Pfad fehlt}; shift 2 ;;
    --execute) execute=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unbekannte Option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

library=$(realpath "$library")
[[ -d "$library" && "$library" != "/" ]] || { echo "Ungültige Bibliothek: $library" >&2; exit 1; }
[[ -n "$quarantine" ]] || quarantine="$(dirname "$library")/$(basename "$library")-Quarantaene-$(date +%Y%m%d-%H%M%S)"
quarantine=$(realpath -m "$quarantine")
[[ "$quarantine" != "$library" && "$quarantine" != "$library/"* ]] || {
  echo "Quarantäne darf nicht innerhalb der Bibliothek liegen." >&2; exit 1;
}

declare -A keep=()
complete=0
incomplete=0
while IFS= read -r -d '' mp3; do
  base=${mp3%.mp3}
  required=("$base.lrc" "$base.alignment.json" "$base.instrumental.ogg" "$base.vocals.ogg" "$base.visuals.json")
  valid=1
  for file in "${required[@]}"; do [[ -s "$file" ]] || valid=0; done
  if ((valid)); then
    ((complete+=1))
    keep["$mp3"]=1
    for file in "${required[@]}"; do keep["$file"]=1; done
    # Immutable lyrics input is a reproducibility asset, not a disposable
    # pipeline intermediate. Keep it when the song was imported by a current
    # matcher/editor workflow.
    [[ ! -s "$base.pre-align.lrc" ]] || keep["$base.pre-align.lrc"]=1
    [[ ! -s "$base.transcription.json" ]] || keep["$base.transcription.json"]=1
  else
    ((incomplete+=1))
  fi
done < <(find "$library" -type f -name '*.mp3' -print0)

remove=0
bytes=0
while IFS= read -r -d '' file; do
  [[ ${keep["$file"]+yes} ]] && continue
  ((remove+=1))
  size=$(stat -c %s "$file")
  ((bytes+=size))
  relative=${file#"$library"/}
  printf '%s\n' "$relative"
  if ((execute)); then
    target="$quarantine/$relative"
    mkdir -p "$(dirname "$target")"
    mv -- "$file" "$target"
  fi
done < <(find "$library" -type f -print0)

if ((execute)); then
  find "$library" -depth -type d -empty -delete
  printf 'Quelle: %s\nVollständige Titel: %d\nUnvollständige Titel: %d\nVerschobene Dateien: %d\nBytes: %d\n' \
    "$library" "$complete" "$incomplete" "$remove" "$bytes" > "$quarantine/QUARANTAENE-INFO.txt"
fi

echo
echo "Vollständige Titel: $complete"
echo "Unvollständige Titel: $incomplete"
echo "Zu verschieben: $remove Dateien ($((bytes / 1024 / 1024)) MiB)"
if ((execute)); then echo "Quarantäne: $quarantine"; else echo "Vorschau – noch nichts verändert."; fi
