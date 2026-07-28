using System.Diagnostics;

namespace Karaoke.Server;

public sealed record SongLyricsRecognitionStatus(bool IsRunning, Guid? JobId, Guid? SongId,
    string? SongTitle, int Percent, string Message, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, int? ExitCode, IReadOnlyList<string> RecentOutput);

internal sealed class SongLyricsRecognitionService(
    IWebHostEnvironment environment,
    LibraryRepository library,
    LyricsAlignmentVersionService alignmentVersions,
    ILogger<SongLyricsRecognitionService> logger)
{
    private readonly object _gate = new();
    private SongLyricsRecognitionStatus _status = new(false, null, null, null, 0,
        "Bereit", null, null, null, []);

    public SongLyricsRecognitionStatus GetStatus()
    {
        lock (_gate) return _status;
    }

    public async Task<bool?> TryStartAsync(Guid songId, CancellationToken cancellationToken)
    {
        var song = await library.GetAsync(songId, cancellationToken);
        var audio = await library.GetAudioFileAsync(songId, cancellationToken);
        if (song is null || audio is null) return null;
        lock (_gate)
        {
            if (_status.IsRunning) return false;
            _status = new(true, Guid.CreateVersion7(), songId, $"{song.Title} · {song.Artist}", 1,
                "Volltext-Erkennung wird in die GPU-Queue gestellt …", DateTimeOffset.UtcNow,
                null, null, []);
        }
        _ = Task.Run(() => RunAsync(songId, audio.Value.Path));
        return true;
    }

    private async Task RunAsync(Guid songId, string audioPath)
    {
        var output = new List<string>();
        try
        {
            Guid jobId;
            lock (_gate) jobId = _status.JobId
                                ?? throw new InvalidOperationException("Dem Volltext-Job fehlt eine Job-ID.");
            var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));
            var script = Path.Combine(root, "scripts", "linux", "recognize-song-lyrics.sh");
            if (!File.Exists(script))
                throw new FileNotFoundException("recognize-song-lyrics.sh wurde nicht gefunden.", script);
            var start = new ProcessStartInfo("/bin/bash")
            {
                WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { script, "--audio", audioPath, "--language", "auto" })
                start.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, args) => Add(output, args.Data);
            process.ErrorDataReceived += (_, args) => Add(output, args.Data);
            if (!process.Start())
                throw new InvalidOperationException("Volltext-Worker konnte nicht gestartet werden.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"Volltext-Pipeline wurde mit Code {process.ExitCode} beendet.");

            Set(97, "Erkannte Lyrics werden versioniert und die Bibliothek wird aktualisiert …");
            await library.TryReindexAsync(CancellationToken.None);
            await alignmentVersions.SnapshotAsync(
                songId, $"full-transcription-{jobId:N}", CancellationToken.None);
            lock (_gate)
                _status = _status with
                {
                    IsRunning = false, Percent = 100,
                    Message = "Vollständig erkannte Lyrics sind als neuer Review-Stand bereit.",
                    FinishedAt = DateTimeOffset.UtcNow, ExitCode = 0,
                    RecentOutput = output.ToArray()
                };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Vollständige Lyrics-Erkennung für Song {SongId} fehlgeschlagen", songId);
            Add(output, exception.Message);
            lock (_gate)
                _status = _status with
                {
                    IsRunning = false, Percent = 100, Message = "Vollständige Lyrics-Erkennung fehlgeschlagen.",
                    FinishedAt = DateTimeOffset.UtcNow, ExitCode = -1, RecentOutput = output.ToArray()
                };
        }
    }

    private void Add(List<string> output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            output.Add(line);
            if (output.Count > 160) output.RemoveAt(0);
            var percent = _status.Percent;
            var marker = System.Text.RegularExpressions.Regex.Match(line, @"\[(?<value>\d{1,3})%\]");
            if (marker.Success) percent = Math.Clamp(int.Parse(marker.Groups["value"].Value), percent, 96);
            _status = _status with
            {
                Message = line.Trim(), Percent = percent, RecentOutput = output.ToArray()
            };
        }
    }

    private void Set(int percent, string message)
    {
        lock (_gate) _status = _status with { Percent = percent, Message = message };
    }
}
