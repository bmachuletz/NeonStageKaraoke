# Musical Highlight Timeline – Entwicklungsnotiz

## Architektur

Das linguistische Alignment bleibt unverändert die Ground Truth für Wort- und Silbenzuordnung. Basic-Pitch-Noten werden seitdem als musikalische Evidenz an den vorhandenen Silben mitgeführt und in der gemeinsamen `StagePresentationEngine` in eine visuelle Highlight-Timeline übersetzt. Dadurch profitieren alle Alignment-Varianten von derselben Rendering-Logik, ohne dass der Python-Aligner oder das linguistische Datenmodell ersetzt werden.

Die Trennung lautet:

1. **Alignment:** Wort- und Silbenzeiten aus Forced Alignment.
2. **Musikalische Artikulation:** Noten-Onsets/-Offsets und Pausen aus Basic Pitch.
3. **Visuelles Timing:** `ADVANCE`, `HOLD` und `SNAP` für den Textfüllverlauf.

## Schwellenwerte

Die aktuellen Standardwerte sind in `KaraokeHighlightTimelineOptions` zentralisiert und sollen später in eine serverseitige Konfiguration ausgelagert werden:

- `MinimumPauseDurationSeconds`: 0.055
- `LegatoGapToleranceSeconds`: 0.035
- `MinimumAttackDistanceSeconds`: 0.045
- `MinimumVoicedDurationSeconds`: 0.05
- `MinimumNoteConfidence`: 0.35
- `MinimumPitchChangeSemitones`: 2
- `MaximumNoteOffsetSeconds`: 0.08
- `TimelineVersion`: 1

Einzelne niedrigkonfidente Onsets und Mini-Lücken führen nicht zu sichtbarem Zittern. Wenn keine belastbare musikalische Grenze erkannt wird, greift der bisherige lineare Wort-/Silbenfortschritt.

## Bedienung

- **Beat-Raster:** richtet visuelle Wort- und Silbengrenzen nahe am musikalischen Raster aus, ändert aber keine gespeicherten Lyrics.
- **Musikalischer Füllverlauf:** steuert die Füllgeschwindigkeit mit Basic-Pitch-Noten. Klare Pausen halten, neue Einsätze springen.
- Server: `Karaoke:EnableMusicalHighlightTimeline=true`
- Editor: `NEONSTAGE_MUSICAL_HIGHLIGHT=1`; `0/false/no/off` deaktiviert, Standard ist aktiv.
- Alignment-Menü: **Basic-Pitch ausführen** analysiert den geladenen Song-Stand eigenständig und legt einen Review-Stand an.

## Alignment-übergreifende Übergänge

Der Python-Aligner klassifiziert Zeilenübergänge zusätzlich als `legato`, `separated` oder `ambiguous`. Release und Folgesatz-Onset werden gemeinsam bewertet, damit eine zu lange Endsilbe der vorherigen Zeile nicht automatisch den Satzbeginn verschiebt. Nur ein sicher getrennter Übergang mit tragfähigem Vocal-Angriff führt eine lokale Korrektur aus; manuell editierte Grenzen bleiben geschützt. Der Report enthält die Entscheidung unter `line_transitions`.

Wichtige Gates des joint-line-transition-Passes sind bewusst konservativ:

- Mindestpause: 0.055 s
- Mindestabstand Release→Attack: 0.06 s
- Mindestkorrektur Release: 0.04 s
- Mindestkorrektur Onset: 0.04 s
- Release-Padding: 0.035 s
- Mindestkonfidenz für Mutationen: 0.68

## Debugging

`StagePresentationEngine.DescribeHighlightTimelines(...)` liefert pro Wort die Noten sowie die erzeugten `ADVANCE`-, `HOLD`- und `SNAP`-Segmente. Die Methode ist bewusst rendererunabhängig und kann später in einen Debug-Dialog oder Log-Endpoint eingebunden werden.

## Bewertung

Mit den vorhandenen quantisierten Basic-Pitch-Noten ist ein gutes rhythmisches Darstellungsgefühl für Staccato und klare Pausen erreichbar. Der größte Unsicherheitsfaktor ist nicht das linguistische Alignment, sondern die Qualität von Vocal-Separation und Basic-Pitch-Onsets; falsche/versetzte Onsets sind in der aktuellen Implementierung die wahrscheinlichste Fehlerquelle. Die größte nächste Verbesserung wäre eine feinere, am Vocal-Stem unabhängig validierte Onset-/Voicing-Evidenz.

## Separation-Diagnostik

Der Stage-Baseline-Separator, die beiden Analysis-Separator-Kandidaten und optional per `LRC_SEPARATOR_A_B_MODELS` aktivierbare A/B-Modelle werden als echte Stem-Paare bewertet. ASR bleibt der Hauptscore für Alignment und Stage-Auswahl; Basic-Pitch-Qualität ist zusätzlich als capped Bonusscore und als eigenständige Empfehlung im Report sichtbar.

Pro echtem Stem-Paar werden Vocal-RMS, Instrumental-RMS, Vocal/Instrumental-Kontrast, Lyrics-Zeitfenster-Coverage, Basic-Pitch-Diagnostik und Leak-Verdacht unter `stage_stem_selection.quality` dokumentiert. `hybrid_diagnostics` vergleicht ASR- und Basic-Pitch-Empfehlung; unterschiedliche Gewinner sind erlaubt. Eine Audiostream-Fusion ist bewusst nicht implementiert – der Eintrag ist diagnostisch.

Bereitgestellte Bibliotheksstems bleiben gesperrt und werden nie überschrieben. Neue lokale Modelle werden nicht implizit aktiviert, sondern nur über die leere A/B-Variable `LRC_SEPARATOR_A_B_MODELS` nach einem bewerteten Testlauf.

Der Basic-Pitch-Service gibt `BASIC_PITCH_MINIMUM_NOTE_LENGTH_MS` intern an Basic
Pitch 0.4.0 als `minimum_note_length` in Sekunden weiter. Die Millisekunden-Angabe
bleibt dabei die stabile Außenkante für Compose, `.env` und Nutzerkonfiguration.

## Enterprise-Diagnostik

Basic-Pitch-Onset-Mutationen erfordern jetzt zusätzlich eine unabhängige
Vocal-Envelope-Evidenz (`vocal-envelope-onset-consensus-v1`). Der Report enthält
unter `basic_pitch_analysis.onset_consensus`, welche Energieattacke einen
vorgeschlagenen Onset gestützt hat. Damit reduziert sich das Risiko, dass ein
einzelnes Modellereignis eine Wortgrenze verschiebt.

Pro Stem-Paar werden zusätzlich `vocal_recall` in Lyrics-Fenstern und
`instrumental_leak_in_vocal_pauses` gemessen. Das Stage-Ranking nutzt weiterhin
ASR als Hauptscore, zieht aber bei der Separator-Auswahl diese beiden
Qualitätsmerkmale als begrenzte Penalties heran (`asr-plus-separation-quality-v1`).

Basic-Pitch-Polyphonie wird als `singer_context` geführt: `singer_ambiguity`,
geschätzter Lead-Anteil und lead-gewichtete Qualität. Eine echte Sänger-ID ist
bewusst nicht implementiert; Doppelgesang bleibt damit transparent und
konservativ.

Optionale A/B-Modelle werden nicht blind aktiviert. Wenn
`LRC_SEPARATOR_A_B_MODELS` gesetzt ist, dokumentiert `a_b_summary` pro Modell
Erfolg/Fehler, ASR-Score, Vocal-Recall, Pause-Leak und Singer-Ambiguity.
