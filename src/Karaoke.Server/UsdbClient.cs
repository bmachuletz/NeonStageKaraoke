using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using Karaoke.Editor.Core;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

public sealed partial class UsdbClient : IUsdbClient
{
    private const int MaximumDownloadBytes = 5 * 1024 * 1024;
    private const string BrowserUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36";
    private readonly HttpClient _http;
    private readonly UsdbOptions _options;
    private readonly ILogger<UsdbClient> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<CookieContainer, HttpMessageHandler> _downloadHandlerFactory;
    private readonly string _cachePath;
    private readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<UsdbSearchCandidate>>> _searchCache = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> _failureCache = new();
    private readonly SemaphoreSlim _downloadGate = new(1, 1);

    public UsdbClient(IHttpClientFactory httpClientFactory, IOptions<UsdbOptions> options,
        IWebHostEnvironment environment, TimeProvider timeProvider, ILogger<UsdbClient> logger)
        : this(httpClientFactory.CreateClient("Usdb"), options.Value,
            ResolveCachePath(options.Value.CachePath, environment.ContentRootPath), timeProvider, logger,
            cookies => new HttpClientHandler
            {
                CookieContainer = cookies,
                UseCookies = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All
            })
    {
    }

    internal UsdbClient(HttpClient http, UsdbOptions options, string cachePath, TimeProvider timeProvider,
        ILogger<UsdbClient> logger, Func<CookieContainer, HttpMessageHandler> downloadHandlerFactory)
    {
        _http = http;
        _options = options;
        _cachePath = cachePath;
        _timeProvider = timeProvider;
        _logger = logger;
        _downloadHandlerFactory = downloadHandlerFactory;
        Directory.CreateDirectory(_cachePath);
    }

    public async Task<IReadOnlyList<UsdbSearchCandidate>> SearchAsync(
        string title, string artist, CancellationToken cancellationToken)
    {
        var key = UsdbSongMatcher.Normalize(title) + "|" + UsdbSongMatcher.Normalize(artist);
        if (_searchCache.TryGetValue(key, out var cached) && cached.ExpiresAt > _timeProvider.GetUtcNow())
            return cached.Value;

        var result = await SearchOnceAsync(title, cancellationToken);
        if (result.Count == 0 && !string.IsNullOrWhiteSpace(artist))
            result = await SearchOnceAsync(artist, cancellationToken);
        result = result
            .GroupBy(item => item.DetailUri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(Math.Max(1, _options.MaximumCandidates))
            .ToArray();
        _searchCache[key] = new(result, _timeProvider.GetUtcNow().AddHours(1));
        return result;
    }

    public async Task<IReadOnlyList<UsdbVersionCandidate>> ResolveVersionsAsync(
        UsdbSearchCandidate candidate, CancellationToken cancellationToken)
    {
        using var timeout = Timeout(cancellationToken);
        using var response = await _http.GetAsync(candidate.DetailUri, HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(timeout.Token);
        var uris = new List<Uri>();
        if (response.RequestMessage?.RequestUri is { } final && VersionId(final) is not null) uris.Add(final);
        var songPrefix = candidate.DetailUri.AbsolutePath.TrimEnd('/') + "/";
        foreach (Match match in VersionLinkRegex().Matches(html))
            if (TryUri(match.Groups["href"].Value, out var uri) && VersionId(uri) is not null &&
                uri.AbsolutePath.StartsWith(songPrefix, StringComparison.OrdinalIgnoreCase)) uris.Add(uri);
        return uris
            .DistinctBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(uri => new UsdbVersionCandidate(VersionId(uri)!.Value, candidate.Title, candidate.Artist,
                candidate.Year, ParseLabel(html, "This song is in"), ParseEdition(html), uri))
            .Take(Math.Max(1, _options.MaximumCandidates))
            .ToArray();
    }

    public async Task<UsdbDownloadedLyrics> DownloadAsync(
        UsdbVersionCandidate version, CancellationToken cancellationToken)
    {
        var cacheFile = Path.Combine(_cachePath, $"{version.VersionId}.txt");
        if (TryReadCache(cacheFile, out var cached))
            return new(version, cached, UltraStarLyricsImporter.Parse(cached), true);
        if (_failureCache.TryGetValue(version.VersionId, out var failedUntil) &&
            failedUntil > _timeProvider.GetUtcNow())
            throw new InvalidOperationException("USDB version is temporarily suppressed after a failed download.");

        await _downloadGate.WaitAsync(cancellationToken);
        try
        {
            if (TryReadCache(cacheFile, out cached))
                return new(version, cached, UltraStarLyricsImporter.Parse(cached), true);
            var cookies = new CookieContainer();
            var baseUri = BaseUri();
            cookies.Add(baseUri, new Cookie("archief",
                $"[%22{version.VersionId}%22]", "/"));
            using var handler = _downloadHandlerFactory(cookies);
            using var client = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = baseUri,
                Timeout = System.Threading.Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            var downloadUri = new Uri(baseUri, $"download/{version.VersionId}");
            using var timeout = Timeout(cancellationToken,
                TimeSpan.FromSeconds(Math.Max(0, _options.DownloadWaitSeconds)));

            // USDB binds the countdown to a browser-like session. Visiting the
            // concrete song version before the download route is part of that
            // session initialization and cannot be skipped.
            using (var songPage = await client.GetAsync(version.DetailUri,
                       HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                if (!songPage.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"USDB song page returned HTTP {(int)songPage.StatusCode}.");
            }

            using (var initialRequest = CreateRequest(HttpMethod.Get, downloadUri, version.DetailUri))
            using (var initial = await client.SendAsync(initialRequest,
                       HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                if (!initial.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"USDB download page returned HTTP {(int)initial.StatusCode}.");
                if (await TryExtractLyricsAsync(initial, timeout.Token) is { } immediate)
                    return await CacheAsync(version, immediate, cacheFile, false, timeout.Token);
            }

            // The AJAX endpoint starts the server-side countdown. It is /download,
            // not /download/{versionId}; the latter only renders/reloads the page.
            using (var startRequest = CreateRequest(HttpMethod.Post, new Uri(baseUri, "download"), downloadUri))
            {
                startRequest.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                startRequest.Headers.Accept.Clear();
                startRequest.Headers.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
                using var start = await client.SendAsync(startRequest,
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!start.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"USDB download initialization returned HTTP {(int)start.StatusCode}.");
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.DownloadWaitSeconds)),
                _timeProvider, timeout.Token);
            var attempts = Math.Clamp(_options.DownloadMaximumAttempts, 1, 10);
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                using var finalRequest = CreateRequest(HttpMethod.Get, downloadUri, downloadUri);
                using var final = await client.SendAsync(finalRequest,
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (final.IsSuccessStatusCode &&
                    await TryExtractLyricsAsync(final, timeout.Token) is { } lyrics)
                    return await CacheAsync(version, lyrics, cacheFile, false, timeout.Token);

                var retryable = final.StatusCode == HttpStatusCode.InternalServerError ||
                                final.IsSuccessStatusCode;
                if (!retryable || attempt == attempts)
                {
                    if (!retryable)
                        _logger.LogWarning(
                            "USDB TXT download for version {VersionId} returned HTTP {StatusCode}; trying the official player data fallback",
                            version.VersionId, (int)final.StatusCode);
                    break;
                }

                _logger.LogWarning(
                    "USDB version {VersionId} is not ready after reload {Attempt}/{MaximumAttempts} (HTTP {StatusCode}); retrying",
                    version.VersionId, attempt, attempts, (int)final.StatusCode);
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, _options.DownloadRetryDelaySeconds)),
                    _timeProvider, timeout.Token);
            }
            var playerFallback = await DownloadPlayerPreviewAsync(version, timeout.Token);
            _logger.LogWarning(
                "USDB version {VersionId} was imported through the official player preview because the TXT endpoint did not produce a file",
                version.VersionId);
            return await CacheAsync(version, playerFallback, cacheFile, false, timeout.Token);
        }
        catch
        {
            _failureCache[version.VersionId] = _timeProvider.GetUtcNow()
                .AddMinutes(Math.Max(1, _options.FailureCacheMinutes));
            throw;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri uri, Uri referrer)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Referrer = referrer;
        return request;
    }

    private async Task<string> DownloadPlayerPreviewAsync(UsdbVersionCandidate version,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_options.PlayerApiUrl, UriKind.Absolute, out var apiUri) ||
            apiUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("USDB player API URL is invalid.");
        var requestUri = new UriBuilder(apiUri)
        {
            Query = $"songFile={version.VersionId.ToString(CultureInfo.InvariantCulture)}"
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumDownloadBytes)
            throw new InvalidDataException("USDB player response exceeds the size limit.");
        var payload = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
        if (payload.StartsWith('(') && payload.EndsWith(')')) payload = payload[1..^1];
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("lyrics", out var lyrics) || lyrics.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("USDB player returned no usable lyrics data.");

        var title = PlayerLabel(root, "title") ?? version.Title;
        var artist = PlayerLabel(root, "artist") ?? version.Artist;
        var gap = root.TryGetProperty("gap", out var gapValue) && gapValue.TryGetDouble(out var parsedGap)
            ? parsedGap : 0d;
        var output = new StringBuilder()
            .AppendLine("#TITLE:" + HeaderValue(title))
            .AppendLine("#ARTIST:" + HeaderValue(artist))
            // At BPM 15000 one UltraStar beat equals exactly one millisecond.
            .AppendLine("#BPM:15000")
            .AppendLine("#GAP:" + Math.Round(gap).ToString(CultureInfo.InvariantCulture))
            .AppendLine("#COMMENT:USDB official player preview fallback; timing may require review");
        var lineCount = 0;
        foreach (var line in lyrics.EnumerateObject()
                     .Select(item => item.Value)
                     .Where(item => item.ValueKind == JsonValueKind.Object)
                     .OrderBy(PlayerStart))
        {
            if (!line.TryGetProperty("txt", out var tracks) || tracks.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var track in tracks.EnumerateObject())
            {
                if (track.Value.ValueKind != JsonValueKind.Array) continue;
                var noteCount = 0;
                var lineStop = 0L;
                foreach (var note in track.Value.EnumerateArray())
                {
                    if (!note.TryGetProperty("start", out var startValue) ||
                        !startValue.TryGetDouble(out var start) ||
                        !note.TryGetProperty("stop", out var stopValue) ||
                        !stopValue.TryGetDouble(out var stop) ||
                        !note.TryGetProperty("text", out var textValue)) continue;
                    var noteText = textValue.GetString()?.Replace('\r', ' ').Replace('\n', ' ') ?? string.Empty;
                    if (string.IsNullOrEmpty(noteText)) continue;
                    var startBeat = Math.Max(0L, (long)Math.Round(start));
                    var stopBeat = Math.Max(startBeat + 1, (long)Math.Round(stop));
                    var type = note.TryGetProperty("golden", out var golden) && golden.ValueKind == JsonValueKind.True
                        ? '*' : note.TryGetProperty("freestyle", out var freestyle) &&
                                freestyle.ValueKind == JsonValueKind.True ? 'F' : ':';
                    output.Append(type).Append(' ').Append(startBeat).Append(' ')
                        .Append(stopBeat - startBeat).Append(" 60 ").AppendLine(noteText);
                    lineStop = Math.Max(lineStop, stopBeat);
                    noteCount++;
                }
                if (noteCount == 0) continue;
                output.Append("- ").AppendLine(lineStop.ToString(CultureInfo.InvariantCulture));
                lineCount++;
            }
        }
        if (lineCount == 0)
            throw new InvalidDataException("USDB player returned no importable lyric lines.");
        output.AppendLine("E");
        var result = output.ToString();
        _ = UltraStarLyricsImporter.Parse(result);
        return result;
    }

    private static double PlayerStart(JsonElement value) =>
        value.TryGetProperty("start", out var start) && start.TryGetDouble(out var parsed)
            ? parsed : double.MaxValue;

    private static string? PlayerLabel(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty("label", out var label) ? label.GetString() : null;

    private static string HeaderValue(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

    private async Task<IReadOnlyList<UsdbSearchCandidate>> SearchOnceAsync(
        string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri(), "search"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["actie"] = "zoek",
                ["q"] = query
            })
        };
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("NeonStageKaraoke/1.0");
        using var timeout = Timeout(cancellationToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        return ParseSearch(document.RootElement);
    }

    internal IReadOnlyList<UsdbSearchCandidate> ParseSearch(JsonElement root)
    {
        var result = new List<UsdbSearchCandidate>();
        // USDB represents an empty search as [] but successful searches as an
        // object containing "secties". Both shapes are valid public behavior.
        if (root.ValueKind == JsonValueKind.Array) return result;
        if (root.ValueKind != JsonValueKind.Object) return result;
        if (!root.TryGetProperty("secties", out var sections) || sections.ValueKind != JsonValueKind.Object)
            return result;
        foreach (var section in sections.EnumerateObject())
        {
            if (!section.Value.TryGetProperty("label", out var label) ||
                !label.GetString()!.Equals("Songs", StringComparison.OrdinalIgnoreCase) ||
                !section.Value.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in content.EnumerateArray())
            {
                var itemLabel = WebUtility.HtmlDecode(item.GetProperty("label").GetString() ?? string.Empty).Trim();
                var separator = itemLabel.LastIndexOf(" - ", StringComparison.Ordinal);
                var title = separator > 0 ? itemLabel[..separator].Trim() : itemLabel;
                var artist = separator > 0 ? itemLabel[(separator + 3)..].Trim() : string.Empty;
                if (!TryUri(item.GetProperty("href").GetString(), out var uri)) continue;
                int? year = item.TryGetProperty("note", out var note) && int.TryParse(note.GetString(), out var parsedYear)
                    ? parsedYear : null;
                result.Add(new(itemLabel, title, artist, year, uri));
            }
        }
        return result;
    }

    private bool TryUri(string? value, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.StartsWith("//", StringComparison.Ordinal)) value = BaseUri().Scheme + ":" + value;
        if (!Uri.TryCreate(BaseUri(), WebUtility.HtmlDecode(value), out var parsed) ||
            !parsed.Host.Equals(BaseUri().Host, StringComparison.OrdinalIgnoreCase)) return false;
        uri = parsed;
        return true;
    }

    private static long? VersionId(Uri uri)
    {
        var segment = uri.Segments.LastOrDefault()?.Trim('/');
        return long.TryParse(segment, out var id) ? id : null;
    }

    private static string? ParseLabel(string html, string prefix)
    {
        var index = html.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var tail = WebUtility.HtmlDecode(StripTagsRegex().Replace(html[index..Math.Min(html.Length, index + 160)], " "));
        return WhitespaceRegex().Replace(tail, " ").Trim().TrimEnd('.');
    }

    private static string? ParseEdition(string html)
    {
        var match = EditionRegex().Match(html);
        return match.Success ? WebUtility.HtmlDecode(StripTagsRegex().Replace(match.Groups["value"].Value, " ")).Trim() : null;
    }

    private bool TryReadCache(string path, out string content)
    {
        content = string.Empty;
        if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) <
            _timeProvider.GetUtcNow().UtcDateTime.AddHours(-Math.Max(1, _options.SuccessfulCacheHours))) return false;
        try
        {
            content = File.ReadAllText(path, Encoding.UTF8);
            _ = UltraStarLyricsImporter.Parse(content);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Ignoring invalid USDB cache entry {CacheFile}", path);
            return false;
        }
    }

    private static async Task<string?> TryExtractLyricsAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumDownloadBytes)
            throw new InvalidDataException("USDB response exceeds the TXT size limit.");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaximumDownloadBytes)
                throw new InvalidDataException("USDB response exceeds the TXT size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }
        var bytes = buffer.ToArray();
        if (bytes.Length >= 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
        {
            buffer.Position = 0;
            using var archive = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
            var entry = archive.Entries.FirstOrDefault(item => item.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Length > MaximumDownloadBytes) return null;
            await using var entryStream = entry.Open();
            using var reader = new StreamReader(entryStream, Encoding.UTF8, true);
            var zipped = await reader.ReadToEndAsync(cancellationToken);
            return UltraStarLyricsImporter.LooksLikeUltraStar(zipped) ? zipped : null;
        }
        var text = DecodeText(bytes);
        return UltraStarLyricsImporter.LooksLikeUltraStar(text) ? text : null;
    }

    private static string DecodeText(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    private static async Task<UsdbDownloadedLyrics> CacheAsync(UsdbVersionCandidate version, string content,
        string cacheFile, bool fromCache, CancellationToken cancellationToken)
    {
        var parsed = UltraStarLyricsImporter.Parse(content);
        var temporary = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
        File.Move(temporary, cacheFile, overwrite: true);
        return new(version, content, parsed, fromCache);
    }

    private CancellationTokenSource Timeout(CancellationToken cancellationToken, TimeSpan extra = default)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        result.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)) + extra);
        return result;
    }

    private Uri BaseUri() => new(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);

    private static string ResolveCachePath(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathFullyQualified(configured) ? configured : Path.Combine(contentRoot, configured));

    private sealed record CacheEntry<T>(T Value, DateTimeOffset ExpiresAt);

    [GeneratedRegex("href\\s*=\\s*[\"'](?<href>[^\"']+/\\d+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex VersionLinkRegex();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex StripTagsRegex();
    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
    [GeneratedRegex("Edit(?:ion|on)</div>\\s*<div[^>]*>(?<value>.*?)</div>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex EditionRegex();
}
