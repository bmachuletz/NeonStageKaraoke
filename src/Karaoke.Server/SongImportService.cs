using System.Diagnostics;
using System.Text;
using Karaoke.Editor.Core;

namespace Karaoke.Server;

public sealed record SongImportStatus(bool IsRunning, Guid? JobId, string? Title, string? Artist,
    int Percent, string Message, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, IReadOnlyList<string> RecentOutput);

public sealed class SongImportService(IWebHostEnvironment environment, ServerSettingsService settings,
    LibraryRepository library, UsdbLyricsSourceService usdb, ILogger<SongImportService> logger)
{
    public const string DefaultImportAlignmentProfile = "easyaligner-global";

    private readonly object _gate = new();
    private SongImportStatus _status = new(false, null, null, null, 0, "Bereit", null, null, null, []);

    public SongImportStatus GetStatus() { lock (_gate) return _status; }

    public async Task<SongImportStatus?> TryQueueAsync(IFormFile audio, string title, string artist,
        string? lyrics, bool useLrclib, CancellationToken cancellationToken)
    {
        if (!Path.GetExtension(audio.FileName).Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Der Projektimport unterstützt derzeit MP3-Dateien.");
        var ultraStar = UltraStarLyricsImporter.LooksLikeUltraStar(lyrics)
            ? UltraStarLyricsImporter.Parse(lyrics!)
            : null;
        title = string.IsNullOrWhiteSpace(title)
            ? ultraStar?.Metadata.Title ?? Path.GetFileNameWithoutExtension(audio.FileName)
            : title.Trim();
        artist = string.IsNullOrWhiteSpace(artist)
            ? ultraStar?.Metadata.Artist ?? "Unbekannter Interpret"
            : artist.Trim();
        if (await library.ContainsSongAsync(title, artist, cancellationToken))
            throw new ArgumentException($"{title} · {artist} ist bereits in der Bibliothek.");
        lock (_gate)
        {
            if (_status.IsRunning) return null;
            _status = new(true, Guid.CreateVersion7(), title, artist, 1, "MP3 wird in die Bibliothek importiert …",
                DateTimeOffset.UtcNow, null, null, []);
        }
        var folder = Path.Combine(settings.Get().LibraryPath, SafeName(artist));
        Directory.CreateDirectory(folder);
        var audioPath = UniquePath(folder, $"{SafeName(title)} - {SafeName(artist)}.mp3");
        await using (var target = File.Create(audioPath)) await audio.CopyToAsync(target, cancellationToken);
        var lrcPath = Path.ChangeExtension(audioPath, ".lrc");
        if (!string.IsNullOrWhiteSpace(lyrics))
            await File.WriteAllTextAsync(lrcPath, ultraStar?.ToEnhancedLrc() ?? NormalizeLyrics(lyrics),
                new UTF8Encoding(false), cancellationToken);
        _ = Task.Run(() => ProcessAsync(audioPath, lrcPath, useLrclib && string.IsNullOrWhiteSpace(lyrics)));
        return GetStatus();
    }

    private async Task ProcessAsync(string audioPath, string lrcPath, bool useLrclib)
    {
        var output = new List<string>();
        try
        {
            var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));
            var fullTranscriptCompleted = false;
            if (useLrclib)
            {
                Set(5, "UltraStar-Timings werden in USDB gesucht …");
                using (var audio = TagLib.File.Create(audioPath))
                {
                    var sourceResult = await usdb.ResolveWithFallbackAsync(new(audioPath,
                        GetStatus().Title ?? audio.Tag.Title ?? Path.GetFileNameWithoutExtension(audioPath),
                        GetStatus().Artist ?? string.Join(", ", audio.Tag.Performers),
                        audio.Tag.Album, audio.Properties.Duration), lrcPath, "LRCLIB", async _ =>
                        {
                            Set(8, "LRCLIB-Matching wird gestartet …");
                            var exit = await RunAsync(root, "dotnet", output, "run", "--project",
                                Path.Combine(root, "LrcMatcher"), "--", audioPath, "--overwrite", "--plain-fallback");
                            return exit == 0 && HasLyrics(lrcPath);
                        }, CancellationToken.None);
                    Add(output, sourceResult.Source == "USDB"
                        ? $"USDB: kompatible UltraStar-Version {sourceResult.Usdb.VersionId} übernommen."
                        : $"USDB: {sourceResult.Usdb.Reason} Fallback: {sourceResult.Source}.");
                }
            }
            if (useLrclib && !HasLyrics(lrcPath))
                Add(output, "Kein geeigneter LRCLIB-Treffer; es wird ein Volltranskript aus dem Song erzeugt.");
            int result;
            if (!HasLyrics(lrcPath))
            {
                Set(15, "Keine Lyrics vorhanden · GPU-Volltranskript läuft …");
                var recognitionScript = Path.Combine(root, "scripts", "linux", "recognize-song-lyrics.sh");
                result = await RunAsync(root, "/bin/bash", output, recognitionScript, "--audio", audioPath,
                    "--language", "auto", "--no-canonical", "--no-reindex");
                fullTranscriptCompleted = result == 0;
            }
            else
            {
                Set(20, "GPU-Separation und EasyAligner laufen …");
                var script = Path.Combine(root, "scripts", "linux", "align-library.sh");
                var arguments = new List<string> { script, "--force", "--library",
                    settings.Get().LibraryPath, "--match", Path.GetFileName(audioPath) };
                result = await RunAsync(root, "/bin/bash", output, arguments.ToArray());
            }
            if (result != 0) throw new InvalidOperationException("GPU-Pipeline hat den Song nicht akzeptiert.");
            if (fullTranscriptCompleted) Add(output, "Volltranskript und Wort-/Silbenalignment wurden abgeschlossen.");
            Set(95, "Bibliothek wird aktualisiert …");
            await library.TryReindexAsync(CancellationToken.None);
            lock (_gate) _status = _status with { IsRunning = false, Percent = 100, Message = "Songprojekt ist bereit.",
                FinishedAt = DateTimeOffset.UtcNow, ExitCode = 0, RecentOutput = output.ToArray() };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Songimport {AudioPath} fehlgeschlagen", audioPath);
            Add(output, exception.Message);
            lock (_gate) _status = _status with { IsRunning = false, Percent = 100, Message = "Songimport fehlgeschlagen.",
                FinishedAt = DateTimeOffset.UtcNow, ExitCode = -1, RecentOutput = output.ToArray() };
        }
    }

    private async Task<int> RunAsync(string workingDirectory, string executable, List<string> output, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Add(output, e.Data);
        process.ErrorDataReceived += (_, e) => Add(output, e.Data);
        if (!process.Start()) throw new InvalidOperationException($"{executable} konnte nicht gestartet werden.");
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private void Add(List<string> output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            output.Add(line); if (output.Count > 120) output.RemoveAt(0);
            _status = _status with { Message = line, RecentOutput = output.ToArray(), Percent = Math.Min(90, _status.Percent + 1) };
        }
    }
    private void Set(int percent, string message) { lock (_gate) _status = _status with { Percent = percent, Message = message }; }
    private static string NormalizeLyrics(string value)
    {
        if (UltraStarLyricsImporter.LooksLikeUltraStar(value))
            return UltraStarLyricsImporter.Parse(value).ToEnhancedLrc();
        return value.Contains('[', StringComparison.Ordinal) || value.Contains('<', StringComparison.Ordinal)
            ? value.Trim() + Environment.NewLine
            : "[re:Plain lyrics imported by Neon Stage; GPU alignment required]" + Environment.NewLine + value.Trim() + Environment.NewLine;
    }
    private static bool HasLyrics(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(result) ? "Unbenannt" : result;
    }
    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        for (var index = 2; File.Exists(path); index++) path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({index}).mp3");
        return path;
    }
}
