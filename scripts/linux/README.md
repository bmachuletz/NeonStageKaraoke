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

## MP3-Ordner importieren

Der Server muss bereits laufen. Der Pfad muss auf dem Serverrechner erreichbar
sein. Die Originaldateien werden nicht verändert:

```bash
./scripts/linux/process-mp3-folder.sh /pfad/zu/meinen/mp3s
```

Nur den gewählten Ordner ohne Unterordner durchsuchen:

```bash
./scripts/linux/process-mp3-folder.sh /pfad/zu/meinen/mp3s --no-recursive
```

Die gleiche Pipeline steht im Lyrics Editor unter
**Verwaltung → MP3-Ordner importieren …** bereit. Lyrics werden in dieser
Reihenfolge gesucht: benachbarte `.lrc`/`.txt`, eingebettete ID3-Lyrics,
anschließend LRCLIB. Danach folgen GPU-Stem-Separation, Wort-/Silbenalignment
und der Review-Status.

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
