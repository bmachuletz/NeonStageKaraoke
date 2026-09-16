# Release builds

Neon Stage release artifacts contain application binaries only. They never contain a karaoke library, lyrics, cover art, generated stems, local databases, downloaded AI models, credentials, or alignment job output.

## GitHub release workflow

`.github/workflows/release.yml` runs for version tags (`v*`) and manual dispatches. It:

1. checks the tracked source tree with the media guard;
2. builds and tests the .NET projects;
3. publishes the Linux server and lyrics editor;
4. creates self-contained x86_64 Server and Lyrics Editor AppImages (the editor includes LibVLC and its plugins);
5. copies license and notice documents into every distributable package;
6. checks the finished package tree again;
7. creates SHA-256 checksums;
8. uploads workflow artifacts and, for tags, attaches them to a GitHub Release.

The separate manually dispatched `unity-stage-release.yml` workflow always builds the Linux Stage AppImage with GameCI. Optional inputs add native macOS ARM64 and Windows x64 jobs on appropriately labelled self-hosted Unity runners. Configure `UNITY_LICENSE`, `UNITY_EMAIL`, and `UNITY_PASSWORD` as encrypted repository secrets before using GameCI. Its optional `release_tag` input attaches the Linux result to an existing GitHub Release. Never store a Unity license, keystore, password, Spotify secret, LiveKit secret, or signing key in the repository. See the [official GameCI builder documentation](https://game.ci/docs/github/builder/) for current licensing instructions.

## Local release preparation

```bash
./scripts/release/build-release.sh --platform linux
```

This is the preferred release entry point. It performs the source media guard,
Release build and both executable test suites; embeds one version/build identity
in .NET and Unity; builds the selected platforms; adds license and notice files;
creates `RELEASE-METADATA.txt` and `SHA256SUMS`; checks the result; and only then
increments `release-version.env`. Finally it asks whether the complete release
should be created and uploaded using the authenticated GitHub CLI (`gh`). Use
`--no-publish` for a guaranteed local-only build or `--publish` for automation.
The upload is accepted only when `gh auth status` succeeds and the exact source
commit is already present on a remote branch; the script never silently pushes
source changes.

### Version and build numbers

- Product versions use Semantic Versioning: start with `0.1.0`, later use
  `0.1.1` for fixes, `0.2.0` for a meaningful feature milestone, and `1.0.0`
  when the public contract is considered stable.
- `BUILD` is a positive, monotonically increasing package revision. The checked
  in initial state is build `0`; the first successful release is build `1`.
- Every platform produced for the same release uses the same pair, for example
  `0.1.0`, build `1`. Artifact folders and GitHub tags use the unambiguous form
  `v0.1.0-build.1`.
- A new `--version 0.2.0` starts that product version at build `1`. A second
  native host can build the already reserved number with `--reuse-build` after
  the updated `release-version.env` has been committed and pulled there.

Examples:

```bash
# Linux: Server, Editor and Unity Stage AppImages
./scripts/release/build-release.sh --platform linux

# Linux plus a production-signed Android/ARMv7 APK
./scripts/release/build-release.sh --platform linux,android

# On the matching hosts, reuse the same release identity
./scripts/release/build-release.sh --platform macos --reuse-build
./scripts/release/build-release.sh --platform windows --reuse-build

# Preview validation, paths and next number without changing anything
./scripts/release/build-release.sh --platform linux --dry-run
```

macOS players must be built on macOS and Windows players on Windows. The script
rejects unsupported host/target combinations instead of silently producing a
different artifact. Linux can additionally build the Android target when the
Unity Android module is installed.

The Windows target creates three portable, self-contained archives with the
same release identity:

- `NeonStage-Server-…-windows-x64.zip` with the ASP.NET Core runtime, web
  portals, `ffmpeg.exe`, license files, and a launcher using per-user data paths;
- `NeonStage-LyricsEditor-…-windows-x64.zip` with the .NET runtime, LibVLC and
  its plugins, `ffmpeg.exe`, licenses, and a server-aware launcher;
- `NeonStage-Stage-…-windows-x64.zip` with the compiled Unity player and license
  notices.

Install a Windows FFmpeg build and make `ffmpeg.exe` available through `PATH`,
or set `FFMPEG_EXE=C:\path\to\ffmpeg.exe` before starting the release. Use
`--skip-unity` when only the Windows Server and Editor should be produced. The
native PowerShell component packager can also be called directly by tooling as
`scripts/windows/build-release.ps1`; the cross-platform entry point remains
`scripts/release/build-release.sh` because it owns numbering, checksums, and the
optional GitHub upload. The Stage builder discovers a normal Unity Hub
installation automatically; `UNITY_EDITOR=C:\path\to\Unity.exe` overrides it.

The initial Windows ZIPs are portable but not Authenticode-signed, so Windows
SmartScreen may show an unknown-publisher warning. Code signing can be added
later through a protected certificate/CI secret without changing the versioning
or package layout; private keys must never enter the repository.

### Android signing

Production Android builds require these environment variables; the keystore and
passwords must remain outside Git:

```bash
export NEONSTAGE_ANDROID_KEYSTORE=/absolute/private/path/neonstage.keystore
export NEONSTAGE_ANDROID_KEYALIAS=neonstage
export NEONSTAGE_ANDROID_KEYSTORE_PASS='...'
export NEONSTAGE_ANDROID_KEYALIAS_PASS='...'
./scripts/release/build-release.sh --platform linux,android
```

`--allow-debug-android-signing` exists only for installable test packages. It is
not appropriate for a public upgrade chain because later APKs must use the same
protected signing key.

### May compiled Unity players be published?

Yes, in general: Unity's current Editor Software Terms explicitly permit the
Unity Runtime to be distributed as an integrated part of a project, subject to
the applicable subscription/tier, fee, project-use and other terms. They also
state that Unity 6 project runtimes have no additional runtime fee or revenue
share when those conditions are met. This permission covers the compiled
Neon Stage player—not the Unity Editor, Hub, SDK installation, license files, or
build caches. Package-specific and third-party terms still apply. Keep
`THIRD_PARTY_NOTICES.md` with every release and review the current
[Unity Editor Software Terms](https://unity.com/legal/editor-terms-of-service/software)
before publication. This repository documentation is operational guidance, not
individual legal advice.

Individual packages can be rebuilt with `build-server-appimage.sh`, `build-editor-appimage.sh`, or `build-stage-appimage.sh`. Set `NEONSTAGE_SKIP_UNITY_BUILD=1` when a current Unity Linux player already exists in `src/Karaoke.Stage.Unity/Builds/Linux`.

`scripts/build-appimages.sh` is the supported local entry point. It resolves
open-source build dependencies through `apt`, `dnf`, `pacman`, or `zypper`, can
install .NET 10 into the ignored `.tools/dotnet` directory, and caches downloaded
AppImage tooling under `.tools/appimage`. Use `--skip-stage` on machines without
Unity and `--no-auto-install` to turn all automatic installation into a strict
dependency check. Optional `APPIMAGETOOL_SHA256` and `LINUXDEPLOY_SHA256` values
pin and verify downloaded build tools in controlled release environments.

Unity builds:

```bash
./scripts/linux/build-unity-stage-linux.sh
./scripts/linux/build-unity-stage-android.sh
# Run on the corresponding host platform:
./scripts/macos/build-unity-stage-macos.sh
# PowerShell: scripts/windows/build-unity-stage-windows.ps1
```

Run the media guard against `src/Karaoke.Stage.Unity/Builds` before packaging it.

## Artifact policy

- `server-linux`: ASP.NET Core server and web portals; no database and no library
- `editor-linux-x64`: Avalonia editor; no cached audio and no recovery drafts
- `NeonStage-Server-x86_64.AppImage`: self-contained ASP.NET Core server, web portals, and FFmpeg; configuration and persistent data remain outside the image
- `NeonStage-LyricsEditor-x86_64.AppImage`: self-contained editor including FFmpeg, LibVLC, and VLC plugins; server URL is supplied through `KARAOKE_SERVER`

The Docker Compose image is the server/API deployment. GPU alignment, local
folder ingestion, and wishlist download processing stay in separately managed
host or worker processes; models, downloader environments, and karaoke media
are never bundled into the server image.
- `NeonStage-Stage-x86_64.AppImage`: Unity Linux player; no songs or server state; server URL is supplied through `KARAOKE_SERVER`, `NEONSTAGE_SERVER_URL`, or `--server`
- `stage-linux-x64`: unpacked Unity player; no songs or server state
- `stage-android-armv7`: Android package for 32-bit devices such as the tested Ikarao hardware
- `stage-android-arm64`: Android package for modern 64-bit devices
- `NeonStage-Stage-macOS-arm64.zip`: native Apple-Silicon application bundle
- `stage-windows-x64`: native Windows x64 Unity player directory
- `NeonStage-Server-…-windows-x64.zip`: self-contained Windows x64 server with FFmpeg and launcher
- `NeonStage-LyricsEditor-…-windows-x64.zip`: self-contained Windows x64 editor with LibVLC, FFmpeg, and launcher
- `NeonStage-Stage-…-windows-x64.zip`: portable Windows x64 Unity Stage

Published packages must include `LICENSE`, `NOTICE`, and `THIRD_PARTY_NOTICES.md`. Android signing uses protected CI secrets or the local release environment variables documented above; signing material is never stored in the repository or artifact folder.

## Running the Linux AppImages

The Server listens on all interfaces at port 5274 by default so phones on the local network can reach it. Its defaults are:

- library: `~/Music/NeonStage`
- database and persistent state: `${XDG_DATA_HOME:-~/.local/share}/neon-stage/server`
- optional configuration: `~/.local/share/neon-stage/server/server.env`

`packaging/linux/server.env.example` documents the supported path and Spotify settings. Override the file location with `NEONSTAGE_SERVER_ENV`. A complete imported song package does not require the downloader or aligner; only creating or realigning material does.

```bash
chmod +x artifacts/NeonStage-*.AppImage
artifacts/NeonStage-Server-x86_64.AppImage
KARAOKE_SERVER=http://127.0.0.1:5274 artifacts/NeonStage-LyricsEditor-x86_64.AppImage
KARAOKE_SERVER=http://127.0.0.1:5274 artifacts/NeonStage-Stage-x86_64.AppImage
```

## Container deployment

Copy `.env.example` to the untracked `.env`, configure the absolute library
path and data path, then optionally add Spotify or Online-Karaoke credentials.
Run `docker compose up -d --build server`. Compose mounts
the private library and `KARAOKE_DATA_PATH`; neither is copied into the image.
The public web portal, administration portal, editor API, audio streams, event
state and SQLite persistence are served from the same ASP.NET Core container.

## Creating a release

1. Update the changelog/release notes.
2. Run tests and both media-guard passes locally.
3. Tag a reviewed commit, for example `v0.1.0`.
4. Push the tag.
5. Verify every checksum and inspect package contents before publishing the GitHub Release.
