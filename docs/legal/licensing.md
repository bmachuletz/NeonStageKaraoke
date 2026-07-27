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
- the optional torchaudio MMS forced-alignment weights are recorded as
  CC-BY-NC-4.0 and excluded from commercial deployments without permission
- website Legal page and packaged application About/Legal view point to the matching release notices
- dependency vulnerability scan and build/test suite pass

## Known work before a public binary release

`lyrics-word-aligner/requirements.txt` currently installs Transformers from a moving Git branch. Pin it to an immutable reviewed commit or released version. Model files are intentionally ignored; provide a downloader that verifies checksums and shows the applicable model terms instead of committing weights.

The Sunnify integration downloads and executes a separate GPL-3.0 project. Keep that boundary explicit. Spotify API access does not grant rights to download or redistribute music. Operators must comply with service terms and local copyright law.

The optional `torchaudio.pipelines.MMS_FA` model is published under
CC-BY-NC-4.0. Its non-commercial restriction is a model-level constraint even
though torchaudio itself is open-source. Disable `LRC_MMS_VERIFY` or obtain
separate permission for deployments outside those terms.

## Maintaining the register

Run the repository license check before every release:

```bash
./scripts/license/check-release-tree.sh
```

Then review restored NuGet metadata, Python package metadata, `Packages/packages-lock.json`, native runtime contents and downloaded model cards. A clean script result is a guardrail, not a substitute for this artefact-level review.
