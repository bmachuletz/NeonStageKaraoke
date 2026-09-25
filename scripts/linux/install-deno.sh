#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project_root="$(cd -- "$script_dir/../.." && pwd)"
tools_dir="$project_root/.tools"
target="$tools_dir/deno"
release_base="https://github.com/denoland/deno/releases/latest/download"

case "$(uname -s):$(uname -m)" in
  Linux:x86_64|Linux:amd64) asset="deno-x86_64-unknown-linux-gnu.zip" ;;
  Linux:aarch64|Linux:arm64) asset="deno-aarch64-unknown-linux-gnu.zip" ;;
  Darwin:arm64) asset="deno-aarch64-apple-darwin.zip" ;;
  *) echo "Fehler: Nicht unterstützte Plattform: $(uname -s) $(uname -m)" >&2; exit 1 ;;
esac

for command_name in curl unzip awk; do
  command -v "$command_name" >/dev/null 2>&1 || { echo "Fehlendes Programm: $command_name" >&2; exit 1; }
done
if command -v sha256sum >/dev/null 2>&1; then SHA256=(sha256sum)
elif command -v shasum >/dev/null 2>&1; then SHA256=(shasum -a 256)
else echo "sha256sum oder shasum wurde nicht gefunden." >&2; exit 1
fi

mkdir -p "$tools_dir"
temporary="$(mktemp -d)"
trap 'rm -rf -- "$temporary"' EXIT
echo "Aktuelle offizielle Deno-Version wird geladen …"
curl --fail --location --retry 3 --silent --show-error "$release_base/$asset" -o "$temporary/$asset"
curl --fail --location --retry 3 --silent --show-error "$release_base/$asset.sha256sum" -o "$temporary/checksum"
expected="$(awk '{print $1; exit}' "$temporary/checksum")"
actual="$("${SHA256[@]}" "$temporary/$asset" | awk '{print $1}')"
[[ -n "$expected" && "$actual" == "$expected" ]] || { echo "Deno-Prüfsumme stimmt nicht überein." >&2; exit 1; }
unzip -q "$temporary/$asset" -d "$temporary/unpacked"
install -m 0755 "$temporary/unpacked/deno" "$target"
curl --fail --location --retry 3 --silent --show-error \
  "https://raw.githubusercontent.com/denoland/deno/main/LICENSE.md" -o "$tools_dir/deno-LICENSE.md"
echo "Installiert: $target"
"$target" --version | head -n 1
