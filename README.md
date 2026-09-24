<div align="center">

# NEON STAGE

### Local-first karaoke that feels like a game

Unity stage · Mobile guest portal · Events · GPU lyrics alignment · Millisecond editor

</div>

Neon Stage is a self-hosted karaoke system. Guests join from an event QR code, search the released library, manage the queue, request missing tracks, and send live reactions to the singer. The Unity stage mixes instrumental and vocal stems and renders word- and syllable-timed lyrics. The desktop editor provides sample-stable playback and detailed manual review.

## What works today

Neon Stage is already an end-to-end system rather than a standalone lyrics
player. A single server coordinates every screen while media remains on the
operator's machine.

| Area | Current capability |
|---|---|
| Party operation | Multiple simultaneous Stages with independent queues/playback, planned events, invitation links and QR codes, mobile guest portal, released-library search, song requests, and live reactions |
| Stage | Unity 6 player for Linux, Android/ARM32, Windows x64, and macOS ARM64 with a touch-friendly image launcher, Direct Stage, synchronized stems, video backgrounds, reactive shaders, word/syllable highlighting, and DSP-clock timing |
| Lyrics production | Avalonia editor with waveform editing, loops, undo/redo, version/review workflow, Enhanced LRC and UltraStar import, live windowed Stage testing, and deterministic MP4 export |
| Song preparation | MP3/FLAC folder import, preserved original masters, portable song packages, LRCLIB/USDB source selection, full transcription fallback, vocal separation, and EasyAligner word timing |
| Automation | German and English global CTC models, adaptive backing-vocal recovery, batch realignment, quality gates, technical/human lyric views, and explicit release approval |
| Online MVP | Self-hosted LiveKit rooms with one Singer location and multiple Listener stages, short-lived role-scoped tokens, music plus up to two microphone inputs, and a single-host Docker stack |

Automatically prepared songs always enter review. Nothing becomes visible to
guests or the Stage until an operator explicitly releases a lyrics version.

## Get a running system quickly

### 1. Start the server and web portals

The shortest reproducible path uses Docker Compose. Docker, Git, an existing
host directory for the private karaoke library, and a writable data directory
are sufficient for the server and both web portals.

```bash
git clone https://github.com/bmachuletz/NeonStageKaraoke.git
cd NeonStageKaraoke
cp .env.example .env
mkdir -p data
# Edit .env and set at least:
# KARAOKE_LIBRARY_PATH=/absolute/path/to/your/karaoke/library
# KARAOKE_DATA_PATH=./data
# NEON_STAGE_PUBLIC_URL=http://YOUR-LAN-IP:5274
./scripts/containers/create.sh server
./scripts/containers/manage.sh status
curl http://127.0.0.1:5274/api/health
```

Open `http://SERVER:5274/admin.html` to prepare or activate an event. The guest
portal is served from `http://SERVER:5274/`. Use the server's LAN address in
`NEON_STAGE_PUBLIC_URL` when phones need to open the generated QR invitation.
Spotify, Qobuz, USDB credentials, CUDA alignment, and Online-Karaoke are
optional; the server starts without them.

The container helper can create or update the server, CUDA EasyAligner and
single-host LiveKit stacks together while preserving all persistent data:

```bash
./scripts/containers/create.sh             # first start of all three stacks
./scripts/containers/update.sh             # pull/rebuild and replace all three
./scripts/containers/update.sh server      # update only one stack
./scripts/containers/manage.sh logs server --follow
```

See [`scripts/containers/README.md`](scripts/containers/README.md) for restart,
stop, status, dry-run and managed-TLS variants.

### 2. Add the Stage and editor

Use reviewed artifacts from [GitHub Releases](https://github.com/bmachuletz/NeonStageKaraoke/releases)
when available, or build the clients from this checkout. A local Unity 6
installation is required only for building the Stage.

```bash
# Native .NET applications
./scripts/linux/build.sh
./scripts/linux/start-desktop.sh http://127.0.0.1:5274

# Unity Stage
./scripts/linux/build-unity-stage-linux.sh
./scripts/linux/start-unity-stage.sh http://127.0.0.1:5274
```

For Android/ARM32 use `./scripts/linux/build-unity-stage-android.sh`. Complete
Linux AppImages can be produced with `./scripts/build-appimages.sh`; add
`--skip-stage` on a machine without Unity. Existing complete song packages can
be imported and played without the CUDA stack. Preparing or realigning new
material requires the separately managed aligner described below.

> Status: active development. This is not yet a polished end-user release.

> **Development disclosure:** A large part of Neon Stage has been created
> through AI-assisted “vibe coding”: the product direction, requirements,
> testing, and acceptance decisions are human-led, while substantial portions
> of implementation and documentation were produced collaboratively with AI
> coding tools. Contributions and careful technical review are very welcome.

If Neon Stage is useful to you and you would like to support its continued
development: [Support Neon Stage on Ko-fi](https://ko-fi.com/Z6Q023YEX5).

## Screens

These are real captures of the current Unity stage and Avalonia editor running
against an isolated synthetic demo library. The test audio was generated
locally, the cover uses project-owned artwork, and the only lyric-like text is
the pangram “The quick brown fox jumps over the lazy dog.” No real songs,
artist material, production library data, or real lyrics are shown.

![Neon Stage Unity stage demo with safe placeholder lyrics](site/assets/stage-demo.png)

![Neon Stage Lyrics Editor demo with safe placeholder lyrics](site/assets/editor-demo.png)

## Architecture

```text
Guest phones ── HTTP/SSE ──▶ ASP.NET Core server ── state/audio ──▶ Unity stage
                                  │                                      │
                                  ├── SQLite events, queue and reviews    └── DSP audio + lyric FX
                                  ├── local media library
                                  └── CUDA alignment service ◀──────── Lyrics editor
```

| Component | Technology | Location |
|---|---|---|
| Server and web portals | ASP.NET Core / .NET 10 | `src/Karaoke.Server` |
| Stage | Unity 6 | `src/Karaoke.Stage.Unity` |
| Lyrics editor | Avalonia / .NET 10 | `src/Karaoke.App.Desktop` |
| Alignment service | Python, FastAPI, PyTorch, CUDA | `lyrics-word-aligner` |
| Lyrics matcher | .NET CLI | `LrcMatcher` |

## How server and clients work together

The ASP.NET Core server is the single source of truth. It owns event/session state, invitations, queue order, playback state, review metadata, and paths into the operator's local media library. Clients never ship with songs and do not need direct filesystem access.

1. **Create and publish a Stage.** An administrator prepares a Stage in the editor, optionally uploads its launcher image, and publishes it. Publishing does not replace another Stage: the server can run multiple independent queues and playback sessions at once. The permanent Direct Stage remains available for immediate local use.
2. **Join from a phone.** A guest opens `/e/{invite-token}`. The responsive portal resolves that token through the server, stores only the guest's chosen display name locally, and queries the released song catalog.
3. **Build the queue.** Search, enqueue, reorder, remove, and request operations are HTTP calls scoped to the resolved event. Server-side validation prevents unreleased or incomplete songs from entering stage playback.
4. **Drive the stage.** On startup Unity presents the Direct Stage and all published Stages as image cards. After selection, every queue, playback command, control lease, QR code, reaction and Online room is scoped to that Stage ID. Instrumental and vocal files are decoded locally by Unity and synchronized against its DSP clock; lyric progress uses that same clock rather than network request timing.
5. **Send audience reactions.** Guest phones post a small reaction type (`heart`, `smile`, `like`, `clap`, or `fire`). The stage fetches the event's reaction stream and renders font-independent animated graphics. No emoji font is required on Android or Linux.
6. **Prepare new material.** The request worker downloads authorized source material, matches lyrics, and sends audio plus lyrics to the CUDA alignment container. Generated LRC, alignment diagnostics, and stems remain in the local library, outside application packages.
7. **Review and release.** The editor reads server metadata and local audio streams, caches temporary editing audio on the workstation, and saves versioned lyric documents back to the server. Re-alignment can target one song or the full library. Only an explicitly released version becomes visible to the Unity stage.
8. **Propagate changes.** Server-Sent Events notify portals and the editor about library and request changes. Incremental refreshes preserve the current editor selection and timeline work.

```text
Admin portal ── create/activate event ──┐
Guest portal ─ search/queue/react ─────┼──▶ Server API + SQLite + local library
Lyrics editor ─ review/release ────────┘                │
                                                       ├──▶ Unity stage (state, lyrics, covers, audio streams)
CUDA aligner ◀── jobs from server/scripts ──────────────┘
```

Network latency can delay a command reaching the stage, but it does not continuously drive lyric highlighting: after media is prepared, Unity derives audio and lyric position from its local synchronized playback clock.

## Local development workflow

Requirements: .NET 10, FFmpeg, LibVLC, Unity 6 for stage builds, Docker, and optionally NVIDIA Container Toolkit plus a CUDA GPU.

```bash
./scripts/linux/build.sh
./scripts/linux/start-server.sh /path/to/karaoke/library
./scripts/linux/start-desktop.sh http://127.0.0.1:5274
./scripts/linux/start-unity-stage.sh http://127.0.0.1:5274
```

Guest portal: `http://SERVER:5274/`  
Administration: `http://SERVER:5274/admin.html`

### Docker Compose server

The server image contains the application binaries, web assets, and the small
EasyAligner client workers used for re-alignment and full transcription. The
CUDA models and the aligner service itself remain in the separate
`lyrics-word-aligner` container. The media library and SQLite state stay on the
host as bind mounts.

```bash
cp .env.example .env
# Edit .env: set the library/data paths; integrations are optional
docker compose up -d --build server
docker compose ps
curl http://127.0.0.1:5274/api/health
```

`KARAOKE_LIBRARY_PATH` is mounted read/write at `/library`, while
`KARAOKE_DATA_PATH` stores the database and Spotify authorization token. The
secret `.env` file is ignored by Git. Set `LRC_ALIGNER_URL` to
`http://host.docker.internal:8081` when using the separately published CUDA
aligner on the same Linux host. The editor connects by setting
`KARAOKE_SERVER=http://SERVER:5274`.

This is intentionally the server/API deployment: library editing, events,
queues, wishes, web portals, playback coordination, Spotify catalog access,
re-alignment, and full transcription run in the container. Alignment requires
the separately running CUDA aligner configured through `LRC_ALIGNER_URL`.
MP3/FLAC-folder ingestion and request download processing remain host/worker
jobs because their downloader and matching toolchains are not release payloads.
Run the documented worker scripts against the container URL for those jobs.

### Optional Online-Karaoke with LiveKit

The Online-Karaoke MVP connects Stage devices at different locations. One
Stage joins as **Singer** and publishes the locally mixed song plus up to two
microphone inputs; any number of **Listener** stages receive separate music,
original-vocal and live-microphone tracks without audible local playback. The server issues short-lived,
role-scoped LiveKit tokens and guarantees that only one Singer owns a room.

1. Start the documented [single-host LiveKit stack](deploy/livekit/README.md),
   or use an existing LiveKit deployment.
2. Open **Settings → Online-Karaoke** in the desktop editor, enter the WSS URL,
   API key, API secret, and room prefix, then use **Test LiveKit connection**.
3. Create an event as an **Online stage** (offline is the default), choose a
   stage password, optionally enable the all-site pause conversation, and
   activate it.
4. On every Stage device, press `F8` or click/tap the broadcast microphone
   icon, select the advertised stage, enter its password, and join. Roles then
   follow the location GUID in the queue automatically; the first queued song
   gives its location the controls but waits for Play.

The broadcast brackets are gray while offline and colored after a successful
connection. LiveKit credentials remain server-side; they are never sent to a
Stage. If an Android karaoke box exposes two physical radio microphones as one
multichannel system input, Neon Stage preserves that complete input. If the OS
exposes two separate microphone devices, both are captured and mixed with
headroom. A Singer can optionally lock the Listener mix and prescribe the
remote microphone volume from the mobile song page. In pause-conversation
mode, every site publishes only its microphone while no song is playing; all
Listener microphones are unpublished immediately on Resume. See the [Online-Karaoke architecture and operating guide](docs/online-karaoke.md)
for ports, audio routing, security, logs, and current MVP limits.

### Configure Spotify Web API access

Spotify integration is optional, but it is not anonymous. Search uses Spotify's
official Web API with an application access token, while connecting the
operator's account uses Authorization Code flow with the
`playlist-modify-private` scope to maintain the private Neon Stage request
playlist. You must supply a Client ID, Client Secret, and registered Redirect
URI to the server.

1. Sign in to the [Spotify for Developers Dashboard](https://developer.spotify.com/dashboard).
2. Choose **Create app**, enter an app name and description, accept Spotify's
   Developer Terms, and create the app.
3. Open the app's settings and add this development Redirect URI exactly:

   ```text
   http://127.0.0.1:5274/api/spotify/callback
   ```

   Spotify requires an exact match. For plain HTTP it permits explicit
   loopback addresses such as `127.0.0.1`; `localhost` is not accepted. For a
   remotely hosted server, register the actual HTTPS callback instead, for
   example `https://karaoke.example.com/api/spotify/callback`.
4. Copy the app's Client ID and Client Secret into the untracked root `.env`:

   ```dotenv
   SPOTIFY_CLIENT_ID=
   SPOTIFY_CLIENT_SECRET=
   SPOTIFY_REDIRECT_URI=http://127.0.0.1:5274/api/spotify/callback
   ```

5. Restart the server. On the server computer, open
   `http://127.0.0.1:5274/` and choose **Connect Spotify** once. The callback
   exchanges the authorization code server-side and stores the resulting token
   below `KARAOKE_DATA_PATH`, never in a client application.

Keep the Client Secret and the generated `spotify-connection.json` private.
Never put either value into Unity, the mobile portal, a Docker image, logs, or
Git. Spotify apps start in Development Mode and remain subject to Spotify's
current user and quota restrictions. See Spotify's official
[app registration](https://developer.spotify.com/documentation/web-api/concepts/apps),
[Redirect URI](https://developer.spotify.com/documentation/web-api/concepts/redirect_uri),
and [Authorization Code](https://developer.spotify.com/documentation/web-api/tutorials/code-flow)
documentation.

### Optional Qobuz purchase-download provider

Qobuz can replace the YouTube/Sunnify download step when an official Qobuz
integration is configured. The combined request search marks every result as
Spotify or Qobuz; Qobuz results also show the catalog price and maximum audio
quality when those fields are returned by the account's API response. Directly
selected Qobuz results retain their catalog track ID through the worker. Prices
are informational: Neon Stage never purchases a track automatically.

Configure the plugin in **Management → Configure download provider…** in the
Lyrics Editor, or provide `QOBUZ_*` values in the untracked `.env`. Qobuz API
integration access is not an anonymous public credential: contact
[`api@qobuz.com`](mailto:api@qobuz.com) for the current partner onboarding and
documentation. Neon Stage needs an App ID, App Secret, and authorized User Auth
Token; it never asks for or stores a Qobuz account password.

The default format is lossless CD-quality FLAC. Matcher, aligner, library, and
stage consume FLAC directly, while MP3 320 and Hi-Res FLAC remain selectable.
The provider requests only Qobuz `intent=download` URLs authorized for the
configured account. It does not turn subscription streams into library files,
extract application keys, or bypass purchase entitlements. If Qobuz rejects a
download, the request remains open and is not silently routed to YouTube.
The Editor writes secrets only when it reaches the server on the same host or
through HTTPS; remote plain-HTTP configuration is rejected.

See [`docs/wishlist-worker.md`](docs/wishlist-worker.md) for worker and security
details. Qobuz currently documents normal file downloads for purchased tracks
in its [official download guide](https://help.qobuz.com/en/articles/369151-tutorial-how-to-download-without-qobuz-downloader).

## Alignment container

```bash
cd lyrics-word-aligner
cp .env.example .env
docker compose build
docker compose up -d
curl http://127.0.0.1:8081/health
```

The first job downloads model data into the ignored `lyrics-word-aligner/models` directory. Jobs run sequentially to protect consumer GPUs from concurrent model loads.

Align one file through the service:

```bash
curl -X POST http://127.0.0.1:8081/api/jobs \
  -F 'audio=@Song.mp3' \
  -F 'lyrics=@Song.lrc' \
  -F 'language=auto' \
  -F 'separate=true' \
  -F 'alignment_device=cuda'
```

Align a library or one matching song:

```bash
./scripts/linux/align-library.sh --library /path/to/library --force
./scripts/linux/align-library.sh --library /path/to/library --force --match 'Artist - Title.mp3'
```

The product has one forced-alignment implementation: **EasyAligner**. The
editor first shows the available Lyrics sources. Selecting LRCLIB or USDB keeps
that human-readable text and aligns it on the saved vocal stem with the global
German or English CTC path. The same implementation is used for single songs,
multi-selection, full-library jobs and imports.

The source list always includes the virtual entry **Full transcript**. Choose it
when no provider match exists or the matches are implausible. The isolated
transcription worker recognizes the complete sung text and then feeds that text
through EasyAligner. Generated output always returns as **In review**; it never
silently replaces a released Stage version.

The library list supports native batch selection: **Ctrl+click** toggles
individual songs and **Shift+click** selects a contiguous range. Choosing
**Alignment → Realign selected songs** submits exactly that selection as one
server job; songs and requested variants run sequentially on the single GPU,
and a failure remains isolated to its song instead of aborting the batch.

For an **In review** song, **Management → Show / edit base lyrics…** opens the
recognized or downloaded source text used by the next realignment. Saving it
does not mutate the currently loaded timeline. Section annotations such as
`[Chorus]`, `Refrain`, `Verse 1`, `Bridge`, or `Instrumental` are retained in
the editable source but ignored as non-sung structure markers when lyrics are
loaded or aligned, so they never become timed words.

**Management → Retrieve new lyrics…** and the same action in a song's context
menu search the configured USDB providers and LRCLIB together. With a server-side
`Genius__AccessToken`, the same dialog also shows Genius discovery results and
opens their official pages. Genius' official API does not return lyrics text, so
those results are deliberately never scraped or passed to EasyAligner. The dialog shows
which providers returned compatible versions and lets the operator select one.
The selected provider text replaces the realignment source, then runs directly
through EasyAligner against the saved vocal stem. Its result is stored as a new
review version; the loaded editor timeline and any published version remain
unchanged. The exact downloaded provider payload and a provenance sidecar are
retained with the song and included in portable song packages.

Choosing **EasyAligner Direct** for one or more selected songs opens this same
fresh-source workflow and preselects LRCLIB when a compatible result exists.
Synchronized LRCLIB is preferred because its phrase breaks make useful display
lines, but all source timestamps are discarded before the single global CTC
path is calculated. EasyAligner also records its lowercase, punctuation-free
grapheme representation alongside the original words. The editor can toggle
this technical CTC text for diagnosis; it is read-only and the stage/export
always receives the unchanged human-readable lyrics. IPA is deliberately not
fed to this profile because its German and English CTC tokenizers expect
graphemes, not a phonetic alphabet.

The Genius client token can also be stored under **Settings → Lyrics sources**.
When registering the read-only API client, use `Neon Stage Karaoke` as app name,
`https://<host>/assets/neon-stage-icon.png` as icon,
`https://<host>/` as website, and
`https://<host>/api/admin/settings/genius/callback` as redirect URI. Neon Stage
uses only the generated Client Access Token; it never needs the client secret or
a Genius account password. Genius states that commercial API use requires a
separate license.

One or more selected songs can run through this replacement workflow in sequence.
Cancelling a source dialog skips only that song; a selected result is aligned before
the next song is offered.

Use **Edit → Remove all songs from the stage** to return every released song to
review without deleting media or versions. **Edit → Delete every version of every
song** removes all server-side lyrics revisions and reports, clears local editor
recoveries, reclaims SQLite space, and removes every song from the stage while
preserving the actual audio, stems, LRC, and source files.

The song context menu's **Choose video…** action searches YouTube for manual
suggestions or accepts an operator-owned local video file. Internet downloads
require an explicit rights confirmation. Neon Stage transcodes the selected asset
to a silent H.264/MP4 sidecar (`*.video.mp4`), stores its adjustable sync offset in
`*.video.json`, previews it behind the lyrics in the editor, streams it behind all
Stage UI, and includes both files in song-package export/import. Positive video
offsets delay the video relative to the audio. `ffmpeg` and Node.js 22 or newer
must be available on the server for online video selection; local uploads require
only `ffmpeg`. Install or refresh the project-local, checksum-verified `yt-dlp`
runtime with `./scripts/linux/install-yt-dlp.sh`. The server prefers it over a
possibly outdated system package. `NEONSTAGE_YT_DLP_PATH` can override its path.

Use **Alignment → Apply global lyrics shift…** for a uniform timing correction. Positive milliseconds move every line, word, and syllable later; negative values move all of them earlier. Neon Stage changes the actual segment coordinates rather than writing an LRC offset tag. The operation is one atomic undo step and is rejected if any segment would move before the audio start or beyond its end.

Pipeline output can include enhanced LRC, `*.alignment.json`, vocal and instrumental FLAC stems, stem metadata, transcript verification, and candidate diagnostics. See [`lyrics-word-aligner/README.md`](lyrics-word-aligner/README.md).

For an existing song, **Management → Import Enhanced LRC…** accepts line and
word timestamps such as `[00:19.68]<00:19.68>Word`. The editor shows a
non-destructive confirmation first, keeps the imported readable text, and saves
the result only when the operator creates a new version.

## Command-line workflows

| Task | Command |
|---|---|
| Build .NET projects | `./scripts/linux/build.sh` |
| Start server | `./scripts/linux/start-server.sh /library/path` |
| Start editor | `./scripts/linux/start-desktop.sh [server-url]` |
| Start Linux stage | `./scripts/linux/start-unity-stage.sh [server-url]` |
| Build macOS Stage | `./scripts/macos/build-unity-stage-macos.sh` (on macOS) |
| Build Windows Server, Editor and Stage | `.\scripts\windows\release.cmd` (no Git Bash/WSL required) |
| Build Android stage | `./scripts/linux/build-unity-stage-android.sh` |
| Match library lyrics | `./scripts/linux/match-library-lrc.sh` |
| Align library | `./scripts/linux/align-library.sh --force` |
| Recognize complete lyrics | `./scripts/linux/recognize-song-lyrics.sh --audio '/library/Artist - Title.mp3'` (retains matching LRCLIB/editor spelling on the acoustic timing scaffold) |
| Process requests | `./scripts/linux/process-wishlist.sh` |
| Import a local MP3/FLAC folder | `./scripts/linux/process-audio-folder.sh /path/to/audio` |
| Analyze stage timing | `./scripts/linux/analyze-stage-timing.sh` |
| Verify release contents | `./scripts/release/verify-no-media.sh` |
| Build a versioned release | `./scripts/release/build-release.sh --platform linux` |
| Review, commit, and optionally push changes | `./scripts/git/commit-and-push.sh` |

More examples: [`scripts/linux/README.md`](scripts/linux/README.md), the [Online-Karaoke MVP](docs/online-karaoke.md), the [self-hosted LiveKit stack](deploy/livekit/README.md), the [macOS/Apple-Silicon build guide](docs/macos-build.md), [`docs/wishlist-worker.md`](docs/wishlist-worker.md), [`docs/lyrics-editor-integration.md`](docs/lyrics-editor-integration.md), and the detailed [music-reactive background shader guide](docs/background-shaders.md).

## Release builds

Release artifacts, supported platforms, version/build numbering, checksums,
signing expectations, optional GitHub upload, and the GitHub workflow are
documented in [`docs/RELEASES.md`](docs/RELEASES.md). Every release job runs the
media guard before packaging and again against produced artifacts.

Create the first local release as product version `0.1.0`, build `1`:

```bash
# Linux Server, Editor and Unity Stage; asks before uploading with gh
./scripts/release/build-release.sh --platform linux

# Linux plus signed Android/ARMv7 in one numbered release
./scripts/release/build-release.sh --platform linux,android

# In Windows PowerShell or by double-clicking release.cmd:
.\scripts\windows\release.ps1 -ReuseBuild
.\scripts\windows\release.cmd -ReuseBuild

# On macOS: self-contained ARM64 Server, Editor.app and Unity Stage ZIPs
./scripts/release/build-release.sh --platform macos --reuse-build
```

On Windows, `scripts/windows/prepare-build.ps1` automatically installs a
missing .NET 10 SDK and FFmpeg. It prefers WinGet and falls back to local tools
under the ignored `.tools/windows/` directory. Unity and its platform modules
remain the only deliberately manual dependency.

Every Windows component is emitted both as a conventional portable ZIP and as
an AppImage-like single EXE which self-extracts its versioned payload below
`%LOCALAPPDATA%\NeonStage\portable`. The standalone Server, Editor and Stage are
preconfigured for `http://127.0.0.1:5274`; the Server binds only to localhost,
uses `%LOCALAPPDATA%\NeonStage\standalone-server` for state, and starts with
LiveKit disabled. Unity and VLC still require their native companion files at
runtime, which is why the EXE performs a managed extraction instead of pretending
those applications are physically one file.

On macOS, `scripts/macos/prepare-build.sh` installs missing .NET 10, FFmpeg,
dylibbundler and VLC dependencies through Homebrew and restores all projects.
Unity 6, its activated license and Mac Build Support remain manual. The release
packages a native ARM64 Server, a self-contained Lyrics Editor `.app`, and the
Unity Stage; use `--skip-unity` when only Server and Editor are needed.

The product version follows Semantic Versioning, while the monotonically
increasing build number distinguishes rebuilt artifacts without pretending that
the product gained a new feature version. `release-version.env` begins at
`VERSION=0.1.0` and `BUILD=0`; only a successful build changes it to build `1`.
The script creates `artifacts/releases/v0.1.0-build.1/`, generates SHA-256
checksums, and then asks whether it should create the matching prerelease and
upload all files through the authenticated GitHub CLI (`gh`).

Build all three self-contained Linux AppImages locally:

```bash
./scripts/build-appimages.sh
./artifacts/NeonStage-Server-x86_64.AppImage
KARAOKE_SERVER=http://SERVER:5274 ./artifacts/NeonStage-LyricsEditor-x86_64.AppImage
KARAOKE_SERVER=http://SERVER:5274 ./artifacts/NeonStage-Stage-x86_64.AppImage
```

The build entry point detects `apt`, `dnf`, `pacman`, or `zypper`, installs
missing open-source build dependencies by default, installs .NET 10 locally
under the ignored `.tools/` directory when necessary, and downloads
`linuxdeploy`/`appimagetool` into the same cache. The Server and Editor images
include FFmpeg; the Editor also includes LibVLC and its plugins. Unity itself is
the one intentional external prerequisite because its installation and license
must be managed through Unity Hub. Build the .NET images without it via
`./scripts/build-appimages.sh --skip-stage`. Pass `--no-auto-install` for a
read-only dependency check.

The Server AppImage contains the API and both web portals, but no private library, database, credentials, downloader, models, or GPU aligner. It can serve an existing library and import complete `.neonstage.zip` song packages without the alignment stack. New downloads, folder ingestion, and realignment still require the separately managed worker/alignment environment.

## Media safety and review rules

Songs, lyrics, cover art, generated stems, local databases, service credentials, model weights, and alignment job output must never be committed or included in deployments or release artifacts. `.gitignore`, the release guard, and GitHub Actions enforce this policy.

A stage-ready library entry requires audio, lyrics, instrumental, and vocal stems. Automatically aligned material enters **In review**. Only an explicitly **Released** editor version is available to the stage.

If request processing downloaded valid audio but could not obtain or align suitable lyrics, the request stays open and the editor exposes two explicit admin actions. **Remove** discards the request. **Adopt** keeps the audio as an unreleased **Without lyrics** project, which can be found through the matching editor status filter. Imported lyrics become the source for a later per-song alignment. The project moves into the regular **In review** category only after usable lyrics and both instrumental and vocal stems exist; incomplete projects can never enter the guest library, queue, or stage.

The editor exposes the same folder workflow under **Management → Import audio folder (MP3/FLAC)**. It reads ID3 or Vorbis-comment metadata, preserves the original MP3 or lossless FLAC master, and prefers adjacent or embedded lyrics. Without local lyrics, request and folder imports look for a recording-compatible UltraStar TXT on USDB and then fall back to LRCLIB. Every selected text source—including converted UltraStar—is aligned by EasyAligner; provider word times are treated as hints, not immutable truth. If neither source yields usable text, the GPU worker creates a full transcript with word boundaries and aligns that text through the same EasyAligner path. Every import route performs the same normalized title/artist duplicate check; existing review and without-lyrics projects also count as imported. The workflow automatically admits only technically complete projects. The explicit **Adopt** action above is the sole incomplete-project exception and remains stage-blocked. Spotify and the optional Qobuz integration supply catalog metadata for request imports; neither is treated as a lyrics source. See [USDB lyrics-source integration](docs/usdb-lyrics-source.md) for configuration, matching safeguards, cache behavior, and known brittle points.

### Import UltraStar Deluxe lyrics

Neon Stage imports UltraStar Deluxe `.txt` lyrics with their beat-level timing,
including `#BPM`, `#GAP`, absolute or `#RELATIVE:yes` timing, normal, golden,
freestyle, rap, and rap-golden notes. Notes are reconstructed as timed words and
syllables for the live editor preview.

- For a new project, choose the UltraStar TXT in **New song**. Title and artist
  metadata are filled when available; a referenced adjacent MP3 is selected
  automatically when it exists.
- For a song already open in the editor, choose **Management → Import UltraStar
  lyrics…**. Confirming replaces the complete current lyrics document, but does
  not modify audio, stems, cover art, or any saved lyrics version. Undo is
  available before closing, and **Save** creates a new version.
- The MP3/FLAC-folder and server song-import workflows recognize UltraStar TXT
  sidecars as well and convert their timing to Enhanced LRC before alignment.
- **Management → Check USDB for lyrics…** searches USDB on the server for the
  loaded song, preselects the best candidate, and creates a separate review
  revision only after the operator confirms **Select**.

Variable-BPM directives and duet tracks are currently rejected explicitly
because Neon Stage cannot preserve those semantics yet. Malformed source files
with backwards-running notes or overlapping lyric lines are also rejected
instead of being silently mistimed. No UltraStar song, audio, cover, or lyrics
data is included in this repository or any release.

Use **Management → Song packages** to export one song, export a selected group, or import one or several portable `.neonstage.zip` packages. A package contains the master audio, instrumental and vocal stems, synchronized lyrics, alignment and visualization sidecars, cover art, review state, and the complete editor lyrics-version history. Imports validate every file checksum, never overwrite an existing song project, and only publish a song after the complete package has been verified.

## Localization

Web portals use the browser locale: German for `de`, English otherwise. `localStorage.neonStageLocale` can override the browser value. Song metadata, lyrics, event descriptions, and user names are never translated.

## Media, services, and privacy

Operators are responsible for all media rights and third-party terms. Neon Stage is not affiliated with or endorsed by Spotify, Qobuz, USDB, LRCLIB, Genius, YouTube, artists, or labels. See [`docs/legal/media-and-services.md`](docs/legal/media-and-services.md).

## License

Original Neon Stage source is licensed under the [Apache License 2.0](LICENSE). Third-party software remains under its own licenses. Distribution requirements are listed in [`NOTICE`](NOTICE), [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md), and [`docs/legal/licensing.md`](docs/legal/licensing.md).

## Acknowledgements

Neon Stage is built on years of work by open-source maintainers across UI,
multimedia, databases, speech recognition, forced alignment, and source
separation. Thank you. The projects and communities are recognized in
[`ACKNOWLEDGEMENTS.md`](ACKNOWLEDGEMENTS.md); this gratitude complements, but
does not replace, the formal third-party notices.

## Contributing and security

Read [`CONTRIBUTING.md`](CONTRIBUTING.md) and [`SECURITY.md`](SECURITY.md). Never upload copyrighted media, credentials, local databases, generated stems, or model weights to issues or pull requests.
