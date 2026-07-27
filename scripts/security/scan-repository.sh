#!/usr/bin/env bash
set -Eeuo pipefail

repository_root=$(git rev-parse --show-toplevel)
cd "$repository_root"

violations=()
while IFS= read -r tracked_file; do
  case "$tracked_file" in
    .env|.env.*|*/.env|*/.env.*)
      [[ "$tracked_file" == *.example ]] || violations+=("$tracked_file")
      ;;
    src/variables|*/src/variables|*.pem|*.key|*.pfx|*.p12|*.jks|*.keystore|*/secrets.json|secrets.json|*/spotify-connection.json|spotify-connection.json|*.qobuz-plugin.json|*appsettings.*.local.json)
      violations+=("$tracked_file")
      ;;
  esac
done < <(git ls-files)

if ((${#violations[@]})); then
  echo "ERROR: credential-bearing file paths are tracked:" >&2
  printf '  %s\n' "${violations[@]}" >&2
  exit 1
fi

bash scripts/release/verify-no-media.sh

if command -v gitleaks >/dev/null 2>&1; then
  gitleaks git --redact --no-banner "$repository_root"
elif command -v docker >/dev/null 2>&1; then
  docker run --rm \
    -v "$repository_root:/repo" \
    -w /repo \
    zricethezav/gitleaks@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f \
    git --redact --no-banner /repo
else
  echo "ERROR: install gitleaks or Docker before publishing." >&2
  exit 2
fi

echo "Repository credential scan passed."
