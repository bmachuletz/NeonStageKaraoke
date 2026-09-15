# Linux-Testskripte

Alle Befehle können aus einem beliebigen Arbeitsverzeichnis ausgeführt werden.

## Build

```bash
./scripts/linux/build.sh
```

## Server

Musikordner als Argument:

```bash
./scripts/linux/start-server.sh /pfad/zur/musikbibliothek
```

Alternativ über Umgebungsvariablen:

```bash
KARAOKE_LIBRARY_PATH=/pfad/zur/musik \
KARAOKE_DATABASE_PATH=/pfad/zu/karaoke.db \
./scripts/linux/start-server.sh
```

Ohne Argument verwendet der Server die Werte aus `src/Karaoke.Server/appsettings.json`.

## Desktop-App

Lokaler Server:

```bash
./scripts/linux/start-desktop.sh
```

Server auf einem anderen Gerät:

```bash
./scripts/linux/start-desktop.sh http://192.168.1.50:5274
```

Server und Desktop-App laufen jeweils im Vordergrund. Zum Beenden `Strg+C` drücken.

## Linux-AppImages

Server, Lyrics-Editor und Unity-Stage gemeinsam bauen:

```bash
./scripts/build-appimages.sh
```

Das Skript erkennt `apt`, `dnf`, `pacman` oder `zypper` und installiert fehlende
freie Build-Abhängigkeiten standardmäßig automatisch. Ein fehlendes .NET-10-SDK
wird lokal und ohne Root-Rechte unter `.tools/dotnet` installiert;
`linuxdeploy` und `appimagetool` landen ebenfalls im ignorierten `.tools`-Cache.
FFmpeg wird in Server und Editor eingebettet, LibVLC samt Plugins zusätzlich in
den Editor. Unity selbst muss wegen Installation und Lizenz über Unity Hub
bereitgestellt werden.

Nur Server und Editor bauen:

```bash
./scripts/build-appimages.sh --skip-stage
```

Abhängigkeiten lediglich prüfen, ohne Systempakete oder Tools zu installieren:

```bash
./scripts/build-appimages.sh --skip-stage --no-auto-install
```

Die Vorbereitung kann auch getrennt ausgeführt werden:

```bash
./scripts/setup-appimage-build-deps.sh all
```

Die Ergebnisse liegen unter `artifacts/`. Der Server speichert Daten und
Bibliothek ausdrücklich nicht im AppImage. Ohne Konfiguration verwendet er
`~/.local/share/neon-stage/server` und `~/Music/NeonStage`:

```bash
./artifacts/NeonStage-Server-x86_64.AppImage
KARAOKE_SERVER=http://127.0.0.1:5274 ./artifacts/NeonStage-LyricsEditor-x86_64.AppImage
KARAOKE_SERVER=http://127.0.0.1:5274 ./artifacts/NeonStage-Stage-x86_64.AppImage
```

Eine fertige Bibliothek und vollständige Songpakete funktionieren ohne
Aligner. Downloader, Ordnerimport und neues Alignment benötigen weiterhin den
separaten Worker-/GPU-Stack.

## Audio-Ordner mit MP3 und FLAC importieren

Der Server muss bereits laufen. Der Pfad muss auf dem Serverrechner erreichbar
sein. Die Originaldateien werden nicht verändert:

```bash
./scripts/linux/process-audio-folder.sh /pfad/zu/meinen/audiodateien
```

Nur den gewählten Ordner ohne Unterordner durchsuchen:

```bash
./scripts/linux/process-audio-folder.sh /pfad/zu/meinen/audiodateien --no-recursive
```

Der bisherige Name `process-mp3-folder.sh` bleibt als kompatibler Alias erhalten.

Die gleiche Pipeline steht im Lyrics Editor unter
**Verwaltung → Audio-Ordner (MP3/FLAC) importieren …** bereit. MP3-Metadaten
werden aus ID3-Tags und FLAC-Metadaten aus Vorbis Comments gelesen. Das
ursprüngliche Audioformat bleibt als Master erhalten. Lyrics werden in dieser
Reihenfolge gesucht: benachbarte `.lrc`/`.txt`, eingebettete Tag-Lyrics,
anschließend LRCLIB. Ein gefundener Text läuft durch EasyAligner. Gibt es
überhaupt keinen verwertbaren Text, erzeugt der GPU-Worker automatisch ein
Volltranskript und führt dieses danach ebenfalls durch EasyAligner. Anschließend folgen Review-Status und
Bibliotheksaktualisierung.

## Stage-Timing untersuchen

Der aktuelle Unity-Development-Build sendet während der Wiedergabe einmal pro
Sekunde DSP-, Sample- und Lyrics-Zeitwerte an den Server. Zusammenfassung:

```bash
./scripts/linux/analyze-stage-timing.sh
```

Für einen Server auf einem anderen Host:

```bash
./scripts/linux/analyze-stage-timing.sh --server http://192.168.178.91:5274
```
