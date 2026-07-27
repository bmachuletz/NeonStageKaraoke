namespace Karaoke.Editor.Core;

public readonly record struct WaveformPeak(float Minimum, float Maximum);

public sealed record WaveformLevel(int SamplesPerPeak, IReadOnlyList<WaveformPeak> Peaks);

public sealed class WaveformPyramid
{
    public required int SampleRate { get; init; }
    public required IReadOnlyList<WaveformLevel> Levels { get; init; }
    public TimeSpan Duration => Levels.Count == 0 ? TimeSpan.Zero :
        TimeSpan.FromSeconds((double)Levels[0].Peaks.Count * Levels[0].SamplesPerPeak / SampleRate);

    public WaveformLevel SelectLevel(double secondsPerPixel)
    {
        if (Levels.Count == 0) throw new InvalidOperationException("Die Waveform enthält keine Auflösungsstufen.");
        var desiredSamples = Math.Max(1, secondsPerPixel * SampleRate);
        return Levels.LastOrDefault(level => level.SamplesPerPeak <= desiredSamples) ?? Levels[0];
    }

    public static WaveformPyramid Create(ReadOnlySpan<float> samples, int sampleRate, int baseSamplesPerPeak = 16)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (baseSamplesPerPeak <= 0) throw new ArgumentOutOfRangeException(nameof(baseSamplesPerPeak));
        var levels = new List<WaveformLevel>();
        var peaks = ReduceSamples(samples, baseSamplesPerPeak);
        var samplesPerPeak = baseSamplesPerPeak;
        levels.Add(new(samplesPerPeak, peaks));
        while (peaks.Count > 2048)
        {
            peaks = ReducePeaks(peaks, 4);
            samplesPerPeak *= 4;
            levels.Add(new(samplesPerPeak, peaks));
        }
        return new() { SampleRate = sampleRate, Levels = levels };
    }

    private static List<WaveformPeak> ReduceSamples(ReadOnlySpan<float> samples, int size)
    {
        var result = new List<WaveformPeak>((samples.Length + size - 1) / size);
        for (var offset = 0; offset < samples.Length; offset += size)
        {
            var block = samples.Slice(offset, Math.Min(size, samples.Length - offset));
            var minimum = float.MaxValue;
            var maximum = float.MinValue;
            foreach (var sample in block) { minimum = Math.Min(minimum, sample); maximum = Math.Max(maximum, sample); }
            result.Add(new(minimum, maximum));
        }
        return result;
    }

    private static List<WaveformPeak> ReducePeaks(IReadOnlyList<WaveformPeak> source, int size)
    {
        var result = new List<WaveformPeak>((source.Count + size - 1) / size);
        for (var offset = 0; offset < source.Count; offset += size)
        {
            var minimum = float.MaxValue;
            var maximum = float.MinValue;
            for (var index = offset; index < Math.Min(source.Count, offset + size); index++)
            {
                minimum = Math.Min(minimum, source[index].Minimum);
                maximum = Math.Max(maximum, source[index].Maximum);
            }
            result.Add(new(minimum, maximum));
        }
        return result;
    }
}
