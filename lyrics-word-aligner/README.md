# Neon Stage Lyrics Aligner

Der Dienst besitzt bewusst nur noch zwei Produktwege:

1. **EasyAligner** richtet eine gewählte, kanonische Lyrics-Fassung auf dem
   Vocal-Stem aus. Dies ist der Standard für Deutsch und Englisch.
2. **Volltranskript** erkennt den Text aus der vollständigen Audiodatei und
   übergibt das Ergebnis anschließend an denselben EasyAligner.

Frühere konkurrierende Profile und Forschungsvarianten sind nicht mehr Teil des
Dienstes. Es gibt keine auswählbare `alignment_profile`-Option mehr.

## Start

```bash
cd lyrics-word-aligner
cp .env.example .env
docker compose up -d --build
curl http://127.0.0.1:8081/health
```

Benötigt werden Docker mit NVIDIA-GPU-Unterstützung und FFmpeg auf dem Host für
die aufrufenden Importskripte. Modelle werden im eingebundenen Verzeichnis
`./models` zwischengespeichert; Jobdateien liegen unter `./data/output`.

## EasyAligner

```bash
./scripts/linux/align-library.sh \
  --force \
  --library /pfad/zur/bibliothek \
  --match "Songdatei.mp3" \
  --lyrics-source /pfad/zur/gewaehlten.lrc \
  --reuse-stems
```

Der Dienst entfernt Satzzeichen und normalisiert den Text nur für den
technischen CTC-Pfad. Im Ergebnis bleiben die ursprünglichen, menschenlesbaren
Lyrics erhalten. Vorhandene Enhanced-LRC-Wortzeiten werden nicht als Wahrheit
übernommen; plausible Zeilenzeiten dürfen lediglich lokale Suchfenster gegen
globale Sprünge begrenzen.

EasyAligner verwendet:

- einen globalen, monotonen Wav2Vec2-CTC/Viterbi-Pfad;
- `jonatasgrosman/wav2vec2-large-xlsr-53-german` für Deutsch;
- `facebook/wav2vec2-base-960h` für Englisch;
- eine optionale lokale IPA-Prüfung schwacher Wortanfänge;
- eine adaptive All-Vocals-Nachmessung für Chorus-/Backing-Vocals, die der
  primäre Karaoke-Separator dem Instrumental zugeordnet hat;
- Vocal-/Instrumental-Kontrast für plausible Ausklänge;
- Silbenableitung erst nach den fertigen Wortfenstern.

Die zusätzliche BS-Roformer-Separation läuft nur, wenn der primäre Vocal-Stem
eine zeilenverankerte Passage mit sehr schwacher oder kollabierter CTC-Evidenz
enthält. Übernommen werden ausschließlich deutlich bessere lokale Messungen.
Zeitgleich erkannte Backing Vocals erhalten eine eigene Editor-Stimmspur; die
gespeicherten Stage-Stems werden dadurch nicht verändert.

## Volltranskript

```bash
./scripts/linux/recognize-song-lyrics.sh \
  --audio /pfad/zum/song.mp3 \
  --language auto \
  --no-canonical
```

Der Volltranskript-Worker erzeugt zuerst einen vollständigen Text- und
Zeitkandidaten. Danach ruft das Skript automatisch EasyAligner auf. Im Editor
ist dieser Weg als virtueller Eintrag **Volltranskript** in der Auswahl der
Lyrics-Quellen verfügbar. Er eignet sich, wenn kein Match existiert oder alle
Provider-Fassungen unplausibel sind.

## HTTP-API

- `POST /api/jobs`: Audio, `.lrc`, optional vorhandene Vocal-/Instrumental-Stems
- `POST /api/transcription-jobs`: Audio, optional kanonische `.lrc`
- `GET /api/jobs/{id}`: Status und Ausgabedateien
- `DELETE /api/jobs/{id}`: einzelnen Job abbrechen
- `POST /api/jobs/cancel-all`: alle wartenden/laufenden Jobs abbrechen
- `POST /align`: synchroner EasyAligner-Kompatibilitätsendpunkt
- `GET /health`: Bereitschaft

Alle Alignment-Jobs laufen isoliert in einem Workerprozess. Abbruch beendet den
aktiven Prozess; ein Containerneustart markiert unterbrochene Jobs als
fehlgeschlagen, statt sie unbemerkt erneut zu starten.

## Konfiguration

Die aktuellen Variablen stehen vollständig in [`.env.example`](.env.example).
Wesentlich sind:

- `LRC_EASYALIGNER_MODEL_DE`, `LRC_EASYALIGNER_MODEL_EN`
- `LRC_EASYALIGNER_PHONEME_VERIFY`
- `LRC_EASYALIGNER_BACKING_RECOVERY`, `LRC_EASYALIGNER_BACKING_RECOVERY_MODEL`
- `LRC_PHONEME_CTC_MODEL`
- `LRC_ASR_MODEL`, `LRC_STABLE_TS_MODEL`
- `LRC_STAGE_SEPARATOR_MODEL`
- `LRC_ALIGNER_DATA_PATH`, `LRC_ALIGNER_MODEL_PATH`

## Tests

```bash
cd lyrics-word-aligner
python3 -m unittest discover -s tests -p 'test_*.py'
```

Die Tests decken den EasyAligner-Kern, kanonisches Text-Mapping,
Volltranskription, Stems, Silben, Validierung, API-Kommandos sowie
Job-Abbruch/-Recovery ab.
