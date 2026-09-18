#!/usr/bin/env bash
set -Eeuo pipefail
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if (($#)); then exec "$script_dir/manage.sh" create "$@"
else exec "$script_dir/manage.sh" create all
fi
