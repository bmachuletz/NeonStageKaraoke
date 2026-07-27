#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_root"

failed=0
while IFS= read -r path; do
  printf 'Nicht veröffentlichen: %s\n' "$path" >&2
  failed=1
done < <(find . -type f \( -iname '*.mp3' -o -iname '*.wav' -o -iname '*.flac' -o -iname '*.ogg' -o -iname '*.ckpt' -o -iname '*.pt' -o -iname '*.onnx' -o -iname '*.safetensors' -o -iname '*.db' -o -iname '*.sqlite' -o -iname '.env' \) \
  -not -path './.git/*' -not -path './.tools/*' -not -path './data/*' -not -path './*/data/*' -not -path './lyrics-word-aligner/data/*' \
  -not -path './lyrics-word-aligner/models/*' -not -path './src/Karaoke.Stage.Unity/Library/*' \
  -not -path './.env' -print)

if rg --files-with-matches --hidden -g '!.tools/**' -g '!src/variables' -g '!**/bin/**' -g '!**/obj/**' -g '!**/Library/**' -g '!**/data/**' \
  '^[[:space:]]*(SPOTIFY_CLIENT_SECRET|Spotify__ClientSecret|CLIENT_SECRET|API_KEY|PASSWORD)[[:space:]]*[:=][[:space:]]*[^$<{[:space:]][^[:space:]]+' .; then
  printf 'Möglicher fest codierter Zugangswert gefunden.\n' >&2
  failed=1
fi

if [[ ! -f LICENSE ]]; then
  printf 'Projektlizenz fehlt: vor einer öffentlichen Freigabe LICENSE auswählen.\n' >&2
  failed=1
fi

for required in NOTICE THIRD_PARTY_NOTICES.md ACKNOWLEDGEMENTS.md; do
  if [[ ! -s "$required" ]]; then
    printf 'Rechtlicher Release-Bestandteil fehlt: %s\n' "$required" >&2
    failed=1
  fi
done

if ! rg -q 'TagLib#.*LGPL-2\.1-only' THIRD_PARTY_NOTICES.md; then
  printf 'TagLib# muss als LGPL-2.1-only dokumentiert sein.\n' >&2
  failed=1
fi
if ! rg -q 'MMS_FA.*CC-BY-NC-4\.0|MMS.*CC-BY-NC-4\.0' THIRD_PARTY_NOTICES.md; then
  printf 'Die nicht-kommerzielle MMS-Modelllizenz fehlt.\n' >&2
  failed=1
fi
if ! rg -q 'storage\.ko-fi\.com' site/index.html || \
   ! rg -q 'Ko-fi' site/legal.html || ! rg -q 'IP address' site/legal.html; then
  printf 'Ko-fi-Widget oder zugehöriger Datenschutzhinweis fehlt.\n' >&2
  failed=1
fi
if ! rg -q 'LGPL-2\.1 LGPL-3 GPL-2 GPL-3' scripts/release/build-editor-appimage.sh; then
  printf 'Das AppImage-Buildscript übernimmt die VLC-Lizenztexte nicht vollständig.\n' >&2
  failed=1
fi
if [[ ! -s packaging/licenses/AppImage-Type2-Runtime-LICENSE.txt ]]; then
  printf 'Der eingebettete AppImage-Type-2-Runtime-Lizenzhinweis fehlt in einem AppImage.\n' >&2
  failed=1
fi
for appimage_builder in scripts/release/build-editor-appimage.sh \
  scripts/release/build-server-appimage.sh scripts/release/build-stage-appimage.sh; do
  if ! rg -q 'AppImage-Type2-Runtime-LICENSE\.txt' "$appimage_builder"; then
    printf 'AppImage-Runtime-Lizenz wird nicht gepackt: %s\n' "$appimage_builder" >&2
    failed=1
  fi
done

if (( failed )); then exit 1; fi
printf 'Release-Tree-Basisprüfung bestanden. Vollständige Artefakt-Lizenzprüfung bleibt erforderlich.\n'
