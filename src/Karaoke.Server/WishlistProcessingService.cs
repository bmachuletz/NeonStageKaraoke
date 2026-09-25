using System.Diagnostics;
using Karaoke.Contracts;

namespace Karaoke.Server;

public sealed record WishlistProcessingStatus(bool IsRunning, Guid? EventId, string? EventName,
    DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int? ExitCode, string Message,
    IReadOnlyList<string> RecentOutput, int Current, int Total, int Percent);

public sealed class WishlistProcessingService(
    ServerSettingsService settings,
    WishlistRepository wishes,
    EventRepository events,
    LibraryRepository library,
    UsdbLyricsSourceService usdb,
    YtDlpService ytDlp,
    QobuzDownloadService qobuz,
    AlignerPipelineService aligner,
    IWebHostEnvironment environment,
    ILogger<WishlistProcessingService> logger)
{
    private readonly object _gate = new();
    private WishlistProcessingStatus _status = new(false, null, null, null, null, null,
        "Bereit", [], 0, 0, 0);

    public WishlistProcessingStatus GetStatus() { lock (_gate) return _status; }

    public bool TryStart(KaraokeEventDto? karaokeEvent, int maximum, int availableWishes,
        Guid? wishId = null, bool allEvents = false)
    {
        lock (_gate)
        {
            if (_status.IsRunning) return false;
            var total = maximum > 0 ? Math.Min(maximum, availableWishes) : availableWishes;
            _status = new(true, karaokeEvent?.Id, allEvents ? "Alle Sessions" : karaokeEvent?.Name,
                DateTimeOffset.UtcNow, null, null, "Wunschlisten-Worker wird gestartet …", [], 0, total, 0);
        }
        _ = Task.Run(() => RunAsync(karaokeEvent, maximum, wishId, allEvents));
        return true;
    }

    private async Task RunAsync(KaraokeEventDto? karaokeEvent, int maximum, Guid? wishId, bool allEvents)
    {
        var output = new List<string>(); var processed = 0; var failed = 0;
        var imported = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var queue = new List<(Guid EventId, WishDto Wish)>();
            if (allEvents)
            {
                foreach (var item in await events.GetAllAsync(CancellationToken.None))
                    queue.AddRange((await wishes.GetAsync(item.Id, CancellationToken.None))
                        .Select(wish => (item.Id, wish)));
            }
            else if (karaokeEvent is not null)
                queue.AddRange((await wishes.GetAsync(karaokeEvent.Id, CancellationToken.None))
                    .Select(wish => (karaokeEvent.Id, wish)));
            queue = queue.Where(item => wishId is null || item.Wish.Id == wishId).Reverse().ToList();
            if (maximum > 0) queue = queue.Take(maximum).ToList();
            SetTotal(queue.Count);

            foreach (var item in queue)
            {
                BeginWish(output, item.Wish);
                var identity = UsdbSongMatcher.Normalize(item.Wish.Track.Title) + "\n" +
                               UsdbSongMatcher.Normalize(item.Wish.Track.Artist);
                try
                {
                    if (imported.Contains(identity) || await library.ContainsSongAsync(item.Wish.Track.Title,
                            item.Wish.Track.Artist, CancellationToken.None))
                    {
                        Add(output, "Bereits in der Bibliothek; Wunsch wird entfernt.", .95);
                        await wishes.RemoveAsync(item.EventId, item.Wish.Id, CancellationToken.None);
                        processed++; CompleteWish(); continue;
                    }
                    await ProcessWishAsync(item.EventId, item.Wish, output, CancellationToken.None);
                    imported.Add(identity); processed++; CompleteWish();
                }
                catch (Exception exception)
                {
                    failed++;
                    logger.LogWarning(exception, "Wunsch {WishId} konnte nicht verarbeitet werden", item.Wish.Id);
                    Add(output, exception.Message + " · Wunsch bleibt erhalten.", 1);
                    CompleteWish();
                }
            }
            if (processed > 0) await library.TryReindexAsync(CancellationToken.None);
            Complete(failed == 0 ? 0 : 1,
                $"Abgeschlossen: {processed} erfolgreich, {failed} fehlgeschlagen.", output);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Wunschlisten-Worker ist fehlgeschlagen");
            Add(output, exception.Message, 1);
            Complete(-1, "Worker konnte nicht ausgeführt werden.", output);
        }
    }

    private async Task ProcessWishAsync(Guid eventId, WishDto wish, List<string> output,
        CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "neonstage-wish", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        string? destination = null;
        try
        {
            string downloaded;
            if (wish.Track.DownloadSource == AudioDownloadSource.Qobuz)
            {
                Add(output, "Download-Provider: Qobuz (autorisierter Kaufdownload)", .08);
                downloaded = await qobuz.DownloadAsync(wish.Track, temporary, cancellationToken);
            }
            else
            {
                var query = YouTubeQuery(wish.Track);
                Add(output, wish.Track.Source == AudioCatalogSource.YouTube
                    ? "Download-Provider: Nur YouTube" : "Download-Provider: YouTube-Titelsuche", .08);
                downloaded = await ytDlp.DownloadAudioAsync(query, temporary, cancellationToken);
            }
            var folder = Path.Combine(settings.Get().LibraryPath, SafeName(wish.Track.Artist));
            Directory.CreateDirectory(folder);
            destination = UniquePath(folder, $"{SafeName(wish.Track.Title)} - {SafeName(wish.Track.Artist)}" +
                                             Path.GetExtension(downloaded).ToLowerInvariant());
            File.Move(downloaded, destination);
            WriteTags(destination, wish.Track);
            await wishes.SetAudioCandidateAsync(eventId, wish.Id, destination,
                "Audio gefunden · Lyrics werden verarbeitet", cancellationToken);
            var basePath = Path.Combine(Path.GetDirectoryName(destination)!,
                Path.GetFileNameWithoutExtension(destination));
            var lyrics = basePath + ".lrc";

            Add(output, "USDB/LRCLIB: beste Lyrics-Quelle wird gesucht …", .18);
            using (var audio = TagLib.File.Create(destination))
            {
                var resolved = await usdb.ResolveWithFallbackAsync(new(destination, wish.Track.Title,
                    wish.Track.Artist, wish.Track.Album, audio.Properties.Duration), lyrics, "LRCLIB",
                    _ => RunLrcMatcherAsync(destination, output, cancellationToken), cancellationToken);
                Add(output, resolved.Source == "USDB"
                    ? $"USDB: kompatible UltraStar-Version {resolved.Usdb.VersionId} übernommen."
                    : $"USDB: {resolved.Usdb.Reason} Fallback: {resolved.Source}.", .28);
            }

            AlignmentPipelineResult result;
            if (!HasFile(lyrics))
            {
                await wishes.SetAudioCandidateAsync(eventId, wish.Id, destination,
                    "Audio gefunden · Volltranskript wird erzeugt", cancellationToken);
                result = await aligner.TranscribeAndAlignAsync(destination, basePath,
                    (percent, message) => Add(output, message, .30 + percent / 100d * .62), cancellationToken);
            }
            else
            {
                result = await aligner.AlignAsync(destination, lyrics, basePath,
                    (percent, message) => Add(output, message, .30 + percent / 100d * .62), cancellationToken);
            }
            foreach (var required in new[] { ".lrc", ".pre-align.lrc", ".alignment.json",
                         ".instrumental.ogg", ".vocals.ogg", ".visuals.json" })
                if (!HasFile(basePath + required))
                    throw new InvalidDataException("Erforderliche Bibliotheksdatei fehlt: " +
                                                   Path.GetFileName(basePath + required));
            File.Delete(basePath + ".instrumental.flac"); File.Delete(basePath + ".vocals.flac");
            File.Delete(basePath + ".stems.json");
            Add(output, result.Publishable
                ? $"Quality-Gate akzeptiert: Score {result.Score:0.###}, Stufe {result.Grade}"
                : $"Technisch vollständig; manuelles Review (Score {result.Score:0.###}, Stufe {result.Grade}).", .96);
            if (!await wishes.RemoveAsync(eventId, wish.Id, cancellationToken))
                throw new InvalidOperationException("Der fertige Wunsch konnte nicht aus der Wunschliste entfernt werden.");
            Add(output, "Komplett importiert und aus der Wunschliste entfernt: " + wish.Track.Title, 1);
        }
        finally
        {
            try { if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true); }
            catch (IOException exception) { logger.LogDebug(exception, "Temporärer Wunschordner konnte nicht entfernt werden"); }
            if (destination is not null && File.Exists(destination) && new FileInfo(destination).Length == 0)
                File.Delete(destination);
        }
    }

    private async Task<bool> RunLrcMatcherAsync(string audioPath, List<string> output,
        CancellationToken cancellationToken)
    {
        var executable = ExternalToolLocator.LrcMatcher(environment);
        if (executable is null)
        {
            Add(output, "LRC-Matcher fehlt; Volltranskript wird verwendet.", .25);
            return false;
        }
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { audioPath, "--plain-fallback", "--max-duration-difference", "5",
                     "--aligner-url", AlignerUrl() }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("LRC-Matcher konnte nicht gestartet werden.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        foreach (var line in ((await stdout) + Environment.NewLine + (await stderr))
                     .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).TakeLast(8))
            Add(output, line, .26);
        return process.ExitCode == 0 && HasFile(Path.ChangeExtension(audioPath, ".lrc"));
    }

    internal static string YouTubeQuery(SpotifyTrackDto track)
    {
        if (track.Source == AudioCatalogSource.YouTube)
        {
            var id = track.Id.StartsWith("youtube:", StringComparison.Ordinal) ? track.Id[8..] : string.Empty;
            if (id.Length != 11 || id.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
                throw new InvalidDataException("Der Wunsch enthält keine gültige YouTube-ID.");
            return "https://www.youtube.com/watch?v=" + id;
        }
        return $"ytsearch1:{track.Artist} - {track.Title} audio";
    }

    private static void WriteTags(string path, SpotifyTrackDto track)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.Title = track.Title; file.Tag.Performers = [track.Artist]; file.Tag.Album = track.Album;
        file.Save();
    }

    private void BeginWish(List<string> output, WishDto wish)
    {
        lock (_gate)
        {
            output.Add($"Wunsch: {wish.Track.Title} · {wish.Track.Artist}"); Trim(output);
            _status = _status with { Current = Math.Min(_status.Total, _status.Current + 1),
                Message = output[^1], RecentOutput = output.ToArray() };
        }
    }

    private void Add(List<string> output, string message, double stage)
    {
        lock (_gate)
        {
            output.Add(message); Trim(output);
            var percent = _status.Total == 0 ? 0 : (int)Math.Clamp(Math.Round(
                ((_status.Current - 1 + Math.Clamp(stage, 0, 1)) / _status.Total) * 100), 0, 99);
            _status = _status with { Message = message, RecentOutput = output.ToArray(),
                Percent = Math.Max(_status.Percent, percent) };
        }
    }

    private void CompleteWish() { }
    private void SetTotal(int total) { lock (_gate) _status = _status with { Total = total }; }
    private void Complete(int exitCode, string message, List<string> output)
    {
        lock (_gate) _status = _status with { IsRunning = false, FinishedAt = DateTimeOffset.UtcNow,
            ExitCode = exitCode, Message = message, RecentOutput = output.ToArray(), Percent = 100 };
    }
    private static void Trim(List<string> output) { if (output.Count > 80) output.RemoveAt(0); }
    private static bool HasFile(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
    private static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var result = new string(value.Select(character => invalid.Contains(character) || character is '/' or '\\'
            ? '_' : character).ToArray()).Trim().Trim('.');
        return string.IsNullOrWhiteSpace(result) ? "Unbenannt" : result;
    }
    private static string UniquePath(string folder, string name)
    {
        var path = Path.Combine(folder, name);
        for (var index = 2; File.Exists(path); index++)
            path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({index}){Path.GetExtension(name)}");
        return path;
    }
    private static string AlignerUrl() =>
        Environment.GetEnvironmentVariable("LRC_ALIGNER_URL")?.Trim() is { Length: > 0 } value
            ? value : "http://127.0.0.1:8081";
}
