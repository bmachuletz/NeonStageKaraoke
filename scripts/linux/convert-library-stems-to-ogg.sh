#!/usr/bin/env bash
set -Eeuo pipefail

library="${1:-/home/benjamin/Karaoke/Sunnify}"
command -v ffmpeg >/dev/null || { echo "ffmpeg wurde nicht gefunden." >&2; exit 1; }
[[ -d "$library" ]] || { echo "Bibliothek nicht gefunden: $library" >&2; exit 1; }

converted=0
skipped=0
failed=0
while IFS= read -r -d '' source; do
  target="${source%.flac}.ogg"
  if [[ -s "$target" && "$target" -nt "$source" ]]; then
    ((++skipped))
    continue
  fi
  echo "Ogg: $source"
  if ffmpeg -nostdin -hide_banner -loglevel error -y -i "$source" -map_metadata -1 -c:a libvorbis -q:a 6 "$target"; then
    ((++converted))
  else
    rm -f "$target"
    ((++failed))
  fi
done < <(find "$library" -type f \( -name '*.vocals.flac' -o -name '*.instrumental.flac' \) -print0)

printf 'Fertig: %d konvertiert, %d aktuell, %d fehlgeschlagen.\n' "$converted" "$skipped" "$failed"
(( failed == 0 ))
