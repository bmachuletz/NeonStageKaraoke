# LrcMatcher (.NET 10)

Ein eigenständiges CLI-Tool, das Audio-Dateien in einem Playlist- oder Musikordner untersucht, Metadaten aus den Tags liest, passende Liedtexte über LRCLIB sucht und eine gleichnamige `.lrc`-Datei neben die Audiodatei legt.

## Voraussetzungen

- .NET 10 SDK
- Internetzugriff auf `https://lrclib.net`

## Bauen

```bash
dotnet restore
dotnet build -c Release
```

## Starten

Windows:

```powershell
dotnet run -c Release -- "D:\Musik\Meine Playlist"
```

Linux (Ordner):

```bash
dotnet run -c Release -- "/home/benjamin/Musik/Meine Playlist"
```

Eine zuvor geprüfte Variante für genau eine Audiodatei laden:

```bash
dotnet run -c Release -- "/musik/Kuhle Typen.mp3" --overwrite --lrclib-id 11678517
```

## Optionen

```text
--recursive                    Unterordner durchsuchen (Standard)
--no-recursive                 Nur den angegebenen Ordner durchsuchen
--overwrite                    Vorhandene .lrc-Dateien überschreiben
--dry-run                      Treffer nur anzeigen, nichts schreiben
--plain-fallback               Notfalls unsynchronisierten Text für das GPU-Vollspur-Alignment schreiben
--parallel <1-16>              Parallele LRCLIB-Abfragen (Standard: 3)
--max-duration-difference <s>  Max. Dauerabweichung (Standard: 5)
--report <Datei>               Eigener Pfad für den JSON-Bericht
--lrclib-id <ID>               Für eine einzelne Audiodatei exakt diese LRCLIB-Fassung laden
--aligner-url <URL>            CUDA-Vocalanalyse bei verschiedenen Startzeiten
```

## Beispiele

```bash
dotnet run -c Release -- "/musik/Playlist" --dry-run
```

```bash
dotnet run -c Release -- "/musik/Playlist" --overwrite --parallel 2
```

## Publish als einzelne Anwendung

Windows x64:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

Linux x64:

```bash
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true
```

Die Ausgabe liegt anschließend unter:

```text
bin/Release/net10.0/<runtime>/publish/
```

## Matching

Das Tool verwendet:

- Titel
- Künstler
- Album
- tatsächliche lokale Audiodauer
- Versionshinweise wie `Live`, `Remix`, `Acoustic` oder `Radio Edit`

Die Auswahl erfolgt hierarchisch: Zuerst müssen Titel, Künstler und Versionsart kompatibel sein. Danach wird eine identische Audiodauer bevorzugt. Gibt es keinen solchen Treffer, erweitert der Matcher die erlaubte Abweichung symmetrisch auf ±1, ±2, ±3 Sekunden usw. Album- und Textähnlichkeit entscheiden erst zwischen Kandidaten derselben Dauerdistanz.

Existieren bei gleicher Dauer mehrere synchronisierte Fassungen mit deutlich verschiedenen ersten Gesangseinsätzen, wird der Treffer als `Ambiguous` gemeldet und nicht automatisch geschrieben. Der JSON-Bericht enthält die LRCLIB-IDs und Startzeitpunkte der konkurrierenden Fassungen. Nach einer Hörprobe kann die richtige Fassung mit `--lrclib-id` gezielt und unverändert für eine einzelne Audiodatei geladen werden.

Ist der Lyrics Word Aligner erreichbar, analysiert der Matcher in diesem Sonderfall automatisch den ersten Gesangseinsatz der lokalen MP3. Die Analyse läuft mit Vocal-Separation und CUDA in einem breiten Introfenster, das nicht von den Kandidaten-Zeitstempeln abhängt. Automatisch gewählt wird nur, wenn die beste LRC höchstens 1,25 Sekunden vom erkannten Start abweicht und mindestens 1,5 Sekunden Vorsprung zur nächsten Zeitvariante hat. Andernfalls bleibt das Ergebnis `Ambiguous`.

Unsichere Treffer werden nicht automatisch geschrieben, sondern im JSON-Bericht als `Ambiguous` aufgeführt.

Hat ein sicherer LRCLIB-Treffer keine synchronisierte, aber eine unsynchronisierte
Textfassung, kann `--plain-fallback` diese als Eingabe für den Lyrics Word Aligner
speichern. Die Datei wird mit `LRCLIB plain lyrics; GPU alignment required`
gekennzeichnet. Der Aligner richtet dann isolierte Vocals und den vollständigen
Text gemeinsam auf der GPU aus; künstlich geschätzte Zeilenzeiten werden nicht
verwendet.

## Hinweis

Die LRCLIB-Schnittstelle ist ein externer Dienst. Rate Limits, Datenbestand und API-Verhalten können sich ändern. Die Parallelität ist deshalb standardmäßig auf drei Anfragen begrenzt.


## Änderungen in v0.2

- LRCLIB `duration` wird als Dezimalzahl verarbeitet.
- HTTP-Timeout auf 90 Sekunden erhöht.
- Bis zu drei Versuche bei Timeout, HTTP 429 und temporären 5xx-Fehlern.
