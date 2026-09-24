using System.Text.Json;

namespace Karaoke.App.Desktop;

internal sealed record EditorAudioSettings(string? OutputDeviceId = null, string? OutputDeviceName = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NeonStage", "editor-audio-settings-v1.json");

    public static EditorAudioSettings Load()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<EditorAudioSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                  ?? new EditorAudioSettings()
                : new EditorAudioSettings();
        }
        catch { return new EditorAudioSettings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, SettingsPath, true);
    }
}
