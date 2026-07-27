using System.Diagnostics;

namespace Karaoke.Server;

public sealed record WishlistProcessingStatus(bool IsRunning, Guid? EventId, string? EventName, DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt, int? ExitCode, string Message, IReadOnlyList<string> RecentOutput,
    int Current, int Total, int Percent);

public sealed class WishlistProcessingService(IWebHostEnvironment environment, ServerSettingsService settings, ILogger<WishlistProcessingService> logger)
{
    private readonly object _gate = new();
    private WishlistProcessingStatus _status = new(false, null, null, null, null, null, "Bereit", [], 0, 0, 0);

    public WishlistProcessingStatus GetStatus() { lock (_gate) return _status; }

    public bool TryStart(Karaoke.Contracts.KaraokeEventDto? karaokeEvent, int maximum, int availableWishes,
        Guid? wishId = null, bool allEvents = false)
    {
        lock (_gate)
        {
            if (_status.IsRunning) return false;
            var total = maximum > 0 ? Math.Min(maximum, availableWishes) : availableWishes;
            _status = new(true, karaokeEvent?.Id, allEvents ? "Alle Sessions" : karaokeEvent?.Name, DateTimeOffset.UtcNow, null, null,
                "Wunschlisten-Worker wird gestartet …", [], 0, total, 0);
        }
        _ = Task.Run(() => RunAsync(karaokeEvent, maximum, wishId, allEvents));
        return true;
    }

    private async Task RunAsync(Karaoke.Contracts.KaraokeEventDto? karaokeEvent, int maximum, Guid? wishId, bool allEvents)
    {
        var output = new List<string>();
        try
        {
            var repositoryRoot = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));
            var script = Path.Combine(repositoryRoot, "scripts", "linux", "process-wishlist.sh");
            if (!File.Exists(script)) throw new FileNotFoundException("process-wishlist.sh wurde nicht gefunden.", script);
            var start = new ProcessStartInfo("/bin/bash")
            {
                WorkingDirectory = repositoryRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(script);
            if (allEvents) start.ArgumentList.Add("--all-events");
            else if (karaokeEvent is not null)
            {
                start.ArgumentList.Add("--event-token"); start.ArgumentList.Add(karaokeEvent.InviteToken);
            }
            if (wishId is not null) { start.ArgumentList.Add("--wish-id"); start.ArgumentList.Add(wishId.Value.ToString()); }
            start.ArgumentList.Add("--server"); start.ArgumentList.Add("http://127.0.0.1:5274");
            start.ArgumentList.Add("--library"); start.ArgumentList.Add(settings.Get().LibraryPath);
            if (maximum > 0) { start.ArgumentList.Add("--max"); start.ArgumentList.Add(maximum.ToString()); }
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, args) => AddLine(output, args.Data);
            process.ErrorDataReceived += (_, args) => AddLine(output, args.Data);
            if (!process.Start()) throw new InvalidOperationException("Wunschlisten-Worker konnte nicht gestartet werden.");
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            lock (_gate)
                _status = new(false, karaokeEvent?.Id, allEvents ? "Alle Sessions" : karaokeEvent?.Name, _status.StartedAt, DateTimeOffset.UtcNow,
                    process.ExitCode, process.ExitCode == 0 ? "Wunschliste wurde abgearbeitet." : "Worker mit Fehler beendet.",
                    output.ToArray(), _status.Total, _status.Total, 100);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Wunschlisten-Worker für Event {EventId} ist fehlgeschlagen", karaokeEvent?.Id);
            AddLine(output, exception.Message);
            lock (_gate)
                _status = new(false, karaokeEvent?.Id, allEvents ? "Alle Sessions" : karaokeEvent?.Name, _status.StartedAt, DateTimeOffset.UtcNow,
                    -1, "Worker konnte nicht ausgeführt werden.", output.ToArray(), _status.Current, _status.Total,
                    _status.Total == 0 ? 0 : Math.Min(99, _status.Percent));
        }
    }

    private void AddLine(List<string> output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            output.Add(line);
            if (output.Count > 40) output.RemoveAt(0);
            var current = _status.Current;
            var stage = 0d;
            if (line.StartsWith("Wunsch:", StringComparison.Ordinal)) { current = Math.Min(_status.Total, current + 1); stage = .02; }
            else if (line.StartsWith("Spotify:", StringComparison.Ordinal)) stage = .08;
            else if (line.StartsWith("LRCLIB-Matching:", StringComparison.Ordinal)) stage = .25;
            else if (line.StartsWith("GPU-Wort-/Silbenalignment:", StringComparison.Ordinal)) stage = .48;
            else if (line.StartsWith("Quality-Gate akzeptiert:", StringComparison.Ordinal)) stage = .9;
            else if (line.StartsWith("Komplett importiert", StringComparison.Ordinal) || line.Contains("bleibt erhalten", StringComparison.OrdinalIgnoreCase)) stage = 1;
            var percent = _status.Total == 0 ? 0 : (int)Math.Clamp(Math.Round(((Math.Max(1, current) - 1 + stage) / _status.Total) * 100), 0, 99);
            _status = _status with { Message = line, RecentOutput = output.ToArray(), Current = current, Percent = Math.Max(_status.Percent, percent) };
        }
    }
}
