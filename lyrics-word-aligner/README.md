# Neon Stage Lyrics Word Aligner

GPU-assisted alignment service for turning audio plus line-timed lyrics into
reviewable word- and syllable-timed karaoke data:

```text
MP3 + synchronized LRC -> Enhanced LRC + alignment report + audio stems
```

The service is implemented with FastAPI, PyTorch, CUDA, source separation,
independent ASR verification, and forced alignment. It is designed for the
Neon Stage review workflow: automatic output is diagnostic material, not an
automatic claim that a song is ready for the stage.

## Pipeline overview

The default mode aligns small line windows. This keeps long songs, dense punk
vocals, and repeated choruses within a manageable model context. Full-song ASR
remains an independent check for coverage, ordering, missing lyrics, and
unexpected repetitions.

The pipeline can also align a complete song in one monotonic GPU pass. That
prevents overlapping LRC windows from assigning repeated text to the same
audio region. For long tracks or limited GPU memory, timed LRC input
automatically falls back to line windows.

Source separation has two explicit roles. General BS-/Mel-RoFormer candidates
produce an all-vocals **analysis stem** for ASR, alignment, Basic Pitch and
pYIN. A separately configured Karaoke RoFormer produces the immutable
**Stage vocal/instrumental pair** used for playback and export. An analysis
winner can never replace or hybridize the Stage pair. Separator models are
loaded sequentially and released before Qwen/Whisper, preserving the validated
8 GiB execution model.

`Qwen3-ForcedAligner-0.6B` supplies the primary acoustic word boundaries. Qwen
ASR, Stable-TS/Whisper, CTC phonemes, vocal activity, spectral attacks, Basic
Pitch and pYIN provide typed independent evidence. Basic Pitch can confirm an
existing word or syllable boundary but cannot create lyrics or infer singer
identity. Melismas remain multiple notes on one syllable; missing pitch never
penalizes screams, shouts, rap or spoken vocals.

```dotenv
LRC_ANALYSIS_SEPARATOR_ENABLED=true
LRC_ANALYSIS_SEPARATOR_MODELS=model_bs_roformer_ep_317_sdr_12.9755.ckpt,model_mel_band_roformer_ep_3005_sdr_11.4360.ckpt
LRC_STAGE_SEPARATOR_MODEL=mel_band_roformer_karaoke_aufr33_viperx_sdr_10.1956.ckpt
LRC_ORIGINAL_MIX_BLEND_MODE=fallback # off, candidate, fallback
LRC_BASIC_PITCH_ENABLED=true
LRC_BASIC_PITCH_USE_FOR_ALIGNMENT=true
LRC_EVIDENCE_FUSION_MODE=shadow      # select after corpus review
LRC_PYIN_ENABLED=true
```

Core alignment settings:

```dotenv
LRC_ALIGNMENT_MODE=line-windows       # or full-song
LRC_ENGINE_V2_MODE=select             # production; shadow or off for diagnostics
LRC_FULL_SONG_MAX_SECONDS=300
LRC_SECTION_PAUSE_GAP=7
LRC_SECTION_MAX_DURATION=45
LRC_SECTION_CANDIDATE_SECONDS=18,30,45
```

The editor also exposes a **reference-guided realignment** profile for songs
that already contain reliable manual corrections. Exact manual lines remain
immutable. Their measured start/end residuals calibrate only nearby untouched
lines, while outliers are rejected and the resulting candidate still has to
beat the normal alignment against the final vocal stem. The influence radius
and largest accepted calibration shift are configurable with
`LRC_EDITOR_GUIDANCE_MAX_DISTANCE` (45 seconds) and
`LRC_EDITOR_GUIDANCE_MAX_SHIFT` (1.5 seconds).

### Lyrics Engine v2

Engine v2 treats transcript recognition, word placement, and sung-word release
as separate problems. It keeps every alignment path immutable and compares:

- an anchor-free global XLSR/eSpeak phoneme path in the research profile;
- canonical LRCLIB text in local forced-alignment windows;
- canonical text projected onto a long-context Stable-TS transcript;
- the same projection using short, overlapping 12-second windows;
- independent CTC/MMS/SOFA refinements; and
- the historical sequential pipeline as a mandatory fallback.

Each line is scored against vocal activity, onset evidence, model reliability,
candidate agreement, geometry, and the original LRC cue. A Viterbi-style path
search prevents cross-line collisions. Large disagreements need confirmation
from two independent method families; energy-only placement cannot replace a
stronger forced alignment unless the old line is demonstrably outside the
vocal region.

`select` is the validated production mode. It applies only alternatives which
measurably beat the historical path and have sufficient independent acoustic
support. `shadow` records the complete decision report in `*.alignment.json`
without changing output, while `off` retains the legacy engine. Regardless of
the mode, the final Stage-vocal boundary, monotonic word geometry, and
within-lane overlap gates remain mandatory.

The design follows the same separation of concerns as
[WhisperX](https://github.com/m-bain/whisperX) (VAD, transcription, then forced
alignment) while retaining singing-specific candidate consensus. Note that the
current TorchAudio forced-alignment API is deprecated upstream; the CTC adapter
is isolated so it can be replaced without changing the engine contract.

If a full-song result scores poorly, the pipeline can compare full-song,
section-window, and line-window results. Only the highest-scoring candidate is
selected. The JSON report records `alignment_mode` and a `quality` object with
the score, quality tier, and `publishable` assessment. Geometry-only repairs
reduce the score because their boundaries were not confirmed acoustically.

An adaptive energy analysis of the isolated vocal stem first repairs collapsed
lines inside measured vocal activity (`vocal-activity-repair`). The explicitly
marked geometric fallback (`geometric-repair`) is used only when reliable
activity is unavailable. Experimental recovery of unanchored word fragments is
disabled unless `LRC_EXPERIMENTAL_ANCHOR_CONTEXT=true` is set.

## Requirements

- Docker with Docker Compose
- NVIDIA GPU with a supported driver
- NVIDIA Container Toolkit
- Sufficient disk space for the container image and downloaded model caches
- Approximately 8 GB of VRAM for the validated large-model configuration

The image itself contains no songs, lyrics, stems, model weights, or service
credentials. Model files are downloaded lazily into the mounted `models`
directory when a job first needs them.

### Concurrent singing voices (MedleyVox)

The optional MedleyVox adapter examines short windows around non-lexical
backing-vocal phrases such as `woho`/`lalala`, plus lines already assigned to a
secondary vocal lane in the editor. It separates the finalized vocal stem into
two anonymous voice candidates using GPU-bounded 3-second overlap-add chunks.
`shadow` mode writes two diagnostic FLAC files and report evidence without
changing lyrics. `promote` additionally moves only an explicit non-lexical
backing phrase to voice lane 2 when one separated acoustic component is both
strong and unambiguous. Ordinary words and manually reviewed lines are never
promoted by this heuristic. The persisted editor revision retains the two
candidate stems and the evidence used for every accepted or rejected proposal.

Separator outputs are anonymous and may otherwise swap between disjoint song
windows. Neon Stage keeps them attached to a stable singer identity with a
compact timbre fingerprint (MFCC distribution, spectral shape and median F0).
Voice-lane corrections saved by the editor become per-song reference anchors
on the next run. A two-thirds majority and a clear energy ratio are required;
ambiguous simultaneous singing remains review material rather than being
silently assigned to the wrong singer.

```dotenv
LRC_MEDLEYVOX_ENABLED=true
LRC_MEDLEYVOX_MODE=promote
LRC_MEDLEYVOX_MAX_WINDOW_SECONDS=12
LRC_MEDLEYVOX_MAX_WINDOWS=12
LRC_MEDLEYVOX_CHUNK_SECONDS=3
LRC_MEDLEYVOX_CHUNK_OVERLAP_SECONDS=1
LRC_MEDLEYVOX_PROMOTION_MIN_SCORE=0.42
LRC_MEDLEYVOX_PROMOTION_MIN_MARGIN=0.08
```

Only the pinned `multi_singing_librispeech/vocals.pth` checkpoint (about
233 MB) is downloaded, not the complete model repository. The official
MedleyVox research repository does not publish pretrained weights, so this
adapter uses the independently published `Cyru5/MedleyVox` checkpoint. It is
CC-BY-4.0 licensed; its exact revision and attribution are recorded in every
report and in the root `THIRD_PARTY_NOTICES.md`.

## Configuration

Create the local configuration file before starting the service:

```bash
cp .env.example .env
docker compose build
docker compose up -d
curl http://127.0.0.1:8081/health
```

Compose reads `lyrics-word-aligner/.env`. The real `.env`, `data`, and `models`
paths are ignored by Git. Never commit downloaded weights or alignment output.

The checked-in `.env.example` contains safe defaults for the validated RTX 4070
Laptop / 8 GB configuration. Smaller GPUs can use:

```dotenv
LRC_ASR_MODEL=Qwen/Qwen3-ASR-0.6B-hf
LRC_STABLE_TS_MODEL=turbo
LRC_STABLE_TS_CHUNK_SECONDS=20
```

Check the service and GPU runtime:

```bash
docker compose ps
docker compose exec lyrics-aligner python3 -c \
  "import torch; print(torch.__version__, torch.version.cuda, torch.cuda.is_available())"
```

## Submit a job

The asynchronous endpoint is recommended for editor and batch workflows:

```bash
curl -X POST http://127.0.0.1:8081/api/jobs \
  -F 'audio=@Demo.mp3' \
  -F 'lyrics=@Demo.lrc' \
  -F 'language=auto' \
  -F 'separate=true' \
  -F 'alignment_device=cuda'
```

The response contains a `job_id`. Poll it with:

```bash
curl http://127.0.0.1:8081/api/jobs/JOB_ID
```

The synchronous compatibility endpoint is also available:

```bash
curl -X POST http://127.0.0.1:8081/align \
  -F 'audio=@Demo.mp3' \
  -F 'lyrics=@Demo.lrc' \
  -F 'language=auto' \
  -F 'separate=true' \
  -F 'alignment_device=cuda'
```

Output is written below `data/output/<job-id>/` and can include:

- `*.word-synced.lrc`
- `*.alignment.json`
- `*.vocals.flac`
- `*.instrumental.flac`
- `*.stems.json`
- transcript verification and candidate diagnostics

Jobs run sequentially so several large models cannot exhaust a consumer GPU at
the same time.

## Recognize complete lyrics without an input transcript

The full-transcription endpoint is intended for review projects whose audio is
available but whose lyrics are missing or unusable:

```bash
curl -X POST http://127.0.0.1:8081/api/transcription-jobs \
  -F 'audio=@Demo.mp3' \
  -F 'language=auto' \
  -F 'separate=true' \
  -F 'alignment_device=cuda'
```

Poll `/api/jobs/JOB_ID` just like a regular alignment job. Output includes
`*.transcribed.lrc`, a plain transcript, and `*.transcription.json`. The
transcription is deliberately a candidate for manual review, not authoritative
published lyrics.

The worker runs these stages sequentially:

1. karaoke vocal separation;
2. Qwen3-ASR complete-text recognition;
3. independent Stable-TS `large-v3` transcription in overlapping 30-second
   windows;
4. Qwen3 Forced Aligner word timing when the Qwen transcript is selected;
5. monotonic, non-overlapping karaoke-line grouping.

A small configurable portion of the original mix is blended into the analysis
stem so backing and chorus vocals removed by the karaoke separator are still
available to ASR. Each job runs in a child process. Separator, Qwen ASR,
Stable-TS, and the forced aligner explicitly release allocator caches between
stages; child-process exit then releases all remaining RAM and VRAM before the
next queued job acquires the single processing slot.

Songs up to `LRC_TRANSCRIPTION_CHUNK_THRESHOLD_SECONDS` (300 seconds by
default) retain the complete-song Qwen path. Only longer recordings are
transcribed and forced-aligned in bounded windows. Their size and overlap are
controlled by `LRC_TRANSCRIPTION_CHUNK_SECONDS` (20) and
`LRC_TRANSCRIPTION_CHUNK_OVERLAP_SECONDS` (2); non-overlapping ownership
intervals discard duplicate words from adjacent windows.

If a complete-song Qwen request unexpectedly returns no text, full-text
recognition retries that song once in the same bounded windows. This fallback
covers dense tracks for which the isolated vocal stem contains clear vocals but
whole-track decoding still produces an empty result.

The same long-song boundary and empty-result fallback also apply to the regular
pipeline's independent Qwen transcript verification and audio-candidate
comparison. Adjacent text chunks are merged by their shared word sequence
before LRCLIB lyrics are compared, so the post-transcription alignment pass
cannot reintroduce the full-track GPU allocation or reject a healthy vocal stem
solely because whole-track decoding returned no text.

The server/editor wrapper downloads this initial LRC and immediately submits it
to the normal alignment pipeline. If the song already has LRCLIB or manually
edited lyrics, their exact spelling, capitalization, punctuation, and line
structure are retained. A global phonetic transcript match transfers the
full-recognition word scaffold to those canonical lines before the regular
word/syllable alignment runs. If fewer than 55% of canonical words can be
mapped, the workflow safely falls back to the acoustic transcript instead of
forcing unrelated lyrics onto the song. Override this guard with
`LRC_CANONICAL_TRANSFER_MIN_COVERAGE` when needed. Run the same workflow from
the command line:

```bash
./scripts/linux/recognize-song-lyrics.sh --audio '/library/Artist - Title.mp3'
```

## Alignment quality funnel

The regular aligner follows a strict broad-to-local order. Every generated
alignment report records the executed order under `pipeline_phase_order`; the
worker fails fast if a future code change enters these phases out of order.

1. prepare and select recognition audio;
2. collect independent Qwen and Stable-TS transcript evidence;
3. create the primary forced word alignment;
4. refine uncertain regions with CTC, MMS, EasyAligner, SOFA, and Stable-TS;
5. finalize the exact vocal/instrumental stem pair exposed to the editor and
   Stage, including locally recovered separator leakage;
6. score timing candidates against that final Stage vocal stem;
7. resolve macro onsets, chorus anchors, overlaps, and missing lyric regions;
8. apply late word geometry, sustain, and repetition rules;
9. preserve a human editor boundary unless an independent word model proves a
   materially different edge;
10. reprocess every sentence in a bounded IPA/CTC window and verify every word
    with independent onset, spectral-transition, or release evidence;
11. apply explicit source/non-lexical boundaries, enforce the final single-lane
    and exact-Stage-stem constraints, then run a read-only word-boundary audit;
12. derive syllable timing strictly inside the immutable word windows;
13. validate and export.

The detailed per-word result is available as `final_word_boundary_audit`.
`verified` means the final persisted edge still matches the sentence-local
phonetic and acoustic proof. `hard-constrained` means an explicit LRC boundary,
non-lexical vocalization, single-lane rule, or exact Stage stem safely overruled
the raw model edge. Remaining `unverified` words stay visible for review.

## Test without source separation

To verify forced alignment before downloading a large separation model:

```bash
curl -X POST http://127.0.0.1:8081/api/jobs \
  -F 'audio=@Demo.mp3' \
  -F 'lyrics=@Demo.lrc' \
  -F 'language=en' \
  -F 'separate=false' \
  -F 'alignment_device=cuda'
```

## Detect the first vocal entry

The preflight endpoint searches for the first known lyric line inside a broad
intro window. It deliberately ignores the incoming LRC timestamp and runs on
the GPU:

```bash
curl -X POST http://127.0.0.1:8081/analyze/vocal-start \
  -F 'audio=@Demo.mp3' \
  -F 'text=The quick brown fox jumps over the lazy dog.' \
  -F 'language=en' \
  -F 'separate=true' \
  -F 'alignment_device=cuda' \
  -F 'search_seconds=30'
```

`first_word_start` can be compared with the first timestamp from duration-
matched LRCLIB candidates. Candidates outside the configured tolerance are
discarded before the full word-alignment pipeline starts.

## ASR verification and prompts

Before forced alignment, `Qwen3-ASR-1.7B-hf` independently transcribes the
vocal stem. On the validated RTX 4070 Laptop, prompt-assisted inference peaked
at 3.87 GiB of allocated VRAM. The separator releases its CUDA cache before ASR
and alignment models are loaded sequentially.

The report records matched, substituted, missing, and unexpectedly repeated
words under `transcript_verification`. Disable this check with
`LRC_ASR_VERIFY=false`. Select a model and generation limit with
`LRC_ASR_MODEL` and `LRC_ASR_MAX_NEW_TOKENS`.

Qwen ASR and Stable-TS/Whisper receive a generated context hint by default. It
contains the project name, language, and a prioritized deduplicated vocabulary
of names, compounds, slang, and uncommon lyric words. The service deliberately
does not provide the complete lyrics in expected order because that could make
generative ASR anticipate or repeat chorus text.

```dotenv
LRC_ASR_PROMPT=true
LRC_ASR_PROMPT_MAX_CHARS=1600
LRC_STABLE_TS_PROMPT_MAX_CHARS=700
LRC_ASR_EXTRA_CONTEXT=
```

Forced aligners, CTC, MMS, EasyAligner, and SOFA continue to receive the exact
target text and do not need generative prompts. The report documents prompt
construction under `asr_prompt` without duplicating the full lyrics. Set
`LRC_ASR_PROMPT=false` for an A/B comparison.

Stable-TS uses Whisper `large-v3` in the validated configuration. A real
45-second window peaked at 6.26 GiB allocated and 6.62 GiB reserved VRAM. Set
`LRC_STABLE_TS_MODEL=turbo` when memory is tighter.

## Candidate audio selection

The existing single-stem pipeline remains the mandatory baseline. Additional
audio candidates are scored only when enabled, and the baseline wins ties or
when an alternative does not exceed the configured minimum improvement.

```dotenv
LRC_ALIGNMENT_CANDIDATES=true
LRC_ALIGNMENT_CANDIDATE_BLEND=true
LRC_ALIGNMENT_BLEND_RATIOS=0.10,0.15
LRC_ALIGNMENT_MIN_IMPROVEMENT=0.01
```

Optional candidates:

```dotenv
# Compare the original mix as a separate analysis candidate.
LRC_ALIGNMENT_CANDIDATE_ORIGINAL=false

# Enable only when this audio-separator model is available.
LRC_ALIGNMENT_CANDIDATE_ALTERNATIVE=true
LRC_ALIGNMENT_ALTERNATIVE_MODEL=model_mel_band_roformer_ep_3005_sdr_11.4360.ckpt

# Preserve temporary candidate WAV files for diagnostics.
LRC_ALIGNMENT_KEEP_CANDIDATES=false

# When a real alternative separator improves lyric recognition clearly, export
# its complementary vocal/instrumental pair for Editor and Stage as well.
LRC_STAGE_STEM_AUTO_SELECT=true
LRC_STAGE_STEM_MIN_IMPROVEMENT=0.05
```

Candidate scores use lyric coverage, matched-word share, and transcript
similarity. Their weights are configured with
`LRC_ALIGNMENT_WEIGHT_COVERAGE`, `LRC_ALIGNMENT_WEIGHT_MATCHING`, and
`LRC_ALIGNMENT_WEIGHT_SIMILARITY`. Failures in optional candidates do not fail
the job; the baseline remains available.

Original-mix blends are analysis-only. They can improve timestamps, but are
never exported as controllable karaoke audio. Playback stem selection is
restricted to complete, sample-aligned vocal/instrumental pairs from a real
separator model and is recorded in `stage_stem_selection` and the stem
manifest.

## Missing lyrics and repeated sections

After alignment, `vocal-energy-plus-independent-asr-gap-v2` searches the vocal
stem for activity islands not covered by any word window. Each island is
transcribed independently on the GPU without the song prompt.

- With a reliable LRC line anchor, only that line is re-aligned. A separate
  pickup pass handles short swallowed opening words.
- When a local transcript securely matches a known line or two-line block, a
  missing repetition may be reconstructed.
- Ambiguous or unique speech is never invented as lyrics. It remains in the
  review gate with its exact time range and ASR diagnostics.

Results appear under `missing_lyric_reanalysis` and `lyrics_completeness` in
the alignment report. Overlapping lyric lines are treated as conflicts and
must be repaired or reviewed.

## Syllable data

The GPU alignment determines acoustic word boundaries. Variant 1.2 adds an
independent IPA/CTC pass over short lyric-line windows, followed by a
class-aware micro-boundary pass. It searches a 2.5 ms grid around IPA phone
priors, combines multiple analysis-window sizes, and chooses a locally
monotonic path with phone-duration constraints. An IPA word onset may replace
the prior onset only when all of these gates pass:

- the phone path and the individual word exceed their confidence thresholds;
- the candidate remains within 140 ms of the trusted word window;
- an independent local energy/spectral measurement confirms the same edge;
- applying it preserves positive word duration and the single, non-overlapping
  karaoke lane.

Held endings use a separate targeted pYIN pass over the Stage vocal stem. It
can extend a connected voiced release, or conservatively shorten one only when
an earlier sustain estimate already exists. Exact frame-center timebase data,
micro-path decisions, rejected candidates, and movements against the immutable
Enhanced-LRC input are written to `*.alignment.json`.

The `research-shadow` profile is a clean-room comparison path. It retains only
the immutable source text, line order, and optional rough line cues from the
pre-align file. Before any model runs, it deletes enhanced word timestamps,
manual line ranges, editor syllables, hold/effect metadata, and all manual
authority flags. The resulting alignment is therefore independent from saved
editor corrections and is stored as a separate loadable version by the server.
The report records this guarantee under `alignment_profile` and
`research_shadow_input`, including the number of discarded timing records.

Two or more adjacent words which have collapsed to 90 ms or less are treated
as one local repair problem. They are expanded from the IPA path only when the
complete replacement lies between stable neighbouring anchors and at least
55% of that interval contains independently measured Stage-vocal activity. A
single short function word is never expanded by this rule. Remaining compressed
runs are exposed by the quality gate instead of being hidden by a high overall
coverage score.

The same pass also detects an unassigned hole of at least 160 ms between two
otherwise ordered words. It moves only the two inner word edges when the IPA
path reduces the hole to at most 100 ms, at least 55% of the old hole contains
independently measured vocal activity, and one unchanged outer word edge is a
reliable anchor. Consequently a sung transition is not left blank, while an
actual breath or instrumental pause is preserved. These decisions are reported
separately as `ipa_vocal_hole_repairs`.

Complete lines made exclusively from at least three repetitions of the same
short word unit receive an additional repetition-aware pass. Long-context
Stable-TS identifies the correct chorus occurrence, but every repeated word is
then re-anchored independently against a multiresolution onset measurement in
the Stage vocal stem. The line is changed atomically only when every onset has
stronger evidence than its ASR prior. This prevents identical calls from
borrowing duration from the preceding or following repetition. The report key
is `repeated_phrase_refinement`.

German syllables are post-validated phonetically because Pyphen describes
typographic hyphenation rather than sung nuclei. In particular, `au`, `ei`,
`eu`, `äu`, `ai`, and `ie` remain intact across a dictionary hyphenation edge;
for example, `Träum` is one sung syllable rather than `Trä-um`.

Each word can additionally contain `syllables`, `syllable_confidence`, and
`syllable_method`. A language-aware dictionary supplies the written syllables;
the IPA phone sequence places a new syllable at its sung consonant onset rather
than waiting for the vowel nucleus. A conservative multiband change-point pass
may refine that internal boundary further. Neon Stage uses syllables at a
confidence of at least 0.62 and otherwise falls back to word timing.

The promotion gates can be made stricter for diagnostics through
`LRC_PHONEME_PROMOTION_MIN_CONFIDENCE`,
`LRC_PHONEME_PROMOTION_MIN_EVIDENCE`, and
`LRC_PHONEME_PROMOTION_MAX_SHIFT` (seconds). Loosening them is not recommended
without comparing the generated alignment reports against manually reviewed
versions.

Set `LRC_MICRO_BOUNDARY_MODE=shadow` to record Variant 1.2 candidates without
changing timestamps, `select` to apply candidates which pass every gate, or
`off` to disable the pass. `LRC_MICRO_BOUNDARY_SEARCH_RADIUS` and
`LRC_MICRO_BOUNDARY_MIN_IMPROVEMENT` control its local search and acceptance
threshold. Human-readable version reports expose the same decisions and make
clear that movement from the input is a diagnostic measurement, not by itself
proof of better timing.

### Anchor-free global phoneme primary hypothesis

The `research-shadow` profile can use
`facebook/wav2vec2-xlsr-53-espeak-cv-ft` as its primary geometry source for
plain lyrics and LRC files whose line timestamps are not trustworthy. eSpeak
converts the known canonical words to IPA. XLSR calculates frame-level phone
posteriors in bounded, overlapping GPU chunks, and a single monotone CTC path
then places the complete phoneme sequence on the full track. Chunk context is
discarded before concatenation, so neither a chunk seam nor an input timestamp
becomes an artificial lyric anchor.

This path uses only lyric text and line order; it explicitly reports
`uses_input_timestamps: false`. Qwen, Stable-TS, local CTC and vocal activity
remain independent competitors and downstream validators. Variant 1.2 and
manual editor versions are not changed by this experimental architecture.

```dotenv
LRC_GLOBAL_PHONEME_MODE=primary   # primary, shadow, or off
LRC_GLOBAL_PHONEME_CHUNK_SECONDS=24
LRC_GLOBAL_PHONEME_CONTEXT_SECONDS=1.2
```

`primary` makes the global path a selectable, placement-capable Engine-v2
hypothesis in a research-shadow run. It deliberately does **not** make the raw
one-pass path the unconditional safety baseline: repeated verses and choruses
can be mapped to the wrong occurrence while retaining plausible-looking CTC
geometry. The established cascade remains the fallback, and the global path
wins a line only when independent candidate families or stronger acoustic
evidence support it. `shadow` computes diagnostics without making it
selectable, and `off` skips it. Disabling Engine v2 also keeps the global path
diagnostic-only because its safety gate would otherwise be absent. Long vowel
releases are still finalized later against the exact Stage vocal stem; the CTC
token span alone is not treated as a sung sustain.

## Supported alignment languages

`de`, `en`, `fr`, `es`, `it`, `pt`, `ru`, `zh`, `yue`, `ja`, `ko`

## Manual test interface

Open `http://127.0.0.1:8081/` after startup. The interface supports:

- selecting several audio and LRC files;
- pairing files by equal base name, for example `Demo.mp3` + `Demo.lrc`;
- per-job progress;
- downloading the enhanced LRC and JSON review report.

## Basic Pitch A/B prototype

The optional `basic-pitch` Compose service is an isolated CPU inference
sidecar. Start the regular stack with `docker compose up -d`; the aligner calls
the sidecar internally at `http://basic-pitch:8090`. Port `8091` is exposed on
the host for diagnostics and can be changed with `BASIC_PITCH_PORT`.

In the Lyrics Editor choose the alignment action and then **Spotify Basic
Pitch A/B prototype**. For the whole library the server selects at most five
songs and rejects every document with UltraStar timing heritage. Each song
produces two independent review versions:

- `basic-pitch-ab-control`: the existing standard path (A);
- `basic-pitch-ab-treatment`: B, derived directly from A without rerunning a
  stochastic ASR or forced-alignment model.

Basic Pitch does not replace lyric or phoneme alignment. The treatment uses
the decoded notes plus the model's frame-level vocal-onset peaks. It proposes
nearby line starts and final-note releases, always requiring an independently
measured vocal boundary which the instrumental stem cannot explain. At least
six consistent onset anchors spanning one minute can additionally establish a
robust linear timing drift; only matching, independently accepted lines may be
shifted as complete phrases. The JSON report records accepted and rejected
onsets, releases and drift diagnostics under `basic_pitch_evidence`; both
generated versions remain in review until auditioned and explicitly released.
An adaptive vocal range derived from robust decoded-note percentiles rejects
remote raw-onset harmonics without assuming a fixed male or female range.
For raw pitch onsets, the treatment searches up to 120 ms earlier for a
leakage-independent sibilant/plosive boundary so a word can start at its
leading consonant rather than at the later voiced vowel. Repeated exact lyric
lines also receive relative-pitch DTW fingerprints, group confidence and
outlier lines for chorus-placement review.

Existing internal word and syllable boundaries receive additional pitch-onset
diagnostics. They remain in `shadow` mode by default because a melodic note
change may be a melisma rather than a new lyric unit. Even in `select` mode a
boundary moves only when the pitch event is close to the existing boundary and
an independently measured vocal attack is not explained by the instrumental
stem. A sufficiently close event is recorded as `confirmed-existing` and can
later feed an editor confidence overlay without changing timing. Enable
mutation explicitly with `BASIC_PITCH_INTERNAL_WORD_MODE=select`
or `BASIC_PITCH_SYLLABLE_MODE=select` after auditioning a representative A/B
set.

The report also contains a compact `pitch_timeline` of MIDI note events mapped
to lyric lines. The desktop editor renders these events as a magenta, read-only
note overlay on the vocal waveform. `word_pitch_evidence` additionally records
voiced coverage, dominant MIDI pitch, pitch span and sustained-note evidence
for each covered word; `alignment_confidence` aggregates independent evidence
per line and marks large shifts or repetition outliers for review. These values
are suitable for editor visualization, melody comparison and future segmentation
work. They are deliberately labelled as quantized note data,
not a continuous F0 track: production-quality vocal pitch shifting will later
need a dedicated voiced/unvoiced F0 estimator and a formant-aware audio
processor; Basic Pitch can provide its musical note targets and segment map.

## CUDA troubleshooting

The container pins `onnxruntime-gpu==1.20.1` for the CUDA 12.8 / cuDNN 9 base
image. If `libcudart.so.13` appears after a dependency update, rebuild without
cache:

```bash
docker compose down
docker compose build --no-cache
docker compose up -d
```

Inspect the runtime providers:

```bash
docker compose exec lyrics-aligner python3 -c \
  "import torch, onnxruntime as ort; print(torch.__version__, torch.version.cuda); print(ort.__version__, ort.get_available_providers())"
```

## Limitations and review policy

Singing alignment is probabilistic. Implausible lines are marked `uncertain`
in the JSON report and intentionally emitted without word timestamps in the
LRC. Automatic output must be reviewed in the Neon Stage Lyrics Editor before
release. A quality or timing conflict is useful evidence for a human editor,
not a reason to fabricate certainty.

The architecture is informed by ideas used in Nightingale's lyrics pipeline,
but this service is an independent, reduced implementation. See the repository
`THIRD_PARTY_NOTICES.md`, `ACKNOWLEDGEMENTS.md`, and licensing documentation
before redistribution. Downloaded model weights remain subject to their own
model cards and license terms.
