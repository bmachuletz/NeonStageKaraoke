# Neon Stage – Unity Stage Client

Unity übernimmt ausschließlich die performante Bühnenansicht. Server, Warteliste,
Matcher, Aligner, Web-Frontend und Avalonia-Verwaltung bleiben bestehen.

## Erster Meilenstein

- verbindet sich auf Android und Linux standardmäßig mit
  `http://cloud.hdvtec.de:5274`
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

Die Serveradresse wird in
`Assets/NeonStage/Runtime/NeonStageBootstrap.cs` über `DefaultServer`
festgelegt. Standalone-Builds können sie zusätzlich mit `--server URL`,
`NEONSTAGE_SERVER_URL` oder `KARAOKE_SERVER` überschreiben. Ein gültiger
PlayerPref `NeonStage.Server` hat ebenfalls Vorrang vor dem Default. Android-
APK-Dateien verwenden normalerweise den beim Build einkompilierten Default.

## Lyrics-/Audio-Synchronisation

Die Stage korrigiert die Lyrics-Zeit automatisch um die von Unity gemeldete
DSP-Pufferdauer. Dadurch bleiben die kanonischen Wortzeiten unverändert und die
Anzeige folgt auf Geräten mit größerem Audioausgabepuffer dem tatsächlich
hörbaren Signal. Der aktive Wert steht unten in der Stage als `Lyrics-Sync`.

Für eine gemessene gerätespezifische Kalibrierung kann die Automatik mit
`--stage-audio-latency-ms=85`, `NEONSTAGE_AUDIO_LATENCY_MS=85` oder dem
PlayerPref `NeonStage.AudioLatencyMs` überschrieben werden. Erlaubt sind
0–500 ms; `auto` aktiviert wieder die DSP-Pufferschätzung.

## Linux-Bühne

```bash
./scripts/linux/build-unity-stage-linux.sh
./scripts/linux/start-unity-stage.sh
```

Der ausführbare Build liegt unter `src/Karaoke.Stage.Unity/Builds/Linux/NeonStage`.

## Testlauf aus dem Lyrics-Editor

Der Editor findet den lokalen Development-Build automatisch. Für einen
installierten oder abweichenden Build kann der plattformneutrale Pfad gesetzt
werden:

```bash
export NEONSTAGE_STAGE_EXECUTABLE=/voller/pfad/zu/NeonStage
```

Unter Windows verweist die Variable entsprechend auf `NeonStage.exe`. Danach:

1. Song im Editor öffnen und `Song auf Stage testen` wählen.
2. Auf den Status `Stage bereit · Live-Test aktiv` warten.
3. Play, Pause, Stop und Seek über die vorhandenen Editor-Bedienelemente prüfen.
4. Eine Wortgrenze und einen Lyrics-Text ändern; beides muss ohne Speichern oder
   Stage-Neustart sichtbar werden.
5. Mit `Stage-Test beenden` schließen. Der Editor beendet ausschließlich den
   Prozess seiner eigenen, durch Sitzung und Token geschützten Loopback-Session.

Die Test-Stage startet mit 1280 × 720 in einem Fenster. Während der Sitzung ist
die Editor-Audioausgabe stumm und lädt keine Stem-Mischung; hörbar ist nur der
echte Unity-Stage-Mix. Der Editor hält lediglich einen stummen Originalstream als
Masterclock für Transport und Driftkorrektur.

Der Parameter `--editor-test` ist intern für diesen Startweg reserviert. In
diesem Modus greift die Stage nicht auf Queue oder Playback-Controller-Lease zu.
Die Lobby-/Intro-Musik bleibt für die komplette Lebensdauer des Testfensters
unterdrückt. Play, Pause, Seek, Stop und Snapshot-Wechsel können sie nicht erneut
aktivieren; hörbar ist ausschließlich das zum Test geladene Songmaterial.

Außerhalb des Testmodus verbindet das prozedurale, nahtlos geloopte Intro den
Amiga-/Tracker-Kern mit einer eigenständigen Punk-Schicht: doppelt geführte
Powerchord-Downstrokes, treibende Kick/Snare-Akzente, Crashs und kurze Tom-Fills.
Es werden weiterhin keine fremden Samples oder Melodien verwendet.

## Deterministischer MP4-Basisexport

`MP4 exportieren …` im Editor startet denselben Unity-Renderer mit einem
unveränderlichen Snapshot der aktuellen Lyrics. Die Stage berechnet die Songzeit
aus `Frame / FPS`, rendert unabhängig von der Fenstergröße in eine RenderTexture
und streamt die RGBA-Frames direkt an FFmpeg. Das Originalaudio wird als AAC mit
dem H.264/yuv420p-Video gemuxt. Der Editor lädt das Original dafür vor dem
Renderstart vollständig in seinen lokalen Audiocache; FFmpeg hängt damit während
des Exports weder von einem Container-Dateipfad noch von HTTP-/Proxy-Streaming ab.
Fortschritt und Abbruch laufen über die geschützte
Loopback-Session; bei Abbruch werden FFmpeg und die Teildatei entfernt.

Der Export verwendet 1920 × 1080 bei 60 FPS. Seine Audio-Reaktivität wird
deterministisch aus der vorhandenen 10-Hz-Songanalyse auf jeden Exportframe
interpoliert. Energie, Bass, Mitten, Höhen und Beat-Pulse hängen damit nicht von
einer in Echtzeit laufenden AudioSource ab. Vorhandene Songvideos dekodiert ein
separater FFmpeg-Prozess sequenziell im Exporttakt; auch der gespeicherte
Video-Offset wird dabei berücksichtigt.

Der Export läuft ohne sichtbares Unity-Fenster im Batch-Modus. Vier begrenzte
Framepuffer entkoppeln Rendering, GPU-Readback und FFmpeg voneinander;
Video-Decoding und Encoding arbeiten parallel. Wenn der Grafiktreiber
`AsyncGPUReadback` erst unter längerer Last verliert, reicht ein einzelner
Probelauf nicht zuverlässig aus. Der automatische Modus verwendet deshalb den
deterministischen synchronen Readback. Standardmäßig encodiert
`libx264` mit dem Preset `fast`. Unter **Einstellungen → Video-Export** steht
die Auswahl standardmäßig auf **Automatisch**: Der Editor lässt FFmpeg einen
echten Testframe encodieren und verwendet NVENC automatisch, sobald GPU,
Treiber und FFmpeg gemeinsam funktionieren. Dort kann NVENC auch fest erzwungen
oder mit **Software (libx264)** deaktiviert werden.

Ein Encoderfehler wird an den Editor gemeldet und die `.partial.mp4` wird
entfernt. Da Unity-Readback und Encoder auf einzelnen Treibern bei
gleichzeitiger GPU-Nutzung kollidieren, verwendet `auto` den stabilen
synchronen Readback. Der Modus kann für Diagnose oder abweichende
Treiber explizit über `NEONSTAGE_EXPORT_GPU_READBACK=async` beziehungsweise
`sync` gewählt werden.

## Wichtiger Audiotest

Der Prototyp versucht FLAC-Stems zunächst mit Unitys plattformeigenem Decoder.
Falls die Shell-S2-Firmware FLAC darüber nicht unterstützt, stellen wir die
Server-Stems im nächsten Schritt zusätzlich als Ogg/Vorbis bereit. Die normale
MP3-Masterspur nutzt explizit `AudioType.MPEG`.
