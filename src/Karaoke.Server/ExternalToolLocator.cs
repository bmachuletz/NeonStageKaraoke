namespace Karaoke.Server;

internal static class ExternalToolLocator
{
    public static string YtDlp(IWebHostEnvironment environment) => Resolve(
        "NEONSTAGE_YT_DLP_PATH", OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp",
        environment.ContentRootPath, Path.Combine(".tools", OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp"));

    public static string? Deno(IWebHostEnvironment environment) => ResolveOptional(
        "NEONSTAGE_DENO_PATH", OperatingSystem.IsWindows() ? "deno.exe" : "deno",
        environment.ContentRootPath, Path.Combine(".tools", OperatingSystem.IsWindows() ? "deno.exe" : "deno"));

    public static string Ffmpeg(IWebHostEnvironment environment) => Resolve(
        "NEONSTAGE_FFMPEG_PATH", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg",
        environment.ContentRootPath, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    public static string? LrcMatcher(IWebHostEnvironment environment)
    {
        var name = OperatingSystem.IsWindows() ? "LrcMatcher.exe" : "LrcMatcher";
        var resolved = ResolveOptional("LRC_MATCHER_EXE", name, environment.ContentRootPath,
            Path.Combine("tools", name));
        if (resolved is not null) return resolved;
        var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));
        foreach (var configuration in new[] { "Debug", "Release" })
        {
            var candidate = Path.Combine(root, "LrcMatcher", "bin", configuration, "net10.0", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static string Resolve(string variable, string packagedName, string contentRoot, string sourceRelative) =>
        ResolveOptional(variable, packagedName, contentRoot, sourceRelative) ??
        throw new FileNotFoundException($"Das benötigte Werkzeug {packagedName} wurde nicht gefunden.");

    private static string? ResolveOptional(string variable, string packagedName, string contentRoot,
        string sourceRelative)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return Path.GetFullPath(configured);
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, packagedName),
                     Path.Combine(AppContext.BaseDirectory, "tools", packagedName),
                     Path.GetFullPath(Path.Combine(contentRoot, "..", "..", sourceRelative))
                 })
            if (File.Exists(candidate)) return candidate;
        return null;
    }
}
