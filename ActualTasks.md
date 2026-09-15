# Actual Tasks

> Historical engineering log. The multi-profile, Basic Pitch, SOFA, MMS, and
> MedleyVox experiments described below have been superseded and removed. The
> current product path is EasyAligner plus the optional Full transcript source;
> see `README.md` and `lyrics-word-aligner/README.md` for the maintained state.

## Current status 2026-09-15

- EasyAligner is the single forced-alignment path for imports, selected songs,
  and the complete library.
- Full transcript is a virtual lyrics source and feeds its result through the
  same EasyAligner path.
- German and English use separate global CTC models. Human-readable and
  technical normalized lyrics remain separately inspectable.
- Suspicious chorus/backing passages receive a bounded independent all-vocals
  recovery pass; only stronger local evidence is accepted.
- Enhanced LRC import, cancellation, windowed Editor-to-Stage testing, song
  video playback, and deterministic MP4 export are implemented.
- Legacy experimental alignment services, profiles, vendored SOFA sources, and
  their tests/configuration have been removed.

## Aktiver Auftrag: Alignment-Zeilenübergänge verbessern

### Ziel

Satzanfänge sollen nicht mehr durch eine falsch erkannte, zu lange Endsilbe der vorherigen Zeile verschoben werden. Release und Folgesatz-Onset sollen gemeinsam und evidenzbasiert entschieden werden.

### Fahrplan

1. [x] Bestehende Alignment-Pfade und kritische Heuristiken analysieren.
2. [x] Gemeinsames Übergangsmodell `legato / separated / ambiguous` entwerfen.
3. [x] `app/line_transitions.py` implementieren.
4. [x] `analyze_line_transitions` in die Pipeline integrieren.
5. [x] Tests für klare Pause, Legato, Mini-Pause und manuelle Grenzen ergänzen.
6. [x] Python-Syntax- und Testlauf abschließen.
7. [x] Pipeline-Phasen-/ smoke tests ausführen.
8. [x] Architektur- und Tuningnotizen in der Entwicklungsdoku ergänzen.
9. [x] Abschlussbewertung und verbleibende Grenzen dokumentieren.

### Umsetzungsdetails

- Neue Analyse: `lyrics-word-aligner/app/line_transitions.py`
- Pipeline-Anbindung nach dem konservativen Stage-Vocal- und Stem-Contrast-Pass:
  `lyrics-word-aligner/app/pipeline.py`
- Report-Feld: `line_transitions`
- Mutationen:
  - `joint_line_transition_release_shift_ms`
  - `joint_line_transition_onset_shift_ms`
  - `pre_joint_line_transition_end`
  - `pre_joint_line_transition_start`
- Manuell angepasste Zeilen/Wörter bleiben geschützt.
- Klassifikation ist nur dann `separated`, wenn:
  - eine andauernde tiefe Energiemulme existiert,
  - danach ein starker, tragfähiger Vocal-Angriff folgt,
  - die Mulme mindestens `minimum_pause` lang ist,
  - Kandidaten maximal lokal um die bestehenden Grenzen liegen.

### Validierung

- `tests.test_line_transitions`: 4 Tests erfolgreich.
- `tests.test_vocal_boundaries` und `tests.test_stem_contrast`: 22 Tests erfolgreich.
- `tests.test_pipeline_phase_order`: 14 Tests erfolgreich.
- `py_compile` für `line_transitions.py` und `pipeline.py`: erfolgreich.
- Server- und Desktop-Build (`dotnet build`): erfolgreich.
- Python-Testumgebung: `.tools/aligner-test-venv` mit NumPy.

### Nachbereitung 2026-08-29

- `line_transitions` ist jetzt auch ohne Stage-Stems immer im Report definiert.
- Der Menüpunkt **Basic-Pitch ausführen** nutzt jetzt das echte
  `basic-pitch-postprocess`-Profil und damit keinen ASR-/Forced-Alignment-Lauf.
- Der reine Basic-Pitch-Pfad verlangt vorhandene `.vocals.flac` und
  `.instrumental.flac`, da der Postprocessor beide Stems als feste Referenz braucht.
- Architektur- und Schwellenwertnotizen sind in
  `docs/musical-highlight-timeline.md` dokumentiert.

### Störung: Alignment blieb bei 49 % hängen

- Betroffener Job: `r6fnthsb` (`research-shadow`, „Wir nennen Dich Mücke – Madsen“).
- Beobachtung: Worker nutzte über 12 Minuten etwa 12 CPU-Kerne, GPU blieb leer,
  Status blieb bei `49 % / All-Vocals-Kandidaten`.
- Ursache: Der Container hatte trotz `device=cuda` keine sichtbare CUDA-GPU
  (`torch.cuda.is_available() == False`). Der Audio-Separator lief deshalb im
  extrem langsamen CPU-Fallback.
- Sofortmaßnahme: `docker restart lyrics-aligner`; danach war CUDA wieder
  verfügbar (`True`, 1 GPU). Der laufende Alt-Job wurde beim Neustart beendet.
- Prävention: `pipeline.py` meldet jetzt vor Modellstart via `notify`, wenn CUDA
  angefordert aber nicht verfügbar ist, und schreibt `runtime_device` in den
  Abschlussreport.
- Startup-Recovery implementiert: `app/main.py` markiert beim FastAPI-Start alle
  `queued`/`processing`-Jobs als `failed`, erhält den letzten Stand in
  `interrupted_*` und dokumentiert `recovery=startup-recovery-v1`.
- Tests: `lyrics-word-aligner/tests/test_job_recovery.py`
- Echtvalidierung: neuer Aligner-Container gestartet; Job `r6fnthsb` ist jetzt
  korrekt als unterbrochen markiert, Service und CUDA sind gesund.

### Korrektur zu späte Satzanfänge 2026-08-30

#### Auslöser

- Referenzlauf: `cwv1ygks`, „Na gut dann nicht – Madsen“, Profil
  `research-shadow`, Qualität 83.6.
- Symptom: besonders frühe Satzanfänge sind häufig zu spät.

#### Diagnose

- `line_transitions` begrenzte den Onset-Kandidaten auf
  `next_start - .02` und erlaubte Mutationen nur für positive
  `onset_delta`. Frühere akustische Angriffe konnten daher strukturell
  nicht übernommen werden.
- Der Basic-Pitch-Onset-Konsensus lehnte teils sehr starke Kandidaten mit
  `independent_db` über 11/12 dB wegen `no-nearby-onset` ab. Die Toleranz
  von 45 ms war für Vocal-Envelope-Onsets knapp.
- Der Server-Report hatte einen fehlenden Helper für optionale
  Prozentwerte und baute dadurch nicht.

#### Korrektur

- `line_transitions` erlaubt jetzt frühere Onsets bis maximal
  `LRC_LINE_TRANSITION_MAX_EARLY_ONSET_SHIFT=0.12` Sekunden.
- Mutationen bleiben begrenzt und überschneidungsfrei:
  - Kandidat muss mindestens den konfigurierten Shift-Abstand haben.
  - Der neue Onset darf die bereinigte Grenze der vorherigen Zeile nicht
    unterschreiten.
  - Das erste Wort muss mindestens
    `LRC_LINE_TRANSITION_MIN_ONSET_DURATION=0.08` Sekunden lang bleiben.
  - Manuell angepasste Grenzen bleiben geschützt.
- Der Onset-Konsensus toleriert jetzt 65 ms
  (`LRC_ONSET_CONSENSUS_TOLERANCE_SECONDS=0.065`).
- Sehr starke unabhängige Band-Evidenz ab
  `LRC_ONSET_CONSENSUS_STRONG_INDEPENDENT_DB=12` darf fehlende
  Envelope-Onsets ausnahmsweise überstimmen; schwache Kandidaten bleiben
  abgelehnt.
- Der menschliche Report formatiert optionale A/B-Metriken ohne Absturz.

#### Validierung

- `tests/test_line_transitions.py` und
  `tests/test_basic_pitch_evidence.py`: 27 Tests erfolgreich.
- Vollständiger Python-Testbestand: 488 erfolgreich, 2 environmentbedingte
  Alt-Fehler (`easyaligner` fehlt lokal; pYIN-Test abhängig von optionaler
  Umgebung).
- `dotnet build src/Karaoke.Server/Karaoke.Server.csproj --no-restore`:
  erfolgreich.
- Playback-Integrationstests: erfolgreich.

#### Noch offen

- Aligner-Image gebaut; vor dem Neustart wurde bestätigt, dass kein Job
  `queued` oder `processing` war.
- Aligner-Container ist neu gestartet, Health-Check ist okay, CUDA ist
  sichtbar (`True`, 1 GPU).
- „Na gut dann nicht“ als Schattenlauf wiederholen und frühe Zeilenanfänge
  mit `cwv1ygks` vergleichen.

### Nachkontrolle Screenshot und Lauf `lih4ihrk`

#### Befund

- Der Nutzer-Screenshot zeigt Textfüllung ohne Vocal- oder Pitch-Ausschlag.
- Lauf `lih4ihrk` beginnt Zeile 1 bei `44.23 s`.
- Der echte Vocal-Onset liegt bei `44.62/44.63 s` (Vocal-RMS vorher praktisch
  digitaler Nullwert; Basic-Pitch-Noten beginnen ebenfalls später).
- Die bestehende Silent-Prefix-Recovery hatte die Korrektur erkannt
  (`44.23 → 44.63`, 400 ms), aber mit
  `shifted-line-overruns-next-line-in-lane` abgelehnt.

#### Korrektur

- Wenn eine starre Vollzeilenverschiebung die Folgezeile überlaufen würde,
  wird jetzt ein **release-erhaltender Reflow** versucht.
- Der Reflow startet die Zeile am echten Vocal-Onset, behält das gemessene
  Release bei und verteilt die Wörter monoton über den verbleibenden Zeitraum.
- Kein Überlauf in dieselbe Voice-Lane; wenn kein gültiger Reflow möglich ist,
  bleibt die frühere Ablehnung mit Begründung erhalten.
- Neuer Reportgrund: `release-preserving-reflow`.

#### Validierung

- `tests/test_vocal_boundaries.py`: 21 Tests erfolgreich.
- Zielgerichtete Kombination (`vocal_boundaries`, `line_transitions`,
  `basic_pitch_evidence`, `pipeline_phase_order`): 63 Tests erfolgreich.
- `py_compile` für `vocal_boundaries.py` und `pipeline.py`: erfolgreich.
- Vor dem Deploy war kein Job `queued`/`processing`.
- Aligner-Image neu gebaut, Container neu gestartet; Health-Check okay,
  CUDA `True`, 1 GPU.

#### Nächster Kontrolllauf

- „Na gut dann nicht“ erneut als Schattenlauf starten.
- Zeile 1 muss dann bei etwa `44.63 s` beginnen und der Textfortschritt darf
  erst mit dem sichtbaren Vocal-/Pitch-Onset einsetzen.

### Nacharbeit Release-erhaltender Reflow 2026-08-31

#### Nutzer-Referenz

- Nutzerreferenz ist Revision 12 von „Na gut dann nicht – Madsen“:
  Zeile 1 beginnt bei `44.63 s`; die manuellen Wortgrenzen liegen unter anderem
  bei `44.645`, `44.941`, `45.848`, `46.125`, `46.989`, `47.273`, `47.584`
  und `47.838 s`.
- Die erste Reflow-Version verschob nur starr um `+400 ms`; das war falsch,
  weil die relativen Wortgrenzen nicht akustisch überprüft wurden.

#### Korrektur

- `_reflow_after_silent_prefix` verschiebt die relativen ASR-Wortzentren zuerst
  kohärent zur gemessenen Zeilen-Onset-Position und skaliert sie nur, wenn der
  behaltene Release kürzer wird.
- Danach werden starke, geordnete Vocal-Onset-Peaks den Wörtern zugewiesen.
  Die Zuordnung ist monoton und verhindert, dass mehrere Wörter denselben
  alten Onset belegen.
- Wenige eindeutige Onsets führen weiterhin zum konservativen skalierten
  Reflow statt zu einer willkürlichen Gleichverteilung.
- Releases bleiben erhalten; Wortgrenzen bleiben monoton und überschreiten die
  Folgezeile in derselben Voice-Lane nicht.

#### Validierung

- `tests/test_vocal_boundaries.py`: 22 Tests erfolgreich.
- Zielgerichtete Kombination (`vocal_boundaries`, `line_transitions`,
  `basic_pitch_evidence`, `pipeline_phase_order`): 64 Tests erfolgreich.
- `py_compile` für `vocal_boundaries.py` und `pipeline.py`: erfolgreich.
- `git diff --check`: erfolgreich.

#### Noch offen

- Aligner-Image neu gebaut; vorher war kein Job `queued`/`processing`.
- Aligner-Container neu gestartet; Health-Check `{"status":"ok"}`.
- CUDA ist sichtbar (`True`, 1 GPU).
- Container-Code enthält den neuen Reflow (`threshold_db=95.0`).
- Danach „Na gut dann nicht“ als Schattenlauf wiederholen und Revision 12 als
  Referenz vergleichen.

### Rückfallanalyse 17:11 / 17:19 2026-08-31

#### Befund

- 17:11 entsprach Revision 14 bzw. Version 12:
  `Na 44.645`, `gut 44.941`, `dann 45.848`, `nicht 46.125`, …
- 17:19 (Revision 15) drückte dieselbe Zeile in `44.63–45.391` zusammen.
- Beide Reports zeigen dieselbe Silent-Prefix-Korrektur. Der Unterschied entstand
  erst danach:
  `compressed_word_repair` stieg von 5 auf 10 gedrückte Wörter und „reparierte“
  die evidenzbasierte Zeile 1 neu.

#### Ursache

- Der Release-erhaltende Reflow erzeugt kurzlebige, akustisch bewusst gesetzte
  Wortfenster, die nicht der 55-ms-Silbenheuristik entsprechen.
- `repair_compressed_word_runs` sah diese Fenster als „compressed“, erweiterte
  sie mit Nachbarn und überschrieb die guten Grenzen durch eine proportionale
  Aktivitätsverteilung.

#### Korrektur

- Zeilen mit `stage_vocal_silent_prefix_original_start` werden jetzt als
  evidenzbasiert geschützt und nicht mehr vom Compressed-Word-Repair behandelt.
- Normale tatsächlich kollabierte Wörter bleiben reparierbar; manuelle Zeilen
  bleiben ebenfalls geschützt.

#### Validierung

- Neue Regression:
  `test_release_preserving_silent_prefix_reflow_is_not_redispatched`.
- `tests/test_collapsed_lines.py`, `tests/test_vocal_boundaries.py`: 42 Tests.
- Zielkombination inkl. Zeilenübergängen, Basic-Pitch-Evidenz und
  Pipeline-Phasen: 42 weitere Tests erfolgreich.
- `py_compile` und `git diff --check`: erfolgreich.
- Deploy nach Idle-Prüfung durchgeführt; Health-Check okay, CUDA sichtbar
  (`True`, 1 GPU), Schutz befindet sich im Container-Code.

## Abgeschlossene Aufträge

### Musical Highlight Timeline

- Getrennte Ebenen Alignment, Artikulation und visuelles Timing implementiert.
- `ADVANCE / HOLD / SNAP` in gemeinsamer Presentation-Engine umgesetzt.
- Basic-Pitch-Noten werden durch Server-DTOs und Editor-Dokumente bis Renderer geführt.
- Fallback auf bisheriges lineares Highlight bleibt erhalten.
- UI heißt jetzt `Beat-Raster` und `Musikalischer Füllverlauf`.
- Server und Editor sind global aktiviert.
- Menüpunkt `Basic-Pitch ausführen` ergänzt.
- Doku: `docs/musical-highlight-timeline.md`

## Aktiver Auftrag: Separation- und Basic-Pitch-Qualität

### Ziel

- Basic-Pitch-Decoding über Umgebungsvariablen steuern.
- Stage-Baseline- und Analysis-Separator-Stems als echte Kandidaten gemeinsam bewerten.
- ASR- und Basic-Pitch-Qualität getrennt und nachvollziehbar im Report ausweisen.
- Stem-Fusion ausschließlich diagnostisch halten.
- Neue lokale Separator-Modelle nur optional per A/B-Variable zulassen.

### Fahrplan

1. [x] Basic-Pitch-Parameter im Service, Compose und `.env.example` ergänzen.
2. [x] Metriken für Vocal-RMS, Instrumental-RMS, Kontrast, Coverage, Pitch-Qualität und Leak-Verdacht ergänzen.
3. [x] Stage-Baseline als echten Alignment-Kandidaten bewerten.
4. [x] Stage-Stem-Auswahl nur ohne bereitgestellte Bibliotheksstems umsetzen.
5. [x] ASR- und Pitch-Empfehlung getrennt reporten; Fusion diagnostisch halten.
6. [x] Optionale A/B-Modelle konfigurierbar, aber standardmäßig deaktiviert lassen.
7. [x] Python-Tests, Docker-Build und Health-Check ausführen.
8. [x] Ergebnis, Verhalten und Grenzen hier dokumentieren.

### Umsetzungsdetails

- Basic-Pitch-Service: `BASIC_PITCH_ONSET_THRESHOLD`, `BASIC_PITCH_FRAME_THRESHOLD`, `BASIC_PITCH_MINIMUM_NOTE_LENGTH_MS`.
- Kandidatenbewertung: `LRC_BASIC_PITCH_USE_FOR_SEPARATOR_SCORE=true` liefert zusätzlich Tonalitätsdiagnostik; ASR bleibt der Hauptscore.
- Stage-Schutz: bereitgestellte Bibliotheksstems bleiben gesperrt und werden nie überschrieben.
- A/B-Modelle: `LRC_SEPARATOR_A_B_MODELS=` bleibt leer, bis ein repräsentativer Testlauf ausgewertet wurde.

### Ergebnis

- `configured-stage-separator` ist jetzt ein echter, ASR-bewerteter Baseline-Kandidat.
- `provided-stage-stems` wird ebenfalls als Baseline-Kandidat bewertet, aber der Stage-Export bleibt gesperrt.
- Nur echte Vocal/Instrumental-Paare konkurrieren für Stage; Originalmix-/Blend-Kandidaten bleiben Analysis-only.
- `stage_stem_selection.quality` dokumentiert RMS-Werte, Kontrast, Lyrics-Coverage, Basic-Pitch-Diagnostik und Leak-Verdacht.
- `stage_stem_selection.hybrid_diagnostics` dokumentiert ASR- und Basic-Pitch-Empfehlung getrennt; Audiostream-Fusion ist nicht implementiert.
- `LRC_SEPARATOR_A_B_MODELS` ergänzt zusätzliche Modelle nur explizit; Duplikate der normalen Analysis-Modelle werden ignoriert.
- `LRC_BASIC_PITCH_USE_FOR_SEPARATOR_SCORE` ist standardmäßig aktiv; der Pitch-Anteil bleibt in der Gesamtscore-Gewichtung capped.

### Validierung

- `py_compile`: `pipeline.py`, `stem_roles.py`, `candidate_selection.py`, `basic_pitch_evidence.py`, `main.py`, `line_transitions.py` erfolgreich.
- `pytest tests/test_candidate_selection.py`: 23 Tests erfolgreich.
- `unittest`: 41 Tests erfolgreich (`basic_pitch_evidence`, `stem_roles`, `pipeline_phase_order`, `job_recovery`, `line_transitions`).
- Docker-Build für `basic-pitch` und `lyrics-aligner` erfolgreich.
- Beide Container sind healthy; Aligner sieht CUDA (`True`, 1 GPU).
- Umgebungsprüfung bestätigt Basic-Pitch-Parameter `0.45 / 0.25 / 70 ms`, `LRC_BASIC_PITCH_USE_FOR_SEPARATOR_SCORE=true` und leere A/B-Modellliste.

## Behobener Fehler: Basic-Pitch-A/B mit bereitgestellten Stems

### Symptom

- Job `1z05vqc0`, „Protest ist cool aber anstrengend – Madsen“.
- Status: `failed` bei 100 %.
- Fehler: `cannot access local variable 'candidates' where it is not associated with a value`.

### Ursache

- Der neue Stage-Baseline-Kandidaten-Code versuchte während des Ladens bereitgestellter Stems auf `candidates[0]` zuzugreifen.
- `candidates` wird aber erst später durch `build_analysis_candidates(...)` aufgebaut.
- Betroffen war der Kontroll-/Behandlungspfad von Basic-Pitch-A/B mit `provided-library-stems`; ein normaler Alignment-Lauf konnte funktionieren.

### Korrektur

- Bereitgestellte Vocals werden sofort beim Laden in 16 kHz konvertiert und in `stage_vocal_audio` gehalten.
- Der echte Kandidat `provided-stage-stems` verwendet danach dieselbe Audioreferenz.
- Es gibt keinen Zugriff auf `candidates` mehr, bevor die Kandidatenliste existiert.
- Bereitgestellte Stems bleiben weiterhin gesperrt und werden nicht überschrieben.

### Validierung

- Regressionstest: `test_provided_stage_vocals_are_loaded_before_candidate_list_exists`.
- `py_compile`: `pipeline.py`, `basic_pitch_postprocess.py`, `job_worker.py` erfolgreich.
- 26 zielgerichtete Tests erfolgreich, darunter Basic-Pitch-Postprocessing, Stage-Rollen, Pipeline-Phasen, Recovery und Zeilenübergänge.
- Aligner-Container neu gebaut; Health-Check erfolgreich, CUDA sichtbar (`True`, 1 GPU).
- Container-Code bestätigt die korrekte Reihenfolge: bereitgestellte Vocals laden → Kandidatenliste aufbauen.

### End-to-End-Nachlauf 2026-08-29

- Job `13rxx5ev` lief als erste Kontrolle durch: `completed`, Qualität 96.7, CUDA,
  36 Zeilen, 2 Review-Zeilen, keine Zeilen-/Wortüberlappungen.
- Job `jllig9fn` lief nach Reparatur des Basic-Pitch-Service erneut vollständig durch:
  `completed`, gleiche Qualität 96.7, CUDA, bereitgestellte Stems wurden als
  `provided-stage-stems` ausgewählt und nicht überschrieben.
- In `jllig9fn` wurde Basic-Pitch jetzt angewendet: 12 Onset-Korrekturen und
  8 Release-Korrekturen (`basic_pitch_analysis.applied = 20`).

### Nachträglich gefundener Basic-Pitch-Fehler

- Symptom: Basic-Pitch-Aufrufe während der Kandidatenbewertung schlugen mit
  `HTTP 500` fehl; die Separation-Quality enthielt `sidecar-unavailable`.
- Ursache: Basic-Pitch 0.4.0 erwartet `minimum_note_length` in Sekunden,
  nicht `minimum_note_length_ms`.
- Korrektur: `BASIC_PITCH_MINIMUM_NOTE_LENGTH_MS` wird im Service nach Sekunden
  umgerechnet und als `minimum_note_length` übergeben
  (`basic-pitch-service/app.py`).
- Absicherung: `basic-pitch-service/test_app.py` prüft die resultierende
  Prediction-Konfiguration (`0.45 / 0.25 / 0.07 s`).
- Service-Neustart und Direktaufruf erfolgreich: 2782 Noten, 1196 Onsets,
  3937 Contour-Punkte bei den Madsen-Vocals.
- Hinweis: Die Basic-Pitch-Diagnostik der Kandidaten in `jllig9fn` blieb leer,
  weil der Service erst nach der Kandidatenphase repariert wurde. Ein weiterer
  Lauf ist nötig, um die neue Kandidaten-Pitch-Qualität im Report zu sehen.

## Fehlerprüfung der letzten Alignment-Jobs 2026-08-29

### Aktive Fehler

- Jobs `45znicu4` und `t72nihus`, Profil `basic-pitch-postprocess`:
  `Model file provided-library-stems not found in supported model files`.
- Ursache: Der Postprocessor las `analysis_stem_selection.model == "provided-library-stems"`
  und übergab diesen Wert an den Audio-Separator. Der Wert beschreibt jedoch
  bereitgestellte Stems und ist kein Separator-Checkpoint.
- Korrektur: Wenn `analysis_stem_selection.separator == "provided"` ist, lädt
  der Postprocessor jetzt die vorhandenen Vocals/Instrumental direkt in 16 kHz
  (`provided-selected-analysis-stem`); `separate_stems` wird nicht aufgerufen.
- Absicherung: `test_provided_selected_analysis_stem_is_not_reseparated`.
- Wiederholungslauf: Job `8hn1shlv` ist `completed`, Qualität 99.9, ohne
  Overlap-Konflikte, `stochastic_models_rerun=false`; 3 Onset- und 4
  Release-Korrekturen wurden angewendet.
- 44 zielgerichtete Aligner-Tests erfolgreich; Aligner-Container neu gebaut,
  Health-Check und CUDA (`True`, 1 GPU) sind in Ordnung.

### Ältere Fehler

- `candidates`-Fehler: durch den Fix aus Job `jllig9fn` verifiziert.
- Basic-Pitch-HTTP-500: durch Sekunden-/Millisekunden-Konvertierung behoben.
- Aligner-Restart-Abbrüche: werden jetzt durch Startup-Recovery als `failed`
  markiert und müssen bewusst neu gestartet werden.
- `No CUDA GPUs are available`: Container-/Treiberproblem, kein Pipelinecode.
- Ältere `Vocal- oder Instrumental-Spur fehlt in Separator-Ausgabe: []`-Läufe
  betreffen die vorherige Separator-Fehlerkette und sollten erst wieder bewertet
  werden, wenn sie mit dem aktuellen Stand erneut auftreten.

## Schattenlauf mit Überlappungen 2026-08-30

### Betroffener Lauf

- Song: „Highway to Hell – AC/DC“.
- Aligner-Job: `h43wl4y4`, Profil `research-shadow`.
- Der Aligner selbst war erfolgreich: `completed`, 46 Zeilen, 5 Review-Zeilen,
  Qualität 78.6, `line_overlap_conflicts=0`, `word_overlap_conflicts=0`.

### Warum der Schattenlauf nicht landete

- Der Server hat erst nach dem Alignment beim Anlegen der Lyrics-Version abgebrochen.
- `SnapshotFileAsync` verlangte bisher eine vollständig konfliktfreie
  Editor-Hierarchie. `TimelineEditing.ValidateLineSequence` bewertet dabei auch
  verschachtelte Wort-/Silbengrenzen und kann daher Konflikte sehen, die der
  Python-Validator als reine Zeilen-/Wortüberlappung noch nicht meldet.
- Ergebnis: Der teure Alignment-Lauf wurde weggeworfen, obwohl er genau für die
  Review-Betrachtung gedacht war.

### Neue Kategorie

- `LyricsVersionStatus.ReviewOverlaps` heißt im Editor
  **In Prüfung – Überlappungen**.
- Alignment-Snapshots mit Timing-Konflikten werden jetzt als diese Version
  gespeichert und gehen nicht mehr verloren.
- Der Snapshot setzt intern `AllowTimingConflicts=true`, damit die Review-Version
  entstehen darf.
- Normale Freigaben bleiben geschützt: `ReviewOverlaps → Reviewed` erfordert
  weiterhin die ausdrückliche `AllowTimingConflicts`-Bestätigung im API-Aufruf.
- `ReviewOverlaps` kann als Arbeitsversion geladen und dann in `InReview`
  überführt werden.

### Validierung

- Neuer Integrationstest:
  `VerifyOverlappingAlignmentSnapshotsLandInReviewCategoryAsync`.
- `dotnet build` für Server und Playback-Tests erfolgreich.
- Playback-Integrationstests vollständig erfolgreich.

## Enterprise-Ausbau Alignment-Evidenz 2026-08-30

### Ziel

- Basic-Pitch-Mutationen durch unabhängige Onset-Evidenz absichern.
- Separation nicht nur nach RMS/ASR, sondern nach Vocal-Recall und Leak bewerten.
- Doppelgesang sichtbar und konservativ behandeln.
- A/B-Modelle nachvollziehbar, aber standardmäßig inaktiv lassen.

### Umsetzung

- `vocal_onset_consensus(...)` erzeugt eine pitch-unabhängige
  Vocal-Envelope-Onset-Stimme. Basic-Pitch-Onset-Mutationen brauchen jetzt
  Zustimmung dieser unabhängigen Evidenzfamilie.
- `separation_quality_report(...)` ergänzt `vocal_recall` in Lyrics-Fenstern
  und `instrumental_leak_in_vocal_pauses`.
- `candidate_pitch_quality(...)` ergänzt `singer_context` mit Polyphonieanteil,
  Singer-Ambiguity, geschätztem Lead-Anteil und lead-gewichteter Qualität.
- Stage-Ranking nutzt `asr-plus-separation-quality-v1`: ASR bleibt Hauptscore,
  schlechter Vocal-Recall und Pause-Leak führen zu begrenzten Penalties.
- `a_b_summary` dokumentiert optionale Modelle mit Score, Recall, Leak und
  Singer-Ambiguity; `LRC_SEPARATOR_A_B_MODELS` bleibt standardmäßig leer.
- Neue Konfiguration: `LRC_ONSET_CONSENSUS_*`.

### Validierung

- `py_compile`: `basic_pitch_evidence.py`, `candidate_selection.py`,
  `pipeline.py`.
- 62 zielgerichtete Tests erfolgreich:
  `test_basic_pitch_evidence`, `test_candidate_selection`, `test_stem_roles`,
  `test_pipeline_phase_order`.
- Noch offen: repräsentativer E2E-A/B-Lauf mit aktivierten lokalen Modellen
  und anschließender Auswertung.

### Lokale A/B-Aktivierung

- `lyrics-word-aligner/.env` enthält jetzt gezielt
  `LRC_SEPARATOR_A_B_MODELS=melband_roformer_instvox_duality_v2.ckpt`.
- Begründung: Das Modell ergänzt die beiden Standardmodellfamilien am sinnvollsten
  und testet Lead/Backing-Trennung, ohne jeden Lauf um drei zusätzliche
  Separationen zu verlangsamen.
- `model_bs_roformer_ep_368...` ist gegenüber dem bereits aktiven
  `model_bs_roformer_ep_317...` nur minimal schlechter im Namen angegebenen SDR
  und wurde daher zunächst nicht aktiviert.
- `vocals_mel_band_roformer.ckpt` bleibt zunächst deaktiviert, weil mit dem
  Standard-Mel-Band-Modell bereits eine ähnliche Familie läuft.

## Nachkontrolle Version 16 / 17 2026-08-31

### Versionserklärung

- Der Forschungs-Schattenlauf erzeugt nur **eine** neue Version.
- Revision 17 (Job `dual-01a0597f…`) ist der echte Schattenlauf.
- Revision 16 ist eine Editor-Folgerevision der alten InReview-Basis aus Job
  `dual-01a05675…`, nicht ein zweiter Schattenlauf.
- Revision 16 war dadurch zeitweise neuer als der noch laufende Schattenlauf;
  nach Abschluss wurde Revision 17 als `Generated` ergänzt.

### Pitch-Befund

- Revision 16 enthält 9.716 Pitch-Noten im Dokument.
- Der Editor zeichnet Pitch jedoch aus `alignmentReportJson`; dieser Report ging
  beim Folgespeichern verloren, weshalb Version 16 scheinbar „keinen Pitch“ hatte.
- `UpdateLyricsVersionRequest` transportiert jetzt den Alignment-Report mit.
- Beim Speichern einer Folgerevision bleibt bzw. wird die Provenienz erhalten.

### Version-17-Befund

- Compressed-Word-Repair hat Zeile 1 diesmal **nicht** verändert.
- Der gute Reflow war im `pre_verification_stage_vocal_boundaries`-Report korrekt.
- Die anschließende Phoneme-CTC-Verifikation sah nur
  `mean_confidence=0.0585`, schrieb die Zeile aber trotzdem in die alte,
  zusammengepresste Geometrie zurück.
- Zeilen mit `stage_vocal_silent_prefix_original_start` werden jetzt von der
  IPA-Verifikation geschützt und nur als
  `protected-release-preserving-reflow` diagnostiziert.
- Der finale Audit markiert diese Reflow-Zeilen als `hard-constrained`.

### Validierung

- `pytest lyrics-word-aligner/tests/test_phoneme_ctc_aligner.py`: 60 Tests.
- Zielkombination (`phoneme_ctc`, `collapsed_lines`, `vocal_boundaries`):
  102 Tests erfolgreich.
- Server- und Desktop-Build erfolgreich.
- Playback-Integrationstests erfolgreich.

### Noch offen

## Abschluss der Korrektur 2026-09-01

### Reflow über mehrere Vocal-Inseln

- Version 18 presste Zeile 1 in die erste Vocal-Insel, obwohl die Phrase aus
  `44.63–45.51`, `45.82–46.62` und `46.95–48.99` besteht.
- `_recover_silently_placed_line` sammelt jetzt alle überlappenden
  Stage-Vocal-Inseln der Phrase und nutzt deren letztes Ende als gemessenes
  Release.
- Dadurch wird der Release-erhaltende Reflow auch ohne direkte
  `vocal_audio`-Referenz über die komplette Phrase verteilt.
- Neuer Regressionstest:
  `test_release_preserving_reflow_uses_all_islands_of_the_phrase`.

### Doppelte Schattenlauf-Versionen

- Reine Forschungs-Schattenläufe speichern den lokalen Editorstand jetzt nicht
  mehr automatisch vor dem Start.
- Nur Varianten mit Editor-Basis (`IncludeEditorBasis`) oder
  Editor-Guidance (`IncludeEditorGuidance`) erzeugen vorher einen Snapshot.
- Damit erzeugt ein reiner Schattenlauf wieder genau eine neue Version.

### Alignment-Report-Provenienz

- `UpdateLyricsVersionRequest` transportiert den technischen Alignment-Report.
- Beim Folgespeichern bleibt der vorhandene Report erhalten; ein explizit
  mitgesendeter Report ersetzt ihn.
- Dadurch zeigt der Editor Pitch weiterhin an, auch wenn man eine
  Alignment-Version als Editor-Draft weiterbearbeitet.

### Behobene Laufzeitfehler

- `Object of type bool is not JSON serializable`:
  `job_worker.py` serialisiert Status jetzt mit `default=str`.
- `'AnalysisCandidates' object has no attribute 'pairs'`:
  Pipelinezugriffe verwenden jetzt das tatsächliche Feld `stem_pairs`.
- Duplizierte Report-Validierung in `LyricsVersionRepository` entfernt.

### Validierung

- `tests/test_vocal_boundaries.py`: 23 Tests erfolgreich.
- Zielkombination `vocal_boundaries`, `line_transitions`,
  `basic_pitch_evidence`, `pipeline_phase_order`: 65 Tests erfolgreich.
- Nach Pipeline-Fix erneut `pipeline_phase_order` und `vocal_boundaries`:
  38 Tests erfolgreich.
- Server-, Contracts- und Desktop-Build erfolgreich, jeweils ohne Warnungen.
- Playback-Integrationstests vollständig erfolgreich.
- `git diff --check`: erfolgreich.

### Deployment

- Vor dem Neustart gab es keine aktiven/queued Aligner-Jobs.
- Aligner- und Basic-Pitch-Images wurden neu gebaut.
- Beide Container laufen über Compose; Health ist okay.
- Aligner: CUDA verfügbar, 1 GPU.
- Aktiv:
  `BASIC_PITCH_URL=http://basic-pitch-evidence:8090`,
  `LRC_BASIC_PITCH_ENABLED=true`,
  `LRC_SEPARATOR_A_B_MODELS=melband_roformer_instvox_duality_v2.ckpt`.
- Der alte manuelle Aligner-Container wurde durch den Compose-Container ersetzt.

### Noch offen

- Neuer reiner Forschungs-Schattenlauf für „Na gut dann nicht“:
  - genau eine neue Version,
  - Zeile 1 nahe Revision 12 halten,
  - `protected_release_preserving_reflow_lines=2`,
  - Pitch im Editor sichtbar,
  - kein `pairs`-/JSON-Serialisierungsfehler.
- Server ist aktuell nicht erreichbar (`127.0.0.1:5274`); vor dem Kontrolllauf
  muss er gestartet werden. Desktop ebenfalls neu starten, damit der
  Shadow-Save-Fix aktiv ist.

## Karaoke-Artikulationsfenster 2026-09-01

### Nachvollzogene Handkorrekturen bis 01:14

- Verglichen wurden die automatisch erzeugte Revision 22 und die manuell
  überarbeitete Revision 23 von „Na gut dann nicht“.
- Bei `Ich unterschreib` hatte das kurze Wort `Ich` den Anfang des folgenden,
  mehrsilbigen Wortes absorbiert. Der lokale IPA-Pfad lag mit
  `49.601–51.104` bereits sehr nahe an der Handkorrektur von `unterschreib`.
- Die manuell gesetzten Silben von `unterschreib` sind bewusst nicht
  lückenlos: Sie markieren die hörbaren Artikulationskerne und erzeugen so das
  gewünschte knackige Karaoke-Gefühl.
- Die folgende Zeile begann automatisch bei `53.220`, also noch im Ausklang
  der vorherigen Phrase. Der nächste echte Gesangseinsatz liegt bei etwa
  `53.780`; die Handkorrektur setzte ihn bei `53.820`.

### Implementierte Regeln

- Eine lokale IPA-Reparatur erkennt jetzt, wenn ein kurzes Vorgängerwort Zeit
  eines mehrsilbigen Nachfolgers absorbiert. Sie greift nur bei stabilen
  Folgeankern, passender Gesamtdauer und ausreichend sicherem IPA-Kandidaten.
- Mehrsilbige Wörter können ihre Silben nun aus den gemessenen Vokalkernen
  ableiten. Reale Artikulationspausen bleiben zwischen den Silben erhalten;
  der letzte Silbenausklang darf weiterhin bis zum Wortende reichen.
- Geschützte Release-Reflows behalten ihre Wortgeometrie, dürfen aber
  weiterhin Phonemdaten für diese internen Silbenfenster übernehmen.
- Die Stage-Vocal-Analyse erkennt einen Zeilenanfang innerhalb des Ausklangs
  der Vorgängerphrase als `predecessor-tail collision` und verschiebt ihn auf
  die nächste Gesangsinsel hinter einer echten Pause.

### Kontrollwerte

- IPA-Reparatur `Ich`: `49.301–49.541`.
- IPA-Reparatur `unterschreib`: `49.601–51.104`.
- Beispielhafte Artikulationsfenster `un / ter / schreib`:
  `49.601–49.822`, `50.443–50.603`, `50.763–51.104`.
- Trockentest der betroffenen Folgezeile: neuer Einsatz `53.780` statt
  `53.220`.

### Validierung

- Zielkombination `vocal_boundaries`, `syllables`, `phoneme_ctc_aligner`,
  `line_transitions`, `pipeline_phase_order`: 124 Tests erfolgreich.
- Vollständige Python-Testsuite des Aligners: 500 Tests erfolgreich.
- Desktop- und Server-Build: erfolgreich, jeweils ohne Warnungen.
- Editor-Core- und Playback-Integrationstests: vollständig erfolgreich.
- `git diff --check`: erfolgreich.
- Der laufende Aligner und die übrigen Container wurden für diese Änderung
  noch nicht neu gebaut oder gestartet.
