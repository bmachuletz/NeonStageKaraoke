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

Optional source separation isolates controllable karaoke vocals and
instrumental audio. `Qwen3-ForcedAligner-0.6B` supplies the primary acoustic
word boundaries. Qwen ASR, Stable-TS/Whisper, vocal activity, and optional
candidate stems provide independent evidence for difficult passages.

Core alignment settings:

```dotenv
LRC_ALIGNMENT_MODE=line-windows       # or full-song
LRC_FULL_SONG_MAX_SECONDS=300
LRC_SECTION_PAUSE_GAP=7
LRC_SECTION_MAX_DURATION=45
LRC_SECTION_CANDIDATE_SECONDS=18,30,45
```

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

The same long-song boundary also applies to the regular pipeline's independent
Qwen transcript verification and audio-candidate comparison. Adjacent text
chunks are merged by their shared word sequence before LRCLIB lyrics are
compared, so the post-transcription alignment pass cannot reintroduce the
full-track GPU allocation.

The server/editor wrapper downloads this initial LRC and immediately submits it
to the normal alignment pipeline. Run the same workflow from the command line:

```bash
./scripts/linux/recognize-song-lyrics.sh --audio '/library/Artist - Title.mp3'
```

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
```

Candidate scores use lyric coverage, matched-word share, and transcript
similarity. Their weights are configured with
`LRC_ALIGNMENT_WEIGHT_COVERAGE`, `LRC_ALIGNMENT_WEIGHT_MATCHING`, and
`LRC_ALIGNMENT_WEIGHT_SIMILARITY`. Failures in optional candidates do not fail
the job; the baseline remains available.

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

The GPU alignment determines acoustic word boundaries. Each word in
`*.alignment.json` can additionally contain `syllables`,
`syllable_confidence`, and `syllable_method`. A language-aware dictionary
distributes syllable windows inside the detected word interval. These are more
granular than word timing but are not yet acoustic phoneme boundaries
(`acoustic_syllable_boundaries: false`). Neon Stage uses syllables at a
confidence of at least 0.62 and otherwise falls back to word timing.

## Supported alignment languages

`de`, `en`, `fr`, `es`, `it`, `pt`, `ru`, `zh`, `yue`, `ja`, `ko`

## Manual test interface

Open `http://127.0.0.1:8081/` after startup. The interface supports:

- selecting several audio and LRC files;
- pairing files by equal base name, for example `Demo.mp3` + `Demo.lrc`;
- per-job progress;
- downloading the enhanced LRC and JSON review report.

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
