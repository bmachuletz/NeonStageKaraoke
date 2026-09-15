using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Karaoke.Contracts;

namespace Karaoke.Server;

public sealed class SongVideoService(
    LibraryRepository library,
    IWebHostEnvironment environment,
    ILogger<SongVideoService> logger)
{
    private const long MaximumUploadBytes = 4L * 1024 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, PendingVideo> _pending = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _androidConversions = new();
    private readonly string _ytDlpExecutable = ResolveYtDlpExecutable(environment.ContentRootPath);

    public async Task<SongVideoSearchDto> SearchYouTubeAsync(Guid songId, string query, CancellationToken ct)
    {
        if (await library.GetAsync(songId, ct) is null) throw new FileNotFoundException("Song wurde nicht gefunden.");
        if (string.IsNullOrWhiteSpace(query)) return new([], "Bitte einen Suchbegriff eingeben.");
        CleanupExpired();
        var output = await RunForOutputAsync(_ytDlpExecutable, YtDlpArguments(
            "--dump-single-json", "--flat-playlist", "--no-warnings", "--playlist-end", "10",
            $"ytsearch10:{query.Trim()}"), ct);
        using var json = JsonDocument.Parse(output);
        if (!json.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            return new([], "YouTube hat keine verwendbaren Vorschläge geliefert.");
        var candidates = new List<SongVideoCandidateDto>();
        foreach (var entry in entries.EnumerateArray())
        {
            var id = Text(entry, "id");
            var title = Text(entry, "title");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title)) continue;
            var duration = Number(entry, "duration");
            if (duration is <= 0 or > 60 * 60 * 2) continue;
            // Die Auswahl bleibt auch nach einem Serverneustart gültig. Es
            // wird ausschließlich eine streng validierte YouTube-ID codiert;
            // Metadaten und URL werden beim Übernehmen erneut vom Provider gelesen.
            var token = "youtube:" + id;
            var url = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(id);
            var channel = Text(entry, "channel") ?? Text(entry, "uploader") ?? "YouTube";
            var thumbnail = Text(entry, "thumbnail");
            _pending[token] = new(songId, id, title, channel, duration, thumbnail, url,
                DateTimeOffset.UtcNow.AddMinutes(30));
            candidates.Add(new(token, "YouTube", id, title, channel, duration, thumbnail, url));
        }
        return new(candidates, $"YouTube: {candidates.Count} manuell auswählbare Vorschläge");
    }

    public async Task<SongVideoInfoDto?> DownloadSelectedAsync(Guid songId, string token,
        bool downloadAuthorized, CancellationToken ct)
    {
        if (!downloadAuthorized)
            throw new ArgumentException("Die Download-Berechtigung muss ausdrücklich bestätigt werden.");
        if (!_pending.TryRemove(token, out var pending) || pending.SongId != songId ||
            pending.ExpiresAt < DateTimeOffset.UtcNow)
        {
            if (!token.StartsWith("youtube:", StringComparison.Ordinal) ||
                !Regex.IsMatch(token[8..], "^[A-Za-z0-9_-]{11}$")) return null;
            pending = await ResolveYouTubeAsync(songId, token[8..], ct);
        }
        return await StoreAsync(songId, pending.Url, "YouTube", pending.Id, pending.Title,
            pending.Channel, pending.Duration, pending.Url, ct);
    }

    private async Task<PendingVideo> ResolveYouTubeAsync(Guid songId, string id, CancellationToken ct)
    {
        if (await library.GetAsync(songId, ct) is null) throw new FileNotFoundException("Song wurde nicht gefunden.");
        var url = "https://www.youtube.com/watch?v=" + id;
        var output = await RunForOutputAsync(_ytDlpExecutable, YtDlpArguments(
            "--dump-single-json", "--skip-download", "--no-playlist", "--no-warnings", url), ct);
        using var json = JsonDocument.Parse(output);
        var title = Text(json.RootElement, "title") ?? throw new InvalidDataException("Der Videotitel fehlt.");
        var channel = Text(json.RootElement, "channel") ?? Text(json.RootElement, "uploader") ?? "YouTube";
        var duration = Number(json.RootElement, "duration");
        return new(songId, id, title, channel, duration, Text(json.RootElement, "thumbnail"), url,
            DateTimeOffset.UtcNow.AddMinutes(30));
    }

    public async Task<SongVideoInfoDto> StoreUploadedAsync(Guid songId, Stream source, long length,
        string fileName, CancellationToken ct)
    {
        if (length is <= 0 or > MaximumUploadBytes)
            throw new ArgumentException("Die Videodatei ist leer oder größer als 4 GiB.");
        var upload = Path.Combine(Path.GetTempPath(), $"neon-stage-video-upload-{Guid.NewGuid():N}" +
            Path.GetExtension(fileName));
        try
        {
            await using (var target = new FileStream(upload, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await source.CopyToAsync(target, ct);
            return await StoreAsync(songId, upload, "LocalFile", null,
                Path.GetFileNameWithoutExtension(fileName), null, null, null, ct);
        }
        finally { TryDelete(upload); }
    }

    public async Task<SongVideoInfoDto?> GetInfoAsync(Guid songId, CancellationToken ct)
    {
        var paths = await ResolvePathsAsync(songId, ct);
        if (paths is null || !File.Exists(paths.Value.Video)) return null;
        if (File.Exists(paths.Value.Metadata))
        {
            try
            {
                await using var stream = File.OpenRead(paths.Value.Metadata);
                var stored = await JsonSerializer.DeserializeAsync<SongVideoInfoDto>(stream,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);
                if (stored is not null) return stored with { FileSize = new FileInfo(paths.Value.Video).Length };
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                logger.LogWarning(exception, "Video-Metadaten für Song {SongId} sind beschädigt", songId);
            }
        }
        return new("Unknown", null, null, Path.GetFileNameWithoutExtension(paths.Value.Video), null, null,
            new FileInfo(paths.Value.Video).Length, File.GetLastWriteTimeUtc(paths.Value.Video));
    }

    public async Task<(string Path, string ContentType)?> GetFileAsync(Guid songId, CancellationToken ct)
    {
        var paths = await ResolvePathsAsync(songId, ct);
        return paths is not null && File.Exists(paths.Value.Video) ? (paths.Value.Video, "video/mp4") : null;
    }

    public async Task<(string Path, string ContentType)?> GetLinuxFileAsync(Guid songId, CancellationToken ct)
    {
        var paths = await ResolvePathsAsync(songId, ct);
        return paths is not null && File.Exists(paths.Value.LinuxVideo)
            ? (paths.Value.LinuxVideo, "video/webm")
            : null;
    }

    public async Task<(string Path, string ContentType)?> GetAndroidFileAsync(Guid songId, CancellationToken ct)
    {
        var paths = await ResolvePathsAsync(songId, ct);
        if (paths is null || !File.Exists(paths.Value.Video)) return null;
        if (!File.Exists(paths.Value.AndroidVideo))
        {
            var gate = _androidConversions.GetOrAdd(songId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                if (!File.Exists(paths.Value.AndroidVideo))
                    await CreateAndroidVideoAsync(paths.Value.Video, paths.Value.AndroidVideo, ct);
            }
            finally { gate.Release(); }
        }
        return File.Exists(paths.Value.AndroidVideo)
            ? (paths.Value.AndroidVideo, "video/mp4")
            : null;
    }

    public async Task<bool> DeleteAsync(Guid songId, CancellationToken ct)
    {
        var paths = await ResolvePathsAsync(songId, ct);
        if (paths is null) return false;
        var deleted = false;
        foreach (var path in new[]
                 { paths.Value.Video, paths.Value.LinuxVideo, paths.Value.AndroidVideo, paths.Value.Metadata })
        {
            if (!File.Exists(path)) continue;
            File.Delete(path);
            deleted = true;
        }
        return deleted;
    }

    public async Task<SongVideoInfoDto?> UpdateOffsetAsync(Guid songId, int offsetMilliseconds,
        CancellationToken ct)
    {
        if (offsetMilliseconds is < -300_000 or > 300_000)
            throw new ArgumentException("Der Video-Versatz muss zwischen -5 und +5 Minuten liegen.");
        var paths = await ResolvePathsAsync(songId, ct);
        var info = await GetInfoAsync(songId, ct);
        if (paths is null || info is null) return null;
        info = info with { OffsetMilliseconds = offsetMilliseconds };
        await File.WriteAllTextAsync(paths.Value.Metadata,
            JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                { WriteIndented = true }), ct);
        return info;
    }

    private async Task<SongVideoInfoDto> StoreAsync(Guid songId, string input, string source,
        string? sourceId, string title, string? channel, double? duration, string? sourceUrl,
        CancellationToken ct)
    {
        var paths = await ResolvePathsAsync(songId, ct)
                    ?? throw new FileNotFoundException("Song wurde nicht gefunden.");
        var downloadBase = Path.Combine(Path.GetTempPath(), $"neon-stage-video-{Guid.NewGuid():N}");
        var normalized = downloadBase + ".normalized.mp4";
        var linuxVideo = downloadBase + ".stage.webm";
        var androidVideo = downloadBase + ".android.mp4";
        try
        {
            var inputPath = input;
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            {
                await RunAsync(_ytDlpExecutable, YtDlpArguments(
                    "--no-playlist", "--max-filesize", "4G", "--merge-output-format", "mp4",
                    "-f", "bv*+ba/b", "-o", downloadBase + ".%(ext)s", input), ct);
                inputPath = Directory.EnumerateFiles(Path.GetDirectoryName(downloadBase)!,
                        Path.GetFileName(downloadBase) + ".*", SearchOption.TopDirectoryOnly)
                    .First(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
            }
            await RunAsync("ffmpeg",
                ["-hide_banner", "-loglevel", "error", "-y", "-i", inputPath,
                    "-map", "0:v:0", "-an", "-vf", "scale=min(1920\\,iw):-2",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-profile:v", "high", "-level", "4.1",
                    "-movflags", "+faststart", normalized], ct);
            if (!File.Exists(normalized) || new FileInfo(normalized).Length == 0)
                throw new InvalidDataException("Das normalisierte Video ist leer.");
            await CreateAndroidVideoAsync(normalized, androidVideo, ct);
            // Unitys nativer Linux-VideoPlayer unterstützt MP4/H.264 je nach
            // Distribution nicht. VP8 im WebM-Container ist dort der stabile
            // plattformnative Pfad. Die Stage-Fassung bleibt bewusst stumm und
            // wird auf 720p begrenzt, damit Decodierung und Transfer flüssig sind.
            await RunAsync("ffmpeg",
                ["-hide_banner", "-loglevel", "error", "-y", "-i", normalized,
                    "-map", "0:v:0", "-an", "-vf", "fps=30,scale=min(1280\\,iw):-2",
                    "-c:v", "libvpx", "-pix_fmt", "yuv420p", "-deadline", "realtime",
                    "-cpu-used", "8", "-crf", "24", "-b:v", "2500k", linuxVideo], ct);
            if (!File.Exists(linuxVideo) || new FileInfo(linuxVideo).Length == 0)
                throw new InvalidDataException("Die Linux-Stage-Fassung des Videos ist leer.");
            File.Move(normalized, paths.Video, overwrite: true);
            File.Move(linuxVideo, paths.LinuxVideo, overwrite: true);
            File.Move(androidVideo, paths.AndroidVideo, overwrite: true);
            var info = new SongVideoInfoDto(source, sourceId, sourceUrl, title, channel, duration,
                new FileInfo(paths.Video).Length, DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(paths.Metadata,
                JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    { WriteIndented = true }), ct);
            return info;
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(downloadBase)!,
                         Path.GetFileName(downloadBase) + ".*", SearchOption.TopDirectoryOnly).ToArray())
                TryDelete(path);
        }
    }

    private static async Task CreateAndroidVideoAsync(string input, string output, CancellationToken ct)
    {
        var temporary = output + $".{Guid.NewGuid():N}.tmp.mp4";
        try
        {
            await RunAsync("ffmpeg",
                ["-hide_banner", "-loglevel", "error", "-y", "-i", input,
                    "-map", "0:v:0", "-an", "-vf", "fps=24,scale=min(960\\,iw):-2",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-profile:v", "baseline", "-level", "3.1",
                    "-preset", "veryfast", "-tune", "fastdecode", "-crf", "25",
                    "-maxrate", "1600k", "-bufsize", "3200k", "-g", "48", "-keyint_min", "48",
                    "-movflags", "+faststart", temporary], ct);
            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new InvalidDataException("Die Android-Stage-Fassung des Videos ist leer.");
            File.Move(temporary, output, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private async Task<(string Video, string LinuxVideo, string AndroidVideo, string Metadata)?> ResolvePathsAsync(
        Guid songId, CancellationToken ct)
    {
        var audio = await library.GetAudioFileAsync(songId, ct);
        if (audio is null) return null;
        var basis = Path.Combine(Path.GetDirectoryName(audio.Value.Path)!,
            Path.GetFileNameWithoutExtension(audio.Value.Path));
        return (basis + ".video.mp4", basis + ".video.webm", basis + ".video.android.mp4",
            basis + ".video.json");
    }

    private static async Task<string> RunForOutputAsync(string executable, IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        var start = StartInfo(executable, arguments);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{executable} konnte nicht gestartet werden.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException(error.Trim());
        return await stdout;
    }

    private static async Task RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct) =>
        _ = await RunForOutputAsync(executable, arguments, ct);

    private static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }

    private static IReadOnlyList<string> YtDlpArguments(params string[] arguments)
    {
        // YouTube liefert Medien-URLs inzwischen regelmäßig erst nach einer
        // JavaScript-Challenge. Node ist auf der Neon-Stage-Hostplattform
        // vorhanden; EJS wird von yt-dlp signiert/versioniert über das eigene
        // offizielle Release-Repository bezogen und im yt-dlp-Cache gehalten.
        var result = new List<string>
        {
            "--no-update", "--js-runtimes", "node", "--remote-components", "ejs:github"
        };
        result.AddRange(arguments);
        return result;
    }

    private static string ResolveYtDlpExecutable(string contentRootPath)
    {
        var configured = Environment.GetEnvironmentVariable("NEONSTAGE_YT_DLP_PATH");
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);

        var repositoryRoot = Path.GetFullPath(Path.Combine(contentRootPath, "..", ".."));
        // Absichtlich kein Rückfall auf ein globales `yt-dlp`: Distributionen
        // liefern häufig eine für YouTube bereits zu alte Version aus. Fehlt
        // das projektlokale Werkzeug, soll der Fehler den konkreten Pfad nennen.
        return Path.Combine(repositoryRoot, ".tools", "yt-dlp");
    }

    private void CleanupExpired()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _pending.Where(entry => entry.Value.ExpiresAt < now))
            _pending.TryRemove(entry.Key, out _);
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
    private static double? Number(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetDouble(out var number) ? number : null;
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } }
    private sealed record PendingVideo(Guid SongId, string Id, string Title, string Channel, double? Duration,
        string? Thumbnail, string Url, DateTimeOffset ExpiresAt);
}
