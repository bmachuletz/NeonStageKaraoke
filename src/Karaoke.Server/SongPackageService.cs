using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Karaoke.Contracts;

namespace Karaoke.Server;

internal sealed record SongPackageExportResult(string Path, string FileName);

internal sealed class SongPackageService(ServerSettingsService settings, LibraryRepository library,
    LyricsVersionRepository versions, ChangeFeedService changes, ILogger<SongPackageService> logger)
{
    private const string PackageFormat = "neon-stage-song-package";
    private const int SchemaVersion = 1;
    private const long MaximumCompressedBytes = 50L * 1024 * 1024 * 1024;
    private const long MaximumExpandedBytes = 100L * 1024 * 1024 * 1024;
    private const int MaximumEntries = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly HashSet<string> AllowedSidecarSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".lrc", ".pre-align.lrc", ".alignment.json", ".instrumental.ogg", ".instrumental.flac",
        ".vocals.ogg", ".vocals.flac", ".visuals.json", ".cover.jpg", ".stems.json",
        ".transcription.json"
    };
    private static readonly HashSet<string> AllowedMasterExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma"
    };

    public async Task<SongPackageExportResult> ExportAsync(IReadOnlyCollection<Guid>? requestedSongIds,
        CancellationToken ct)
    {
        if (requestedSongIds is null) throw new ArgumentException("Die Songauswahl fehlt.");
        var songIds = requestedSongIds.Where(id => id != Guid.Empty).Distinct().ToArray();
        if (songIds.Length == 0) throw new ArgumentException("Mindestens ein Song muss ausgewählt werden.");
        if (songIds.Length > 500) throw new ArgumentException("Pro Paket können höchstens 500 Songs exportiert werden.");

        var target = Path.Combine(Path.GetTempPath(), $"neon-stage-export-{Guid.NewGuid():N}.neonstage.zip");
        try
        {
            var packageSongs = new List<PackageSong>(songIds.Length);
            await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (var songIndex = 0; songIndex < songIds.Length; songIndex++)
                {
                    ct.ThrowIfCancellationRequested();
                    var songId = songIds[songIndex];
                    var song = await library.GetAsync(songId, ct)
                               ?? throw new FileNotFoundException($"Song {songId} wurde nicht gefunden.");
                    var audio = await library.GetAudioFileAsync(songId, ct)
                                ?? throw new FileNotFoundException($"Die Masterspur von „{song.Title}“ fehlt.");
                    EnsureInsideLibrary(audio.Path);
                    var projectFiles = FindProjectFiles(audio.Path);
                    ValidateRequiredProjectFiles(song.Title, projectFiles);
                    var key = $"songs/{songIndex + 1:D4}";
                    var files = new List<PackageFile>(projectFiles.Count);
                    for (var fileIndex = 0; fileIndex < projectFiles.Count; fileIndex++)
                    {
                        var source = projectFiles[fileIndex];
                        var archivePath = $"{key}/files/{fileIndex + 1:D4}{ArchiveExtension(source.Suffix)}";
                        var compression = source.Role is "master" or "instrumental" or "vocals" or "cover"
                            ? CompressionLevel.NoCompression
                            : CompressionLevel.Optimal;
                        var hash = await AddFileAsync(archive, source.Path, archivePath, compression, ct);
                        files.Add(new(archivePath, source.Suffix, source.Role,
                            new FileInfo(source.Path).Length, hash));
                    }

                    var packageVersions = new List<PackageLyricsVersion>();
                    foreach (var summary in await versions.GetAllAsync(songId, ct))
                    {
                        var version = await versions.GetAsync(songId, summary.Id, ct);
                        if (version is null) continue;
                        var archivePath = $"{key}/lyrics-versions/{version.Revision:D8}.json";
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(version, JsonOptions);
                        var hash = await AddBytesAsync(archive, bytes, archivePath, ct);
                        packageVersions.Add(new(archivePath, bytes.LongLength, hash));
                    }

                    packageSongs.Add(new(song.Id, song.Title, song.Artist, song.Album,
                        song.DurationSeconds, song.ReviewStatus, files, packageVersions));
                }

                var manifest = new PackageManifest(PackageFormat, SchemaVersion, DateTimeOffset.UtcNow,
                    packageSongs);
                var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
                await AddBytesAsync(archive, manifestBytes, "manifest.json", ct);
            }

            var displayName = songIds.Length == 1
                ? SafeName(packageSongs[0].Title, "song") + ".neonstage.zip"
                : $"neon-stage-{songIds.Length}-songs-{DateTimeOffset.Now:yyyyMMdd-HHmm}.neonstage.zip";
            return new(target, displayName);
        }
        catch
        {
            TryDelete(target);
            throw;
        }
    }

    public async Task<SongPackageImportResultDto> ImportAsync(Stream packageStream, CancellationToken ct)
    {
        var uploadPath = Path.Combine(Path.GetTempPath(), $"neon-stage-import-{Guid.NewGuid():N}.zip");
        var stagingPath = Path.Combine(Path.GetTempPath(), $"neon-stage-import-stage-{Guid.NewGuid():N}");
        var publishedFiles = new List<string>();
        var indexedSongIds = new List<Guid>();
        try
        {
            await CopyUploadAsync(packageStream, uploadPath, ct);
            Directory.CreateDirectory(stagingPath);
            using var archive = ZipFile.OpenRead(uploadPath);
            if (archive.Entries.Count is 0 or > MaximumEntries)
                throw new InvalidDataException("Das Songpaket enthält zu viele oder keine Einträge.");
            var entries = BuildEntryMap(archive);
            if (!entries.TryGetValue("manifest.json", out var manifestEntry) || manifestEntry.Length > 20 * 1024 * 1024)
                throw new InvalidDataException("Das Songpaket enthält kein gültiges Manifest.");
            PackageManifest manifest;
            await using (var manifestStream = manifestEntry.Open())
                manifest = await JsonSerializer.DeserializeAsync<PackageManifest>(manifestStream, JsonOptions, ct)
                           ?? throw new InvalidDataException("Das Paketmanifest ist leer.");
            ValidateManifest(manifest);

            var stagedSongs = new List<StagedSong>(manifest.Songs.Count);
            var packageIdentities = new HashSet<string>(StringComparer.Ordinal);
            long expandedBytes = 0;
            for (var songIndex = 0; songIndex < manifest.Songs.Count; songIndex++)
            {
                var packageSong = manifest.Songs[songIndex];
                ValidatePackageSong(packageSong);
                var identity = UsdbSongMatcher.Normalize(packageSong.Title) + "\n" +
                               UsdbSongMatcher.Normalize(packageSong.Artist);
                if (!packageIdentities.Add(identity) ||
                    await library.ContainsSongAsync(packageSong.Title, packageSong.Artist, ct))
                {
                    logger.LogInformation("Song package entry {Title} by {Artist} is already imported and will be skipped",
                        packageSong.Title, packageSong.Artist);
                    continue;
                }
                var stagedBase = Path.Combine(stagingPath, $"song-{songIndex + 1:D4}");
                var stagedFiles = new List<StagedFile>(packageSong.Files.Count);
                foreach (var file in packageSong.Files)
                {
                    var entry = GetVerifiedEntry(entries, file.ArchivePath, file.Size);
                    expandedBytes = checked(expandedBytes + entry.Length);
                    if (expandedBytes > MaximumExpandedBytes)
                        throw new InvalidDataException("Das entpackte Songpaket ist zu groß.");
                    var target = stagedBase + file.Suffix;
                    var hash = await CopyAndHashAsync(entry, target, ct);
                    if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Prüfsumme für {file.ArchivePath} stimmt nicht.");
                    stagedFiles.Add(new(target, file.Suffix, file.Role));
                }

                var importedVersions = new List<LyricsVersionDto>(packageSong.LyricsVersions.Count);
                foreach (var version in packageSong.LyricsVersions)
                {
                    var entry = GetVerifiedEntry(entries, version.ArchivePath, version.Size);
                    // A version can contain the editor document (up to 10 MiB)
                    // plus its immutable technical alignment report (up to 20 MiB).
                    if (entry.Length > 32 * 1024 * 1024)
                        throw new InvalidDataException("Eine Lyrics-Version überschreitet 32 MiB.");
                    expandedBytes = checked(expandedBytes + entry.Length);
                    if (expandedBytes > MaximumExpandedBytes)
                        throw new InvalidDataException("Das entpackte Songpaket ist zu groß.");
                    var bytes = await ReadAndHashAsync(entry, version.Sha256, ct);
                    var payload = JsonSerializer.Deserialize<LyricsVersionDto>(bytes, JsonOptions)
                                  ?? throw new InvalidDataException("Eine Lyrics-Version ist ungültig.");
                    if (payload.SongId != packageSong.SourceSongId)
                        throw new InvalidDataException("Eine Lyrics-Version gehört zu einem anderen Song.");
                    importedVersions.Add(payload);
                }
                stagedSongs.Add(new(packageSong, stagedFiles, importedVersions));
            }

            var libraryRoot = Path.GetFullPath(settings.Get().LibraryPath);
            Directory.CreateDirectory(libraryRoot);
            var publishedSongs = new List<PublishedSong>(stagedSongs.Count);
            foreach (var staged in stagedSongs)
            {
                var artistFolder = Path.Combine(libraryRoot, SafeName(staged.Song.Artist, "Imported"));
                Directory.CreateDirectory(artistFolder);
                var proposedBase = SafeName(string.IsNullOrWhiteSpace(staged.Song.Artist)
                    ? staged.Song.Title
                    : $"{staged.Song.Title} - {staged.Song.Artist}", "Imported song");
                var destinationBase = UniqueBasePath(artistFolder, proposedBase);
                var master = staged.Files.Single(file => file.Role == "master");
                foreach (var file in staged.Files.Where(file => file.Role != "master").Append(master))
                {
                    var destination = destinationBase + file.Suffix;
                    File.Copy(file.Path, destination, overwrite: false);
                    publishedFiles.Add(destination);
                }
                publishedSongs.Add(new(staged, destinationBase + master.Suffix));
            }

            await ReindexAsync(ct);
            var imported = new List<ImportedSongPackageDto>(publishedSongs.Count);
            foreach (var published in publishedSongs)
            {
                var songId = LibraryRepository.StableId(published.AudioPath);
                var song = await library.GetAsync(songId, ct)
                           ?? throw new InvalidDataException($"„{published.Staged.Song.Title}“ wurde nach dem Import nicht indexiert.");
                indexedSongIds.Add(songId);
                var rewritten = published.Staged.Versions.Select(version => version with
                {
                    Id = Guid.NewGuid(),
                    SongId = songId,
                    DocumentJson = RewriteSongId(version.DocumentJson, songId)
                }).ToArray();
                var importedVersions = await versions.ImportAsync(songId, rewritten, ct);
                var reviewStatus = published.Staged.Song.ReviewStatus;
                song = await library.SetReviewStatusAsync(songId, reviewStatus, ct) ?? song;
                imported.Add(new(songId, song.Title, song.Artist, song.ReviewStatus,
                    published.Staged.Files.Count, importedVersions));
            }
            changes.Publish("lyrics-version-changed");
            return new(imported.Count, imported);
        }
        catch
        {
            foreach (var songId in indexedSongIds)
            {
                try { await library.DeleteSongAsync(songId, CancellationToken.None); }
                catch (Exception exception) { logger.LogWarning(exception, "Teilimport {SongId} konnte nicht zurückgerollt werden", songId); }
            }
            foreach (var path in publishedFiles) TryDelete(path);
            if (publishedFiles.Count > 0)
            {
                try { await ReindexAsync(CancellationToken.None); }
                catch (Exception exception) { logger.LogWarning(exception, "Bibliothek konnte nach Importfehler nicht neu indexiert werden"); }
            }
            throw;
        }
        finally
        {
            TryDelete(uploadPath);
            try { if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true); }
            catch (IOException exception) { logger.LogWarning(exception, "Import-Staging {Path} konnte nicht entfernt werden", stagingPath); }
        }
    }

    private static List<ProjectFile> FindProjectFiles(string audioPath)
    {
        var directory = Path.GetDirectoryName(audioPath)!;
        var baseName = Path.GetFileNameWithoutExtension(audioPath);
        var result = new List<ProjectFile> { new(audioPath, Path.GetExtension(audioPath), "master") };
        foreach (var path in Directory.EnumerateFiles(directory, baseName + ".*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFullPath(path).Equals(Path.GetFullPath(audioPath), StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(path);
            var suffix = name[baseName.Length..];
            if (!AllowedSidecarSuffixes.Contains(suffix)) continue;
            result.Add(new(path, suffix, Role(suffix)));
        }
        return result.OrderBy(file => file.Role == "master" ? 0 : 1).ThenBy(file => file.Suffix).ToList();
    }

    private static void ValidateRequiredProjectFiles(string title, IReadOnlyList<ProjectFile> files)
    {
        if (!files.Any(file => file.Suffix.Equals(".lrc", StringComparison.OrdinalIgnoreCase)) ||
            !files.Any(file => file.Role == "instrumental") || !files.Any(file => file.Role == "vocals"))
            throw new InvalidDataException($"„{title}“ besitzt nicht alle erforderlichen Lyrics- und Stem-Dateien.");
    }

    private static string Role(string suffix) => suffix.ToLowerInvariant() switch
    {
        ".lrc" => "lyrics", ".instrumental.ogg" or ".instrumental.flac" => "instrumental",
        ".vocals.ogg" or ".vocals.flac" => "vocals", ".cover.jpg" => "cover",
        ".alignment.json" => "alignment", ".visuals.json" => "visualization",
        ".transcription.json" => "transcription", _ => "sidecar"
    };

    private static string ArchiveExtension(string suffix) => suffix.Length <= 32 &&
        Regex.IsMatch(suffix, "^\\.[A-Za-z0-9._-]+$") ? suffix.ToLowerInvariant() : ".bin";

    private static async Task<string> AddFileAsync(ZipArchive archive, string sourcePath, string archivePath,
        CompressionLevel compression, CancellationToken ct)
    {
        var entry = archive.CreateEntry(archivePath, compression);
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var target = entry.Open();
        return await CopyAndHashAsync(source, target, ct);
    }

    private static async Task<string> AddBytesAsync(ZipArchive archive, byte[] bytes, string archivePath,
        CancellationToken ct)
    {
        var entry = archive.CreateEntry(archivePath, CompressionLevel.Optimal);
        await using var target = entry.Open();
        await target.WriteAsync(bytes, ct);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static async Task CopyUploadAsync(Stream source, string targetPath, CancellationToken ct)
    {
        await using var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total = checked(total + read);
            if (total > MaximumCompressedBytes) throw new InvalidDataException("Das Songpaket ist größer als 50 GiB.");
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        if (total == 0) throw new InvalidDataException("Das Songpaket ist leer.");
    }

    private static Dictionary<string, ZipArchiveEntry> BuildEntryMap(ZipArchive archive)
    {
        var result = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            ValidateArchivePath(entry.FullName);
            if (!result.TryAdd(entry.FullName, entry))
                throw new InvalidDataException($"Doppelter Archiveintrag: {entry.FullName}");
        }
        return result;
    }

    private static void ValidateArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') || path.StartsWith('\\') ||
            path.Contains("..", StringComparison.Ordinal) || path.Contains('\\') || Path.IsPathRooted(path))
            throw new InvalidDataException("Das Songpaket enthält einen unsicheren Pfad.");
    }

    private static void ValidateManifest(PackageManifest manifest)
    {
        if (manifest.Format != PackageFormat || manifest.SchemaVersion != SchemaVersion)
            throw new InvalidDataException("Unbekanntes Neon-Stage-Songpaketformat.");
        if (manifest.Songs is null || manifest.Songs.Count is 0 or > 500)
            throw new InvalidDataException("Das Songpaket enthält zu viele oder keine Songs.");
    }

    private static void ValidatePackageSong(PackageSong song)
    {
        if (song.SourceSongId == Guid.Empty || string.IsNullOrWhiteSpace(song.Title) || song.Title.Length > 500 ||
            song.Artist is null || song.Album is null || song.Artist.Length > 500 || song.Album.Length > 500 ||
            song.Files is null || song.Files.Count is 0 or > 100 || song.LyricsVersions is null ||
            song.LyricsVersions.Count > 10_000 || !Enum.IsDefined(song.ReviewStatus))
            throw new InvalidDataException("Das Songpaket enthält ungültige Songmetadaten.");
        if (song.Files.Select(file => file.Suffix).Distinct(StringComparer.OrdinalIgnoreCase).Count() != song.Files.Count)
            throw new InvalidDataException("Das Songpaket enthält doppelte Projektdateien.");
        foreach (var file in song.Files)
        {
            ValidateArchivePath(file.ArchivePath);
            var suffix = file.Suffix ?? string.Empty;
            var validRoleAndSuffix = file.Role == "master"
                ? AllowedMasterExtensions.Contains(suffix)
                : AllowedSidecarSuffixes.Contains(suffix) && Role(suffix) == file.Role;
            if (file.Size < 0 || !Regex.IsMatch(file.Sha256 ?? "", "^[a-fA-F0-9]{64}$") ||
                !Regex.IsMatch(suffix, "^\\.[A-Za-z0-9._-]+$") || !validRoleAndSuffix)
                throw new InvalidDataException("Das Songpaket enthält eine ungültige Projektdatei.");
        }
        if (song.Files.Count(file => file.Role == "master") != 1)
            throw new InvalidDataException("Das Songpaket enthält keine eindeutige Masterspur.");
        foreach (var version in song.LyricsVersions)
        {
            ValidateArchivePath(version.ArchivePath);
            if (version.Size < 0 || !Regex.IsMatch(version.Sha256 ?? "", "^[a-fA-F0-9]{64}$"))
                throw new InvalidDataException("Das Songpaket enthält eine ungültige Lyrics-Version.");
        }
        ValidateRequiredProjectFiles(song.Title,
            song.Files.Select(file => new ProjectFile("", file.Suffix, file.Role)).ToArray());
    }

    private static ZipArchiveEntry GetVerifiedEntry(IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        string path, long expectedSize)
    {
        ValidateArchivePath(path);
        if (!entries.TryGetValue(path, out var entry) || entry.Length != expectedSize)
            throw new InvalidDataException($"Archiveintrag {path} fehlt oder besitzt eine falsche Größe.");
        return entry;
    }

    private static async Task<string> CopyAndHashAsync(ZipArchiveEntry entry, string targetPath,
        CancellationToken ct)
    {
        await using var source = entry.Open();
        await using var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await CopyAndHashAsync(source, target, ct);
    }

    private static async Task<string> CopyAndHashAsync(Stream source, Stream target, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task<byte[]> ReadAndHashAsync(ZipArchiveEntry entry, string expectedHash,
        CancellationToken ct)
    {
        await using var stream = entry.Open();
        using var memory = new MemoryStream((int)entry.Length);
        var actual = await CopyAndHashAsync(stream, memory, ct);
        if (!actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Prüfsumme für {entry.FullName} stimmt nicht.");
        return memory.ToArray();
    }

    private async Task ReindexAsync(CancellationToken ct)
    {
        for (var attempt = 0; attempt < 600; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (await library.TryReindexAsync(ct)) return;
            await Task.Delay(500, ct);
        }
        throw new TimeoutException("Die Bibliothek war fünf Minuten lang mit einer anderen Indexierung beschäftigt.");
    }

    private static string UniqueBasePath(string directory, string proposedBase)
    {
        for (var index = 1; index <= 10_000; index++)
        {
            var name = index == 1 ? proposedBase : $"{proposedBase} ({index})";
            var candidate = Path.Combine(directory, name);
            if (!Directory.EnumerateFiles(directory, name + ".*", SearchOption.TopDirectoryOnly).Any()) return candidate;
        }
        throw new IOException("Es konnte kein freier Dateiname für den importierten Song gefunden werden.");
    }

    private static string RewriteSongId(string documentJson, Guid songId)
    {
        var document = JsonNode.Parse(documentJson) as JsonObject
                       ?? throw new InvalidDataException("Eine Lyrics-Version enthält kein Editor-Dokument.");
        document["songId"] = songId.ToString();
        return document.ToJsonString(JsonOptions);
    }

    private static string SafeName(string value, string fallback)
    {
        var safe = Regex.Replace(value.Trim(), "[\\x00-\\x1f<>:\"/\\\\|?*]+", "_").Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(safe)) safe = fallback;
        return safe.Length <= 140 ? safe : safe[..140].TrimEnd();
    }

    private void EnsureInsideLibrary(string path)
    {
        var root = Path.GetFullPath(settings.Get().LibraryPath).TrimEnd(Path.DirectorySeparatorChar) +
                   Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.GetFullPath(path).StartsWith(root, comparison))
            throw new InvalidDataException("Eine Songdatei liegt außerhalb der konfigurierten Bibliothek.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
    }

    private sealed record ProjectFile(string Path, string Suffix, string Role);
    private sealed record StagedFile(string Path, string Suffix, string Role);
    private sealed record StagedSong(PackageSong Song, IReadOnlyList<StagedFile> Files,
        IReadOnlyList<LyricsVersionDto> Versions);
    private sealed record PublishedSong(StagedSong Staged, string AudioPath);
    private sealed record PackageManifest(string Format, int SchemaVersion, DateTimeOffset CreatedAt,
        IReadOnlyList<PackageSong> Songs);
    private sealed record PackageSong(Guid SourceSongId, string Title, string Artist, string Album,
        double DurationSeconds, SongReviewStatus ReviewStatus, IReadOnlyList<PackageFile> Files,
        IReadOnlyList<PackageLyricsVersion> LyricsVersions);
    private sealed record PackageFile(string ArchivePath, string Suffix, string Role, long Size, string Sha256);
    private sealed record PackageLyricsVersion(string ArchivePath, long Size, string Sha256);
}
