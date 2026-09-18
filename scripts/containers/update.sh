#!/usr/bin/env bash
set -Eeuo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if (($#)); then exec "$script_dir/manage.sh" update "$@"
else exec "$script_dir/manage.sh" update all
fi
