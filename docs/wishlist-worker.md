# Wunschlisten-Worker

Der Worker ist Teil des Neon-Stage-Servers und arbeitet auf Linux, macOS und
Windows ohne Sunnify- oder Python-Laufzeit. Er verarbeitet die ältesten Wünsche
zuerst und führt pro Titel diese Schritte aus:

1. Download über den gewählten Provider: YouTube via `yt-dlp` oder ein von
   Qobuz autorisierter Kaufdownload
2. aufnahmegenauer UltraStar-TXT-Treffer über USDB (optional, ohne
   Browserautomation)
3. sicherer LRCLIB-Treffer über den `LrcMatcher`, falls USDB ausfällt oder
   unsicher ist; synchronisierter Text wird bevorzugt, unsynchronisierter Text
   dient als GPU-Fallback
4. GPU-Wort- und Silbenalignment inklusive Vocal-/Instrumental-Stems
5. Erzeugung der Ogg-Laufzeitspuren und Musik-Visualanalyse
6. Prüfung des GPU-Quality-Gates (`quality.publishable == true`) und aller
   Laufzeitdateien
7. Entfernen ausschließlich des vollständig erzeugten und akzeptierten Wunsches
8. Bibliotheks-Reindex, damit Server und Bühne den Titel sofort sehen

Ein Wunsch bleibt bei jedem Teilfehler oder abgelehnten Alignment in der Liste.
Wenn der Download bereits eine gültige Audiodatei geliefert hat, speichert der
Server diesen Fund intern am Wunsch; der lokale Dateipfad wird nicht an Clients
übertragen. Im Editor kann der Admin den fehlgeschlagenen Wunsch anschließend
löschen oder den Fund ausdrücklich als unveröffentlichtes Projekt **Ohne Lyrics
/ Without Lyrics** übernehmen. Erst nach Lyrics-Import, erneutem Alignment,
beiden Stems und Review wird daraus ein für die Stage freigebbarer Song. Eine
serverseitige Sperre verhindert doppelte parallele Verarbeitung.

## Mitgelieferte Werkzeuge

Die offiziellen Server-Pakete für Linux, macOS und Windows enthalten neben dem
self-contained .NET-Server auch FFmpeg, `yt-dlp`, Deno und den self-contained
`LrcMatcher`. Die portable Windows-EXE bettet diese Dateien in ihre Nutzlast ein
und entpackt sie beim Start; sie sind keine verwalteten Assemblies innerhalb der
Server-EXE. Der Docker-Server enthält dieselben für den Wunschpfad benötigten
Werkzeuge. CUDA-Modelle und GPU-Inferenz bleiben absichtlich im separaten
Aligner-Container.

Beim Start aus einem Quellcheckout findet der Server Werkzeuge über explizite
`NEONSTAGE_*_PATH`-Variablen, neben der Server-Anwendung, in `.tools/` oder im
`PATH`. Die Prepare-/Release-Skripte laden `yt-dlp` und Deno von den offiziellen
Release-Artefakten, prüfen deren veröffentlichte SHA-256-Summen und übernehmen
die Lizenztexte. Der jeweilige Nutzer ist für Downloadberechtigung,
Dienstbedingungen und Medienrechte verantwortlich.

## Spotify- und YouTube-Fallback

Spotify ist nur eine Metadaten- und Suchquelle. Fehlen Client-ID oder Secret,
schlägt Spotify fehl oder liefert die Suche keinen Treffer, fragt der Server
automatisch YouTube ab. Ein ausgewählter YouTube-Treffer behält seine validierte
Video-ID; ein Spotify-Treffer wird beim Download über eine aus Interpret und
Titel gebildete YouTube-Suche aufgelöst. Portable Builds benötigen deshalb für
den Standardweg keine Spotify-Konfiguration.

## Optionales Qobuz-Plugin

Unter **Verwaltung → Download-Provider konfigurieren …** kann Qobuz aktiviert
werden. Die gemeinsame Suche zeigt Spotify- und Qobuz-Treffer mit Quellen-Badge;
bei Qobuz werden – sofern die API sie liefert – Preis und maximale Audioqualität
angezeigt. Ein ausgewählter Qobuz-Treffer behält seine Qobuz-ID bis zum Download,
sodass der Worker nicht erneut raten muss. Der Preis dient nur als
Kataloginformation; Neon Stage löst niemals selbstständig einen Kauf aus.

Solange App-ID, App-Secret und ein autorisierter User-Auth-Token fehlen, bleibt
das Plugin deaktiviert und YouTube wird verwendet. Bei aktiver
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

Die Verarbeitung wird im Lyrics Editor gestartet und läuft im Serverprozess.
Der historische Linux-Helfer `scripts/linux/process-wishlist.sh` bleibt für
bestehende Installationen und Diagnose erhalten, ist aber nicht der portable
Produktpfad.

Der Neon-Stage-Server und der GPU-Aligner müssen laufen. Abweichende Werte können
über `LRC_ALIGNER_URL`, `KARAOKE_LIBRARY_PATH`, `NEONSTAGE_YT_DLP_PATH`,
`NEONSTAGE_DENO_PATH`, `NEONSTAGE_FFMPEG_PATH` und `LRC_MATCHER_EXE` gesetzt
werden.
