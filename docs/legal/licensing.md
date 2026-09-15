# Licensing and release policy

## Project license

Original Neon Stage source and website code are licensed under Apache-2.0 as stated in the root `LICENSE` and `NOTICE`. Redistributions must satisfy Apache-2.0 section 4, including preservation of the license, relevant notices and prominent change notices for modified files. The license grants no trademark rights in the Neon Stage name or logo.

Third-party licenses are independent of that choice. `THIRD_PARTY_NOTICES.md` is the human-readable register; release artefacts need a machine-generated inventory from their exact dependency lock state as well.

## Required release gate

- root `LICENSE` and `NOTICE` are present in source and binary distributions
- no secrets, `.env` files, local IP configuration, databases or user/event data
- no audio, stems, lyrics, cover art, model weights or generated alignment outputs
- NuGet lock/inventory includes transitive and runtime/native packages
- Python requirements are pinned to immutable versions/commits with hashes
- Unity package manifest is locked; Unity/package notices accompany the player
- native VLC notice, LGPL text and source/relinking information accompany each binary distribution
- AI model cards, revisions, licenses and source URLs are captured separately
- every downloaded CTC, ASR, phoneme and separation-model revision is recorded
  with its model card and kept outside application releases
- website Legal page and packaged application About/Legal view point to the matching release notices
- dependency vulnerability scan and build/test suite pass

## Known work before a public binary release

`lyrics-word-aligner/requirements.txt` currently installs Transformers from a moving Git branch. Pin it to an immutable reviewed commit or released version. Model files are intentionally ignored; provide a downloader that verifies checksums and shows the applicable model terms instead of committing weights.

The Sunnify integration downloads and executes a separate GPL-3.0 project. Keep that boundary explicit. Spotify API access does not grant rights to download or redistribute music. Operators must comply with service terms and local copyright law.

The product alignment path uses EasyAligner with German or English Wav2Vec2 CTC
models and optionally downloads Qwen, Whisper/stable-ts, phoneme, and
BS-Roformer weights. Library licenses do not relicense those weights. Record the
exact revision and terms before distributing an alignment image or model pack;
model files remain ignored and are never included in the Server, Editor, Stage,
or GitHub Pages artifacts.

## Maintaining the register

Run the repository license check before every release:

```bash
./scripts/license/check-release-tree.sh
```

Then review restored NuGet metadata, Python package metadata, `Packages/packages-lock.json`, native runtime contents and downloaded model cards. A clean script result is a guardrail, not a substitute for this artefact-level review.
