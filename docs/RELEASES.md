# Release builds

Neon Stage release artifacts contain application binaries only. They never contain a karaoke library, lyrics, cover art, generated stems, local databases, downloaded AI models, credentials, or alignment job output.

## GitHub release workflow

`.github/workflows/release.yml` runs for version tags (`v*`) and manual dispatches. It:

1. checks the tracked source tree with the media guard;
2. builds and tests the .NET projects;
3. publishes the Linux server and lyrics editor;
4. creates a self-contained x86_64 AppImage with LibVLC and its plugins;
5. copies license and notice documents into every distributable package;
6. checks the finished package tree again;
7. creates SHA-256 checksums;
8. uploads workflow artifacts and, for tags, attaches them to a GitHub Release.

Unity Linux and Android builds require a licensed Unity CI setup. Configure the repository secrets expected by the selected Unity CI provider before enabling those jobs. Never store a Unity license, keystore, password, Spotify secret, or signing key in the repository.

## Local release preparation

```bash
./scripts/release/verify-no-media.sh
dotnet test Karaoke.slnx -c Release
dotnet publish src/Karaoke.Server/Karaoke.Server.csproj -c Release -o artifacts/server-linux
dotnet publish src/Karaoke.App.Desktop/Karaoke.App.Desktop.csproj -c Release -r linux-x64 --self-contained false -o artifacts/editor-linux
./scripts/release/build-editor-appimage.sh
./scripts/release/verify-no-media.sh artifacts
```

Unity builds:

```bash
./scripts/linux/build-unity-stage-linux.sh
./scripts/linux/build-unity-stage-android.sh
```

Run the media guard against `src/Karaoke.Stage.Unity/Builds` before packaging it.

## Artifact policy

- `server-linux`: ASP.NET Core server and web portals; no database and no library
- `editor-linux-x64`: Avalonia editor; no cached audio and no recovery drafts
- `NeonStage-LyricsEditor-x86_64.AppImage`: self-contained editor including LibVLC; server URL is supplied through `KARAOKE_SERVER`

The Docker Compose image is the server/API deployment. GPU alignment, local
folder ingestion, and wishlist download processing stay in separately managed
host or worker processes; models, downloader environments, and karaoke media
are never bundled into the server image.
- `stage-linux-x64`: Unity player; no songs or server state
- `stage-android-armv7`: Android package for 32-bit devices such as the tested Ikarao hardware
- `stage-android-arm64`: Android package for modern 64-bit devices

Published packages must include `LICENSE`, `NOTICE`, and `THIRD_PARTY_NOTICES.md`. Android signing happens only in the protected CI/release environment.

## Container deployment

Copy `.env.example` to the untracked `.env`, configure the absolute library
path, Spotify client ID, Spotify client secret, and the exact registered
redirect URL, then run `docker compose up -d --build server`. Compose mounts
the private library and `KARAOKE_DATA_PATH`; neither is copied into the image.
The public web portal, administration portal, editor API, audio streams, event
state and SQLite persistence are served from the same ASP.NET Core container.

## Creating a release

1. Update the changelog/release notes.
2. Run tests and both media-guard passes locally.
3. Tag a reviewed commit, for example `v0.1.0`.
4. Push the tag.
5. Verify every checksum and inspect package contents before publishing the GitHub Release.
