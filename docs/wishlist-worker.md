# Wunschlisten-Worker

Der Worker importiert die lokale Neon-Stage-Wunschliste ohne Sunnify-Oberfläche.
Er verarbeitet die ältesten Wünsche zuerst und führt pro Titel diese Schritte aus:

1. Download und Metadaten/Cover über Sunnifys `MusicScraper`
2. sicherer LRCLIB-Treffer über `LrcMatcher` (synchronisiert bevorzugt,
   unsynchronisierter Text als GPU-Fallback)
3. GPU-Wort- und Silbenalignment inklusive Vocal-/Instrumental-Stems
4. Erzeugung der Ogg-Laufzeitspuren und Musik-Visualanalyse
5. Prüfung des GPU-Quality-Gates (`quality.publishable == true`) und aller Laufzeitdateien
6. Entfernen ausschließlich des vollständig erzeugten und akzeptierten Wunsches
7. Bibliotheks-Reindex, damit Server und Bühne den Titel sofort sehen

Ein Wunsch bleibt bei jedem Teilfehler oder abgelehnten Alignment in der Liste. Eine Prozesssperre verhindert
doppelte parallele Downloads durch manuelle, cron- oder systemd-Aufrufe.

## Einrichten

```bash
./scripts/linux/setup-sunnify-headless.sh
```

Das Setup installiert die festgelegte Sunnify-Version in `.tools/` und erzeugt
eine eigene Python-Umgebung. Sunnify darf ausschließlich für Inhalte verwendet
werden, deren Download erlaubt ist.

## Ausführen

```bash
# Nur anzeigen, was verarbeitet würde
./scripts/linux/process-wishlist.sh --dry-run

# Einen Wunsch vollständig verarbeiten
./scripts/linux/process-wishlist.sh --max 1

# Alle Wünsche verarbeiten
./scripts/linux/process-wishlist.sh
```

Der Neon-Stage-Server und der GPU-Aligner müssen laufen. Abweichende Werte können
über `NEONSTAGE_SERVER_URL`, `LRC_ALIGNER_URL`, `KARAOKE_LIBRARY_PATH`,
`SUNNIFY_SOURCE` und `SUNNIFY_PYTHON` oder über die dokumentierten Argumente
gesetzt werden.
