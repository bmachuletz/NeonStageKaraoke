using System.Text.Json;

namespace Karaoke.App.Services;

public sealed record AppPreferences(string ServerAddress = "http://localhost:5274", int Volume = 85, Guid ControllerId = default, int VocalVolume = 0)
{
    public static string DefaultServerAddress { get; set; } = "http://localhost:5274";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeonStage", "app-settings.json");

    private static AppPreferences CreateDefault() => new(ServerAddress: DefaultServerAddress);

    public static AppPreferences Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(FilePath)) ?? CreateDefault()
                : CreateDefault();
        }
        catch { return CreateDefault(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
