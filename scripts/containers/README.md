# NeonStage-Container verwalten

Das zentrale Skript verwaltet die drei zum Repository gehörenden Stacks:

- `server`: NeonStage API, Web-App und Bibliotheksverwaltung
- `aligner`: CUDA EasyAligner und Volltranskript
- `livekit`: Single-Host-LiveKit, optional mit eigenem Caddy-TLS-Edge

Andere Docker-Projekte auf demselben Host, insbesondere ein vorhandener
Reverse-Proxy, werden nicht verändert.

## Erster Start

Vorher die lokale `.env` anhand von `.env.example` konfigurieren. Danach:

```bash
./scripts/containers/create.sh
```

Nur ausgewählte Stacks erstellen:

```bash
./scripts/containers/create.sh server aligner
```

## Aktualisieren

Alle lokalen Images mit aktuellen Basis-Images neu bauen, LiveKit aus der
Registry aktualisieren und die Container ersetzen:

```bash
./scripts/containers/update.sh
```

Gezielt aktualisieren:

```bash
./scripts/containers/update.sh server
./scripts/containers/update.sh aligner
./scripts/containers/update.sh livekit
```

## Betrieb und Diagnose

```bash
./scripts/containers/manage.sh status
./scripts/containers/manage.sh restart server
./scripts/containers/manage.sh logs server --follow
./scripts/containers/manage.sh stop all
```

Mit `--dry-run` werden die vorgesehenen Befehle nur angezeigt:

```bash
./scripts/containers/manage.sh update all --dry-run
```

Der LiveKit-Stack verwendet standardmäßig den vorhandenen Reverse-Proxy. Nur
wenn LiveKit selbst die öffentlichen Ports 80/443 besitzen soll, wird
`--managed-tls` ergänzt.

Die Aktionen `stop` und `down` verwenden niemals `--volumes`. Bibliothek,
SQLite-Datenbank, Aligner-Daten und Modell-Cache bleiben daher erhalten.
