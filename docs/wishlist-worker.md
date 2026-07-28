# Wunschlisten-Worker

Der Worker importiert die lokale Neon-Stage-Wunschliste ohne Sunnify-Oberfläche.
Er verarbeitet die ältesten Wünsche zuerst und führt pro Titel diese Schritte aus:

1. Download über den gewählten Provider: YouTube/Sunnify oder ein von Qobuz
   autorisierter Kaufdownload
2. sicherer LRCLIB-Treffer über `LrcMatcher` (synchronisiert bevorzugt,
   unsynchronisierter Text als GPU-Fallback)
3. GPU-Wort- und Silbenalignment inklusive Vocal-/Instrumental-Stems
4. Erzeugung der Ogg-Laufzeitspuren und Musik-Visualanalyse
5. Prüfung des GPU-Quality-Gates (`quality.publishable == true`) und aller Laufzeitdateien
6. Entfernen ausschließlich des vollständig erzeugten und akzeptierten Wunsches
7. Bibliotheks-Reindex, damit Server und Bühne den Titel sofort sehen

Ein Wunsch bleibt bei jedem Teilfehler oder abgelehnten Alignment in der Liste. Wenn der Download bereits
eine gültige Audiodatei geliefert hat, speichert der Server diesen Fund intern am Wunsch; der lokale Dateipfad
wird nicht an Clients übertragen. Im Editor kann der Admin den fehlgeschlagenen Wunsch anschließend löschen
oder den Fund ausdrücklich als unveröffentlichtes Projekt **Ohne Lyrics / Without Lyrics** übernehmen. Erst
nach Lyrics-Import, erneutem Alignment, beiden Stems und Review wird daraus ein für die Stage freigebbarer Song.
Eine Prozesssperre verhindert doppelte parallele Downloads durch manuelle, cron- oder systemd-Aufrufe.

## Einrichten

```bash
./scripts/linux/setup-sunnify-headless.sh
```

Das Setup installiert die festgelegte Sunnify-Version in `.tools/` und erzeugt
eine eigene Python-Umgebung. Sunnify darf ausschließlich für Inhalte verwendet
werden, deren Download erlaubt ist.

## Optionales Qobuz-Plugin

Unter **Verwaltung → Download-Provider konfigurieren …** kann Qobuz aktiviert
werden. Die gemeinsame Suche zeigt Spotify- und Qobuz-Treffer mit Quellen-Badge;
bei Qobuz werden – sofern die API sie liefert – Preis und maximale Audioqualität
angezeigt. Ein ausgewählter Qobuz-Treffer behält seine Qobuz-ID bis zum Download,
sodass der Worker nicht erneut raten muss. Der Preis dient nur als
Kataloginformation; Neon Stage löst niemals selbstständig einen Kauf aus.

Solange App-ID, App-Secret und ein autorisierter User-Auth-Token fehlen, bleibt
das Plugin deaktiviert und YouTube/Sunnify wird verwendet. Bei aktiver
Qobuz-Konfiguration wird nicht still auf YouTube zurückgefallen: Ein fehlender
oder nicht zum Kaufdownload berechtigter Qobuz-Treffer bleibt als Wunsch offen.

Standard ist CD-FLAC (`format_id=6`). Alternativ stehen MP3 320 sowie die beiden
Hi-Res-FLAC-Stufen zur Auswahl. Matcher, Aligner, Bibliotheksindex und Stage
verarbeiten FLAC direkt; eine verlustbehaftete Zwischenkonvertierung ist nicht
nötig.

Neon Stage fordert ausschließlich `intent=download` an. Es fragt weder das
Qobuz-Kontopasswort ab noch extrahiert es Zugangsdaten aus Qobuz-Anwendungen.
Die API-Partnerdaten müssen direkt von Qobuz bezogen werden; für
Integrationsanfragen nennt Qobuz `api@qobuz.com`. Der Server akzeptiert das
Speichern von Secrets über den Editor nur vom selben Rechner oder über HTTPS;
entferntes Klartext-HTTP wird abgewiesen. Auf dem Server liegen die Daten
außerhalb des Repositorys mit Benutzer-Dateirechten.

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
`SUNNIFY_SOURCE`, `SUNNIFY_PYTHON` und `NEONSTAGE_DOWNLOAD_PROVIDER` oder über die dokumentierten Argumente
gesetzt werden.
