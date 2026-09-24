using System.Diagnostics;
using System.Runtime.InteropServices;
using Karaoke.Editor.Core;

namespace Karaoke.App.Desktop;

internal sealed class FfmpegWaveformService
{
    private const int SampleRate = 8000;
    private readonly string _cacheRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeonStage", "waveforms-v1");

    public async Task<WaveformPyramid> LoadAsync(Guid songId, Uri audio, CancellationToken ct, string sourceKind = "vocals")
    {
        Directory.CreateDirectory(_cacheRoot);
        var sourceRevision = SourceRevision(audio);
        var cache = Path.Combine(_cacheRoot, $"{songId:N}.{sourceKind}.{sourceRevision}.waveform");
        if (File.Exists(cache))
            try { return await ReadCacheAsync(cache, ct); } catch (Exception) when (!ct.IsCancellationRequested) { }

        var start = FfmpegLocator.CreateStartInfo(redirectStandardOutput: true);
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-i", audio.AbsoluteUri,
                     "-vn", "-ac", "1", "-ar", SampleRate.ToString(), "-f", "f32le", "pipe:1" })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg konnte nicht gestartet werden.");
        var errorTask = process.StandardError.ReadToEndAsync(ct);
        await using var memory = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(memory, ct);
        await process.WaitForExitAsync(ct);
        var error = await errorTask;
        if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg-Waveform fehlgeschlagen: " + error.Trim());
        var byteCount = (int)(memory.Length / sizeof(float) * sizeof(float));
        var samples = MemoryMarshal.Cast<byte, float>(memory.GetBuffer().AsSpan(0, byteCount));
        var pyramid = WaveformPyramid.Create(samples, SampleRate);
        await WriteCacheAsync(cache, pyramid, ct);
        return pyramid;
    }

    private static string SourceRevision(Uri audio)
    {
        if (!audio.IsFile) return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(audio.AbsoluteUri)))[..16].ToLowerInvariant();
        var file = new FileInfo(audio.LocalPath);
        return file.Exists ? $"{file.Length:x}-{file.LastWriteTimeUtc.Ticks:x}" : "missing";
    }

    private static async Task WriteCacheAsync(string path, WaveformPyramid pyramid, CancellationToken ct)
    {
        await using var stream = File.Create(path);
        await using var writer = new BinaryWriterStream(stream);
        await writer.WriteInt32Async(0x4E535746, ct);
        await writer.WriteInt32Async(pyramid.SampleRate, ct);
        await writer.WriteInt32Async(pyramid.Levels.Count, ct);
        foreach (var level in pyramid.Levels)
        {
            await writer.WriteInt32Async(level.SamplesPerPeak, ct);
            await writer.WriteInt32Async(level.Peaks.Count, ct);
            foreach (var peak in level.Peaks) { await writer.WriteSingleAsync(peak.Minimum, ct); await writer.WriteSingleAsync(peak.Maximum, ct); }
        }
    }

    private static async Task<WaveformPyramid> ReadCacheAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        await using var reader = new BinaryReaderStream(stream);
        if (await reader.ReadInt32Async(ct) != 0x4E535746) throw new InvalidDataException();
        var sampleRate = await reader.ReadInt32Async(ct);
        var levels = new List<WaveformLevel>();
        var count = await reader.ReadInt32Async(ct);
        for (var levelIndex = 0; levelIndex < count; levelIndex++)
        {
            var samplesPerPeak = await reader.ReadInt32Async(ct);
            var peakCount = await reader.ReadInt32Async(ct);
            var peaks = new WaveformPeak[peakCount];
            for (var index = 0; index < peakCount; index++)
                peaks[index] = new(await reader.ReadSingleAsync(ct), await reader.ReadSingleAsync(ct));
            levels.Add(new(samplesPerPeak, peaks));
        }
        return new() { SampleRate = sampleRate, Levels = levels };
    }

    private sealed class BinaryWriterStream(Stream stream) : IAsyncDisposable
    {
        public Task WriteInt32Async(int value, CancellationToken ct) => WriteAsync(BitConverter.GetBytes(value), ct);
        public Task WriteSingleAsync(float value, CancellationToken ct) => WriteAsync(BitConverter.GetBytes(value), ct);
        private async Task WriteAsync(byte[] bytes, CancellationToken ct) => await stream.WriteAsync(bytes, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BinaryReaderStream(Stream stream) : IAsyncDisposable
    {
        public async Task<int> ReadInt32Async(CancellationToken ct) => BitConverter.ToInt32(await ReadAsync(4, ct));
        public async Task<float> ReadSingleAsync(CancellationToken ct) => BitConverter.ToSingle(await ReadAsync(4, ct));
        private async Task<byte[]> ReadAsync(int count, CancellationToken ct)
        {
            var bytes = new byte[count];
            await stream.ReadExactlyAsync(bytes, ct);
            return bytes;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
