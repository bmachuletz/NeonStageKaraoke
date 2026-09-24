using System.Text.Json;
using Karaoke.Contracts;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

public sealed record QobuzWorkerSettings(bool Enabled, string AppId, string AppSecret,
    string UserAuthToken, int FormatId, string ApiBaseUrl);

public sealed class QobuzPluginSettingsService(IOptions<KaraokeOptions> karaokeOptions,
    IOptions<QobuzOptions> qobuzOptions)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly KaraokeOptions _karaoke = karaokeOptions.Value;
    private QobuzWorkerSettings _settings = FromOptions(qobuzOptions.Value);
    private bool _initialized;

    private string SettingsPath
    {
        get
        {
            var database = Path.GetFullPath(_karaoke.DatabasePath);
            return Path.Combine(Path.GetDirectoryName(database)!,
                Path.GetFileNameWithoutExtension(database) + ".qobuz-plugin.json");
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
                var stored = await JsonSerializer.DeserializeAsync<QobuzWorkerSettings>(stream,
                    cancellationToken: cancellationToken);
                if (stored is not null) _settings = Normalize(stored);
            }
            _initialized = true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            // A damaged secret file must never silently enable another provider.
            _settings = _settings with { Enabled = false };
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<QobuzPluginSettingsDto> GetAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try { return ToDto(_settings, false); }
        finally { _gate.Release(); }
    }

    public async Task<QobuzWorkerSettings> GetWorkerSettingsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try { return _settings; }
        finally { _gate.Release(); }
    }

    public async Task<QobuzPluginSettingsDto> UpdateAsync(UpdateQobuzPluginSettingsRequest request,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (!Enum.IsDefined(request.Quality)) throw new ArgumentException("Unbekannte Qobuz-Audioqualität.");

        var apiBaseUrl = string.IsNullOrWhiteSpace(request.ApiBaseUrl)
            ? "https://www.qobuz.com/api.json/0.2"
            : ValidateApiUrl(request.ApiBaseUrl);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var secret = request.ClearStoredCredentials ? string.Empty :
                string.IsNullOrWhiteSpace(request.AppSecret) ? _settings.AppSecret : request.AppSecret.Trim();
            var token = request.ClearStoredCredentials ? string.Empty :
                string.IsNullOrWhiteSpace(request.UserAuthToken) ? _settings.UserAuthToken : request.UserAuthToken.Trim();
            var updated = new QobuzWorkerSettings(request.Enabled, request.AppId.Trim(), secret, token,
                (int)request.Quality, apiBaseUrl);
            if (updated.Enabled && !IsConfigured(updated))
                throw new ArgumentException("Zum Aktivieren werden App-ID, App-Secret und ein autorisierter User-Auth-Token benötigt.");

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

    private static string ValidateApiUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Die Qobuz-API-Basisadresse muss eine absolute HTTP- oder HTTPS-Adresse sein.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static QobuzWorkerSettings FromOptions(QobuzOptions options) => Normalize(new(options.Enabled,
        options.AppId.Trim(), options.AppSecret.Trim(), options.UserAuthToken.Trim(),
            options.FormatId, string.IsNullOrWhiteSpace(options.ApiBaseUrl)
                ? "https://www.qobuz.com/api.json/0.2"
                : options.ApiBaseUrl));

    private static QobuzWorkerSettings Normalize(QobuzWorkerSettings value)
    {
        if (!Enum.IsDefined(typeof(QobuzDownloadQuality), value.FormatId))
            throw new ArgumentException("Unbekannte Qobuz-Audioqualität in der Serverkonfiguration.");
        return value with
        {
            AppId = value.AppId?.Trim() ?? string.Empty,
            AppSecret = value.AppSecret?.Trim() ?? string.Empty,
            UserAuthToken = value.UserAuthToken?.Trim() ?? string.Empty,
            ApiBaseUrl = ValidateApiUrl(value.ApiBaseUrl)
        };
    }

    private static bool IsConfigured(QobuzWorkerSettings value) =>
        !string.IsNullOrWhiteSpace(value.AppId) && !string.IsNullOrWhiteSpace(value.AppSecret) &&
        !string.IsNullOrWhiteSpace(value.UserAuthToken);

    private static QobuzPluginSettingsDto ToDto(QobuzWorkerSettings value, bool managed)
    {
        var configured = IsConfigured(value);
        var status = value.Enabled && configured
            ? "Qobuz ist aktiv. Berechtigte Kaufdownloads ersetzen den YouTube-Download."
            : configured
                ? "Qobuz ist konfiguriert, aber deaktiviert. YouTube bleibt aktiv."
                : "Qobuz ist nicht vollständig konfiguriert. YouTube bleibt aktiv.";
        return new(value.Enabled, configured, value.AppId, !string.IsNullOrWhiteSpace(value.AppSecret),
            !string.IsNullOrWhiteSpace(value.UserAuthToken),
            Enum.IsDefined(typeof(QobuzDownloadQuality), value.FormatId)
                ? (QobuzDownloadQuality)value.FormatId : QobuzDownloadQuality.FlacCd,
            value.ApiBaseUrl, managed, status);
    }
}
