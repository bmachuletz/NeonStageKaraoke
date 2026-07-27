# Neon Stage – Unity Stage Client

Unity übernimmt ausschließlich die performante Bühnenansicht. Server, Warteliste,
Matcher, Aligner, Web-Frontend und Avalonia-Verwaltung bleiben bestehen.

## Erster Meilenstein

- verbindet sich standardmäßig mit `http://192.168.178.91:5274`
- beobachtet `/api/queue`
- lädt den aktiven Song beziehungsweise vorhandene Instrumental-/Vocal-Stems
- startet beide AudioSources auf derselben Unity-DSP-Zeit
- exportiert gezielt für ARMv7 der Ikarao Shell S2

## Öffnen und testen

1. Unity Hub und Unity 6 mit Android Build Support installieren. Verifiziert ist
   das Projekt aktuell mit Unity `6000.5.4f1`.
2. Diesen Ordner als Projekt öffnen: `src/Karaoke.Stage.Unity`
3. Eine beliebige leere Szene öffnen oder Play drücken. Der Runtime-Bootstrap
   erzeugt die Stage automatisch.
4. Für die Box: `Neon Stage > Build Shell S2 ARM32 APK`.

Die APK entsteht unter `src/Karaoke.Stage.Unity/Builds/Android/`.

## Linux-Bühne

```bash
./scripts/linux/build-unity-stage-linux.sh
./scripts/linux/start-unity-stage.sh
```

Der ausführbare Build liegt unter `src/Karaoke.Stage.Unity/Builds/Linux/NeonStage`.

## Wichtiger Audiotest

Der Prototyp versucht FLAC-Stems zunächst mit Unitys plattformeigenem Decoder.
Falls die Shell-S2-Firmware FLAC darüber nicht unterstützt, stellen wir die
Server-Stems im nächsten Schritt zusätzlich als Ogg/Vorbis bereit. Die normale
MP3-Masterspur nutzt explizit `AudioType.MPEG`.
