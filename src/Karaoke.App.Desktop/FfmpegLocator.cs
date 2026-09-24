using System.Diagnostics;

namespace Karaoke.App.Desktop;

internal static class FfmpegLocator
{
    private static readonly string DiagnosticRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeonStage", "logs");

    public static string ExecutablePath
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("FFMPEG_EXE")?.Trim();
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

            // Windows releases ship ffmpeg.exe next to the editor. Do not rely
            // on PATH or on users starting the app through the .cmd launcher.
            var bundledName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
            var bundled = Path.Combine(AppContext.BaseDirectory, bundledName);
            return File.Exists(bundled) ? bundled : bundledName;
        }
    }

    public static ProcessStartInfo CreateStartInfo(bool redirectStandardOutput = false) => new(ExecutablePath)
    {
        UseShellExecute = false,
        RedirectStandardOutput = redirectStandardOutput,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    public static string WriteDiagnostic(string operation, string details)
    {
        Directory.CreateDirectory(DiagnosticRoot);
        var path = Path.Combine(DiagnosticRoot, $"ffmpeg-{operation}.log");
        File.WriteAllText(path,
            $"Zeit: {DateTimeOffset.Now:O}{Environment.NewLine}" +
            $"FFmpeg: {ExecutablePath}{Environment.NewLine}" + details.Trim() + Environment.NewLine);
        return path;
    }
}
