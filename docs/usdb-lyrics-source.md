# USDB lyrics-source integration

Neon Stage can use UltraStar DataBase (USDB) as the first timed-lyrics source
for request downloads and MP3/FLAC folder imports. It does not scrape audio,
does not use browser automation, and does not include any USDB data in this
repository or in releases. Operators remain responsible for the service terms
and for all rights covering downloaded lyrics.

## Pipeline order

The source order is deliberately fail-open:

1. adjacent UltraStar/LRC/TXT or embedded lyrics;
2. authenticated `usdb.animux.de` search and UltraStar TXT import when configured;
3. automatic `usdb.eu` fallback, version resolution, recording match, and import;
4. the existing LRCLIB matcher without changed scoring or semantics;
5. full vocal transcription when no usable lyrics exist;
6. EasyAligner separates the song and aligns the selected human-readable text
   through the same global German/English CTC path used by every import;
7. the existing review and release gate.

Any timeout, HTML/schema change, download error, malformed TXT, unsupported
UltraStar construct, ambiguous match, or duration mismatch leaves the target
LRC untouched and continues at step 3. Existing editor versions, released
lyrics, stage contracts, and media files are never overwritten by a failed
USDB attempt.

`FolderImportService` invokes `UsdbLyricsSourceService` directly. The wishlist
worker calls `POST /api/admin/lyrics/resolve-imported` after the selected audio
provider has placed the file in the configured library. `SongImportService`
uses the same service for a new editor song when automatic lyric lookup is
enabled. This keeps one matching and download implementation for all entry
points while preserving each entry point's established LRCLIB fallback.

The editor also exposes **Management → Check USDB for lyrics…** for an explicit
per-song lookup. The dialog starts immediately with the loaded song title,
keeps searching and version resolution on the server, and preselects the best
metadata match without importing it. Search creates only short-lived,
single-use selection tokens. **Cancel** changes nothing. **Select** downloads
and parses that exact resolved UltraStar version, performs the final duration
assessment, and stores its complete word/syllable hierarchy as a new **In
review** lyrics revision. Existing drafts and the published Stage revision are
preserved. The new revision is then loaded into the editor for review.

**Management → Settings… → Lyrics sources** manages both USDB endpoints and
the optional Animux account. The password field is write-only: the API returns
only a `has password` flag. **Test Animux connection** verifies the current
server-side credentials without exposing them to the editor. The same dialog
also groups library, connection URL, and download-provider settings.

## Components

- `AnimuxUsdbClient` maintains an in-memory authenticated cookie session,
  searches the classic song-list form, and downloads the selected TXT through
  the site's `gettxt` form. Credentials and cookies are never logged or cached.
- `PreferredUsdbClient` places Animux results first and appends `usdb.eu`
  fallback candidates. Thus automatic imports can continue after a broken
  Animux item, while the editor still exposes the exact source being selected.
- `UsdbClient` performs the `usdb.eu` JSON search, resolves numeric version links, maintains
  one cookie-backed download session, waits for the server-side countdown, and
  validates TXT or ZIP responses. Successful TXT files are cached outside Git.
- `UsdbSongMatcher` normalizes punctuation and diacritics, compares title and
  contributing artists, checks live/remix/edit markers and optional year, and
  compares the UltraStar `#END` media duration against both the original and a
  virtually edge-trimmed imported recording. The final sung note is used only
  as a conservative fallback for legacy charts without `#END`.
- `UltraStarLyricsImporter` remains the only UltraStar parser. `#BPM`, `#GAP`,
  absolute/relative beats, word boundaries, and note-level syllables are
  converted to the normal editor hierarchy. The shared enhanced-LRC exporter
  carries those syllables as strong, non-manual references into the aligner.
- `UsdbLyricsSourceService` coordinates candidates, records explicit rejection
  reasons, writes an accepted LRC atomically, and contains all failure handling.
- `UltraStarLyricsImporter` retains the source hierarchy for editor import and
  converts it to Enhanced LRC for automated song/folder imports. EasyAligner
  keeps the visible text but deliberately measures fresh word and syllable
  windows instead of selecting a separate trusted-chart pipeline.
- `UsdbEditorLyricsService` owns the explicit editor search, recommended-result
  ranking, single-use selection tokens, and non-destructive version creation.

No session cookie or downloaded lyric content is written to application logs.
Downloads are serialized to avoid bursts, successful payloads are cached, and
failed version IDs receive a short negative cache so a batch does not hammer a
broken item repeatedly.

## Configuration

Defaults live under `Usdb` in `src/Karaoke.Server/appsettings.json` and can be
overridden with normal ASP.NET Core environment variables:

```text
Usdb__Enabled=true
Usdb__BaseUrl=https://usdb.eu
Usdb__RequestTimeoutSeconds=25
Usdb__DownloadWaitSeconds=21
Usdb__MaximumCandidates=5
Usdb__MaximumDurationDifferenceSeconds=10
Usdb__MinimumTitleSimilarity=0.90
Usdb__MinimumArtistSimilarity=0.82
Usdb__SuccessfulCacheHours=168
Usdb__FailureCacheMinutes=30
Usdb__CachePath=/private/persistent/data/usdb-cache
Usdb__Animux__Enabled=true
Usdb__Animux__BaseUrl=https://usdb.animux.de
Usdb__Animux__Username=your-account-name
Usdb__Animux__Password=your-private-password
```

Docker Compose maps the cache to `/data/usdb-cache`. The cache is disposable;
it must not be committed or packaged. Keep Animux credentials only in the
untracked `.env` file (or a private AppImage environment file). With blank
credentials Neon Stage uses `usdb.eu`; set `USDB_ENABLED=false` to bypass all
USDB sources completely.

Native editor changes are stored beside the SQLite database in
`*.usdb-providers.json` with user-only permissions on Linux/macOS. Deployment
environment variables remain authoritative and make the corresponding dialog
fields read-only. Never commit this private runtime file.

## Verification

```bash
dotnet build Karaoke.slnx --no-restore
dotnet run --project tests/Karaoke.Editor.Core.Tests/Karaoke.Editor.Core.Tests.csproj
dotnet run --project tests/Karaoke.Server.PlaybackTests/Karaoke.Server.PlaybackTests.csproj
bash -n scripts/linux/process-wishlist.sh
```

The tests use mocked HTTP handlers. They cover Animux login/list/TXT requests,
provider preference and fallback, `usdb.eu` search JSON, numeric version links,
the session/countdown sequence, TXT parsing, relative timing, decimal
BPM, GAP, syllable preservation, matching a close recording, rejecting a wrong
duration, and leaving the regular fallback open after an HTTP failure. They do
not download copyrighted lyrics during CI.

## Known brittle points and TODOs

- Both sites expose web behavior rather than a versioned API contract. The
  Animux request flow was independently implemented after studying the
  GPL-3.0-only [USDB Syncer](https://github.com/bohning/usdb_syncer); no Syncer
  source code or executable is incorporated into Neon Stage.
- USDB's search JSON and download countdown are public web behavior, not a
  versioned API contract. A future markup or endpoint change should fail into
  LRCLIB; update `UsdbClient` and its captured mock fixtures when verified.
- Legacy UltraStar files without `#END` still require the conservative final-
  note duration check. Uncertain matches are never accepted automatically.
- Variable-BPM UltraStar files and P1/P2 duet directives are intentionally
  rejected until the importer can preserve their semantics without loss.
- Edition, album, language, and year metadata are not consistently available.
  They refine a decision but never compensate for a poor title, artist, version,
  or duration match.
- If USDB introduces a documented API, replace the HTML/version resolver and
  countdown adapter while keeping `IUsdbClient`, matcher, and fallback contract.
