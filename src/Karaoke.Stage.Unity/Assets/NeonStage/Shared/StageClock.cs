using System;

namespace NeonStage.Timing
{

/// <summary>The one time snapshot consumed by every song-time-dependent renderer.</summary>
public readonly struct StageClockFrame
{
    public StageClockFrame(double positionSeconds, double lyricsPositionSeconds, bool isPlaying,
        double deltaSeconds)
    {
        PositionSeconds = Normalize(positionSeconds);
        LyricsPositionSeconds = Normalize(lyricsPositionSeconds);
        IsPlaying = isPlaying;
        DeltaSeconds = isPlaying ? Math.Clamp(Normalize(deltaSeconds), 0, .25) : 0;
    }

    public double PositionSeconds { get; }
    public double LyricsPositionSeconds { get; }
    public bool IsPlaying { get; }
    public double DeltaSeconds { get; }

    private static double Normalize(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? 0 : Math.Max(0, value);
}

/// <summary>
/// Source-neutral Stage clock. Live audio and deterministic export clocks expose
/// the same immutable frame to lyrics, shaders and background video.
/// </summary>
public interface IStageClock
{
    StageClockFrame Capture();
}

/// <summary>Deterministic clock used by the upcoming offline renderer.</summary>
public sealed class FrameStageClock : IStageClock
{
    private int _frameIndex;
    private int _framesPerSecond = 60;
    private double _lyricsOffsetSeconds;

    public void SetFrame(int frameIndex, int framesPerSecond, double lyricsOffsetSeconds = 0)
    {
        if (frameIndex < 0) throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (framesPerSecond is < 1 or > 240) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        _frameIndex = frameIndex;
        _framesPerSecond = framesPerSecond;
        _lyricsOffsetSeconds = lyricsOffsetSeconds;
    }

    public StageClockFrame Capture()
    {
        var position = _frameIndex / (double)_framesPerSecond;
        return new StageClockFrame(position, position + _lyricsOffsetSeconds, true,
            1d / _framesPerSecond);
    }
}

public static class StageMediaTiming
{
    public static int VideoLeadFrames(double offsetSeconds, int framesPerSecond) =>
        Math.Max(0, (int)Math.Ceiling(offsetSeconds * framesPerSecond));

    public static double VideoSourceStartSeconds(double offsetSeconds) => Math.Max(0, -offsetSeconds);
}

}
