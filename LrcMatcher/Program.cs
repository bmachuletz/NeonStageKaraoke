using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using TagLib;

Console.OutputEncoding = Encoding.UTF8;

var parse = AppOptions.Parse(args);
if (!parse.Success)
{
    Console.Error.WriteLine(parse.Error);
    AppOptions.PrintHelp();
    return 2;
}

var options = parse.Options!;
if (options.ShowHelp)
{
    AppOptions.PrintHelp();
    return 0;
}

if (!Directory.Exists(options.Path) && !System.IO.File.Exists(options.Path))
{
    Console.Error.WriteLine($"Datei oder Ordner nicht gefunden: {options.Path}");
    return 2;
}

if (options.LrclibId is not null && Directory.Exists(options.Path))
{
    Console.Error.WriteLine("--lrclib-id kann nur zusammen mit einer einzelnen Audiodatei verwendet werden.");
    return 2;
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
    Console.WriteLine("\nAbbruch angefordert …");
};

using var http = new HttpClient
{
    BaseAddress = new Uri("https://lrclib.net/"),
    Timeout = TimeSpan.FromSeconds(90)
};
http.DefaultRequestHeaders.UserAgent.ParseAdd("LrcMatcher/0.2 (+local music library tool)");

var processor = new FolderProcessor(http, options);
var report = await processor.RunAsync(cts.Token);

var reportPath = options.ReportPath ?? (System.IO.File.Exists(options.Path)
    ? Path.Combine(Path.GetDirectoryName(options.Path)!, $"{Path.GetFileNameWithoutExtension(options.Path)}.lrcmatcher-report.json")
    : Path.Combine(options.Path, "lrcmatcher-report.json"));
await System.IO.File.WriteAllTextAsync(
    reportPath,
    JsonSerializer.Serialize(report, JsonContext.Default.ProcessingReport),
    Encoding.UTF8,
    CancellationToken.None);

Console.WriteLine();
Console.WriteLine($"Fertig: {report.Written} geschrieben, {report.Existing} vorhanden, " +
                  $"{report.NotFound} nicht gefunden, {report.Ambiguous} unsicher, {report.Moved} aussortiert, {report.Errors} Fehler.");
Console.WriteLine($"Bericht: {reportPath}");
return report.Errors > 0 ? 1 : 0;

internal sealed record AppOptions(
    string Path,
    bool Recursive,
    bool Overwrite,
    bool DryRun,
    bool PlainFallback,
    int Parallelism,
    int MaxDurationDifference,
    string? ReportPath,
    string? MoveMissingTo,
    int? LrclibId,
    string AlignerUrl,
    bool ShowHelp)
{
    public static ParseResult Parse(string[] args)
    {
        if (args.Length == 0)
            return ParseResult.Fail("Es fehlt der zu verarbeitende Ordner.");

        string? path = null;
        string? report = null;
        var recursive = true;
        var overwrite = false;
        var dryRun = false;
        var plainFallback = false;
        var parallelism = 3;
        var maxDurationDifference = 5;
        var help = false;
        string? moveMissingTo = null;
        int? lrclibId = null;
        var alignerUrl = Environment.GetEnvironmentVariable("LRC_ALIGNER_URL") ?? "http://127.0.0.1:8081";

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    help = true;
                    break;
                case "--recursive":
                    recursive = true;
                    break;
                case "--no-recursive":
                    recursive = false;
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--plain-fallback":
                    plainFallback = true;
                    break;
                case "--parallel":
                    if (!TryReadInt(args, ref i, out parallelism) || parallelism is < 1 or > 16)
                        return ParseResult.Fail("--parallel erwartet eine Zahl zwischen 1 und 16.");
                    break;
                case "--max-duration-difference":
                    if (!TryReadInt(args, ref i, out maxDurationDifference) || maxDurationDifference is < 0 or > 60)
                        return ParseResult.Fail("--max-duration-difference erwartet 0 bis 60 Sekunden.");
                    break;
                case "--report":
                    if (++i >= args.Length)
                        return ParseResult.Fail("--report erwartet einen Dateipfad.");
                    report = args[i];
                    break;
                case "--move-missing-to":
                    if (++i >= args.Length)
                        return ParseResult.Fail("--move-missing-to erwartet einen Zielordner.");
                    moveMissingTo = System.IO.Path.GetFullPath(args[i]);
                    break;
                case "--lrclib-id":
                    if (!TryReadInt(args, ref i, out var parsedLrclibId) || parsedLrclibId <= 0)
                        return ParseResult.Fail("--lrclib-id erwartet eine positive LRCLIB-ID.");
                    lrclibId = parsedLrclibId;
                    break;
                case "--aligner-url":
                    if (++i >= args.Length || !Uri.TryCreate(args[i], UriKind.Absolute, out _))
                        return ParseResult.Fail("--aligner-url erwartet eine absolute URL.");
                    alignerUrl = args[i].TrimEnd('/');
                    break;
                default:
                    if (arg.StartsWith('-'))
                        return ParseResult.Fail($"Unbekannte Option: {arg}");
                    if (path is not null)
                        return ParseResult.Fail("Es darf nur ein Quellordner angegeben werden.");
                    path = System.IO.Path.GetFullPath(arg);
                    break;
            }
        }

        if (help)
            return ParseResult.Ok(new AppOptions(path ?? ".", recursive, overwrite, dryRun,
                plainFallback, parallelism, maxDurationDifference, report, moveMissingTo, lrclibId, alignerUrl, true));

        if (path is null)
            return ParseResult.Fail("Es fehlt der zu verarbeitende Ordner.");

        return ParseResult.Ok(new AppOptions(path, recursive, overwrite, dryRun,
            plainFallback, parallelism, maxDurationDifference, report, moveMissingTo, lrclibId, alignerUrl, false));
    }

    private static bool TryReadInt(string[] args, ref int index, out int value)
    {
        value = 0;
        return ++index < args.Length && int.TryParse(args[index], out value);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
LrcMatcher – synchronisierte Liedtexte von LRCLIB neben Audiodateien speichern

Aufruf:
  LrcMatcher <Datei|Ordner> [Optionen]

Optionen:
  --recursive                    Unterordner durchsuchen (Standard)
  --no-recursive                 Nur den angegebenen Ordner durchsuchen
  --overwrite                    Vorhandene .lrc-Dateien überschreiben
  --dry-run                      Treffer nur anzeigen, keine .lrc schreiben
  --plain-fallback               Notfalls unsynchronisierten Liedtext schreiben
  --parallel <1-16>              Gleichzeitige Abfragen (Standard: 3)
  --max-duration-difference <s>  Maximale Dauerabweichung (Standard: 5)
  --report <Datei>               Pfad des JSON-Berichts
  --move-missing-to <Ordner>     Dateien ohne sicheren LRC-Treffer dorthin verschieben
  --lrclib-id <ID>               Für eine einzelne Audiodatei genau diesen Treffer laden
  --aligner-url <URL>             CUDA-Aligner für mehrdeutige Startzeiten (Standard: http://127.0.0.1:8081)
  -h, --help                     Hilfe anzeigen

Beispiel:
  LrcMatcher "D:\\Musik\\Meine Playlist" --parallel 3
""");
    }
}

internal sealed record ParseResult(bool Success, AppOptions? Options, string? Error)
{
    public static ParseResult Ok(AppOptions options) => new(true, options, null);
    public static ParseResult Fail(string error) => new(false, null, error);
}

internal sealed class FolderProcessor(HttpClient http, AppOptions options)
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".flac", ".opus", ".ogg", ".wav", ".wma", ".aac"
    };

    private readonly LrclibClient _client = new(http);
    private readonly VocalStartClient _vocalStartClient = new(options.AlignerUrl);

    public async Task<ProcessingReport> RunAsync(CancellationToken cancellationToken)
    {
        var files = System.IO.File.Exists(options.Path)
            ? Extensions.Contains(System.IO.Path.GetExtension(options.Path)) ? [options.Path] : []
            : Directory.EnumerateFiles(options.Path, "*",
                    options.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .Where(path => Extensions.Contains(System.IO.Path.GetExtension(path)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

        Console.WriteLine($"Gefundene Audiodateien: {files.Length}");

        var results = new ConcurrentBag<FileResult>();
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = options.Parallelism,
            CancellationToken = cancellationToken
        };

        try
        {
            await Parallel.ForEachAsync(files, parallelOptions, async (audioPath, ct) =>
            {
                var result = await ProcessFileAsync(audioPath, ct);
                results.Add(result);
                PrintResult(result);
            });
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("Verarbeitung abgebrochen.");
        }

        var ordered = results.OrderBy(r => r.AudioPath, StringComparer.OrdinalIgnoreCase).ToArray();
        return new ProcessingReport(
            DateTimeOffset.UtcNow,
            options.Path,
            files.Length,
            ordered.Count(r => r.Status == FileStatus.Written),
            ordered.Count(r => r.Status == FileStatus.Existing),
            ordered.Count(r => r.Status == FileStatus.NotFound),
            ordered.Count(r => r.Status == FileStatus.Ambiguous),
            ordered.Count(r => r.Status == FileStatus.Moved),
            ordered.Count(r => r.Status == FileStatus.Error),
            ordered);
    }

    private async Task<FileResult> ProcessFileAsync(string audioPath, CancellationToken ct)
    {
        var lrcPath = System.IO.Path.ChangeExtension(audioPath, ".lrc");
        if (System.IO.File.Exists(lrcPath) && !options.Overwrite)
            return FileResult.Existing(audioPath, lrcPath);

        AudioMetadata metadata;
        try
        {
            metadata = AudioMetadataReader.Read(audioPath);
        }
        catch (Exception ex)
        {
            return FileResult.Error(audioPath, lrcPath, $"Metadaten konnten nicht gelesen werden: {ex.Message}");
        }

        var embeddedLyrics = AudioMetadataReader.ReadEmbeddedLyrics(audioPath);
        if (!string.IsNullOrWhiteSpace(embeddedLyrics))
        {
            var normalized = embeddedLyrics.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd() + Environment.NewLine;
            var synchronized = Regex.IsMatch(normalized, @"(?m)^\[\d{1,3}:\d{1,2}(?:[\.:]\d{1,3})?\]");
            if (synchronized || options.PlainFallback)
            {
                if (!synchronized)
                    normalized = $"[re:ID3 plain lyrics; GPU alignment required]{Environment.NewLine}" + normalized;
                if (!options.DryRun)
                    await System.IO.File.WriteAllTextAsync(lrcPath, normalized, new UTF8Encoding(false), ct);
                return FileResult.WrittenEmbedded(audioPath, lrcPath, metadata, options.DryRun, !synchronized);
            }
        }

        if (string.IsNullOrWhiteSpace(metadata.Title) || string.IsNullOrWhiteSpace(metadata.Artist))
            return FileResult.Error(audioPath, lrcPath, "Titel oder Künstler fehlt in den Audio-Tags.", metadata);

        try
        {
            var match = await FindBestMatchAsync(audioPath, metadata, ct);
            if (match is null)
                return MoveMissingIfConfigured(FileResult.NotFound(audioPath, lrcPath, metadata));

            var lyrics = match.Item.SyncedLyrics;
            var usedPlain = false;
            if (string.IsNullOrWhiteSpace(lyrics) && options.PlainFallback)
            {
                lyrics = match.Item.PlainLyrics;
                usedPlain = true;
            }

            if (string.IsNullOrWhiteSpace(lyrics))
                return MoveMissingIfConfigured(FileResult.NotFound(audioPath, lrcPath, metadata, "Treffer enthält keine synchronisierten Lyrics."));

            if (!match.Confident)
                return MoveMissingIfConfigured(FileResult.Ambiguous(audioPath, lrcPath, metadata, match.Item, match.Score,
                    match.Reason));

            if (!options.DryRun)
            {
                var normalized = lyrics.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd() + Environment.NewLine;
                if (usedPlain)
                    normalized = $"[re:LRCLIB plain lyrics; GPU alignment required]{Environment.NewLine}" + normalized;
                await System.IO.File.WriteAllTextAsync(lrcPath, normalized, new UTF8Encoding(false), ct);
            }

            return FileResult.Written(audioPath, lrcPath, metadata, match.Item, match.Score,
                options.DryRun, usedPlain, match.Reason);
        }
        catch (HttpRequestException ex)
        {
            return FileResult.Error(audioPath, lrcPath, $"LRCLIB-Anfrage fehlgeschlagen: {ex.Message}", metadata);
        }
        catch (Exception ex)
        {
            return FileResult.Error(audioPath, lrcPath, ex.Message, metadata);
        }
    }

    private FileResult MoveMissingIfConfigured(FileResult result)
    {
        if (string.IsNullOrWhiteSpace(options.MoveMissingTo) || options.DryRun)
            return result;

        try
        {
            var relative = System.IO.Path.GetRelativePath(options.Path, result.AudioPath);
            var destination = System.IO.Path.Combine(options.MoveMissingTo, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
            destination = EnsureUniqueDestination(destination);
            System.IO.File.Move(result.AudioPath, destination);
            return FileResult.Moved(result, destination);
        }
        catch (Exception ex)
        {
            return FileResult.Error(result.AudioPath, result.LrcPath,
                $"Aussortieren fehlgeschlagen: {ex.Message}", result.Metadata);
        }
    }

    private static string EnsureUniqueDestination(string destination)
    {
        if (!System.IO.File.Exists(destination)) return destination;
        var dir = System.IO.Path.GetDirectoryName(destination)!;
        var name = System.IO.Path.GetFileNameWithoutExtension(destination);
        var ext = System.IO.Path.GetExtension(destination);
        for (var i = 2; ; i++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{name} ({i}){ext}");
            if (!System.IO.File.Exists(candidate)) return candidate;
        }
    }

    private async Task<ScoredMatch?> FindBestMatchAsync(string audioPath, AudioMetadata metadata, CancellationToken ct)
    {
        if (options.LrclibId is { } selectedId)
        {
            var selected = await _client.GetByIdAsync(selectedId, ct)
                ?? throw new InvalidOperationException($"LRCLIB-ID {selectedId} wurde nicht gefunden.");
            var scored = MatchScorer.Score(metadata, selected, options.MaxDurationDifference);
            return scored with
            {
                Confident = true,
                Reason = $"Manuell ausgewählter LRCLIB-Treffer {selectedId}"
            };
        }

        var exact = await _client.GetExactAsync(metadata, ct);
        var candidates = await _client.SearchAsync(metadata, ct);
        var titleCandidates = await _client.SearchByTitleAsync(metadata.Title, ct);
        var allCandidates = candidates
            .Concat(titleCandidates)
            .Concat(exact is null ? [] : [exact])
            .GroupBy(candidate => candidate.Id)
            .Select(group => group.First())
            .ToArray();
        if (allCandidates.Length == 0)
            return null;

        var best = MatchScorer.SelectBest(metadata, allCandidates, options.MaxDurationDifference);
        if (best is null) return null;

        // A downloader's selected source can be a few seconds shorter than
        // Spotify/LRCLIB while still containing the same recording. Only
        // relax the duration gate when at least two metadata-compatible
        // LRCLIB entries independently agree on virtually identical lyrics.
        // Their line timestamps are deliberately discarded: the GPU full-
        // track alignment is the authority for the local audio file.
        if (!best.Confident && options.PlainFallback)
        {
            var consensusPool = allCandidates
                .Select(candidate => MatchScorer.Score(metadata, candidate, options.MaxDurationDifference))
                .Where(candidate => candidate.MetadataCompatible &&
                                    candidate.DurationDifference <= Math.Max(12, options.MaxDurationDifference + 5) &&
                                    !string.IsNullOrWhiteSpace(MatchScorer.LyricsText(candidate.Item)))
                .OrderBy(candidate => candidate.DurationDifference)
                .ThenByDescending(candidate => candidate.Score)
                .ToArray();
            if (consensusPool.Length >= 2)
            {
                var winner = consensusPool[0];
                var winnerText = MatchScorer.LyricsText(winner.Item)!;
                var agreeing = consensusPool.Count(candidate =>
                    MatchScorer.LyricsSimilarity(winnerText, MatchScorer.LyricsText(candidate.Item)!) >= 0.97);
                if (agreeing >= 2)
                {
                    return winner with
                    {
                        Item = winner.Item with { PlainLyrics = winnerText, SyncedLyrics = null },
                        Confident = true,
                        Reason = $"Lyrics-Konsens aus {agreeing} LRCLIB-Fassungen; " +
                                 $"Metadaten kompatibel, lokale Dauerabweichung {winner.DurationDifference:0.0}s; " +
                                 "Zeitmarken werden durch CUDA-Vollspur-Alignment neu erzeugt"
                    };
                }
            }
        }

        var timingCandidates = allCandidates
            .Select(candidate => MatchScorer.Score(metadata, candidate, options.MaxDurationDifference))
            .Where(candidate => candidate.TitleSimilarity >= 0.88 &&
                                candidate.ArtistSimilarity >= 0.45 &&
                                candidate.DurationDifference <= options.MaxDurationDifference &&
                                candidate.FirstSyncedTimestamp is not null &&
                                !string.IsNullOrWhiteSpace(candidate.Item.SyncedLyrics))
            .OrderBy(candidate => candidate.DurationDifference)
            .ThenByDescending(candidate => candidate.Score)
            .ToArray();
        var distinctStarts = timingCandidates
            .DistinctBy(candidate => candidate.FirstSyncedTimestamp)
            .OrderBy(candidate => candidate.FirstSyncedTimestamp)
            .ToArray();
        if (distinctStarts.Length < 2 ||
            distinctStarts[^1].FirstSyncedTimestamp - distinctStarts[0].FirstSyncedTimestamp < TimeSpan.FromSeconds(1.5))
            return best;

        var firstText = MatchScorer.FirstSyncedText(timingCandidates[0].Item.SyncedLyrics);
        if (string.IsNullOrWhiteSpace(firstText))
            return best with { Confident = false, Reason = "Zeitvarianten gefunden, aber keine erste Textzeile für die KI-Analyse." };

        try
        {
            var analysis = await _vocalStartClient.AnalyzeAsync(audioPath, firstText, ct);
            var ranked = timingCandidates
                .Select(candidate => (Candidate: candidate,
                    Difference: Math.Abs(candidate.FirstSyncedTimestamp!.Value.TotalSeconds - analysis.FirstWordStart)))
                .OrderBy(result => result.Difference)
                .ThenByDescending(result => result.Candidate.Score)
                .ToArray();
            var winner = ranked[0];
            var runnerUp = ranked.FirstOrDefault(result => result.Candidate.Item.Id != winner.Candidate.Item.Id &&
                                                           Math.Abs(result.Candidate.FirstSyncedTimestamp!.Value.TotalSeconds -
                                                                    winner.Candidate.FirstSyncedTimestamp!.Value.TotalSeconds) >= 1.5);
            if (winner.Difference <= 1.25 && runnerUp.Candidate is not null && runnerUp.Difference - winner.Difference >= 1.5)
            {
                return winner.Candidate with
                {
                    Confident = true,
                    Reason = $"CUDA-Vocalanalyse: erstes Wort bei {analysis.FirstWordStart:0.00}s; " +
                             $"LRCLIB {winner.Candidate.Item.Id} weicht {winner.Difference:0.00}s ab"
                };
            }

            return best with
            {
                Confident = false,
                Reason = $"CUDA-Vocalanalyse bei {analysis.FirstWordStart:0.00}s war nicht eindeutig; " +
                         $"bester Kandidat LRCLIB {winner.Candidate.Item.Id} mit {winner.Difference:0.00}s Abweichung"
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return best with
            {
                Confident = false,
                Reason = $"Mehrdeutige Startzeiten; CUDA-Vocalanalyse nicht verfügbar: {ex.Message}"
            };
        }
    }

    private static void PrintResult(FileResult result)
    {
        var marker = result.Status switch
        {
            FileStatus.Written => result.DryRun ? "[DRY]" : "[OK ]",
            FileStatus.Existing => "[SKIP]",
            FileStatus.NotFound => "[MISS]",
            FileStatus.Ambiguous => "[???]",
            FileStatus.Moved => "[MOVE]",
            _ => "[ERR]"
        };
        Console.WriteLine($"{marker} {System.IO.Path.GetFileName(result.AudioPath)}" +
                          (string.IsNullOrWhiteSpace(result.Message) ? "" : $" – {result.Message}"));
    }
}

internal static class AudioMetadataReader
{
    public static AudioMetadata Read(string path)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        var title = tag.Title?.Trim() ?? "";
        var artists = tag.Performers?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray()
                      ?? [];
        var artist = artists.Length > 0 ? string.Join(", ", artists) : tag.FirstPerformer?.Trim() ?? "";
        var album = tag.Album?.Trim() ?? "";
        var duration = (int)Math.Round(file.Properties.Duration.TotalSeconds, MidpointRounding.AwayFromZero);
        return new AudioMetadata(title, artist, album, duration);
    }

    public static string? ReadEmbeddedLyrics(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            return string.IsNullOrWhiteSpace(file.Tag.Lyrics) ? null : file.Tag.Lyrics.Trim();
        }
        catch { return null; }
    }
}

internal sealed class LrclibClient(HttpClient http)
{
    private const int MaxAttempts = 3;

    public async Task<LrclibTrack?> GetByIdAsync(int id, CancellationToken ct)
    {
        using var response = await GetWithRetryAsync($"api/get/{id}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(JsonContext.Default.LrclibTrack, ct);
    }

    public async Task<LrclibTrack?> GetExactAsync(AudioMetadata metadata, CancellationToken ct)
    {
        var query = QueryString(new Dictionary<string, string>
        {
            ["track_name"] = metadata.Title,
            ["artist_name"] = metadata.Artist,
            ["album_name"] = metadata.Album,
            ["duration"] = metadata.DurationSeconds.ToString(CultureInfo.InvariantCulture)
        });

        using var response = await GetWithRetryAsync($"api/get?{query}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(JsonContext.Default.LrclibTrack, ct);
    }

    public async Task<IReadOnlyList<LrclibTrack>> SearchAsync(AudioMetadata metadata, CancellationToken ct)
    {
        var fields = new Dictionary<string, string>
        {
            ["track_name"] = metadata.Title,
            ["artist_name"] = metadata.Artist
        };
        if (!string.IsNullOrWhiteSpace(metadata.Album))
            fields["album_name"] = metadata.Album;

        using var response = await GetWithRetryAsync($"api/search?{QueryString(fields)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(JsonContext.Default.ListLrclibTrack, ct) ?? [];
    }

    public async Task<IReadOnlyList<LrclibTrack>> SearchByTitleAsync(string title, CancellationToken ct)
    {
        var query = QueryString(new Dictionary<string, string> { ["track_name"] = title });
        using var response = await GetWithRetryAsync($"api/search?{query}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return [];
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(JsonContext.Default.ListLrclibTrack, ct) ?? [];
    }

    private async Task<HttpResponseMessage> GetWithRetryAsync(string relativeUri, CancellationToken ct)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var response = await http.GetAsync(relativeUri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!IsTransient(response.StatusCode) || attempt == MaxAttempts)
                    return response;

                response.Dispose();
                await Task.Delay(Backoff(attempt), ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attempt < MaxAttempts)
            {
                lastError = new TimeoutException($"LRCLIB timeout on attempt {attempt}.");
                await Task.Delay(Backoff(attempt), ct);
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts)
            {
                lastError = ex;
                await Task.Delay(Backoff(attempt), ct);
            }
        }

        throw lastError ?? new HttpRequestException("LRCLIB request failed after retries.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests ||
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode >= 500;

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(750 * attempt);

    private static string QueryString(IReadOnlyDictionary<string, string> values) =>
        string.Join("&", values.Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
}

internal sealed class VocalStartClient
{
    private readonly HttpClient _http;

    public VocalStartClient(string baseUrl)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(10)
        };
    }

    public async Task<VocalStartResponse> AnalyzeAsync(string audioPath, string text, CancellationToken ct)
    {
        await using var audio = System.IO.File.OpenRead(audioPath);
        using var form = new MultipartFormDataContent();
        using var audioContent = new StreamContent(audio);
        form.Add(audioContent, "audio", System.IO.Path.GetFileName(audioPath));
        form.Add(new StringContent(text, Encoding.UTF8), "text");
        form.Add(new StringContent("de"), "language");
        form.Add(new StringContent("true"), "separate");
        form.Add(new StringContent("cuda"), "alignment_device");
        form.Add(new StringContent("30"), "search_seconds");

        using var response = await _http.PostAsync("analyze/vocal-start", form, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(JsonContext.Default.VocalStartResponse, ct)
               ?? throw new HttpRequestException("Leere Antwort der Vocal-Start-Analyse.");
    }
}

internal static class MatchScorer
{
    internal static string? LyricsText(LrclibTrack track)
    {
        if (!string.IsNullOrWhiteSpace(track.PlainLyrics))
            return track.PlainLyrics;
        if (string.IsNullOrWhiteSpace(track.SyncedLyrics))
            return null;
        return Regex.Replace(track.SyncedLyrics, @"(?m)^\[\d{1,3}:\d{1,2}(?:[\.:]\d{1,3})?\]\s*", "").Trim();
    }

    internal static double LyricsSimilarity(string left, string right) => Similarity(left, right);

    public static ScoredMatch? SelectBest(AudioMetadata source, IEnumerable<LrclibTrack> candidates, int maxDurationDifference)
    {
        var scoredCandidates = candidates.Select(item => Score(source, item, maxDurationDifference)).ToArray();
        var metadataCompatible = scoredCandidates.Where(candidate => candidate.MetadataCompatible).ToArray();
        var selectionPool = metadataCompatible.Length > 0 ? metadataCompatible : scoredCandidates;
        var ordered = selectionPool
            .OrderBy(candidate => candidate.DurationRank)
            .ThenBy(candidate => candidate.DurationDifference)
            .ThenByDescending(candidate => candidate.AlbumSimilarity)
            .ThenByDescending(candidate => candidate.TitleSimilarity)
            .ThenByDescending(candidate => candidate.ArtistSimilarity)
            .ThenByDescending(candidate => candidate.Score)
            .ToArray();
        var best = ordered.FirstOrDefault();
        if (best is null) return null;

        var competingTimings = ordered
            .Where(candidate => candidate.MetadataCompatible &&
                                candidate.DurationRank == best.DurationRank &&
                                Math.Abs(candidate.DurationDifference - best.DurationDifference) <= 0.5 &&
                                candidate.FirstSyncedTimestamp is not null)
            .Select(candidate => (candidate.Item.Id, Start: candidate.FirstSyncedTimestamp!.Value))
            .DistinctBy(candidate => candidate.Start)
            .OrderBy(candidate => candidate.Start)
            .ToArray();
        if (competingTimings.Length > 1 &&
            competingTimings[^1].Start - competingTimings[0].Start >= TimeSpan.FromSeconds(1.5))
        {
            var variants = string.Join(", ", competingTimings.Select(candidate =>
                $"LRCLIB {candidate.Id} ab {candidate.Start:mm\\:ss\\.ff}"));
            return best with
            {
                Confident = false,
                Reason = $"Mehrere zeitlich verschiedene Lyrics-Fassungen bei gleicher Songdauer: {variants}"
            };
        }
        return best;
    }

    public static ScoredMatch Score(AudioMetadata source, LrclibTrack candidate, int maxDurationDifference)
    {
        var titleSimilarity = Similarity(source.Title, candidate.TrackName);
        var artistSimilarity = Similarity(source.Artist, candidate.ArtistName);
        var albumSimilarity = string.IsNullOrWhiteSpace(source.Album) || string.IsNullOrWhiteSpace(candidate.AlbumName)
            ? 0.5
            : Similarity(source.Album, candidate.AlbumName);
        var durationDiff = Math.Abs(source.DurationSeconds - candidate.Duration);

        var score = titleSimilarity * 45 + artistSimilarity * 30 + albumSimilarity * 15;
        score += durationDiff switch
        {
            <= 0.5 => 30,
            <= 1 => 25,
            <= 2 => 20,
            <= 3 => 15,
            <= 5 => 8,
            <= 10 => -5,
            _ => -30
        };

        if (string.IsNullOrWhiteSpace(candidate.SyncedLyrics))
            score -= 15;

        var conflictingVersion = HasVersionConflict(source.Title, candidate.TrackName);
        if (conflictingVersion)
            score -= 35;

        var metadataCompatible = titleSimilarity >= 0.88 && artistSimilarity >= 0.75 && !conflictingVersion;
        var confident = metadataCompatible &&
                        durationDiff <= maxDurationDifference && !conflictingVersion && score >= 85;

        var signedDurationDifference = candidate.Duration - source.DurationSeconds;
        var durationDirection = Math.Abs(signedDurationDifference) <= 0.5
            ? "gleich"
            : signedDurationDifference > 0 ? $"{signedDurationDifference:+0.0}s länger" : $"{signedDurationDifference:0.0}s kürzer";

        var reason = confident
            ? $"Sicherer Treffer; Dauer {durationDirection}"
            : $"Score {score:F1}; Titel {titleSimilarity:P0}; Künstler {artistSimilarity:P0}; Album {albumSimilarity:P0}; Dauer {durationDirection}";

        return new ScoredMatch(candidate, Math.Round(score, 1), confident, reason,
            titleSimilarity, artistSimilarity, albumSimilarity, durationDiff,
            durationDiff <= 0.5 ? 0 : (int)Math.Ceiling(durationDiff), metadataCompatible,
            FirstSyncedTimestamp(candidate.SyncedLyrics));
    }

    internal static TimeSpan? FirstSyncedTimestamp(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics)) return null;
        foreach (var line in lyrics.Split('\n'))
        {
            var match = Regex.Match(line, @"^\[(?<minutes>\d{1,3}):(?<seconds>\d{1,2}(?:[\.:]\d{1,3})?)\]\s*(?<text>.*)$");
            if (!match.Success || string.IsNullOrWhiteSpace(match.Groups["text"].Value)) continue;
            var minutes = int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture);
            var seconds = double.Parse(match.Groups["seconds"].Value.Replace(':', '.'), CultureInfo.InvariantCulture);
            return TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        }
        return null;
    }

    internal static string? FirstSyncedText(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics)) return null;
        foreach (var line in lyrics.Split('\n'))
        {
            var match = Regex.Match(line, @"^\[\d{1,3}:\d{1,2}(?:[\.:]\d{1,3})?\]\s*(?<text>.*)$");
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups["text"].Value))
                return match.Groups["text"].Value.Trim();
        }
        return null;
    }

    private static bool HasVersionConflict(string source, string candidate)
    {
        string[] markers = ["live", "remix", "acoustic", "radio edit", "instrumental", "karaoke", "sped up", "slowed"];
        var a = Normalize(source);
        var b = Normalize(candidate);
        return markers.Any(marker => a.Contains(marker, StringComparison.Ordinal) != b.Contains(marker, StringComparison.Ordinal));
    }

    private static double Similarity(string left, string right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        if (a == b) return 1;
        if (a.Length == 0 || b.Length == 0) return 0;
        var distance = Levenshtein(a, b);
        return 1.0 - (double)distance / Math.Max(a.Length, b.Length);
    }

    private static string Normalize(string value)
    {
        var formD = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}

internal sealed record AudioMetadata(string Title, string Artist, string Album, int DurationSeconds);
internal sealed record VocalStartResponse(
    [property: JsonPropertyName("first_word_start")] double FirstWordStart,
    [property: JsonPropertyName("first_word")] string FirstWord,
    [property: JsonPropertyName("alignment_device")] string AlignmentDevice);
internal sealed record ScoredMatch(
    LrclibTrack Item,
    double Score,
    bool Confident,
    string Reason,
    double TitleSimilarity,
    double ArtistSimilarity,
    double AlbumSimilarity,
    double DurationDifference,
    int DurationRank,
    bool MetadataCompatible,
    TimeSpan? FirstSyncedTimestamp);

internal sealed record LrclibTrack(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("trackName")] string TrackName,
    [property: JsonPropertyName("artistName")] string ArtistName,
    [property: JsonPropertyName("albumName")] string AlbumName,
    [property: JsonPropertyName("duration"), JsonConverter(typeof(FlexibleDurationConverter))] double Duration,
    [property: JsonPropertyName("instrumental")] bool Instrumental,
    [property: JsonPropertyName("plainLyrics")] string? PlainLyrics,
    [property: JsonPropertyName("syncedLyrics")] string? SyncedLyrics);

/// <summary>
/// LRCLIB contains a small number of legacy records whose duration is null or
/// encoded as text. A single malformed candidate must not discard the complete
/// search result. Unknown durations become zero and are naturally ranked last.
/// </summary>
internal sealed class FlexibleDurationConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var number)) return number;
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return number;
            if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var duration)) return duration.TotalSeconds;
        }
        if (reader.TokenType is JsonTokenType.Null or JsonTokenType.False) return 0;
        using var ignored = JsonDocument.ParseValue(ref reader);
        return 0;
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

internal enum FileStatus { Written, Existing, NotFound, Ambiguous, Moved, Error }

internal sealed record FileResult(
    string AudioPath,
    string LrcPath,
    FileStatus Status,
    AudioMetadata? Metadata,
    LrclibTrack? Match,
    double? Score,
    string? Message,
    bool DryRun,
    bool UsedPlainLyrics,
    string? MovedTo)
{
    public static FileResult Existing(string audio, string lrc) =>
        new(audio, lrc, FileStatus.Existing, null, null, null, null, false, false, null);
    public static FileResult NotFound(string audio, string lrc, AudioMetadata metadata, string? message = null) =>
        new(audio, lrc, FileStatus.NotFound, metadata, null, null, message, false, false, null);
    public static FileResult Ambiguous(string audio, string lrc, AudioMetadata metadata, LrclibTrack match,
        double score, string message) =>
        new(audio, lrc, FileStatus.Ambiguous, metadata, match, score, message, false, false, null);
    public static FileResult Error(string audio, string lrc, string message, AudioMetadata? metadata = null) =>
        new(audio, lrc, FileStatus.Error, metadata, null, null, message, false, false, null);
    public static FileResult Written(string audio, string lrc, AudioMetadata metadata, LrclibTrack match,
        double score, bool dryRun, bool usedPlain, string reason) =>
        new(audio, lrc, FileStatus.Written, metadata, match, score,
            usedPlain ? $"Unsynchronisierte Lyrics verwendet – {reason}" : reason, dryRun, usedPlain, null);
    public static FileResult WrittenEmbedded(string audio, string lrc, AudioMetadata metadata, bool dryRun,
        bool usedPlain) =>
        new(audio, lrc, FileStatus.Written, metadata, null, 100,
            usedPlain ? "Unsynchronisierte ID3-Lyrics verwendet" : "Synchronisierte ID3-Lyrics verwendet",
            dryRun, usedPlain, null);
    public static FileResult Moved(FileResult source, string destination) =>
        source with { Status = FileStatus.Moved, Message = $"Aussortiert nach {destination}", MovedTo = destination };
}

internal sealed record ProcessingReport(
    DateTimeOffset CreatedUtc,
    string RootPath,
    int AudioFiles,
    int Written,
    int Existing,
    int NotFound,
    int Ambiguous,
    int Moved,
    int Errors,
    IReadOnlyList<FileResult> Files);

[JsonSerializable(typeof(LrclibTrack))]
[JsonSerializable(typeof(List<LrclibTrack>))]
[JsonSerializable(typeof(ProcessingReport))]
[JsonSerializable(typeof(VocalStartResponse))]
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    Converters = [typeof(JsonStringEnumConverter<FileStatus>)])]
internal partial class JsonContext : JsonSerializerContext;
