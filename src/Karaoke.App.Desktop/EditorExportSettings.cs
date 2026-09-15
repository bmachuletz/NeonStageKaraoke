using System.Diagnostics;
using System.Text.Json;

namespace Karaoke.App.Desktop;

internal enum EditorVideoEncoderMode
{
    Auto,
    Software,
    NvidiaNvenc
}

internal sealed record EditorExportSettings(EditorVideoEncoderMode VideoEncoder = EditorVideoEncoderMode.Auto)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NeonStage", "editor-export-settings-v1.json");
    private static readonly object DetectionLock = new();
    private static Task<bool>? _nvencDetection;

    public static EditorExportSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new EditorExportSettings();
            return JsonSerializer.Deserialize<EditorExportSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                   ?? new EditorExportSettings();
        }
        catch { return new EditorExportSettings(); }
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(directory);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, SettingsPath, true);
    }

    public async Task<string> ResolveEncoderAsync(CancellationToken cancellationToken = default) =>
        VideoEncoder switch
        {
            EditorVideoEncoderMode.Software => "libx264",
            EditorVideoEncoderMode.NvidiaNvenc => "h264_nvenc",
            _ => await DetectNvencAsync(cancellationToken) ? "h264_nvenc" : "libx264"
        };

    public static Task<bool> DetectNvencAsync(CancellationToken cancellationToken = default)
    {
        lock (DetectionLock)
            _nvencDetection ??= DetectNvencCoreAsync(CancellationToken.None);
        return _nvencDetection.WaitAsync(cancellationToken);
    }

    private static async Task<bool> DetectNvencCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("ffmpeg")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
                         "color=c=black:s=64x64:d=0.04", "-frames:v", "1", "-c:v", "h264_nvenc",
                         "-f", "null", "-"
                     })
                process.StartInfo.ArgumentList.Add(argument);
            if (!process.Start()) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }
}
