#!/usr/bin/env bash
set -Eeuo pipefail

root=${1:-.}
[[ -d "$root" ]] || { echo "Guard target does not exist: $root" >&2; exit 2; }

forbidden='\.(mp3|wav|flac|ogg|m4a|aac|wma|lrc|elrc|alignment\.json|stems\.json|lyrics\.json|png|jpg|jpeg|webp|bmp|gif|db|sqlite|sqlite3|ckpt|pt|onnx|safetensors)$'
allow='(^|/)(site/assets/|src/Karaoke\.App/Assets/|src/Karaoke\.App\.Android/Resources/drawable/app_icon\.png|src/Karaoke\.Stage\.Unity/Assets/(TextMesh Pro/|NeonStage/Branding/|Resources/NeonStageIcon\.png))'

if [[ -d .git && "$root" == "." ]]; then
  mapfile -d '' files < <(git ls-files -z)
else
  mapfile -d '' files < <(find "$root" \
    \( -type d \( -name .git -o -name bin -o -name obj -o -name Library -o -name Temp -o -name Logs \
       -o -name Builds -o -name artifacts -o -name .tools -o -name models -o -name data -o -name venv -o -name .venv \) -prune \) \
    -o -type f -print0)
fi

violations=()
for file in "${files[@]}"; do
  normalized=${file#./}
  [[ "$normalized" =~ $forbidden ]] || continue
  [[ "$normalized" =~ $allow ]] && continue
  violations+=("$normalized")
done

if ((${#violations[@]})); then
  echo "ERROR: media, lyrics, model data, or local runtime data found in release scope:" >&2
  printf '  %s\n' "${violations[@]}" >&2
  exit 1
fi
echo "Media guard passed: no songs, lyrics, stems, local databases, covers, or model weights found in $root"
