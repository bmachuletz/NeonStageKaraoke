using System.Text.Json;
using Karaoke.Contracts;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

internal sealed record StoredUsdbProviderSettings(bool Enabled, string BaseUrl, bool AnimuxEnabled,
    string AnimuxBaseUrl, string AnimuxUsername, string AnimuxPassword);

public sealed class UsdbProviderSettingsService(
    IOptions<KaraokeOptions> karaokeOptions,
    IOptions<UsdbOptions> usdbOptions)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly KaraokeOptions _karaoke = karaokeOptions.Value;
    private readonly UsdbOptions _options = usdbOptions.Value;
    private bool _initialized;

    private string SettingsPath
    {
        get
        {
            var database = Path.GetFullPath(_karaoke.DatabasePath);
            return Path.Combine(Path.GetDirectoryName(database)!,
                Path.GetFileNameWithoutExtension(database) + ".usdb-providers.json");
        }
    }

    private static bool ManagedByEnvironment => new[]
    {
        "Usdb__Enabled", "Usdb__BaseUrl", "Usdb__Animux__Enabled", "Usdb__Animux__BaseUrl",
        "Usdb__Animux__Username", "Usdb__Animux__Password"
    }.Any(name => Environment.GetEnvironmentVariable(name) is not null);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            if (!ManagedByEnvironment && File.Exists(SettingsPath))
            {
                await using var stream = File.OpenRead(SettingsPath);
                var stored = await JsonSerializer.DeserializeAsync<StoredUsdbProviderSettings>(stream,
                    cancellationToken: cancellationToken);
                if (stored is not null) Apply(Normalize(stored));
            }
            _initialized = true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            _options.Animux.Enabled = false;
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<UsdbProviderSettingsDto> GetAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try { return ToDto(); }
        finally { _gate.Release(); }
    }

    public async Task<UsdbProviderSettingsDto> UpdateAsync(UpdateUsdbProviderSettingsRequest request,
        CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        if (ManagedByEnvironment)
            throw new InvalidOperationException("Die USDB-Konfiguration wird durch Server-Umgebungsvariablen verwaltet.");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var password = request.ClearAnimuxCredentials ? string.Empty :
                string.IsNullOrWhiteSpace(request.AnimuxPassword)
                    ? _options.Animux.Password : request.AnimuxPassword;
            var username = request.ClearAnimuxCredentials ? string.Empty : request.AnimuxUsername;
            var stored = Normalize(new(request.Enabled, request.BaseUrl, request.AnimuxEnabled,
                request.AnimuxBaseUrl, username, password ?? string.Empty));
            if (stored.AnimuxEnabled && (string.IsNullOrWhiteSpace(stored.AnimuxUsername) ||
                                         string.IsNullOrWhiteSpace(stored.AnimuxPassword)))
                throw new ArgumentException("Zum Aktivieren von usdb.animux.de werden Benutzername und Passwort benötigt.");

            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporary = SettingsPath + ".tmp";
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, stored, cancellationToken: cancellationToken);
            File.Move(temporary, SettingsPath, true);
            RestrictFilePermissions(SettingsPath);
            Apply(stored);
            return ToDto();
        }
        finally { _gate.Release(); }
    }

    private static StoredUsdbProviderSettings Normalize(StoredUsdbProviderSettings value) => value with
    {
        BaseUrl = ValidateHttpsUrl(value.BaseUrl, "usdb.eu"),
        AnimuxBaseUrl = ValidateHttpsUrl(value.AnimuxBaseUrl, "usdb.animux.de"),
        AnimuxUsername = value.AnimuxUsername?.Trim() ?? string.Empty,
        AnimuxPassword = value.AnimuxPassword ?? string.Empty
    };

    private static string ValidateHttpsUrl(string value, string label)
    {
        if (!Uri.TryCreate(value?.Trim().TrimEnd('/'), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException($"Die {label}-Basisadresse muss eine absolute HTTPS-Adresse sein.");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    private void Apply(StoredUsdbProviderSettings value)
    {
        _options.Enabled = value.Enabled;
        _options.BaseUrl = value.BaseUrl;
        _options.Animux.Enabled = value.AnimuxEnabled;
        _options.Animux.BaseUrl = value.AnimuxBaseUrl;
        _options.Animux.Username = value.AnimuxUsername;
        _options.Animux.Password = value.AnimuxPassword;
    }

    private UsdbProviderSettingsDto ToDto()
    {
        var configured = !string.IsNullOrWhiteSpace(_options.Animux.Username) &&
                         !string.IsNullOrWhiteSpace(_options.Animux.Password);
        var status = _options.Animux.Enabled && configured
            ? "usdb.animux.de ist konfiguriert und wird vor usdb.eu versucht."
            : configured
                ? "usdb.animux.de ist konfiguriert, aber deaktiviert."
                : "usdb.animux.de ist nicht vollständig konfiguriert; usdb.eu bleibt aktiv.";
        return new(_options.Enabled, _options.BaseUrl, _options.Animux.Enabled,
            _options.Animux.BaseUrl, _options.Animux.Username, !string.IsNullOrWhiteSpace(_options.Animux.Password),
            ManagedByEnvironment, status);
    }

    private static void RestrictFilePermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
