# Musical highlight timeline

## Purpose

Word and syllable coordinates are the timing authority. The shared
`StagePresentationEngine` may additionally turn trustworthy note evidence into
`ADVANCE`, `HOLD`, and `SNAP` segments so a held syllable does not look like a
uniformly moving progress bar. The editor preview, editor-driven Stage test,
live Unity Stage, and deterministic MP4 export use the same presentation code.

The current EasyAligner does not run a separate Basic Pitch service. New
alignments therefore fall back to their measured word and syllable windows
unless imported or older review data already contains usable note evidence.
Missing, sparse, or low-confidence notes never block playback or export.

## Timing layers

1. **Alignment:** EasyAligner supplies monotonic line, word, and syllable
   windows from the vocal signal.
2. **Presentation:** perceptual lead and vocal-release compensation adjust what
   the singer sees without rewriting the stored coordinates.
3. **Optional articulation:** existing note evidence may create short holds or
   attacks inside a word. Unsupported boundaries keep linear progress.
4. **Clock:** Unity derives live timing from the local DSP clock. Editor tests
   mirror the editor transport, while MP4 export uses the deterministic
   `frame / FPS` clock.

This separation is intentional: display tuning must not accumulate changes in
Enhanced LRC or make the Editor and Stage disagree about the canonical data.

## Current thresholds

`KaraokeHighlightTimelineOptions` centralizes the conservative defaults:

- minimum pause: 55 ms;
- legato tolerance: 35 ms;
- minimum attack distance: 45 ms;
- minimum voiced duration: 50 ms;
- minimum note confidence: 0.35;
- minimum pitch change: 2 semitones;
- maximum note-to-word offset: 80 ms;
- timeline version: 1.

Tiny gaps and isolated low-confidence events are ignored. If a timeline cannot
be justified, `KaraokeHighlightTimeline.Create(...)` returns no override and
the renderer uses ordinary word/syllable progress.

## Controls and diagnostics

- Server default: `Karaoke:EnableMusicalHighlightTimeline=true`.
- Editor preview override: `NEONSTAGE_MUSICAL_HIGHLIGHT=1`; use
  `0`, `false`, `no`, or `off` to disable it.
- `StagePresentationEngine.DescribeHighlightTimelines(...)` returns the
  renderer-independent segments for tests and diagnostics.
- UltraStar timing heritage remains protected and is not reshaped by this
  optional presentation layer.

Alignment reports can still contain note/pitch evidence for compatibility with
existing versions. It is diagnostic support rather than a second alignment
pipeline, and the editor retains it when a review revision is saved.

## Validation focus

Regression tests cover sustained syllables, real singing pauses, tiny gaps,
melismas, low-confidence outliers, Stage-test mapping, and consistency between
preview and runtime presentation. Listening review remains required because
separator leakage and backing vocals can make any acoustic boundary ambiguous.
