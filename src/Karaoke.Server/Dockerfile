FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

COPY Directory.Build.props ./
COPY src/Karaoke.Contracts/Karaoke.Contracts.csproj src/Karaoke.Contracts/
COPY src/Karaoke.Editor.Core/Karaoke.Editor.Core.csproj src/Karaoke.Editor.Core/
COPY src/Karaoke.Server/Karaoke.Server.csproj src/Karaoke.Server/
COPY LrcMatcher/LrcMatcher.csproj LrcMatcher/
RUN dotnet restore src/Karaoke.Server/Karaoke.Server.csproj
RUN dotnet restore LrcMatcher/LrcMatcher.csproj

COPY src/Karaoke.Contracts/ src/Karaoke.Contracts/
COPY src/Karaoke.Editor.Core/ src/Karaoke.Editor.Core/
COPY src/Karaoke.App/Assets/neon-stage-icon.png src/Karaoke.App/Assets/neon-stage-icon.png
COPY src/Karaoke.Stage.Unity/Assets/NeonStage/Shared/ src/Karaoke.Stage.Unity/Assets/NeonStage/Shared/
COPY src/Karaoke.Server/ src/Karaoke.Server/
COPY LrcMatcher/ LrcMatcher/
RUN dotnet publish src/Karaoke.Server/Karaoke.Server.csproj -c Release --no-restore -o /publish
RUN dotnet publish LrcMatcher/LrcMatcher.csproj -c Release --no-restore -o /lrcmatcher

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ffmpeg jq python3 unzip \
    && rm -rf /var/lib/apt/lists/*

# Keep the same directory layout used by a source checkout. The server resolves
# its alignment workers relative to ContentRootPath, while the workers resolve
# their lightweight post-processing helpers relative to the repository root.
WORKDIR /workspace/src/Karaoke.Server
COPY --from=build /publish/ ./
COPY --from=build /lrcmatcher/ /workspace/LrcMatcher/
COPY LICENSE NOTICE THIRD_PARTY_NOTICES.md ACKNOWLEDGEMENTS.md ./
COPY scripts/linux/align-library.sh scripts/linux/recognize-song-lyrics.sh scripts/linux/process-wishlist.sh scripts/linux/install-yt-dlp.sh scripts/linux/install-deno.sh /workspace/scripts/linux/
COPY lyrics-word-aligner/scripts/verify_audio_encoding.py lyrics-word-aligner/scripts/analyze_visuals.py /workspace/lyrics-word-aligner/scripts/
RUN /workspace/scripts/linux/install-yt-dlp.sh \
    && /workspace/scripts/linux/install-deno.sh
ENV NEONSTAGE_YT_DLP_PATH=/workspace/.tools/yt-dlp
ENV NEONSTAGE_DENO_PATH=/workspace/.tools/deno
ENV LRC_MATCHER_EXE=/workspace/LrcMatcher/LrcMatcher
EXPOSE 5274
ENTRYPOINT ["dotnet", "Karaoke.Server.dll"]
