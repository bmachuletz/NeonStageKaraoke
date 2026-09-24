using System.Diagnostics;

namespace Karaoke.App.Desktop;

internal static class FfmpegLocator
{
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
}
