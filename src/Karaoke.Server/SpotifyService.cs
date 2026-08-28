using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Karaoke.Contracts;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

public sealed class SpotifyService(IHttpClientFactory clients, IOptions<SpotifyOptions> spotifyOptions,
    IOptions<KaraokeOptions> karaokeOptions, LyricsAvailabilityService lyricsAvailability)
{
    private readonly SpotifyOptions _options = spotifyOptions.Value;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private readonly Dictionary<string, DateTimeOffset> _states = [];
    private TokenState? _tokens;
    private string TokenPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(karaokeOptions.Value.DatabasePath))!, "spotify-connection.json");
    public bool Configured => !string.IsNullOrWhiteSpace(_options.ClientId) && !string.IsNullOrWhiteSpace(_options.ClientSecret);

    public async Task<SpotifyStatusDto> GetStatusAsync(CancellationToken ct)
    {
        await LoadTokensAsync(ct);
        return new(Configured, _tokens?.RefreshToken is not null, _tokens?.PlaylistUrl,
            !Configured ? "Spotify__ClientId und Spotify__ClientSecret fehlen." : null);
    }

    public string CreateAuthorizationUrl()
    {
        if (!Configured) throw new InvalidOperationException("Spotify ist noch nicht konfiguriert.");
        var state = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        lock (_states) _states[state] = DateTimeOffset.UtcNow.AddMinutes(10);
        return "https://accounts.spotify.com/authorize?" + new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.ClientId, ["response_type"] = "code", ["redirect_uri"] = _options.RedirectUri,
            ["scope"] = "playlist-modify-private", ["state"] = state, ["show_dialog"] = "true"
        }).ReadAsStringAsync().GetAwaiter().GetResult();
    }

    public async Task CompleteAuthorizationAsync(string code, string state, CancellationToken ct)
    {
        lock (_states)
        {
            if (!_states.Remove(state, out var expires) || expires < DateTimeOffset.UtcNow)
                throw new InvalidOperationException("Ungültiger oder abgelaufener Spotify-Anmeldevorgang.");
        }
        var token = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = _options.RedirectUri
        }, ct);
        _tokens = new(token.AccessToken, token.RefreshToken!, DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn - 60), null, null);
        await SaveTokensAsync(ct);
        await EnsurePlaylistAsync(ct);
    }

    public async Task<IReadOnlyList<SpotifyTrackDto>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var accessToken = await GetAccessTokenAsync(false, ct);
        var client = clients.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.spotify.com/v1/search?q={Uri.EscapeDataString(query)}&type=track&market=DE&limit=10");
        request.Headers.Authorization = new("Bearer", accessToken);
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        var tracks = document.RootElement.GetProperty("tracks").GetProperty("items").EnumerateArray().Select(item =>
        {
            var artists = string.Join(", ", item.GetProperty("artists").EnumerateArray().Select(a => a.GetProperty("name").GetString()));
            var album = item.GetProperty("album");
            var image = album.GetProperty("images").EnumerateArray().FirstOrDefault();
            var previewUrl = item.TryGetProperty("preview_url", out var preview) &&
                             preview.ValueKind == JsonValueKind.String ? preview.GetString() : null;
            return new SpotifyTrackDto(item.GetProperty("id").GetString()!, item.GetProperty("uri").GetString()!, item.GetProperty("name").GetString()!,
                artists, album.GetProperty("name").GetString() ?? "", image.ValueKind == JsonValueKind.Object ? image.GetProperty("url").GetString() : null,
                item.GetProperty("duration_ms").GetInt32(), false,
                item.GetProperty("external_urls").GetProperty("spotify").GetString(),
                AudioCatalogSource.Spotify,
                item.GetProperty("external_urls").GetProperty("spotify").GetString(),
                PreviewUrl: previewUrl);
        }).ToArray();
        return await Task.WhenAll(tracks.Select(track => lyricsAvailability.CheckAsync(track, ct)));
    }

    public async Task AddToPlaylistAsync(string spotifyUri, CancellationToken ct)
    {
        await EnsurePlaylistAsync(ct);
        var token = await GetAccessTokenAsync(true, ct);
        var client = clients.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.spotify.com/v1/playlists/{_tokens!.PlaylistId}/items");
        request.Headers.Authorization = new("Bearer", token);
        request.Content = JsonContent.Create(new { uris = new[] { spotifyUri } });
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task EnsurePlaylistAsync(CancellationToken ct)
    {
        await LoadTokensAsync(ct);
        if (_tokens?.RefreshToken is null) throw new InvalidOperationException("Spotify-Konto ist noch nicht verbunden.");
        if (!string.IsNullOrWhiteSpace(_tokens.PlaylistId)) return;
        var token = await GetAccessTokenAsync(true, ct);
        var client = clients.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.spotify.com/v1/me/playlists");
        request.Headers.Authorization = new("Bearer", token);
        request.Content = JsonContent.Create(new { name = "Neon Stage – Wunschliste", @public = false, description = "Musikwünsche aus Neon Stage Karaoke" });
        using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        _tokens = _tokens with { PlaylistId = doc.RootElement.GetProperty("id").GetString(), PlaylistUrl = doc.RootElement.GetProperty("external_urls").GetProperty("spotify").GetString() };
        await SaveTokensAsync(ct);
    }

    private async Task<string> GetAccessTokenAsync(bool requireUser, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            await LoadTokensAsync(ct);
            if (_tokens is not null && _tokens.ExpiresAt > DateTimeOffset.UtcNow) return _tokens.AccessToken;
            if (_tokens?.RefreshToken is not null)
            {
                var refreshed = await RequestTokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = _tokens.RefreshToken }, ct);
                _tokens = _tokens with { AccessToken = refreshed.AccessToken, RefreshToken = refreshed.RefreshToken ?? _tokens.RefreshToken, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn - 60) };
                await SaveTokensAsync(ct); return _tokens.AccessToken;
            }
            if (requireUser) throw new InvalidOperationException("Spotify-Konto ist noch nicht verbunden.");
            var appToken = await RequestTokenAsync(new() { ["grant_type"] = "client_credentials" }, ct);
            _tokens = new(appToken.AccessToken, null, DateTimeOffset.UtcNow.AddSeconds(appToken.ExpiresIn - 60), null, null);
            return _tokens.AccessToken;
        }
        finally { _tokenLock.Release(); }
    }

    private async Task<TokenResponse> RequestTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        var client = clients.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.ClientId}:{_options.ClientSecret}")));
        request.Content = new FormUrlEncodedContent(form);
        using var response = await client.SendAsync(request, ct); response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
        return new(doc.RootElement.GetProperty("access_token").GetString()!, doc.RootElement.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null, doc.RootElement.GetProperty("expires_in").GetInt32());
    }

    private async Task LoadTokensAsync(CancellationToken ct)
    {
        if (_tokens is not null || !File.Exists(TokenPath)) return;
        await using var stream = File.OpenRead(TokenPath);
        _tokens = await JsonSerializer.DeserializeAsync<TokenState>(stream, cancellationToken: ct);
    }
    private async Task SaveTokensAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        await using var stream = File.Create(TokenPath);
        await JsonSerializer.SerializeAsync(stream, _tokens, cancellationToken: ct);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(TokenPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    private sealed record TokenState(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? PlaylistId, string? PlaylistUrl);
    private sealed record TokenResponse(string AccessToken, string? RefreshToken, int ExpiresIn);
}
