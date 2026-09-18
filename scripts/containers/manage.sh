#!/usr/bin/env bash
set -Eeuo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"
aligner_dir="$repo_root/lyrics-word-aligner"
livekit_dir="$repo_root/deploy/livekit"

action=""
managed_tls=0
follow=0
tail_lines=120
dry_run=0
declare -a targets=()

usage() {
  cat <<'EOF'
NeonStage-Container erstellen, aktualisieren und verwalten

Verwendung:
  ./scripts/containers/manage.sh AKTION [ZIEL ...] [OPTIONEN]

Aktionen:
  create    Fehlende Images bauen und Container starten
  update    Basis-/Registry-Images aktualisieren, lokal neu bauen und starten
  rebuild   Lokale Images ohne erzwungenes Pull neu bauen und Container ersetzen
  restart   Vorhandene Container neu starten
  stop      Container stoppen (Daten und Container bleiben erhalten)
  down      Container und Compose-Netze entfernen (keine Volumes/Daten)
  status    Status der Stacks anzeigen
  logs      Letzte Logzeilen anzeigen

Ziele:
  all       server, aligner und livekit (Standard)
  server    NeonStage-Server
  aligner   CUDA EasyAligner
  livekit   LiveKit Single-Host-Stack

Optionen:
  --managed-tls   LiveKit zusammen mit dem eigenen Caddy-TLS-Edge verwalten
  -f, --follow    Logs fortlaufend anzeigen (nur für genau ein Ziel)
  --tail N        Anzahl der Logzeilen; Standard: 120
  --dry-run       Befehle nur anzeigen
  -h, --help      Diese Hilfe anzeigen

Beispiele:
  ./scripts/containers/manage.sh create all
  ./scripts/containers/manage.sh update server aligner
  ./scripts/containers/manage.sh update livekit
  ./scripts/containers/manage.sh logs server --follow
  ./scripts/containers/manage.sh status

Die Skripte verwenden die vorhandenen .env-Dateien der jeweiligen Stacks.
Bibliothek, SQLite-Daten, Modell-Cache und Docker-Volumes werden nie gelöscht.
EOF
}

die() {
  echo "ERROR: $*" >&2
  exit 2
}

print_command() {
  printf '  +'
  printf ' %q' "$@"
  printf '\n'
}

run_in() {
  local directory="$1"
  shift
  print_command "$@"
  ((dry_run)) && return 0
  (cd "$directory" && "$@")
}

run_livekit_compose() {
  local config_path="./runtime/livekit.yaml"
  local -a profile_args=()
  if ((managed_tls)); then
    config_path="./runtime/livekit-managed-tls.yaml"
    profile_args=(--profile managed-tls)
  fi
  print_command env "LIVEKIT_CONFIG_PATH=$config_path" docker compose "${profile_args[@]}" "$@"
  ((dry_run)) && return 0
  (cd "$livekit_dir" && LIVEKIT_CONFIG_PATH="$config_path" docker compose "${profile_args[@]}" "$@")
}

validate_environment() {
  command -v docker >/dev/null 2>&1 || die "docker ist nicht installiert."
  docker compose version >/dev/null 2>&1 || die "Docker Compose v2 ist nicht verfügbar."
  if [[ ! -f "$repo_root/.env" ]]; then
    die "$repo_root/.env fehlt. Zuerst .env.example kopieren und KARAOKE_LIBRARY_PATH setzen."
  fi
  if [[ " ${targets[*]} " == *" aligner " && ! -f "$aligner_dir/.env" ]]; then
    echo "HINWEIS: lyrics-word-aligner/.env fehlt; Compose verwendet seine dokumentierten Standardwerte."
  fi
}

normalize_targets() {
  ((${#targets[@]})) || targets=(all)
  local -a normalized=()
  local target existing
  for target in "${targets[@]}"; do
    [[ "$target" == all ]] && { normalized=(server aligner livekit); break; }
    case "$target" in
      server|aligner|livekit) ;;
      *) die "Unbekanntes Ziel: $target" ;;
    esac
    existing=0
    for item in "${normalized[@]}"; do [[ "$item" == "$target" ]] && existing=1; done
    ((existing)) || normalized+=("$target")
  done
  targets=("${normalized[@]}")
  if ((follow)) && ((${#targets[@]} != 1)); then
    die "--follow kann nur mit genau einem Ziel verwendet werden."
  fi
}

ensure_livekit_configuration() {
  local config="runtime/livekit.yaml"
  ((managed_tls)) && config="runtime/livekit-managed-tls.yaml"
  [[ -s "$livekit_dir/$config" ]] && return 0
  echo "LiveKit-Konfiguration fehlt; prepare.sh erzeugt sie und ergänzt die lokale .env."
  run_in "$livekit_dir" "$livekit_dir/prepare.sh"
}

create_target() {
  case "$1" in
    server)  run_in "$repo_root" docker compose up -d --build server ;;
    aligner) run_in "$aligner_dir" docker compose up -d --build lyrics-aligner ;;
    livekit)
      ensure_livekit_configuration
      if ((managed_tls)); then run_in "$livekit_dir" "$livekit_dir/start.sh" --managed-tls
      else run_in "$livekit_dir" "$livekit_dir/start.sh"
      fi
      ;;
  esac
}

update_target() {
  case "$1" in
    server)
      run_in "$repo_root" docker compose build --pull server
      run_in "$repo_root" docker compose up -d --force-recreate server
      ;;
    aligner)
      run_in "$aligner_dir" docker compose build --pull lyrics-aligner
      run_in "$aligner_dir" docker compose up -d --force-recreate lyrics-aligner
      ;;
    livekit)
      ensure_livekit_configuration
      run_livekit_compose pull
      if ((managed_tls)); then run_in "$livekit_dir" "$livekit_dir/start.sh" --managed-tls
      else run_in "$livekit_dir" "$livekit_dir/start.sh"
      fi
      ;;
  esac
}

rebuild_target() {
  case "$1" in
    server)
      run_in "$repo_root" docker compose build server
      run_in "$repo_root" docker compose up -d --force-recreate server
      ;;
    aligner)
      run_in "$aligner_dir" docker compose build lyrics-aligner
      run_in "$aligner_dir" docker compose up -d --force-recreate lyrics-aligner
      ;;
    livekit)
      ensure_livekit_configuration
      if ((managed_tls)); then run_in "$livekit_dir" "$livekit_dir/start.sh" --managed-tls
      else run_in "$livekit_dir" "$livekit_dir/start.sh"
      fi
      ;;
  esac
}

restart_target() {
  case "$1" in
    server)  run_in "$repo_root" docker compose restart server ;;
    aligner) run_in "$aligner_dir" docker compose restart lyrics-aligner ;;
    livekit) run_livekit_compose restart ;;
  esac
}

stop_target() {
  case "$1" in
    server)  run_in "$repo_root" docker compose stop server ;;
    aligner) run_in "$aligner_dir" docker compose stop lyrics-aligner ;;
    livekit) run_livekit_compose stop ;;
  esac
}

down_target() {
  case "$1" in
    server)  run_in "$repo_root" docker compose down ;;
    aligner) run_in "$aligner_dir" docker compose down ;;
    livekit) run_livekit_compose down ;;
  esac
}

status_target() {
  case "$1" in
    server)  run_in "$repo_root" docker compose ps ;;
    aligner) run_in "$aligner_dir" docker compose ps ;;
    livekit) run_livekit_compose ps ;;
  esac
}

logs_target() {
  local -a log_args=(logs --tail "$tail_lines")
  ((follow)) && log_args+=(--follow)
  case "$1" in
    server)  run_in "$repo_root" docker compose "${log_args[@]}" server ;;
    aligner) run_in "$aligner_dir" docker compose "${log_args[@]}" lyrics-aligner ;;
    livekit) run_livekit_compose "${log_args[@]}" ;;
  esac
}

while (($#)); do
  case "$1" in
    create|update|rebuild|restart|stop|down|status|logs)
      [[ -z "$action" ]] || die "Es kann nur eine Aktion angegeben werden."
      action="$1"
      shift
      ;;
    all|server|aligner|livekit) targets+=("$1"); shift ;;
    --managed-tls) managed_tls=1; shift ;;
    -f|--follow) follow=1; shift ;;
    --tail)
      [[ $# -ge 2 && "$2" =~ ^[0-9]+$ && "$2" -gt 0 ]] || die "--tail benötigt eine positive Zahl."
      tail_lines="$2"
      shift 2
      ;;
    --dry-run) dry_run=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "Unbekanntes Argument: $1" ;;
  esac
done

[[ -n "$action" ]] || { usage; exit 2; }
normalize_targets
validate_environment

echo "NeonStage-Container · Aktion: $action · Ziele: ${targets[*]}"
for target in "${targets[@]}"; do
  echo
  echo "== ${target^^} =="
  case "$action" in
    create)  create_target "$target" ;;
    update)  update_target "$target" ;;
    rebuild) rebuild_target "$target" ;;
    restart) restart_target "$target" ;;
    stop)    stop_target "$target" ;;
    down)    down_target "$target" ;;
    status)  status_target "$target" ;;
    logs)    logs_target "$target" ;;
  esac
done

echo
echo "Fertig. Persistente Daten und Volumes wurden nicht verändert."
