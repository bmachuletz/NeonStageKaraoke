using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Karaoke.Contracts;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

public sealed record OnlineRuntimeSettings(bool Enabled, string Provider, string ServerUrl,
    string ApiKey, string ApiSecret, string RoomPrefix, int TokenLifetimeMinutes,
    int ParticipantLeaseSeconds)
{
    public bool IsConfigured => Enabled &&
        Provider.Equals("LiveKit", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) && uri.Scheme is "ws" or "wss" &&
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
}

public sealed class OnlineSettingsService(IOptions<KaraokeOptions> karaokeOptions,
    IOptions<OnlineOptions> onlineOptions, IHttpClientFactory httpClientFactory, TimeProvider timeProvider)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly KaraokeOptions _karaoke = karaokeOptions.Value;
    private OnlineRuntimeSettings _settings = FromOptions(onlineOptions.Value);
    private bool _initialized;

    public OnlineRuntimeSettings Current => _settings;

    private string SettingsPath
    {
        get
        {
            var database = Path.GetFullPath(_karaoke.DatabasePath);
            return Path.Combine(Path.GetDirectoryName(database)!,
                Path.GetFileNameWithoutExtension(database) + ".online-settings.json");
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            if (File.Exists(SettingsPath))
            {
                await using var stream = File.OpenRead(SettingsPath);
                var stored = await JsonSerializer.DeserializeAsync<OnlineRuntimeSettings>(stream,
                    cancellationToken: cancellationToken);
                if (stored is not null) _settings = Normalize(stored);
            }
            _initialized = true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            _settings = _settings with { Enabled = false };
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<OnlineServerSettingsDto> GetAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try { return ToDto(_settings, false); }
        finally { _gate.Release(); }
    }

    public async Task<OnlineServerSettingsDto> UpdateAsync(UpdateOnlineServerSettingsRequest request,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var updated = CreateCandidate(request, _settings);
            if (updated.Enabled && !updated.IsConfigured)
                throw new ArgumentException("Zum Aktivieren werden eine WS/WSS-Adresse, API-Key und API-Secret benötigt.");
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporary = SettingsPath + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, updated, cancellationToken: cancellationToken);
            File.Move(temporary, SettingsPath, true);
            RestrictFilePermissions(SettingsPath);
            _settings = updated;
            return ToDto(_settings, false);
        }
        finally { _gate.Release(); }
    }

    public async Task<OnlineServerTestResultDto> TestAsync(UpdateOnlineServerSettingsRequest request,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        OnlineRuntimeSettings candidate;
        await _gate.WaitAsync(cancellationToken);
        try { candidate = CreateCandidate(request, _settings); }
        finally { _gate.Release(); }
        if (!candidate.IsConfigured)
            throw new ArgumentException("Für den Test werden LiveKit-URL, API-Key und API-Secret benötigt.");

        var now = timeProvider.GetUtcNow();
        var token = LiveKitTokenIssuer.Sign(new Dictionary<string, object?>
        {
            ["iss"] = candidate.ApiKey,
            ["nbf"] = now.AddSeconds(-5).ToUnixTimeSeconds(),
            ["exp"] = now.AddMinutes(2).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["video"] = new { roomList = true }
        }, candidate.ApiSecret);
        var endpoint = BuildRoomServiceEndpoint(candidate.ServerUrl);
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var stopwatch = Stopwatch.StartNew();
        using var response = await httpClientFactory.CreateClient().SendAsync(message, timeout.Token);
        stopwatch.Stop();
        if (!response.IsSuccessStatusCode)
        {
            var detail = response.StatusCode is System.Net.HttpStatusCode.Unauthorized or
                System.Net.HttpStatusCode.Forbidden
                ? "API-Key oder API-Secret wurden abgelehnt."
                : $"LiveKit antwortet mit HTTP {(int)response.StatusCode}.";
            return new(false, detail, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
        }
        return new(true, "LiveKit ist erreichbar und die Zugangsdaten sind gültig.",
            (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
    }

    private static OnlineRuntimeSettings CreateCandidate(UpdateOnlineServerSettingsRequest request,
        OnlineRuntimeSettings current)
    {
        var secret = request.ClearApiSecret ? string.Empty :
            string.IsNullOrWhiteSpace(request.ApiSecret) ? current.ApiSecret : request.ApiSecret.Trim();
        return Normalize(current with
        {
            Enabled = request.Enabled,
            ServerUrl = request.ServerUrl,
            ApiKey = request.ApiKey,
            ApiSecret = secret,
            RoomPrefix = request.RoomPrefix
        });
    }

    private static OnlineRuntimeSettings Normalize(OnlineRuntimeSettings value)
    {
        var serverUrl = (value.ServerUrl ?? string.Empty).Trim().TrimEnd('/');
        if (serverUrl.Length > 0 && (!Uri.TryCreate(serverUrl, UriKind.Absolute, out var uri) ||
                                     uri.Scheme is not ("ws" or "wss")))
            throw new ArgumentException("Die LiveKit-Adresse muss eine absolute ws://- oder wss://-Adresse sein.");
        var prefix = (value.RoomPrefix ?? string.Empty).Trim();
        if (prefix.Length is < 1 or > 40 || !Regex.IsMatch(prefix, "^[A-Za-z0-9_-]+$"))
            throw new ArgumentException("Der Room-Präfix darf nur Buchstaben, Zahlen, _ und - enthalten (1–40 Zeichen).");
        return value with
        {
            Provider = "LiveKit",
            ServerUrl = serverUrl,
            ApiKey = value.ApiKey?.Trim() ?? string.Empty,
            ApiSecret = value.ApiSecret?.Trim() ?? string.Empty,
            RoomPrefix = prefix,
            TokenLifetimeMinutes = Math.Clamp(value.TokenLifetimeMinutes, 5, 240),
            ParticipantLeaseSeconds = Math.Clamp(value.ParticipantLeaseSeconds, 6, 120)
        };
    }

    private static Uri BuildRoomServiceEndpoint(string serverUrl)
    {
        var source = new Uri(serverUrl);
        var builder = new UriBuilder(source)
        {
            Scheme = source.Scheme == "wss" ? "https" : "http",
            Path = source.AbsolutePath.TrimEnd('/') + "/twirp/livekit.RoomService/ListRooms"
        };
        return builder.Uri;
    }

    private static OnlineRuntimeSettings FromOptions(OnlineOptions value) => Normalize(new(
        value.Enabled, value.Provider, value.ServerUrl, value.ApiKey, value.ApiSecret, value.RoomPrefix,
        value.TokenLifetimeMinutes, value.ParticipantLeaseSeconds));

    private static OnlineServerSettingsDto ToDto(OnlineRuntimeSettings value, bool managed)
    {
        var configured = value.IsConfigured;
        var status = configured ? "LiveKit ist vollständig konfiguriert."
            : value.Enabled ? "LiveKit ist aktiviert, aber noch nicht vollständig konfiguriert."
            : "Online-Karaoke ist deaktiviert.";
        return new(value.Enabled, value.Provider, value.ServerUrl, value.ApiKey,
            !string.IsNullOrWhiteSpace(value.ApiSecret), value.RoomPrefix, managed, configured, status);
    }

    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
