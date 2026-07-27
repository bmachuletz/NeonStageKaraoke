namespace Karaoke.App.Desktop;

internal sealed class EditorAudioCache(HttpClient http)
{
    private readonly string _root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeonStage", "editor-audio-v2-pcm");

    public async Task<Uri> GetAsync(Guid songId, string kind, Uri source, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, $"{songId:N}.{kind}.wav");
        if (new FileInfo(target) is { Exists: true, Length: > 4096 }) return new Uri(target);

        var download = target + ".source";
        var temporary = target + ".download";
        try
        {
            using var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(download, FileMode.Create, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await input.CopyToAsync(output, cancellationToken);
            await RunFfmpegAsync([
                "-i", download, "-vn", "-map_metadata", "-1", "-af", "asetpts=N/SR/TB",
                "-c:a", "pcm_s16le", "-f", "wav", temporary
            ], cancellationToken, "PCM-Arbeitsdatei konnte nicht erzeugt werden");
            File.Move(temporary, target, true);
            return new Uri(target);
        }
        finally
        {
            if (File.Exists(download)) File.Delete(download);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<Uri> GetMixAsync(Guid songId, Uri instrumental, Uri vocals, int instrumentalVolume,
        int vocalVolume, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, $"{songId:N}.mix-i{instrumentalVolume}-v{vocalVolume}.wav");
        if (new FileInfo(target) is { Exists: true, Length: > 4096 }) return new Uri(target);
        var temporary = target + ".download";
        try
        {
            await RunFfmpegAsync([
                "-i", instrumental.LocalPath, "-i", vocals.LocalPath,
                "-filter_complex",
                $"[0:a]volume={(instrumentalVolume / 100d).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}[i];" +
                $"[1:a]volume={(vocalVolume / 100d).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}[v];" +
                "[i][v]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.98,asetpts=N/SR/TB[out]",
                "-map", "[out]", "-map_metadata", "-1", "-c:a", "pcm_s16le", "-f", "wav", temporary
            ], cancellationToken, "Preview-Mix fehlgeschlagen");
            File.Move(temporary, target, true);
            return new Uri(target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static async Task RunFfmpegAsync(IEnumerable<string> arguments, CancellationToken cancellationToken,
        string failureMessage)
    {
        var start = new System.Diagnostics.ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true
        };
        foreach (var argument in new[] { "-y", "-nostdin", "-hide_banner", "-loglevel", "error" })
            start.ArgumentList.Add(argument);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ??
                            throw new InvalidOperationException("FFmpeg konnte nicht gestartet werden.");
        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException(failureMessage + ": " + error.Trim());
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
    }
}
