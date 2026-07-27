using System.Diagnostics;

namespace Karaoke.Server;

public sealed record SongRealignmentStatus(bool IsRunning, Guid? JobId, Guid? SongId, string? SongTitle,
    int Percent, string Message, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, IReadOnlyList<string> RecentOutput);

internal sealed class SongRealignmentService(IWebHostEnvironment environment, ServerSettingsService settings,
    LibraryRepository library, LyricsAlignmentVersionService alignmentVersions,
    ILogger<SongRealignmentService> logger)
{
    private readonly object _gate = new();
    private SongRealignmentStatus _status = new(false, null, null, null, 0, "Bereit", null, null, null, []);

    public SongRealignmentStatus GetStatus() { lock (_gate) return _status; }

    public async Task<bool?> TryStartAsync(Guid songId, CancellationToken cancellationToken)
    {
        var song = await library.GetAsync(songId, cancellationToken);
        var audio = await library.GetAudioFileAsync(songId, cancellationToken);
        if (song is null || audio is null) return null;
        lock (_gate)
        {
            if (_status.IsRunning) return false;
            _status = new(true, Guid.CreateVersion7(), songId, $"{song.Title} · {song.Artist}", 1,
                "GPU-Neuausrichtung wird gestartet …", DateTimeOffset.UtcNow, null, null, []);
        }
        _ = Task.Run(() => RunAsync(songId, audio.Value.Path));
        return true;
    }

    public bool TryStartAll()
    {
        lock (_gate)
        {
            if (_status.IsRunning) return false;
            _status = new(true, Guid.CreateVersion7(), null, "Gesamte Bibliothek", 1,
                "GPU-Neuausrichtung der gesamten Bibliothek wird gestartet …", DateTimeOffset.UtcNow, null, null, []);
        }
        _ = Task.Run(() => RunAsync(null, null));
        return true;
    }

    private async Task RunAsync(Guid? songId, string? audioPath)
    {
        var output = new List<string>();
        try
        {
            Guid jobId;
            lock (_gate) jobId = _status.JobId
                                ?? throw new InvalidOperationException("Dem Alignment fehlt eine Job-ID.");
            var fingerprints = songId is null
                ? await alignmentVersions.CaptureSourceFingerprintsAsync(CancellationToken.None)
                : null;
            var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));
            var script = Path.Combine(root, "scripts", "linux", "align-library.sh");
            var start = new ProcessStartInfo("/bin/bash")
            {
                WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { script, "--force", "--library", settings.Get().LibraryPath })
                start.ArgumentList.Add(argument);
            if (!string.IsNullOrWhiteSpace(audioPath))
            {
                start.ArgumentList.Add("--match");
                start.ArgumentList.Add(Path.GetFileName(audioPath));
            }
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, args) => Add(output, args.Data);
            process.ErrorDataReceived += (_, args) => Add(output, args.Data);
            if (!process.Start()) throw new InvalidOperationException("GPU-Alignment konnte nicht gestartet werden.");
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"GPU-Pipeline wurde mit Code {process.ExitCode} beendet.");
            Set(96, "Alignment wird als Lyrics-Version gesichert …");
            int createdVersions;
            if (songId is { } id)
            {
                await alignmentVersions.SnapshotAsync(id, jobId.ToString("N"), CancellationToken.None);
                createdVersions = 1;
            }
            else
            {
                createdVersions = await alignmentVersions.SnapshotChangedAsync(
                    fingerprints!, jobId.ToString("N"), CancellationToken.None);
            }
            Add(output, $"{createdVersions} Lyrics-Version(en) dauerhaft gespeichert.");
            Set(98, "Bibliothek wird aktualisiert …");
            await library.TryReindexAsync(CancellationToken.None);
            lock (_gate) _status = _status with { IsRunning = false, Percent = 100,
                Message = createdVersions == 1
                    ? "Neues Alignment ist als versionierter Review-Stand bereit."
                    : $"{createdVersions} neue Alignments sind als versionierte Review-Stände bereit.",
                FinishedAt = DateTimeOffset.UtcNow,
                ExitCode = 0, RecentOutput = output.ToArray() };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Neuausrichtung für Song {SongId} fehlgeschlagen", songId);
            Add(output, exception.Message);
            lock (_gate) _status = _status with { IsRunning = false, Percent = 100,
                Message = "Neuausrichtung fehlgeschlagen.", FinishedAt = DateTimeOffset.UtcNow,
                ExitCode = -1, RecentOutput = output.ToArray() };
        }
    }

    private void Add(List<string> output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            output.Add(line); if (output.Count > 120) output.RemoveAt(0);
            var percent = _status.Percent;
            var marker = System.Text.RegularExpressions.Regex.Match(line, @"\b(?<value>\d{1,3})%\b");
            if (marker.Success) percent = Math.Clamp(int.Parse(marker.Groups["value"].Value), percent, 95);
            _status = _status with { Message = line.Trim(), Percent = percent, RecentOutput = output.ToArray() };
        }
    }

    private void Set(int percent, string message) { lock (_gate) _status = _status with { Percent = percent, Message = message }; }

    public Task<Karaoke.Contracts.LyricsVersionDto> SnapshotCurrentAsync(Guid songId,
        CancellationToken cancellationToken) =>
        alignmentVersions.SnapshotAsync(songId, $"manual-snapshot-{Guid.CreateVersion7():N}", cancellationToken);
}
