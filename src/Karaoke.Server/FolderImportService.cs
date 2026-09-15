using System.Diagnostics;
using System.Text;
using Karaoke.Editor.Core;
using Karaoke.Contracts;

namespace Karaoke.Server;

/// <summary>
/// Imports complete MP3/FLAC folders without routing the audio through the Spotify/
/// YouTube downloader. Every file is processed in isolation and reaches the
/// library only after lyrics, both stems and an alignment report exist.
/// </summary>
public sealed class FolderImportService(
    IWebHostEnvironment environment,
    ServerSettingsService settings,
    LibraryRepository library,
    UsdbLyricsSourceService usdb,
    ILogger<FolderImportService> logger)
{
    public const string DefaultImportAlignmentProfile = "easyaligner-global";

    private static readonly HashSet<string> SupportedAudioExtensions =
        new([".mp3", ".flac"], StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private FolderImportStatus _status = Idle("Bereit für Audio-Ordnerimport (MP3/FLAC).");

    public FolderImportStatus GetStatus()
    {
        lock (_gate) return _status;
    }

    public FolderImportStatus? TryStart(FolderImportRequest request)
    {
        var sourcePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(request.SourcePath.Trim()));
        if (!Directory.Exists(sourcePath))
            throw new DirectoryNotFoundException($"Importordner nicht gefunden: {sourcePath}");

        var files = DiscoverAudioFiles(sourcePath, request.Recursive);
        if (files.Length == 0)
            throw new ArgumentException("Der gewählte Ordner enthält keine MP3- oder FLAC-Dateien.");

        lock (_gate)
        {
            if (_status.IsRunning) return null;
            _status = new FolderImportStatus(true, Guid.CreateVersion7(), sourcePath, 0, files.Length,
                0, 0, 0, 0, 0, "MP3- und FLAC-Dateien werden vorbereitet …", DateTimeOffset.UtcNow, null, []);
        }

        _ = Task.Run(() => ProcessFolderAsync(files));
        return GetStatus();
    }

    private async Task ProcessFolderAsync(IReadOnlyList<string> sourceFiles)
    {
        var output = new List<string>();
        var jobId = GetStatus().JobId ?? Guid.NewGuid();
        var stagingRoot = Path.Combine(Path.GetTempPath(), "neonstage-folder-import", jobId.ToString("N"));
        Directory.CreateDirectory(stagingRoot);
        try
        {
            var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));
            var libraryRoot = Path.GetFullPath(settings.Get().LibraryPath);
            Directory.CreateDirectory(libraryRoot);
            var alignerUrl = Environment.GetEnvironmentVariable("LRC_ALIGNER_URL")?.Trim();
            if (string.IsNullOrWhiteSpace(alignerUrl)) alignerUrl = "http://127.0.0.1:8081";
            var importedInThisRun = new HashSet<string>(StringComparer.Ordinal);

            foreach (var sourcePath in sourceFiles)
            {
                BeginFile(output, sourcePath);
                try
                {
                    var metadata = ReadMetadata(sourcePath);
                    if (string.IsNullOrWhiteSpace(metadata.Title) || string.IsNullOrWhiteSpace(metadata.Artist))
                        throw new InvalidOperationException("Die Audio-Metadaten enthalten weder einen verwertbaren Titel noch Interpreten.");

                    Add(output, $"METADATA: {metadata.Title} · {metadata.Artist}" +
                        (string.IsNullOrWhiteSpace(metadata.Album) ? string.Empty : $" · {metadata.Album}"));

                    var sourceAlreadyInLibrary = IsWithin(sourcePath, libraryRoot);
                    var identity = UsdbSongMatcher.Normalize(metadata.Title) + "\n" +
                                   UsdbSongMatcher.Normalize(metadata.Artist);
                    if (!sourceAlreadyInLibrary &&
                        (importedInThisRun.Contains(identity) ||
                         await library.ContainsSongAsync(metadata.Title, metadata.Artist,
                             CancellationToken.None)))
                    {
                        SkipFile(output, $"Bereits in der Bibliothek: {metadata.Title} · {metadata.Artist}");
                        continue;
                    }
                    var expectedBase = Path.Combine(libraryRoot, SafeName(metadata.Artist),
                        $"{SafeName(metadata.Title)} - {SafeName(metadata.Artist)}");
                    if (!sourceAlreadyInLibrary && HasCompleteProject(expectedBase))
                    {
                        SkipFile(output, $"Bereits vollständig vorhanden: {metadata.Title}");
                        continue;
                    }
                    var workDirectory = sourceAlreadyInLibrary
                        ? Path.GetDirectoryName(sourcePath)!
                        : Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(workDirectory);
                    var audioExtension = NormalizeAudioExtension(sourcePath);
                    var workPath = sourceAlreadyInLibrary
                        ? sourcePath
                        : Path.Combine(workDirectory,
                            $"{SafeName(metadata.Title)} - {SafeName(metadata.Artist)}{audioExtension}");
                    if (!sourceAlreadyInLibrary) File.Copy(sourcePath, workPath, overwrite: false);

                    var workBase = Path.Combine(Path.GetDirectoryName(workPath)!, Path.GetFileNameWithoutExtension(workPath));
                    await SeedLyricsAsync(sourcePath, workBase + ".lrc", metadata.EmbeddedLyrics);
                    CopyCoverSidecar(sourcePath, workBase + ".cover.jpg");

                    var lyricsPath = workBase + ".lrc";
                    if (!HasUsableLyrics(lyricsPath))
                    {
                        SetMessage("UltraStar-Timings werden in USDB gesucht …", .10);
                        var sourceResult = await usdb.ResolveWithFallbackAsync(new(workPath, metadata.Title,
                            metadata.Artist, metadata.Album, metadata.Duration), lyricsPath, "LRCLIB",
                            async _ =>
                            {
                                SetMessage("Lyrics werden über LRCLIB ermittelt …", .12);
                                var exit = await RunAsync(root, "dotnet", output,
                                    "run", "--project", Path.Combine(root, "LrcMatcher", "LrcMatcher.csproj"),
                                    "--no-build", "--", workPath, "--plain-fallback",
                                    "--max-duration-difference", "5", "--aligner-url", alignerUrl);
                                return exit == 0 && HasUsableLyrics(lyricsPath);
                            }, CancellationToken.None);
                        Add(output, sourceResult.Source == "USDB"
                            ? $"USDB: kompatible UltraStar-Version {sourceResult.Usdb.VersionId} übernommen."
                            : $"USDB: {sourceResult.Usdb.Reason} Fallback: {sourceResult.Source}.");
                        if (!sourceResult.Success)
                            Add(output, "Kein geeigneter lokaler oder LRCLIB-Text; GPU-Volltranskript wird erzeugt.");
                    }

                    int alignExit;
                    if (SelectPipelineRoute(lyricsPath) == FolderImportPipelineRoute.FullTranscript)
                    {
                        // Do not pass a failed/empty matcher result back as a canonical source. The
                        // recognition worker first creates timed words from the complete vocal signal
                        // and then invokes the configured EasyAligner Direct import profile.
                        File.Delete(lyricsPath);
                        SetMessage("Keine Lyrics vorhanden · GPU-Volltranskript mit Wortgrenzen läuft …", .22);
                        alignExit = await RunAsync(root, "/bin/bash", output,
                            Path.Combine(root, "scripts", "linux", "recognize-song-lyrics.sh"),
                            "--audio", workPath, "--language", "auto", "--url", alignerUrl,
                            "--no-canonical", "--no-reindex");
                        if (alignExit == 0)
                            Add(output, "Volltranskript und EasyAligner Direct wurden abgeschlossen.");
                    }
                    else
                    {
                        SetMessage("Lyrics vorhanden · EasyAligner mit GPU-Separation läuft …", .30);
                        var arguments = new List<string>
                        {
                            Path.Combine(root, "scripts", "linux", "align-library.sh"), "--force",
                            "--library", workDirectory, "--match", Path.GetFileName(workPath), "--url", alignerUrl
                        };
                        alignExit = await RunAsync(root, "/bin/bash", output, arguments.ToArray());
                    }
                    if (alignExit != 0)
                        throw new InvalidOperationException("Die GPU-Pipeline konnte den Titel nicht technisch fertigstellen.");

                    var required = new[]
                    {
                        workBase + ".lrc", workBase + ".pre-align.lrc", workBase + ".alignment.json",
                        workBase + ".instrumental.ogg", workBase + ".vocals.ogg", workBase + ".visuals.json"
                    };
                    var missing = required.Where(path => !File.Exists(path) || new FileInfo(path).Length == 0).ToArray();
                    if (missing.Length > 0)
                        throw new InvalidOperationException("Pipeline-Ausgaben fehlen: " +
                            string.Join(", ", missing.Select(Path.GetFileName)));

                    var review = !AlignmentIsPublishable(workBase + ".alignment.json");
                    if (!sourceAlreadyInLibrary)
                        PublishProject(workPath, workBase, metadata, libraryRoot);
                    importedInThisRun.Add(identity);
                    RemoveBuildIntermediates(workBase);
                    CompleteFile(output, review);
                    Add(output, review
                        ? $"REVIEW: {metadata.Title} ist technisch vollständig und wartet auf manuelle Prüfung."
                        : $"OK: {metadata.Title} wurde vollständig importiert.");
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Audio-Ordnerimport für {SourcePath} fehlgeschlagen", sourcePath);
                    FailFile(output, exception.Message);
                }
            }

            SetMessage("Bibliothek wird aktualisiert …", .98);
            await library.TryReindexAsync(CancellationToken.None);
            lock (_gate)
            {
                var message = _status.Failed == 0
                    ? "Audio-Ordnerimport abgeschlossen."
                    : "Audio-Ordnerimport mit einzelnen Fehlern abgeschlossen.";
                _status = _status with
                {
                    IsRunning = false, Percent = 100, Message = message,
                    FinishedAt = DateTimeOffset.UtcNow, RecentOutput = output.ToArray()
                };
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Audio-Ordnerimport ist abgebrochen");
            Add(output, exception.Message);
            lock (_gate)
                _status = _status with
                {
                    IsRunning = false, Percent = 100, Message = "Audio-Ordnerimport ist abgebrochen.",
                    FinishedAt = DateTimeOffset.UtcNow, RecentOutput = output.ToArray()
                };
        }
        finally
        {
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); }
            catch (Exception exception) { logger.LogDebug(exception, "Temporärer Importordner konnte nicht entfernt werden"); }
        }
    }

    private static AudioTags ReadMetadata(string path)
    {
        using var file = TagLib.File.Create(path);
        var title = file.Tag.Title?.Trim() ?? string.Empty;
        var artist = string.Join(", ", file.Tag.Performers
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()));
        var fileName = Path.GetFileNameWithoutExtension(path).Trim();
        var pieces = fileName.Split(" - ", 2, StringSplitOptions.TrimEntries);
        if (string.IsNullOrWhiteSpace(title)) title = pieces[0];
        if (string.IsNullOrWhiteSpace(artist))
            artist = pieces.Length > 1 ? pieces[1] : Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
        return new AudioTags(title, artist, file.Tag.Album?.Trim() ?? string.Empty,
            file.Tag.Lyrics?.Trim(), file.Properties.Duration);
    }

    private static async Task SeedLyricsAsync(string sourcePath, string targetLrc, string? embeddedLyrics)
    {
        var sourceBase = Path.Combine(Path.GetDirectoryName(sourcePath)!, Path.GetFileNameWithoutExtension(sourcePath));
        var lrc = FindCaseInsensitiveSidecar(sourceBase, ".lrc");
        if (lrc is not null)
        {
            if (!Path.GetFullPath(lrc).Equals(Path.GetFullPath(targetLrc), StringComparison.OrdinalIgnoreCase))
                File.Copy(lrc, targetLrc, overwrite: true);
            return;
        }
        var text = FindCaseInsensitiveSidecar(sourceBase, ".txt");
        var lyrics = text is null ? embeddedLyrics : await File.ReadAllTextAsync(text);
        if (string.IsNullOrWhiteSpace(lyrics)) return;
        await File.WriteAllTextAsync(targetLrc, NormalizeLyrics(lyrics), new UTF8Encoding(false));
    }

    private static string? FindCaseInsensitiveSidecar(string sourceBase, string extension)
    {
        var expected = sourceBase + extension;
        if (File.Exists(expected)) return expected;
        var directory = Path.GetDirectoryName(sourceBase)!;
        var name = Path.GetFileName(sourceBase);
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).FirstOrDefault(path =>
            Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileNameWithoutExtension(path).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeLyrics(string value)
    {
        if (UltraStarLyricsImporter.LooksLikeUltraStar(value))
            return UltraStarLyricsImporter.Parse(value).ToEnhancedLrc();
        var normalized = value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var timed = System.Text.RegularExpressions.Regex.IsMatch(normalized,
            @"(?m)^\[(?:\d{1,3}:)?\d{1,2}[.:]\d{1,3}\]");
        return (timed ? string.Empty : "[re:Plain/ID3 lyrics imported by Neon Stage; GPU alignment required]\n") +
               normalized + Environment.NewLine;
    }

    private static void CopyCoverSidecar(string sourcePath, string targetPath)
    {
        var sourceBase = Path.Combine(Path.GetDirectoryName(sourcePath)!, Path.GetFileNameWithoutExtension(sourcePath));
        var cover = FindCaseInsensitiveSidecar(sourceBase + ".cover", ".jpg");
        if (cover is not null && !Path.GetFullPath(cover).Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(cover, targetPath, overwrite: true);
    }

    private static void PublishProject(string workPath, string workBase, AudioTags metadata, string libraryRoot)
    {
        var destinationDirectory = Path.Combine(libraryRoot, SafeName(metadata.Artist));
        Directory.CreateDirectory(destinationDirectory);
        var audioExtension = NormalizeAudioExtension(workPath);
        var destinationAudio = UniquePath(destinationDirectory,
            $"{SafeName(metadata.Title)} - {SafeName(metadata.Artist)}{audioExtension}");
        var destinationBase = Path.Combine(destinationDirectory, Path.GetFileNameWithoutExtension(destinationAudio));
        var published = new List<string>();
        try
        {
            // Publish the audio file last. The library scanner can therefore never
            // observe a half-copied song project.
            foreach (var suffix in new[]
                     {
                         ".lrc", ".pre-align.lrc", ".alignment.json", ".instrumental.ogg", ".vocals.ogg",
                         ".visuals.json", ".transcription.json", ".cover.jpg"
                     })
            {
                var source = workBase + suffix;
                if (!File.Exists(source)) continue;
                var target = destinationBase + suffix;
                File.Copy(source, target, overwrite: false);
                published.Add(target);
            }
            File.Copy(workPath, destinationAudio, overwrite: false);
            published.Add(destinationAudio);
        }
        catch
        {
            foreach (var path in published) File.Delete(path);
            throw;
        }
    }

    private static void RemoveBuildIntermediates(string workBase)
    {
        foreach (var suffix in new[] { ".instrumental.flac", ".vocals.flac", ".stems.json" })
            File.Delete(workBase + suffix);
    }

    private static bool IsCompleteProject(string audioPath)
    {
        if (!File.Exists(audioPath)) return false;
        var projectBase = Path.Combine(Path.GetDirectoryName(audioPath)!, Path.GetFileNameWithoutExtension(audioPath));
        return new[] { ".lrc", ".alignment.json", ".instrumental.ogg", ".vocals.ogg", ".visuals.json" }
            .All(suffix => File.Exists(projectBase + suffix) && new FileInfo(projectBase + suffix).Length > 0);
    }

    private static bool HasCompleteProject(string projectBase)
    {
        var directory = Path.GetDirectoryName(projectBase)!;
        if (!Directory.Exists(directory)) return false;
        var name = Path.GetFileName(projectBase);
        return Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedAudioFile)
            .Where(path => Path.GetFileNameWithoutExtension(path)
                .Equals(name, StringComparison.OrdinalIgnoreCase))
            .Any(IsCompleteProject);
    }

    private static bool AlignmentIsPublishable(string reportPath)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(reportPath));
            return document.RootElement.TryGetProperty("quality", out var quality) &&
                   quality.TryGetProperty("publishable", out var publishable) && publishable.GetBoolean();
        }
        catch { return false; }
    }

    private async Task<int> RunAsync(string workingDirectory, string executable, List<string> output,
        params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Add(output, e.Data);
        process.ErrorDataReceived += (_, e) => Add(output, e.Data);
        if (!process.Start()) throw new InvalidOperationException($"{executable} konnte nicht gestartet werden.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private void BeginFile(List<string> output, string sourcePath)
    {
        lock (_gate)
        {
            var current = Math.Min(_status.Total, _status.Current + 1);
            _status = _status with
            {
                Current = current,
                Percent = Percent(current, _status.Total, .02),
                Message = $"Verarbeite {Path.GetFileName(sourcePath)} …"
            };
        }
        Add(output, string.Empty);
        Add(output, $"> {NormalizeAudioExtension(sourcePath).TrimStart('.').ToUpperInvariant()} " +
            $"{GetStatus().Current}/{GetStatus().Total}: {Path.GetFileName(sourcePath)}");
    }

    private void CompleteFile(List<string> output, bool review)
    {
        lock (_gate)
            _status = _status with
            {
                Succeeded = _status.Succeeded + (review ? 0 : 1),
                Review = _status.Review + (review ? 1 : 0),
                Percent = Percent(_status.Current, _status.Total, 1),
                RecentOutput = output.ToArray()
            };
    }

    private void FailFile(List<string> output, string message)
    {
        Add(output, "FEHLER: " + message);
        lock (_gate)
            _status = _status with
            {
                Failed = _status.Failed + 1,
                Percent = Percent(_status.Current, _status.Total, 1),
                Message = message,
                RecentOutput = output.ToArray()
            };
    }

    private void SkipFile(List<string> output, string message)
    {
        Add(output, "SKIP: " + message);
        lock (_gate)
            _status = _status with
            {
                Skipped = _status.Skipped + 1,
                Percent = Percent(_status.Current, _status.Total, 1),
                Message = message,
                RecentOutput = output.ToArray()
            };
    }

    private void Add(List<string> output, string? line)
    {
        if (line is null) return;
        lock (_gate)
        {
            output.Add(line);
            if (output.Count > 160) output.RemoveAt(0);
            var stage = line.StartsWith("METADATA:", StringComparison.Ordinal) ? .08 :
                line.Contains("LRCLIB", StringComparison.OrdinalIgnoreCase) ? .2 :
                line.Contains("GPU-Aligner", StringComparison.OrdinalIgnoreCase) ? .4 :
                line.Contains("Quality-Gate", StringComparison.OrdinalIgnoreCase) ? .85 : .3;
            _status = _status with
            {
                Message = string.IsNullOrWhiteSpace(line) ? _status.Message : line,
                Percent = Math.Max(_status.Percent, Percent(_status.Current, _status.Total, stage)),
                RecentOutput = output.ToArray()
            };
        }
    }

    private void SetMessage(string message, double stage)
    {
        lock (_gate)
            _status = _status with
            {
                Message = message,
                Percent = Math.Max(_status.Percent, Percent(_status.Current, _status.Total, stage))
            };
    }

    private static int Percent(int current, int total, double stage) => total == 0 ? 0 :
        (int)Math.Clamp(Math.Round(((Math.Max(1, current) - 1 + stage) / total) * 100), 0, 99);

    private static bool IsWithin(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray())
            .Trim().Trim('.');
        return string.IsNullOrWhiteSpace(result) ? "Unbenannt" : result;
    }

    internal static bool IsSupportedAudioFile(string path) =>
        SupportedAudioExtensions.Contains(Path.GetExtension(path));

    internal static string[] DiscoverAudioFiles(string sourcePath, bool recursive) =>
        Directory.EnumerateFiles(sourcePath, "*",
                recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(IsSupportedAudioFile)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static string NormalizeAudioExtension(string path)
    {
        var extension = Path.GetExtension(path);
        if (!SupportedAudioExtensions.Contains(extension))
            throw new ArgumentException($"Nicht unterstütztes Audioformat: {extension}");
        return extension.ToLowerInvariant();
    }

    internal static bool HasUsableLyrics(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0 &&
        !string.IsNullOrWhiteSpace(File.ReadAllText(path));

    internal static FolderImportPipelineRoute SelectPipelineRoute(string lyricsPath) =>
        HasUsableLyrics(lyricsPath)
            ? FolderImportPipelineRoute.EasyAligner
            : FolderImportPipelineRoute.FullTranscript;

    internal static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        var extension = Path.GetExtension(name);
        var baseName = Path.GetFileNameWithoutExtension(name);
        for (var index = 2; File.Exists(path); index++)
            path = Path.Combine(folder, $"{baseName} ({index}){extension}");
        return path;
    }

    private static FolderImportStatus Idle(string message) =>
        new(false, null, null, 0, 0, 0, 0, 0, 0, 0, message, null, null, []);

    private sealed record AudioTags(string Title, string Artist, string Album, string? EmbeddedLyrics,
        TimeSpan Duration);
}

internal enum FolderImportPipelineRoute
{
    EasyAligner,
    FullTranscript
}
