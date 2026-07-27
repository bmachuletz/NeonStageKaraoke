using System.Text.Json;
using Karaoke.Contracts;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

public sealed class ServerSettingsService(IOptions<KaraokeOptions> options)
{
    private readonly KaraokeOptions _options = options.Value;
    private string SettingsPath
    {
        get
        {
            var database = Path.GetFullPath(_options.DatabasePath);
            return Path.Combine(Path.GetDirectoryName(database)!,
                Path.GetFileNameWithoutExtension(database) + ".server-settings.json");
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // Explicit deployment configuration is authoritative. In particular,
        // a persisted host path must never replace the container mount /library.
        if (!string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("Karaoke__LibraryPath"))) return;
        if (!File.Exists(SettingsPath)) return;
        try
        {
            await using var stream = File.OpenRead(SettingsPath);
            var settings = await JsonSerializer.DeserializeAsync<LibrarySettingsDto>(stream, cancellationToken: cancellationToken);
            if (!string.IsNullOrWhiteSpace(settings?.LibraryPath)) _options.LibraryPath = settings.LibraryPath;
        }
        catch (Exception) { }
    }

    public LibrarySettingsDto Get() => new(Path.GetFullPath(_options.LibraryPath));

    public async Task<LibrarySettingsDto> UpdateAsync(string libraryPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(libraryPath)) throw new ArgumentException("Der Bibliothekspfad darf nicht leer sein.");
        var fullPath = Path.GetFullPath(libraryPath.Trim());
        Directory.CreateDirectory(fullPath);
        _options.LibraryPath = fullPath;
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        await using var stream = File.Create(SettingsPath);
        await JsonSerializer.SerializeAsync(stream, new LibrarySettingsDto(fullPath), cancellationToken: cancellationToken);
        return new(fullPath);
    }
}
