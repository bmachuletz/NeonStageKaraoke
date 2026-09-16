#!/usr/bin/env bash
set -euo pipefail

stack_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ ! -s "$stack_dir/runtime/livekit.yaml" ]] ||
   grep -q '^redis:' "$stack_dir/runtime/livekit.yaml"; then
  echo "LiveKit-Konfiguration wird für den Single-Host-Betrieb erzeugt."
  "$stack_dir/prepare.sh"
fi

cd "$stack_dir"
if [[ "${1:-}" == "--managed-tls" ]]; then
  if [[ ! -s runtime/caddy.yaml || ! -s runtime/livekit-managed-tls.yaml ]]; then
    "$stack_dir/prepare.sh"
  fi
  LIVEKIT_CONFIG_PATH=./runtime/livekit-managed-tls.yaml \
    docker compose --profile managed-tls up -d --force-recreate
else
  # A previous managed-TLS run may still own ports 80/443.
  docker compose --profile managed-tls stop edge >/dev/null 2>&1 || true
  LIVEKIT_CONFIG_PATH=./runtime/livekit.yaml docker compose up -d --force-recreate livekit
  echo "LiveKit läuft ohne eigenen TLS-Edge. Den vorhandenen Reverse-Proxy auf 127.0.0.1:7880 konfigurieren."
fi
docker compose ps
