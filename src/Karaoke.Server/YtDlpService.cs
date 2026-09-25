using System.Diagnostics;

namespace Karaoke.Server;

public sealed class YtDlpService(IWebHostEnvironment environment)
{
    private readonly string _executable = ExternalToolLocator.YtDlp(environment);
    private readonly string? _deno = ExternalToolLocator.Deno(environment);

    public async Task<string> RunForOutputAsync(IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add("--no-update");
        if (_deno is not null)
        {
            start.ArgumentList.Add("--js-runtimes");
            start.ArgumentList.Add("deno:" + _deno);
            start.ArgumentList.Add("--remote-components");
            start.ArgumentList.Add("ejs:github");
        }
        else
        {
            start.ArgumentList.Add("--js-runtimes");
            start.ArgumentList.Add("node");
            start.ArgumentList.Add("--remote-components");
            start.ArgumentList.Add("ejs:github");
        }
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ??
            throw new InvalidOperationException($"{_executable} konnte nicht gestartet werden.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            throw;
        }
        var error = await stderr;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"yt-dlp wurde mit Code {process.ExitCode} beendet." : error.Trim());
        return await stdout;
    }

    public async Task<string> DownloadAudioAsync(string queryOrUrl, string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);
        var template = Path.Combine(destinationDirectory, "download.%(ext)s");
        var output = await RunForOutputAsync(
        [
            "--no-playlist", "--extract-audio", "--audio-format", "mp3", "--audio-quality", "0",
            "--output", template, "--print", "after_move:filepath", queryOrUrl
        ], cancellationToken);
        var path = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("yt-dlp hat keinen Ausgabepfad gemeldet.");
        path = Path.GetFullPath(path);
        var root = Path.GetFullPath(destinationDirectory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(path))
            throw new InvalidDataException("yt-dlp hat keine gültige Audiodatei im Arbeitsordner erzeugt.");
        return path;
    }
}
