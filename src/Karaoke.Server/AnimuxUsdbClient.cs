using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Karaoke.Editor.Core;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

/// <summary>
/// Authenticated adapter for the classic usdb.animux.de HTML endpoints.
/// Credentials and session cookies are kept in memory and never logged.
/// </summary>
public sealed partial class AnimuxUsdbClient
{
    private const int MaximumDownloadBytes = 5 * 1024 * 1024;
    private const string BrowserUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/145.0.0.0 Safari/537.36";
    private readonly HttpClient _http;
    private readonly UsdbOptions _options;
    private readonly ILogger<AnimuxUsdbClient> _logger;
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private readonly string _cachePath;
    private bool _authenticated;
    private string? _authenticatedIdentity;

    public AnimuxUsdbClient(IHttpClientFactory httpClientFactory, IOptions<UsdbOptions> options,
        IWebHostEnvironment environment, ILogger<AnimuxUsdbClient> logger)
        : this(httpClientFactory.CreateClient("UsdbAnimux"), options.Value,
            ResolveCachePath(options.Value.CachePath, environment.ContentRootPath), logger)
    {
    }

    internal AnimuxUsdbClient(HttpClient http, UsdbOptions options, string cachePath,
        ILogger<AnimuxUsdbClient> logger)
    {
        _http = http;
        _options = options;
        _cachePath = cachePath;
        _logger = logger;
        Directory.CreateDirectory(_cachePath);
    }

    public bool IsConfigured => _options.Enabled && _options.Animux.Enabled &&
                                !string.IsNullOrWhiteSpace(_options.Animux.Username) &&
                                !string.IsNullOrWhiteSpace(_options.Animux.Password);

    public async Task TestConnectionAsync(CancellationToken cancellationToken)
    {
        EnsureConfigured();
        await EnsureAuthenticatedAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<UsdbSearchCandidate>> SearchAsync(string title, string artist,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        await EnsureAuthenticatedAsync(cancellationToken);
        var result = await SearchOnceAsync(title, artist, cancellationToken);
        if (result.Count == 0 && !string.IsNullOrWhiteSpace(artist))
            result = await SearchOnceAsync(title, string.Empty, cancellationToken);
        return result.Take(Math.Max(1, _options.MaximumCandidates)).ToArray();
    }

    public Task<IReadOnlyList<UsdbVersionCandidate>> ResolveVersionsAsync(UsdbSearchCandidate candidate,
        CancellationToken cancellationToken)
    {
        if (!candidate.Provider.Equals(UsdbProviders.Animux, StringComparison.OrdinalIgnoreCase) ||
            !TryReadSongId(candidate.DetailUri, out var id))
            throw new InvalidOperationException("The USDB Animux candidate has no valid song id.");
        IReadOnlyList<UsdbVersionCandidate> result =
        [new(id, candidate.Title, candidate.Artist, candidate.Year, candidate.Language, candidate.Edition,
            candidate.DetailUri, UsdbProviders.Animux)];
        return Task.FromResult(result);
    }

    public async Task<UsdbDownloadedLyrics> DownloadAsync(UsdbVersionCandidate version,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        var cacheFile = Path.Combine(_cachePath, $"animux-{version.VersionId}.txt");
        if (TryReadCache(cacheFile, out var cached))
            return new(version, cached, UltraStarLyricsImporter.Parse(cached), true);

        await EnsureAuthenticatedAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(BaseUri(), $"index.php?link=gettxt&id={version.VersionId}"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["wd"] = "1" })
        };
        using var timeout = Timeout(cancellationToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var html = await ReadLimitedAsync(response, timeout.Token);
        ThrowIfLoggedOut(html);
        var match = TextAreaRegex().Match(html);
        if (!match.Success)
            throw new InvalidDataException("USDB Animux returned no UltraStar TXT textarea.");
        var content = WebUtility.HtmlDecode(match.Groups["content"].Value).Trim();
        if (!UltraStarLyricsImporter.LooksLikeUltraStar(content))
            throw new InvalidDataException("USDB Animux returned an invalid UltraStar TXT payload.");
        var parsed = UltraStarLyricsImporter.Parse(content);
        await WriteCacheAsync(cacheFile, content, timeout.Token);
        return new(version, content, parsed, false);
    }

    private async Task<IReadOnlyList<UsdbSearchCandidate>> SearchOnceAsync(string title, string artist,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri(), "index.php?link=list"))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["interpret"] = artist,
                ["title"] = title,
                ["order"] = "id",
                ["ud"] = "desc",
                ["limit"] = Math.Max(1, _options.MaximumCandidates).ToString(),
                ["details"] = "1",
                ["start"] = "0",
                ["newsearch"] = "Start Search"
            })
        };
        using var timeout = Timeout(cancellationToken);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var html = await ReadLimitedAsync(response, timeout.Token);
        ThrowIfLoggedOut(html);
        return ParseSearch(html);
    }

    internal IReadOnlyList<UsdbSearchCandidate> ParseSearch(string html)
    {
        var result = new List<UsdbSearchCandidate>();
        foreach (Match row in SongRowRegex().Matches(html))
        {
            if (!long.TryParse(row.Groups["id"].Value, out var id)) continue;
            var cells = CellRegex().Matches(row.Groups["body"].Value)
                .Select(cell => CleanCell(cell.Groups["body"].Value)).ToArray();
            if (cells.Length < 9) continue;
            var artist = cells[2];
            var title = cells[3];
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist)) continue;
            int? year = int.TryParse(cells[5], out var parsedYear) ? parsedYear : null;
            var detail = new Uri(BaseUri(), $"index.php?link=detail&id={id}");
            result.Add(new($"{title} - {artist}", title, artist, year, detail, UsdbProviders.Animux,
                cells[8], cells[6]));
        }
        return result.DistinctBy(item => item.DetailUri.AbsoluteUri, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        var identity = CredentialIdentity();
        if (_authenticated && _authenticatedIdentity == identity) return;
        await _sessionGate.WaitAsync(cancellationToken);
        try
        {
            identity = CredentialIdentity();
            if (_authenticated && _authenticatedIdentity == identity) return;
            _authenticated = false;
            using var timeout = Timeout(cancellationToken);
            // Animux associates the login with a cookie created by the landing
            // page. A direct credential POST can be rejected despite valid
            // credentials, so initialize the browser-like session first.
            using (var landingRequest = CreateBrowserRequest(HttpMethod.Get, BaseUri()))
            using (var landing = await _http.SendAsync(landingRequest,
                       HttpCompletionOption.ResponseHeadersRead, timeout.Token))
                landing.EnsureSuccessStatusCode();

            using var request = CreateBrowserRequest(HttpMethod.Post, BaseUri(), BaseUri());
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["user"] = _options.Animux.Username,
                ["pass"] = _options.Animux.Password,
                ["login"] = "Login"
            });
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            response.EnsureSuccessStatusCode();
            var html = await ReadLimitedAsync(response, timeout.Token);
            if (html.Contains("Login or Password invalid", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("USDB Animux login failed. Check the configured credentials.");

            using var profileRequest = CreateBrowserRequest(HttpMethod.Get,
                new Uri(BaseUri(), "index.php?link=profil"), BaseUri());
            using var profile = await _http.SendAsync(profileRequest,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            profile.EnsureSuccessStatusCode();
            var profileHtml = await ReadLimitedAsync(profile, timeout.Token);
            if (!LoggedInRegex().IsMatch(profileHtml) ||
                profileHtml.Contains("Please login", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("USDB Animux login could not be confirmed.");
            _authenticated = true;
            _authenticatedIdentity = identity;
            _logger.LogInformation("Authenticated USDB Animux session established");
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    private string CredentialIdentity()
    {
        var bytes = Encoding.UTF8.GetBytes(_options.Animux.Username + "\0" + _options.Animux.Password);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static HttpRequestMessage CreateBrowserRequest(HttpMethod method, Uri uri, Uri? referrer = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Headers.Referrer = referrer;
        return request;
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
            throw new InvalidOperationException("USDB Animux credentials are not configured.");
    }

    private void ThrowIfLoggedOut(string html)
    {
        if (html.Contains("You are not logged in. Login to use this function.",
                StringComparison.OrdinalIgnoreCase))
        {
            _authenticated = false;
            _authenticatedIdentity = null;
            throw new InvalidOperationException("The USDB Animux session expired.");
        }
    }

    private bool TryReadCache(string path, out string content)
    {
        content = string.Empty;
        if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) <
            DateTime.UtcNow.AddHours(-Math.Max(1, _options.SuccessfulCacheHours))) return false;
        try
        {
            content = File.ReadAllText(path, Encoding.UTF8);
            _ = UltraStarLyricsImporter.Parse(content);
            return true;
        }
        catch { return false; }
    }

    private static async Task WriteCacheAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporary, content, new UTF8Encoding(false), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<string> ReadLimitedAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumDownloadBytes)
            throw new InvalidDataException("USDB Animux response exceeds the size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + read > MaximumDownloadBytes)
                throw new InvalidDataException("USDB Animux response exceeds the size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static string CleanCell(string value) => WebUtility.HtmlDecode(
        WhitespaceRegex().Replace(StripTagsRegex().Replace(value, " "), " ")).Trim();

    private static bool TryReadSongId(Uri uri, out long id)
    {
        var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        foreach (var pair in query)
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && parts[0].Equals("id", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(Uri.UnescapeDataString(parts[1]), out id)) return true;
        }
        id = 0;
        return false;
    }

    private CancellationTokenSource Timeout(CancellationToken cancellationToken)
    {
        var result = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        result.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));
        return result;
    }

    private Uri BaseUri()
    {
        var uri = new Uri(_options.Animux.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        if (uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("USDB Animux requires an HTTP or HTTPS base URL.");
        return uri;
    }

    private static string ResolveCachePath(string configured, string contentRoot) =>
        Path.GetFullPath(Path.IsPathFullyQualified(configured) ? configured : Path.Combine(contentRoot, configured));

    [GeneratedRegex("<tr\\s+class=[\"']list_tr\\d[\"'][^>]*data-songid=[\"'](?<id>\\d+)[\"'][^>]*>(?<body>.*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SongRowRegex();
    [GeneratedRegex("<td\\b[^>]*>(?<body>.*?)</td>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex CellRegex();
    [GeneratedRegex("<textarea\\b[^>]*>(?<content>.*?)</textarea>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TextAreaRegex();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex StripTagsRegex();
    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
    [GeneratedRegex("(?:Welcome|Willkommen|Bienvenue)[^<]*<b>[^<]+</b>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex LoggedInRegex();
}
