using System.Text.Json;
using Karaoke.Contracts;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

public sealed class GeniusProviderSettingsService(IOptions<KaraokeOptions> karaokeOptions,
    IOptions<GeniusOptions> geniusOptions)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly GeniusOptions _options = geniusOptions.Value;
    private bool _loaded;
    private bool ManagedByEnvironment => !string.IsNullOrWhiteSpace(
        Environment.GetEnvironmentVariable("Genius__AccessToken"));
    private string SettingsPath
    {
        get
        {
            var database = Path.GetFullPath(karaokeOptions.Value.DatabasePath);
            return Path.Combine(Path.GetDirectoryName(database)!,
                Path.GetFileNameWithoutExtension(database) + ".genius-settings.json");
        }
    }

    public async Task<GeniusProviderSettingsDto> GetAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        var configured = _options.Enabled && !string.IsNullOrWhiteSpace(_options.AccessToken);
        return new(_options.Enabled, _options.BaseUrl, !string.IsNullOrWhiteSpace(_options.AccessToken),
            ManagedByEnvironment, configured
                ? "Genius-Discovery ist aktiv. Die offizielle API liefert Links, aber keinen Lyrics-Text."
                : "Genius-Discovery ist nicht vollständig konfiguriert.");
    }

    public async Task<(bool Enabled, string BaseUrl, string AccessToken)> GetWorkerSettingsAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        return (_options.Enabled, _options.BaseUrl, _options.AccessToken);
    }

    public async Task<GeniusProviderSettingsDto> UpdateAsync(UpdateGeniusProviderSettingsRequest request,
        CancellationToken ct)
    {
        await EnsureLoadedAsync(ct);
        if (ManagedByEnvironment) throw new InvalidOperationException("Genius wird über Umgebungsvariablen verwaltet.");
        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("Die Genius-API-Adresse muss eine absolute HTTPS-Adresse sein.");
        _options.Enabled = request.Enabled;
        _options.BaseUrl = uri.AbsoluteUri.TrimEnd('/') + "/";
        if (request.ClearAccessToken) _options.AccessToken = string.Empty;
        else if (!string.IsNullOrWhiteSpace(request.AccessToken)) _options.AccessToken = request.AccessToken.Trim();
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        await File.WriteAllTextAsync(SettingsPath, JsonSerializer.Serialize(new Stored(
            _options.Enabled, _options.BaseUrl, _options.AccessToken), new JsonSerializerOptions
            { WriteIndented = true }), ct);
        return await GetAsync(ct);
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded || ManagedByEnvironment) { _loaded = true; return; }
        await _gate.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            if (File.Exists(SettingsPath))
            {
                try
                {
                    var stored = JsonSerializer.Deserialize<Stored>(await File.ReadAllTextAsync(SettingsPath, ct));
                    if (stored is not null)
                    {
                        _options.Enabled = stored.Enabled;
                        _options.BaseUrl = stored.BaseUrl;
                        _options.AccessToken = stored.AccessToken;
                    }
                }
                catch (Exception exception) when (exception is IOException or JsonException) { }
            }
            _loaded = true;
        }
        finally { _gate.Release(); }
    }

    private sealed record Stored(bool Enabled, string BaseUrl, string AccessToken);
}
