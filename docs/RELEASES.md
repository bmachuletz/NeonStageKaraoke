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
./scripts/release/verify-no-media.sh
dotnet test Karaoke.slnx -c Release
./scripts/build-appimages.sh
./scripts/release/verify-no-media.sh artifacts
```

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

Published packages must include `LICENSE`, `NOTICE`, and `THIRD_PARTY_NOTICES.md`. Android signing happens only in the protected CI/release environment.

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
