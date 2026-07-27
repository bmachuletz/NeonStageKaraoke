using System.Diagnostics;
using System.Text;
using Karaoke.Editor.Core;

namespace Karaoke.Server;

public sealed record SongImportStatus(bool IsRunning, Guid? JobId, string? Title, string? Artist,
    int Percent, string Message, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, IReadOnlyList<string> RecentOutput);

public sealed class SongImportService(IWebHostEnvironment environment, ServerSettingsService settings,
    LibraryRepository library, ILogger<SongImportService> logger)
{
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
            if (useLrclib)
            {
                Set(8, "LRCLIB-Matching wird gestartet …");
                var exit = await RunAsync(root, "dotnet", output, "run", "--project", Path.Combine(root, "LrcMatcher"),
                    "--", audioPath, "--overwrite", "--plain-fallback");
                if (exit != 0 || !File.Exists(lrcPath)) throw new InvalidOperationException("Für den Song konnten keine geeigneten Lyrics geladen werden.");
            }
            if (!File.Exists(lrcPath)) throw new InvalidOperationException("Für die Trennung und Ausrichtung werden Lyrics benötigt.");
            Set(20, "GPU-Separation und Lyrics-Alignment laufen …");
            var script = Path.Combine(root, "scripts", "linux", "align-library.sh");
            var result = await RunAsync(root, "/bin/bash", output, script, "--force", "--library",
                settings.Get().LibraryPath, "--match", Path.GetFileName(audioPath));
            if (result != 0) throw new InvalidOperationException("GPU-Pipeline hat den Song nicht akzeptiert.");
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
