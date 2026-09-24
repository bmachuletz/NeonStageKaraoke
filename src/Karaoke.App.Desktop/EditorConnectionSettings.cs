using System.Text.Json;

namespace Karaoke.App.Desktop;

internal sealed record EditorConnectionSettings(string? ServerUrl = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NeonStage", "editor-connection-settings-v1.json");

    public static Uri ResolveServerAddress()
    {
        var environment = (Environment.GetEnvironmentVariable("NEONSTAGE_SERVER_URL") ??
                           Environment.GetEnvironmentVariable("KARAOKE_SERVER"))?.Trim();
        var stored = Load().ServerUrl;
        var packagedDefault = Environment.GetEnvironmentVariable("NEONSTAGE_DEFAULT_SERVER_URL")?.Trim();
        // Ein expliziter Starter darf ein festes Ziel vorgeben. Ohne Vorgabe
        // bleibt die im Editor gespeicherte Adresse maßgeblich.
        var configured = !string.IsNullOrWhiteSpace(environment) ? environment
            : !string.IsNullOrWhiteSpace(stored) ? stored.Trim()
            : packagedDefault;
        return Uri.TryCreate(configured, UriKind.Absolute, out var address) &&
               address.Scheme is "http" or "https"
            ? address
            : new Uri("http://127.0.0.1:5274");
    }

    public static EditorConnectionSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<EditorConnectionSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                  ?? new EditorConnectionSettings()
                : new EditorConnectionSettings();
        }
        catch
        {
            return new EditorConnectionSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, SettingsPath, true);
    }
}
