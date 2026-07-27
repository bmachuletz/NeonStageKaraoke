# Lyrics Word Aligner

Schlanker Docker-Service für:

```text
MP3 + zeilensynchronisierte LRC -> Enhanced LRC mit Wortzeitstempeln
```

Standardmäßig wird der vollständige Liedtext in einem einzigen, monotonen GPU-Durchlauf gegen die Vocalspur ausgerichtet. Dadurch können wiederholte Textstellen nicht mehr durch überlappende LRC-Zeilenfenster derselben Audiostelle zugeordnet werden. Für zu lange Titel oder knappen GPU-Speicher bleibt bei zeitcodierten LRCs automatisch der bisherige Fenster-Modus als Fallback erhalten. Lead-Vocals werden optional mit einem UVR-Karaoke-Modell isoliert; die Wörter richtet `Qwen3-ForcedAligner-0.6B` aus.

Das Verhalten lässt sich konfigurieren:

- `LRC_ALIGNMENT_MODE=full-song` (Standard) oder `line-windows`
- `LRC_FULL_SONG_MAX_SECONDS=300`
- `LRC_SECTION_PAUSE_GAP=7` und `LRC_SECTION_MAX_DURATION=45`
- `LRC_SECTION_CANDIDATE_SECONDS=18,30,45` für den automatischen Vergleich mehrerer Absatzgrößen

Der JSON-Bericht enthält `alignment_mode` und ein `quality`-Objekt mit Score, Qualitätsstufe und `publishable`. Geometrisch reparierte Zeilen senken den Score, weil ihre Wortgrenzen nicht akustisch bestätigt sind.

Bei einem schlechten Vollspur-Ergebnis vergleicht die Pipeline automatisch drei Varianten: Vollspur, lokal begrenzte Gesangsabsätze und einzelne LRC-Zeilenfenster. Übernommen wird nur die Variante mit dem höchsten Quality-Score.

Eine adaptive Energieanalyse der isolierten Vocalspur repariert kollabierte Zeilen vorrangig innerhalb gemessener Gesangsaktivität (`vocal-activity-repair`). Erst wenn keine belastbare Aktivität vorhanden ist, greift die klar markierte geometrische Notlösung (`geometric-repair`).

Die isolierte Reparatur unverankerter Wortreste ist wegen möglicher Folgeverschiebungen noch experimentell und nur mit `LRC_EXPERIMENTAL_ANCHOR_CONTEXT=true` aktivierbar.

Vor dem Forced Alignment transkribiert `Qwen3-ASR-1.7B-hf` standardmäßig die Vocalspur unabhängig. Auf der vorgesehenen RTX 4070 Laptop mit 8 GB VRAM wurden beim realen promptgestützten Inferenztest 3,87 GiB Spitzenbelegung gemessen. Der Separator gibt seinen CUDA-Cache frei, bevor ASR und Aligner nacheinander geladen werden. Ein dynamischer Wortfolgenvergleich protokolliert Übereinstimmungen, Ersetzungen, fehlende Wörter und zusätzliche Wiederholungen unter `transcript_verification`. Abschalten lässt sich die Prüfung mit `LRC_ASR_VERIFY=false`; Modell und Generierungslimit sind über `LRC_ASR_MODEL` und `LRC_ASR_MAX_NEW_TOKENS` konfigurierbar. Für kleinere GPUs bleibt `LRC_ASR_MODEL=Qwen/Qwen3-ASR-0.6B-hf` verfügbar.

Stable-TS verwendet auf dieser GPU `Whisper large-v3`. Ein realer 45-Sekunden-Test
belegte maximal 6,26 GiB CUDA-Speicher (6,62 GiB reserviert) und lief ohne OOM.
Die Modelle werden strikt nacheinander geladen und wieder freigegeben. Konfiguriert
wird die Variante über `LRC_STABLE_TS_MODEL`; `turbo` bleibt die speichersparende
Alternative für GPUs mit weniger als 8 GB VRAM.

## Automatische ASR-Prompts

Qwen3-ASR und Stable-TS/Whisper erhalten standardmäßig automatisch erzeugte
Kontexthinweise. Verwendet werden Songname, Sprache und ein priorisierter,
deduplizierter Wortschatz aus Eigennamen, Komposita, Slang und seltenen
Lyrics-Begriffen. Die vollständigen Lyrics werden bewusst nicht in ihrer
Reihenfolge vorgegeben: Das würde ein generatives ASR bei Refrains zum
Vorwegnehmen oder Wiederholen von Text verleiten. Forced Aligner, CTC, MMS,
EasyAligner und SOFA arbeiten weiterhin mit dem exakten Solltext und benötigen
keine Prompts.

```bash
LRC_ASR_PROMPT=true
LRC_ASR_PROMPT_MAX_CHARS=1600
LRC_STABLE_TS_PROMPT_MAX_CHARS=700
# optional, z. B. Bandname oder ungewöhnliche Eigenschreibweise
LRC_ASR_EXTRA_CONTEXT="Bad Religion"
```

Der Bericht dokumentiert die Prompt-Erzeugung unter `asr_prompt`, ohne die
vollständigen Lyrics noch einmal in einem Prompt-Feld abzulegen. Für einen
A/B-Vergleich kann das Verhalten mit `LRC_ASR_PROMPT=false` deaktiviert werden.

## Start

Voraussetzungen: Docker, Docker Compose, NVIDIA Container Toolkit.

```bash
docker compose build
docker compose up -d
```

Beim ersten Auftrag werden die Modelle heruntergeladen und unter `./models` gespeichert.

## Aufruf

```bash
curl -X POST http://localhost:8080/align \
  -F 'audio=@Lied.mp3' \
  -F 'lyrics=@Lied.lrc' \
  -F 'language=de' \
  -F 'separate=true' \
  -F 'alignment_device=cpu'
```

Das Ergebnis liegt anschließend unter `data/output/<job-id>/`:

- `Lied.word-synced.lrc`
- `Lied.alignment.json`
- `Lied.vocals.flac`
- `Lied.instrumental.flac`
- `Lied.stems.json`

## Test ohne Vocal-Separation

```bash
curl -X POST http://localhost:8080/align \
  -F 'audio=@Lied.mp3' \
  -F 'lyrics=@Lied.lrc' \
  -F 'language=de' \
  -F 'separate=false' \
  -F 'alignment_device=cpu'
```

Damit lässt sich zuerst prüfen, ob das Qwen-Alignment in der konkreten Umgebung funktioniert, ohne das große UVR-Modell zu laden.

## Ersten Gesangseinsatz bestimmen

Die vorgeschaltete Analyse sucht die erste bekannte Lyrics-Zeile in einem breiten
Introfenster. Sie verwendet bewusst keinen LRC-Zeitstempel und läuft auf der GPU:

```bash
curl -X POST http://localhost:8081/analyze/vocal-start \
  -F 'audio=@Lied.mp3' \
  -F 'text=Ich weiß, ihr habt mich wirklich so vermisst' \
  -F 'language=de' \
  -F 'separate=true' \
  -F 'alignment_device=cuda' \
  -F 'search_seconds=30'
```

`first_word_start` kann anschließend mit dem ersten Textzeitpunkt aller gleich
langen LRCLIB-Kandidaten verglichen werden. Kandidaten außerhalb der festgelegten
Toleranz werden verworfen, bevor die vollständige wortgenaue Pipeline startet.

## Unterstützte Sprachen des Aligners

`de`, `en`, `fr`, `es`, `it`, `pt`, `ru`, `zh`, `yue`, `ja`, `ko`

## Wichtige Einschränkung

Die Ausgabe ist automatisiert und bei Gesang nicht garantiert fehlerfrei. Zeilen mit unplausiblen Ergebnissen werden im JSON-Bericht als `uncertain` markiert und in der LRC bewusst ohne Wortzeitstempel ausgegeben.

## Technische Herkunft

Die Architektur orientiert sich an der Lyrics-Pipeline von Nightingale, ist aber eine eigenständige, reduzierte Implementierung. Insbesondere werden die vorhandenen LRC-Zeitstempel als Zeilenfenster verwendet, statt nur den reinen Text des gesamten Songs auszurichten.

## Manuelle Weboberfläche

Nach dem Start ist die Testoberfläche unter `http://localhost:8080/` erreichbar.

- Mehrere Audio- und LRC-Dateien gleichzeitig auswählen
- Paarung über denselben Dateinamen ohne Erweiterung, z. B. `Song.mp3` + `Song.lrc`
- Fortschrittsanzeige je Job
- Download der wortgenauen LRC und des JSON-Prüfberichts
- Jobs werden absichtlich nacheinander verarbeitet, damit eine 8-GB-GPU nicht durch parallele Modelle überlastet wird


## CUDA-Fehler `libcudart.so.13`

Der Container pinnt `onnxruntime-gpu==1.20.1`, passend zum CUDA-12.8-/cuDNN-9-Basisimage.
Nach einem Update unbedingt ohne Cache neu bauen:

```bash
docker compose down
docker compose build --no-cache
docker compose up -d
```

Prüfen:

```bash
docker compose exec lyrics-aligner python3 -c "import torch, onnxruntime as ort; print(torch.__version__, torch.version.cuda); print(ort.__version__, ort.get_available_providers())"
```
# Silbengenaue Zusatzdaten

Die GPU-Ausrichtung bestimmt weiterhin die akustischen Wortgrenzen. Ab Version 1
enthält die erzeugte `*.alignment.json` in jedem Wort zusätzlich `syllables`,
`syllable_confidence` und `syllable_method`. Die Silbenfenster werden mit einem
sprachabhängigen Wörterbuch innerhalb des erkannten Wortfensters verteilt. Sie
sind damit feiner als Wort-Timing, aber ausdrücklich noch keine akustisch
ermittelten Phonemgrenzen (`acoustic_syllable_boundaries: false`). Neon Stage
verwendet sie ab einer Konfidenz von 0,62 und fällt sonst automatisch auf das
bewährte Wort-Timing zurück.
# Optionale Audio-Kandidatenauswahl

Die bestehende Einzelspur-Pipeline bleibt der Standard. Zusätzliche Audioquellen
werden nur bewertet, wenn `LRC_ALIGNMENT_CANDIDATES=true` gesetzt ist. Der
bisherige Kandidat bleibt der Fallback und gewinnt bei Gleichstand sowie dann,
wenn eine Alternative die konfigurierte Mindestverbesserung nicht erreicht.

Für Punk-/Rock-Diagnosen kann zunächst eine kleine Originalbeimischung getestet
werden:

```bash
LRC_ALIGNMENT_CANDIDATES=true
LRC_ALIGNMENT_CANDIDATE_BLEND=true
LRC_ALIGNMENT_BLEND_RATIOS=0.10,0.15
LRC_ALIGNMENT_MIN_IMPROVEMENT=0.01
```

Weitere optionale Kandidaten:

```bash
# Originalmix als eigener Vergleichskandidat
LRC_ALIGNMENT_CANDIDATE_ORIGINAL=true

# Nur mit einem bereits vorhandenen audio-separator-Modell aktivieren
LRC_ALIGNMENT_CANDIDATE_ALTERNATIVE=true
LRC_ALIGNMENT_ALTERNATIVE_MODEL=alternative-model.ckpt

# Kandidaten-WAVs für eine Diagnose behalten (standardmäßig false)
LRC_ALIGNMENT_KEEP_CANDIDATES=true
```

Die Bewertungsanteile stammen aus der vorhandenen ASR-Textgegenprobe:
Lyrics-Abdeckung, Anteil übereinstimmender Wörter und Textähnlichkeit. Sie sind
über `LRC_ALIGNMENT_WEIGHT_COVERAGE`, `LRC_ALIGNMENT_WEIGHT_MATCHING` und
`LRC_ALIGNMENT_WEIGHT_SIMILARITY` konfigurierbar. Der Report enthält die
Einzelwerte unter `alignment_candidate_selection`. Fehler optionaler Kandidaten
brechen den Job nicht ab; wenn keine Alternative zuverlässig bewertet werden
kann, läuft die bisherige Spur weiter.

## Zeilenfenster und fehlende Lyrics

Das eigentliche Forced Alignment arbeitet standardmäßig in kleinen
Zeilenfenstern. Ein Fenster enthält die Zielzeile mit kurzem Vor-/Nachlauf;
Vollspur-ASR dient nur als unabhängige Kontrolle von Textabdeckung, Reihenfolge
und Wiederholungen. Dadurch überlasten lange Songs oder viele Refrains nicht den
Modellkontext.

Nach der Ausrichtung sucht `vocal-energy-plus-independent-asr-gap-v2` im
isolierten Vocal-Stem nach Aktivitätsinseln, die von keinem Wortfenster bedeckt
sind. Jeder Treffer wird separat und ohne Lied-Prompt auf der GPU transkribiert:

- Liegt ein vertrauenswürdiger LRC-Zeilenanker im Bereich, wird nur diese Zeile
  erneut ausgerichtet. Kurze verschluckte Auftaktwörter erhalten einen eigenen
  Pickup-Pass.
- Passt das lokale Transkript sicher zu einer bekannten Zeile oder einem
  bekannten Zweizeilenblock, kann eine fehlende Wiederholung rekonstruiert
  werden.
- Unklare oder einzigartige Treffer werden nie als Lyrics erfunden. Sie bleiben
  mit exaktem Zeitbereich und ASR-Diagnose im Review-Gate.

Die Ergebnisse stehen im Alignment-Report unter `missing_lyric_reanalysis` und
`lyrics_completeness`.
