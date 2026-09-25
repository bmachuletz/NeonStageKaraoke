using System;

namespace NeonStage.Presentation
{

/// <summary>
/// Keeps the device-specific audio output delay separate from the canonical
/// song timeline. The editor and persisted lyric timestamps remain untouched.
/// </summary>
public static class StageTimingCompensation
{
    public const double MaximumOutputLatencySeconds = 0.5;

    public static double EstimateOutputLatencySeconds(int bufferLength, int bufferCount, int sampleRate,
        bool useWholeRingBuffer = true)
    {
        if (bufferLength <= 0 || bufferCount <= 0 || sampleRate <= 0) return 0;
        var effectiveBufferCount = useWholeRingBuffer ? bufferCount : 1;
        return Math.Min(MaximumOutputLatencySeconds,
            (double)bufferLength * effectiveBufferCount / sampleRate);
    }

    public static double LyricsPositionSeconds(double rawPositionSeconds, double durationSeconds,
        double outputLatencySeconds)
    {
        var maximum = Math.Max(0, durationSeconds);
        var corrected = rawPositionSeconds - Math.Clamp(outputLatencySeconds, 0, MaximumOutputLatencySeconds);
        return Math.Clamp(corrected, 0, maximum);
    }
}
}
