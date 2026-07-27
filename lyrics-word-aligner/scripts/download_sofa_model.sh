#!/usr/bin/env bash
set -euo pipefail

model_root="${SOFA_MODEL_ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/models/sofa}"
archive="$model_root/pretrained_multilingual_singing.zip"
source_url="https://github.com/qiuqiao/SOFA/releases/download/v1.0.1/pretrained_multilingual_singing.zip"
english_root="$model_root/english"
english_checkpoint="$english_root/tgm_en_v100.ckpt"
english_dictionary="$english_root/tgm_sofa_dict.txt"
english_checkpoint_url="https://github.com/spicytigermeat/SOFA-Models/releases/download/v1.0.0_en/tgm_en_v100.ckpt"
english_dictionary_url="https://raw.githubusercontent.com/spicytigermeat/SOFA-Models/main/tgm_sofa_dict.txt"

mkdir -p "$model_root"
if [[ ! -f "$archive" ]]; then
  curl --fail --location --retry 3 --continue-at - --output "$archive" "$source_url"
fi

if [[ ! -f "$model_root/.extracted-v1.0.1" ]]; then
  unzip -o "$archive" -d "$model_root"
  touch "$model_root/.extracted-v1.0.1"
fi

mkdir -p "$english_root"
if [[ ! -f "$english_checkpoint" ]]; then
  curl --fail --location --retry 3 --continue-at - --output "$english_checkpoint" "$english_checkpoint_url"
fi
if [[ ! -f "$english_dictionary" ]]; then
  curl --fail --location --retry 3 --output "$english_dictionary" "$english_dictionary_url"
fi

printf '%s  %s\n' \
  '6aba5f0ba3461155bf0c20190df85fad580de42409784737573309f6962449ff' "$english_checkpoint" \
  '1b2a52ac559169196cb7cdfd3edcfe3947d37ec6a28a6ec281abac838331439c' "$english_dictionary" |
  sha256sum --check --status || {
    echo "SOFA-English-Modell oder Wörterbuch hat eine unerwartete Prüfsumme." >&2
    exit 1
  }

echo "SOFA-Modelle bereit: $model_root"
