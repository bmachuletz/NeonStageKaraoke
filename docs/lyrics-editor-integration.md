# Lyrics-Timing-Editor: Bestandsaufnahme und Integrationsplan

Stand: 21. Juli 2026

## Wiederverwendete Komponenten

- `Karaoke.App.Desktop` wird zur Linux-/Windows-Editoranwendung. Die mobile
  Avalonia-Anwendung und die Unity Stage bleiben davon getrennt.
- `Karaoke.Contracts` bleibt die gemeinsame Transportgrenze für Server, Editor
  und bestehende Clients.
- Der Server liefert bereits Songliste, Songdetails, Originalaudio,
  Vocal-/Instrumental-Stems, Cover, Lyrics und Visualisierungsdaten.
- `LibVlcAudioPlaybackService` ist unter Linux erprobt und wird zunächst für
  Wiedergabe, Seek und Lautstärke wiederverwendet. Editor-spezifische Loops und
  Boundary-Preview werden darüber gekapselt.
- Die KI-Pipeline bleibt der einzige Erzeuger automatischer Wort- und
  Silbentimings. Der Editor dupliziert kein Alignmentmodell.
- Die Unity Stage kann Zeilen, Wörter und Silben bereits darstellen. Sie bleibt
  zunächst unverändert und erhält weiterhin das veröffentlichte Ergebnis über
  `/api/songs/{id}/lyrics`.

## Aktueller Datenfluss

```text
Audio + LRCLIB-LRC
  -> CUDA-Aligner
  -> *.lrc (Zeilen + Wortintervalle)
  -> *.alignment.json (Wortquelle, Status, Silben, Confidence, Laufdiagnostik)
  -> Bibliotheksordner
  -> LibraryRepository/LrcParser
  -> LyricsDto
  -> Avalonia-Clients und Unity Stage
```

Die Song-ID ist aktuell deterministisch aus dem relativen Bibliothekspfad
abgeleitet. `LibraryRepository` ordnet `.lrc`, `.alignment.json`, Stems und
Visualisierungsdateien über den gemeinsamen Dateibasename dem Audio zu.

## Vorhandenes Pipelineformat

Die veröffentlichte `.lrc` enthält Zeilenzeitpunkte und echte Wortintervalle in
der Neon-Stage-Erweiterung:

```text
[00:12.40]<00:12.40,00:13.20>Heute <00:13.20,00:13.80>wird
```

Die zugehörige `.alignment.json` enthält unter `details[]`:

- Zeilentext, Start, Validierungsstatus und Fehlergrund
- Wörter mit Start, Ende und `timing_source`
- orthografische Silben mit Start, Ende und Confidence
- Wort-/Silbenmethode und Konfidenzen

Der Report enthält außerdem Sprache, Alignmentmodus, Modell-/Methodennamen,
Quality-Gate, ASR-Vergleich und Diagnostiken. Er besitzt derzeit keine stabile
Schema-Version, keine dauerhafte Analysis-ID, keine Audio-Prüfsumme und keine
unveränderlichen Segment-IDs. Silbengrenzen sind momentan innerhalb eines
akustisch gemessenen Wortintervalls orthografisch/dauerbasiert verteilt; sie
sind noch keine akustisch erkannten Phonemgrenzen.

`LibraryRepository.ReadLyricsAsync` liest Wortzeiten aus der LRC und ergänzt
Silben aus der Alignment-JSON. Damit ist der bestehende Stage-Datenweg bereits
abwärtskompatibel nutzbar.

## Bereits nutzbare Server-Endpunkte

- `GET /api/songs`
- `GET /api/songs/{songId}`
- `GET /api/songs/{songId}/audio` (Range Requests)
- `GET /api/songs/{songId}/stems`
- `GET /api/songs/{songId}/stems/{kind}` (Range Requests)
- `GET /api/songs/{songId}/lyrics`
- `GET /api/songs/{songId}/visualization`
- `GET /api/songs/{songId}/cover`

Es gibt derzeit keine Lyrics-Versionen, Editorentwürfe, Review-/Publish-
Transitions, ETags oder Optimistic Concurrency. Eine Benutzeranmeldung oder ein
Rollenmodell ist im Server ebenfalls noch nicht implementiert. Deshalb wird
keine Editor-eigene Authentifizierung erfunden; die API wird so vorbereitet,
dass die vorhandene Serverauthentifizierung später zentral davor geschaltet
werden kann.

## Kleinste kompatible Erweiterung

1. Versioniertes Editor-Dokument (`schemaVersion: 1`) mit stabilen Segment-IDs,
   Hierarchie, KI-Originalwerten, aktuellen Werten, Herkunft und Reviewstatus.
2. SQLite-Tabellen für Lyrics-Versionen und unveränderliche Korrekturen; Audio-
   und große Waveformdateien bleiben im Dateispeicher.
3. Endpunkte unter der bestehenden Songressource:
   - `GET /api/songs/{id}/lyrics/editor`
   - `GET /api/songs/{id}/lyrics/versions`
   - `GET/PUT /api/songs/{id}/lyrics/versions/{versionId}`
   - Review-/Approve-/Publish-Aktionen
4. ETag/Versionsnummer bei jedem schreibbaren Dokument. Konflikte liefern 409
   und überschreiben niemals den lokalen Entwurf.
5. `/api/songs/{id}/lyrics` liefert weiterhin nur die veröffentlichte Version;
   solange keine existiert, gilt der bisherige LRC-/Alignment-Fallback.

## Desktop-Zuschnitt

`Karaoke.App.Desktop` erhält eine eigene Avalonia-Application und startet nicht
mehr die bisherige Bühnen-/Queue-Oberfläche. Das plattformneutrale Editor-
Datenmodell und die Command-/Timeline-Geometrie bleiben UI-unabhängig und
werden separat getestet. Das Timeline-Control zeichnet Waveform und sichtbare
Segmente direkt über `DrawingContext`; es erzeugt keine Controls pro Wort oder
Silbe.

Die erste Scheibe umfasst:

- Songauswahl über die vorhandene API
- Laden von Vocalspur und Alignmentreport
- hierarchisches Line/Word/Syllable-Modell mit KI-Snapshot
- performante Zeit-/Pixeltransformation, sichtbarer Bereich und Hit-Testing
- Playhead, Zoom, Scrollen und gemeinsame Silbengrenze
- Undo/Redo und lokaler Entwurf

Waveform-Pyramiden, Serverversionierung, Review und Veröffentlichung folgen auf
diesem Kern. Die Unity Stage muss bis zur Einführung veröffentlichter Versionen
nicht verändert werden.

## Bewusst unveränderte Komponenten

- Audio-/Lyrics-Synchronisation und Rendering der Unity Stage
- Queue, Events, Wunschliste und Spotify-Integration
- KI-Modelle und deren Jobsteuerung
- bestehende Gast- und Admin-Webseiten
- bestehender LRC-Fallback für ältere Bibliothekstitel

