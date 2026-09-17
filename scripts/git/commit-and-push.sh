#!/usr/bin/env bash
set -Eeuo pipefail

repo_root=$(git rev-parse --show-toplevel 2>/dev/null) || {
  echo "Kein Git-Repository gefunden." >&2
  exit 2
}
cd "$repo_root"

message=""
push_mode=ask
dry_run=0

usage() {
  cat <<'EOF'
Verwendung: ./scripts/git/commit-and-push.sh [Optionen]

Prüft die sichtbaren Änderungen, schützt vor typischen privaten Dateien,
erstellt einen Commit und pusht ihn nach separater Bestätigung.

Optionen:
  -m, --message TEXT  Commit-Nachricht ohne Rückfrage setzen
      --push          Nach dem Commit ohne weitere Rückfrage pushen
      --no-push       Nur lokal committen
      --dry-run       Änderungen und Prüfungen zeigen, nichts verändern
  -h, --help          Diese Hilfe anzeigen

Beispiele:
  ./scripts/git/commit-and-push.sh
  ./scripts/git/commit-and-push.sh -m "fix: repair container alignment" --push
  ./scripts/git/commit-and-push.sh --dry-run
EOF
}

while (($#)); do
  case "$1" in
    -m|--message)
      [[ $# -ge 2 ]] || { echo "$1 benötigt eine Commit-Nachricht." >&2; exit 2; }
      message=$2
      shift 2
      ;;
    --push) push_mode=yes; shift ;;
    --no-push) push_mode=no; shift ;;
    --dry-run) dry_run=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unbekannte Option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

git_dir=$(git rev-parse --git-dir)
if [[ -f "$git_dir/MERGE_HEAD" || -d "$git_dir/rebase-merge" || -d "$git_dir/rebase-apply" ]]; then
  echo "Merge oder Rebase ist noch nicht abgeschlossen. Bitte zuerst auflösen." >&2
  exit 1
fi
if [[ -n $(git diff --name-only --diff-filter=U) ]]; then
  echo "Nicht aufgelöste Git-Konflikte gefunden:" >&2
  git diff --name-only --diff-filter=U >&2
  exit 1
fi

branch=$(git symbolic-ref --quiet --short HEAD) || {
  echo "Detached HEAD: Commit-and-Push wird aus Sicherheitsgründen abgebrochen." >&2
  exit 1
}

mapfile -d '' changed_paths < <(
  {
    git diff --name-only -z HEAD
    git ls-files --others --exclude-standard -z
  } | sort -zu
)
if ((${#changed_paths[@]} == 0)); then
  echo "Keine Änderungen zum Committen vorhanden."
  exit 0
fi

echo "Branch: $branch"
echo "Vorgesehene Änderungen:"
git status --short

failed=0
for path in "${changed_paths[@]}"; do
  case "$path" in
    .env.example) ;;
    .env|.env.*|*/.env|*/.env.*|src/variables|*/secrets.json|*/spotify-connection.json|\
    *.pem|*.key|*.pfx|*.p12|*.jks|*.keystore|\
    .release-private-assets/*|*/Branding/Private/*|*/BackgroundShaders/Private/*|\
    *stage-themes.private.json|*Backgrounds.private.json|*Backgrounds.private.json.meta)
      echo "BLOCKIERT (privat oder geheim): $path" >&2
      failed=1
      ;;
    *.mp3|*.wav|*.flac|*.ogg|*.m4a|*.aac|*.opus|*.wma|*.mp4|*.mkv|*.mov|*.avi|\
    *.lrc|*.elrc|*.ckpt|*.pt|*.onnx|*.safetensors|*.sqlite|*.db)
      echo "BLOCKIERT (Medien-, Modell- oder Laufzeitdatei): $path" >&2
      failed=1
      ;;
  esac
  if [[ -f "$path" ]]; then
    size=$(stat -c '%s' -- "$path")
    if ((size > 20 * 1024 * 1024)); then
      echo "BLOCKIERT (größer als 20 MiB): $path" >&2
      failed=1
    fi
    if rg -I -n -m 1 \
      '^[[:space:]]*(SPOTIFY_CLIENT_SECRET|Spotify__ClientSecret|QOBUZ_APP_SECRET|Qobuz__AppSecret|QOBUZ_USER_AUTH_TOKEN|Qobuz__UserAuthToken|LIVEKIT_API_SECRET|Online__ApiSecret|CLIENT_SECRET|API_KEY|PASSWORD)[[:space:]]*[:=][[:space:]]*[^$<{[:space:]][^[:space:]]+' \
      -- "$path" >/dev/null 2>&1; then
      echo "BLOCKIERT (möglicher fest codierter Zugangswert): $path" >&2
      failed=1
    fi
  fi
done
((failed == 0)) || {
  echo "Commit abgebrochen. Private Inhalte entfernen oder sicher konfigurieren." >&2
  exit 1
}

echo
echo "Diff-Übersicht:"
git diff --stat HEAD
untracked_count=$(git ls-files --others --exclude-standard | wc -l)
if ((untracked_count > 0)); then
  echo "Neue Dateien: $untracked_count"
fi

if ((dry_run)); then
  echo "Dry-run bestanden; es wurde nichts gestaged, committed oder gepusht."
  exit 0
fi

if [[ ! -t 0 ]]; then
  [[ -n "$message" ]] || {
    echo "Ohne interaktives Terminal ist --message erforderlich." >&2
    exit 2
  }
  [[ "$push_mode" != ask ]] || {
    echo "Ohne interaktives Terminal ist --push oder --no-push erforderlich." >&2
    exit 2
  }
else
  read -r -p "Alle oben aufgeführten Änderungen stagen? [j/N] " answer
  [[ "$answer" =~ ^[jJyY]$ ]] || { echo "Abgebrochen."; exit 0; }
  if [[ -z "$message" ]]; then
    read -r -p "Commit-Nachricht: " message
  fi
fi

[[ -n ${message//[[:space:]]/} ]] || {
  echo "Die Commit-Nachricht darf nicht leer sein." >&2
  exit 2
}

git add -A
if git diff --cached --quiet; then
  echo "Nach dem Staging sind keine Änderungen vorhanden."
  exit 0
fi

echo
echo "Commit-Inhalt:"
git diff --cached --stat
git commit -m "$message"

if [[ "$push_mode" == ask ]]; then
  read -r -p "Commit jetzt nach GitHub pushen? [j/N] " answer
  [[ "$answer" =~ ^[jJyY]$ ]] && push_mode=yes || push_mode=no
fi
if [[ "$push_mode" == no ]]; then
  echo "Commit wurde lokal erstellt; Push wurde übersprungen."
  exit 0
fi

if upstream=$(git rev-parse --abbrev-ref --symbolic-full-name '@{upstream}' 2>/dev/null); then
  echo "Push: $branch -> $upstream"
  git push
else
  git remote get-url origin >/dev/null 2>&1 || {
    echo "Commit wurde erstellt, aber Remote 'origin' fehlt; kein Push möglich." >&2
    exit 1
  }
  echo "Push mit neuem Upstream: $branch -> origin/$branch"
  git push --set-upstream origin "$branch"
fi

echo "Commit und Push erfolgreich abgeschlossen."
