# Unity-Stage für macOS bauen

## Voraussetzungen

- Apple-Silicon-Mac (M1 oder neuer)
- Internetzugang und ein Benutzerkonto, das Homebrew-Pakete installieren darf
- Unity Hub mit Unity 6 (`6000.x`); die in `ProjectSettings/ProjectVersion.txt`
  festgelegte Version wird bevorzugt, eine andere installierte 6000.x-Version ist zulässig
- Modul **Mac Build Support (Mono)**
- aktivierte Unity-Lizenz
- Xcode Command Line Tools (`xcode-select --install`) für Signatur- und
  Architekturprüfung
- Internetzugang beim ersten Öffnen, damit Unity das LiveKit-Paket von OpenUPM auflöst

## Build

```bash
scripts/macos/build-unity-stage-macos.sh
```

Der Builder führt automatisch zuerst `scripts/macos/prepare-build.sh` aus. Das
Prepare installiert bei Bedarf Homebrew, .NET SDK 10, FFmpeg, `dylibbundler`
und VLC, stellt die .NET-Pakete wieder her und prüft anschließend eine
installierte Unity-6000.x-Version sowie **Mac Build Support (Mono)**. Unity
selbst bleibt wegen Lizenz und Hub-Modulen eine bewusste manuelle Installation.
Danach löst Unity seine Pakete auf und kompiliert die Stage-Skripte. Für die
isolierte Vorbereitung eines Build-Macs kann das Prepare-Skript direkt
gestartet werden.

Alternativ in Unity: **Neon Stage → Build macOS Stage (Apple Silicon)**. Das Ergebnis liegt unter `src/Karaoke.Stage.Unity/Builds/macOS/NeonStage Karaoke.app`.

Standard ist ein nativer ARM64-Build; Rosetta wird nicht benötigt. Für ein Universal-Binary:

```bash
NEONSTAGE_MACOS_UNIVERSAL=1 scripts/macos/build-unity-stage-macos.sh
```

Das LiveKit-Unity-Paket enthält native Bibliotheken für macOS ARM64 und x86_64. NeonStage verwendet bewusst den Unity-Audiopfad statt LiveKits Platform-Audio-Pfad.

Nach dem Build werden App-Bundle, Player- und LiveKit-Architektur,
Mikrofonbeschreibung und ad-hoc-Signatur automatisch geprüft. Ein scheinbar
erfolgreicher, aber unvollständiger Build wird dadurch nicht ausgeliefert.

## Release-Paket

Für das versionierte ZIP wird auf dem Mac ausgeführt:

```bash
scripts/release/build-release.sh --platform macos
```

Das Prepare läuft automatisch vor den Tests und Builds. Anschließend entstehen
drei ZIP-Dateien: ein selbstenthaltener ARM64-Server mit portablem FFmpeg, eine
native `Neon Stage Lyrics Editor.app` inklusive FFmpeg, LibVLC und Plugins sowie
die Unity-Stage. Mit `--skip-tests` lassen sich die Tests bewusst auslassen; mit
`--skip-unity` werden nur Server und Editor vorbereitet und gebaut.

## Start und Mikrofon

```bash
open "src/Karaoke.Stage.Unity/Builds/macOS/NeonStage Karaoke.app" --args \
  --server https://karaoke.example.com
```

Beim ersten Wechsel in die Singer-Rolle fragt macOS nach Mikrofonzugriff. Der Build enthält dafür `NSMicrophoneUsageDescription`. Falls der Zugriff versehentlich abgelehnt wurde: **Systemeinstellungen → Datenschutz & Sicherheit → Mikrofon → Neon Stage Karaoke**.

Ein lokal unsignierter Build kann von Gatekeeper blockiert werden. Für Verteilung außerhalb des eigenen Macs sind Developer-ID-Signatur, Hardened Runtime und Notarisierung erforderlich; sie gehören nicht zum lokalen MVP-Build.

Der Build-Schritt setzt den Mikrofontext in `Info.plist` und signiert das lokale `.app` danach ad hoc neu. Das ist für Tests auf dem Build-Mac gedacht, nicht für eine öffentliche Distribution.

## Weitere Desktop-Ziele

- Linux: `scripts/linux/build-unity-stage.sh`
- Windows: PowerShell `scripts/windows/build-unity-stage-windows.ps1` mit gesetztem `UNITY_EDITOR`
- macOS: `scripts/macos/build-unity-stage-macos.sh`

Alle Ziele verwenden denselben Unity-Stage-Code und dasselbe `IOnlineAudioTransport`-Interface. Plattformabhängig sind nur Unity/LiveKit-Native-Plugins und die Buildausgabe.

## Manueller Online-Test

1. Einen selbst gehosteten LiveKit-Server sowie die `Online__*`-Werte des Karaoke-Servers konfigurieren.
2. Auf Rechner A die Stage starten, `F8` öffnen, einem Room als Singer beitreten und einen Song starten.
3. Auf dem Mac denselben Room als Listener öffnen. Erwartet wird ausschließlich der fertige Mix von Rechner A; der Mac startet keinen lokalen Song.
4. Auf A **Broadcast stoppen**, anschließend auf dem Mac **Singer werden** und den Mikrofonzugriff erlauben.
5. A als Listener verbinden. A muss nun Musik und Mikrofon des Macs hören.

Für den Offline-Smoke-Test auf dem Mac: Stage starten, Song laden und Wiedergabe, Lyrics, Pause, Seek und Stop prüfen. Der Online-Modus ist bei `Online.Enabled=false` vollständig inaktiv.

FFmpeg und LibVLC werden vom normalen Unity-Stage-Player nicht gestartet. Die
macOS-Editor-App bringt beide Laufzeiten selbst mit; sie bleiben Werkzeuge des
Editors und Exports und sind deshalb keine native Laufzeitvoraussetzung der
Stage.
