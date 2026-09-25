using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Karaoke.Server;

public sealed record AlignmentPipelineResult(bool Publishable, double Score, string Grade);

public sealed class AlignerPipelineService(IHttpClientFactory clients, IWebHostEnvironment environment)
{
    private readonly string _ffmpeg = ExternalToolLocator.Ffmpeg(environment);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AlignmentPipelineResult> TranscribeAndAlignAsync(string audioPath,
        string destinationBase, Action<int, string>? progress, CancellationToken cancellationToken)
    {
        progress?.Invoke(2, "Audio wird für das Volltranskript hochgeladen …");
        using var form = new MultipartFormDataContent();
        AddFile(form, "audio", audioPath);
        form.Add(new StringContent("auto"), "language");
        form.Add(new StringContent("true"), "separate");
        form.Add(new StringContent("cuda"), "alignment_device");
        var status = await SubmitAndWaitAsync("api/transcription-jobs", form,
            (percent, message) => progress?.Invoke(2 + percent * 48 / 100, message), cancellationToken);
        var resultLyrics = destinationBase + ".pre-align.lrc";
        await DownloadAsync(status, "output_lrc", resultLyrics, cancellationToken);
        File.Copy(resultLyrics, destinationBase + ".lrc", overwrite: true);
        await DownloadAsync(status, "output_report", destinationBase + ".transcription.json",
            cancellationToken);
        progress?.Invoke(52, "Volltranskript gespeichert; EasyAligner wird gestartet …");
        return await AlignAsync(audioPath, resultLyrics, destinationBase,
            (percent, message) => progress?.Invoke(52 + percent * 47 / 100, message), cancellationToken);
    }

    public async Task<AlignmentPipelineResult> AlignAsync(string audioPath, string lyricsPath,
        string destinationBase, Action<int, string>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(lyricsPath) || new FileInfo(lyricsPath).Length == 0)
            throw new InvalidDataException("Für EasyAligner fehlen verwendbare Lyrics.");
        var preAlign = destinationBase + ".pre-align.lrc";
        if (!Path.GetFullPath(lyricsPath).Equals(Path.GetFullPath(preAlign), PathComparison()))
            File.Copy(lyricsPath, preAlign, overwrite: true);
        using var form = new MultipartFormDataContent();
        AddFile(form, "audio", audioPath);
        AddFile(form, "lyrics", preAlign, Path.GetFileName(destinationBase) + ".lrc");
        form.Add(new StringContent("auto"), "language");
        form.Add(new StringContent("true"), "separate");
        form.Add(new StringContent("cuda"), "alignment_device");
        var status = await SubmitAndWaitAsync("api/jobs", form, progress, cancellationToken);
        await DownloadAsync(status, "output_lrc", destinationBase + ".lrc", cancellationToken);
        await DownloadAsync(status, "output_report", destinationBase + ".alignment.json", cancellationToken);
        var stems = status.GetProperty("stems");
        await DownloadNamedAsync(status, RequiredString(stems, "vocals"), destinationBase + ".vocals.flac",
            cancellationToken);
        await DownloadNamedAsync(status, RequiredString(stems, "instrumental"),
            destinationBase + ".instrumental.flac", cancellationToken);
        if (OptionalString(status, "stems_manifest") is { } manifest)
            await DownloadNamedAsync(status, manifest, destinationBase + ".stems.json", cancellationToken);

        progress?.Invoke(94, "Runtime-Audiospuren werden erzeugt und geprüft …");
        var checks = new JsonArray();
        foreach (var kind in new[] { "vocals", "instrumental" })
        {
            var source = destinationBase + $".{kind}.flac";
            var encoded = destinationBase + $".{kind}.ogg";
            await RunFfmpegAsync([
                "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-i", source,
                "-map_metadata", "-1", "-c:a", "libvorbis", "-q:a", "6", encoded
            ], null, cancellationToken);
            checks.Add(await VerifyEncodingAsync(source, encoded, cancellationToken));
        }
        await AddEncodingChecksAsync(destinationBase + ".alignment.json", checks, cancellationToken);
        await WriteVisualsAsync(destinationBase + ".instrumental.flac",
            destinationBase + ".visuals.json", cancellationToken);

        var quality = status.TryGetProperty("quality", out var qualityElement) ? qualityElement : default;
        return new(
            quality.ValueKind == JsonValueKind.Object && quality.TryGetProperty("publishable", out var publishable) &&
            publishable.ValueKind == JsonValueKind.True,
            quality.ValueKind == JsonValueKind.Object && quality.TryGetProperty("score", out var score) &&
            score.TryGetDouble(out var numericScore) ? numericScore : 0,
            quality.ValueKind == JsonValueKind.Object ? OptionalString(quality, "grade") ?? "unbekannt" : "unbekannt");
    }

    private async Task<JsonElement> SubmitAndWaitAsync(string endpoint, MultipartFormDataContent form,
        Action<int, string>? progress, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient();
        var baseUri = AlignerBaseUri();
        using var response = await client.PostAsync(new Uri(baseUri, endpoint), form, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Aligner-Upload fehlgeschlagen: {responseBody}");
        using var submitted = JsonDocument.Parse(responseBody);
        var jobId = RequiredString(submitted.RootElement, "job_id");
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                using var statusResponse = await client.GetAsync(new Uri(baseUri, $"api/jobs/{Uri.EscapeDataString(jobId)}"),
                    cancellationToken);
                statusResponse.EnsureSuccessStatusCode();
                using var statusDocument = JsonDocument.Parse(
                    await statusResponse.Content.ReadAsStringAsync(cancellationToken));
                var root = statusDocument.RootElement;
                var state = RequiredString(root, "state");
                var percent = root.TryGetProperty("percent", out var percentValue) && percentValue.TryGetInt32(out var parsed)
                    ? parsed : 0;
                progress?.Invoke(percent, OptionalString(root, "message") ?? state);
                if (state == "completed") return root.Clone();
                if (state is "failed" or "cancelled")
                    throw new InvalidOperationException(OptionalString(root, "error") ??
                                                        OptionalString(root, "message") ?? $"Aligner-Job {state}.");
            }
        }
        catch (OperationCanceledException)
        {
            try { await client.DeleteAsync(new Uri(baseUri, $"api/jobs/{Uri.EscapeDataString(jobId)}"), CancellationToken.None); }
            catch (HttpRequestException) { }
            throw;
        }
    }

    private async Task DownloadAsync(JsonElement status, string property, string destination,
        CancellationToken cancellationToken) =>
        await DownloadNamedAsync(status, RequiredString(status, property), destination, cancellationToken);

    private async Task DownloadNamedAsync(JsonElement status, string remoteName, string destination,
        CancellationToken cancellationToken)
    {
        if (remoteName.IndexOfAny(['/', '\\']) >= 0 || string.IsNullOrWhiteSpace(remoteName))
            throw new InvalidDataException("Der Aligner hat einen ungültigen Ausgabedateinamen geliefert.");
        var jobId = RequiredString(status, "job_id");
        var client = clients.CreateClient();
        await using var source = await client.GetStreamAsync(new Uri(AlignerBaseUri(),
            $"jobs/{Uri.EscapeDataString(jobId)}/{Uri.EscapeDataString(remoteName)}"), cancellationToken);
        var temporary = destination + ".download-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await source.CopyToAsync(target, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<JsonObject> VerifyEncodingAsync(string source, string encoded,
        CancellationToken cancellationToken)
    {
        var left = Envelope(await DecodePcmAsync(source, 8000, cancellationToken), 80);
        var right = Envelope(await DecodePcmAsync(encoded, 8000, cancellationToken), 80);
        var candidates = Enumerable.Range(-25, 51)
            .Select(lag => (Lag: lag, Score: Correlation(left, right, lag))).ToArray();
        var best = candidates.MaxBy(item => item.Score);
        var durationDifference = (right.Length - left.Length) * 10;
        var verified = Math.Abs(best.Lag * 10) <= 20 && Math.Abs(durationDifference) <= 30 && best.Score >= .97;
        if (!verified) throw new InvalidDataException($"Die Ogg-Konvertierung hat die Timeline verändert: {Path.GetFileName(encoded)}");
        return new JsonObject
        {
            ["source"] = Path.GetFileName(source), ["encoded"] = Path.GetFileName(encoded),
            ["lagMilliseconds"] = best.Lag * 10, ["durationDifferenceMilliseconds"] = durationDifference,
            ["envelopeCorrelation"] = Math.Round(best.Score, 6), ["verified"] = true,
            ["method"] = "decoded-energy-cross-correlation-v1"
        };
    }

    private async Task WriteVisualsAsync(string audioPath, string outputPath,
        CancellationToken cancellationToken)
    {
        const int rate = 8000, frameSize = 800;
        var samples = await DecodePcmAsync(audioPath, rate, cancellationToken);
        var raw = new List<double[]>();
        var low = 0d;
        var alpha = Math.Exp(-2 * Math.PI * 180 / rate);
        for (var offset = 0; offset + frameSize <= samples.Length; offset += frameSize)
        {
            var total = 0d; var bass = 0d; var high = 0d; var previous = (double)samples[offset];
            for (var index = offset; index < offset + frameSize; index++)
            {
                var value = (double)samples[index]; low = alpha * low + (1 - alpha) * value;
                total += value * value; bass += low * low;
                var delta = value - previous; high += delta * delta; previous = value;
            }
            var energy = Math.Sqrt(total / frameSize); var bassValue = Math.Sqrt(bass / frameSize);
            var highValue = Math.Sqrt(high / frameSize) * .55;
            raw.Add([energy, bassValue, Math.Max(0, energy - bassValue * .45 - highValue * .2), highValue]);
        }
        var scales = Enumerable.Range(0, 4).Select(band =>
        {
            var values = raw.Select(row => row[band]).Order().ToArray();
            return values.Length == 0 ? 1d : Math.Max(values[Math.Min(values.Length - 1, (int)(values.Length * .95))], 1d);
        }).ToArray();
        var frames = new JsonArray(); var recent = new Queue<double>();
        for (var index = 0; index < raw.Count; index++)
        {
            var values = Enumerable.Range(0, 4).Select(band =>
                Math.Min(1, Math.Sqrt(raw[index][band] / scales[band]))).ToArray();
            recent.Enqueue(values[1]); if (recent.Count > 12) recent.Dequeue();
            var baseline = recent.Average(); var beat = recent.Count > 4 && values[1] > Math.Max(.28, baseline * 1.32);
            frames.Add(new JsonObject { ["timeSeconds"] = index / 10d, ["energy"] = values[0],
                ["bass"] = values[1], ["mid"] = values[2], ["high"] = values[3], ["beat"] = beat });
        }
        await File.WriteAllTextAsync(outputPath, frames.ToJsonString(), cancellationToken);
    }

    private async Task<short[]> DecodePcmAsync(string path, int rate, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        await RunFfmpegAsync(["-nostdin", "-v", "error", "-i", path, "-map_metadata", "-1",
            "-ac", "1", "-ar", rate.ToString(), "-f", "s16le", "-"], memory, cancellationToken);
        var bytes = memory.ToArray(); var samples = new short[bytes.Length / 2];
        Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * 2); return samples;
    }

    private async Task RunFfmpegAsync(IReadOnlyList<string> arguments, Stream? stdout,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, RedirectStandardOutput = stdout is not null,
            RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg konnte nicht gestartet werden.");
        var copy = stdout is null ? Task.CompletedTask : process.StandardOutput.BaseStream.CopyToAsync(stdout, cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken); await copy;
        if (process.ExitCode != 0) throw new InvalidOperationException((await error).Trim());
    }

    private static double[] Envelope(short[] samples, int frame) => Enumerable.Range(0, samples.Length / frame)
        .Select(index => Math.Sqrt(samples.AsSpan(index * frame, frame).ToArray().Average(value => (double)value * value))).ToArray();

    private static double Correlation(double[] left, double[] right, int lag)
    {
        var leftStart = Math.Max(0, lag); var rightStart = Math.Max(0, -lag);
        var count = Math.Min(left.Length - leftStart, right.Length - rightStart); if (count < 100) return 0;
        var dot = 0d; var le = 0d; var re = 0d;
        for (var index = 0; index < count; index++) { var x = left[leftStart + index]; var y = right[rightStart + index]; dot += x * y; le += x * x; re += y * y; }
        return dot / Math.Sqrt(Math.Max(1e-12, le * re));
    }

    private static async Task AddEncodingChecksAsync(string reportPath, JsonArray checks,
        CancellationToken cancellationToken)
    {
        var root = JsonNode.Parse(await File.ReadAllTextAsync(reportPath, cancellationToken))?.AsObject()
                   ?? throw new InvalidDataException("Der Alignment-Bericht ist ungültig.");
        root["encoding_validation"] = checks;
        await File.WriteAllTextAsync(reportPath, root.ToJsonString(JsonOptions), cancellationToken);
    }

    private static void AddFile(MultipartFormDataContent form, string field, string path, string? fileName = null)
    {
        var stream = File.OpenRead(path); var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, field, fileName ?? Path.GetFileName(path));
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) ?? throw new InvalidDataException($"Aligner-Antwort enthält kein {name}.");
    private static string? OptionalString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static StringComparison PathComparison() => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static Uri AlignerBaseUri()
    {
        var configured = Environment.GetEnvironmentVariable("LRC_ALIGNER_URL");
        return new Uri((string.IsNullOrWhiteSpace(configured) ? "http://127.0.0.1:8081" : configured).TrimEnd('/') + "/");
    }
}
