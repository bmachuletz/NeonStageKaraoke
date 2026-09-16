#!/usr/bin/env bash
set -euo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$stack_dir/../.." && pwd)"
runtime_dir="$stack_dir/runtime"
root_env="$repo_root/.env"
livekit_domain="${1:-${LIVEKIT_DOMAIN:-livekit.cloud.hdvtec.de}}"
turn_domain="${2:-${LIVEKIT_TURN_DOMAIN:-turn.cloud.hdvtec.de}}"

validate_domain() {
  [[ "$1" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] && [[ "$1" == *.* ]]
}

if ! validate_domain "$livekit_domain" || ! validate_domain "$turn_domain"; then
  echo "Ungültige Domain. Aufruf: $0 livekit.example.de turn.example.de" >&2
  exit 2
fi
if [[ "$livekit_domain" == "$turn_domain" ]]; then
  echo "LiveKit- und TURN-Domain müssen verschieden sein." >&2
  exit 2
fi
command -v openssl >/dev/null || { echo "openssl fehlt." >&2; exit 2; }

read_env() {
  [[ -f "$root_env" ]] || return 0
  sed -n "s/^$1=//p" "$root_env" | tail -n 1
}

api_key="$(read_env LIVEKIT_API_KEY)"
api_secret="$(read_env LIVEKIT_API_SECRET)"
[[ -n "$api_key" ]] || api_key="API$(openssl rand -hex 8)"
[[ -n "$api_secret" ]] || api_secret="$(openssl rand -hex 32)"
if [[ ! "$api_key" =~ ^[A-Za-z0-9_-]{8,128}$ ]] ||
   [[ ! "$api_secret" =~ ^[A-Za-z0-9_-]{32,256}$ ]]; then
  echo "Vorhandene LIVEKIT_API_KEY/LIVEKIT_API_SECRET enthalten nicht unterstützte Zeichen oder sind zu kurz." >&2
  exit 2
fi

umask 077
mkdir -p "$runtime_dir/caddy-data"
sed \
  -e "s/__LIVEKIT_API_KEY__/$api_key/g" \
  -e "s/__LIVEKIT_API_SECRET__/$api_secret/g" \
  -e "s/__LIVEKIT_TURN_DOMAIN__/$turn_domain/g" \
  "$stack_dir/templates/livekit.yaml.template" > "$runtime_dir/livekit.yaml"
sed \
  -e "s/__LIVEKIT_API_KEY__/$api_key/g" \
  -e "s/__LIVEKIT_API_SECRET__/$api_secret/g" \
  -e "s/__LIVEKIT_TURN_DOMAIN__/$turn_domain/g" \
  "$stack_dir/templates/livekit-managed-tls.yaml.template" > "$runtime_dir/livekit-managed-tls.yaml"
sed \
  -e "s/__LIVEKIT_DOMAIN__/$livekit_domain/g" \
  -e "s/__LIVEKIT_TURN_DOMAIN__/$turn_domain/g" \
  "$stack_dir/templates/caddy.yaml.template" > "$runtime_dir/caddy.yaml"
chmod 600 "$runtime_dir/livekit.yaml" "$runtime_dir/livekit-managed-tls.yaml" "$runtime_dir/caddy.yaml"

if [[ ! -f "$root_env" ]]; then
  cp "$repo_root/.env.example" "$root_env"
  chmod 600 "$root_env"
fi

upsert_env() {
  local key="$1" value="$2" temporary
  temporary="$(mktemp "$root_env.XXXXXX")"
  awk -v key="$key" -v value="$value" '
    BEGIN { found = 0 }
    index($0, key "=") == 1 { print key "=" value; found = 1; next }
    { print }
    END { if (!found) print key "=" value }
  ' "$root_env" > "$temporary"
  chmod 600 "$temporary"
  mv "$temporary" "$root_env"
}

upsert_env ONLINE_ENABLED true
upsert_env LIVEKIT_URL "wss://$livekit_domain"
upsert_env LIVEKIT_API_KEY "$api_key"
upsert_env LIVEKIT_API_SECRET "$api_secret"
upsert_env LIVEKIT_ROOM_PREFIX neonstage-

echo "LiveKit-Konfiguration wurde erzeugt."
echo "  Signaling: wss://$livekit_domain"
echo "  TURN:      $turn_domain"
echo "  Secrets:   $root_env und deploy/livekit/runtime/livekit.yaml (nicht versioniert)"
echo "Nächster Schritt: DNS/Firewall prüfen und deploy/livekit/start.sh ausführen."
