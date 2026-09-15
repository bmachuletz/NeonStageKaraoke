using System;
using System.Collections.Generic;
using System.Linq;

namespace NeonStage.Timing
{

public readonly struct StageAudioAnalysisPoint
{
    public StageAudioAnalysisPoint(double timeSeconds, double energy, double bass, double mid, double high,
        bool beat)
    {
        TimeSeconds = Math.Max(0, timeSeconds);
        Energy = Clamp(energy); Bass = Clamp(bass); Mid = Clamp(mid); High = Clamp(high); Beat = beat;
    }
    public double TimeSeconds { get; }
    public double Energy { get; }
    public double Bass { get; }
    public double Mid { get; }
    public double High { get; }
    public bool Beat { get; }
    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
}

public readonly struct StageAudioAnalysisFrame
{
    public StageAudioAnalysisFrame(double energy, double bass, double mid, double high, double beatPulse)
    {
        Energy = energy; Bass = bass; Mid = mid; High = high; BeatPulse = beatPulse;
    }
    public double Energy { get; }
    public double Bass { get; }
    public double Mid { get; }
    public double High { get; }
    public double BeatPulse { get; }
}

/// <summary>Seekable, deterministic interpolation over the server's 10-Hz analysis timeline.</summary>
public sealed class StageAudioAnalysisTimeline
{
    private readonly StageAudioAnalysisPoint[] _points;
    private readonly double[] _beatTimes;
    public static StageAudioAnalysisTimeline Empty { get; } = new(Array.Empty<StageAudioAnalysisPoint>());

    public StageAudioAnalysisTimeline(IEnumerable<StageAudioAnalysisPoint> points)
    {
        _points = points.OrderBy(point => point.TimeSeconds).ToArray();
        _beatTimes = _points.Where(point => point.Beat).Select(point => point.TimeSeconds).ToArray();
    }

    public IReadOnlyList<double> BeatTimes => _beatTimes;
    public bool HasFrames => _points.Length > 0;

    public StageAudioAnalysisFrame Sample(double timeSeconds)
    {
        if (_points.Length == 0) return default;
        var time = Math.Max(0, timeSeconds);
        var right = Array.BinarySearch(_points, new StageAudioAnalysisPoint(time, 0, 0, 0, 0, false),
            PointComparer.Instance);
        if (right < 0) right = ~right;
        if (right <= 0) return Frame(_points[0], BeatPulse(time));
        if (right >= _points.Length) return Frame(_points[^1], BeatPulse(time));
        var left = _points[right - 1];
        var next = _points[right];
        var range = Math.Max(.000001, next.TimeSeconds - left.TimeSeconds);
        var amount = Math.Clamp((time - left.TimeSeconds) / range, 0, 1);
        return new StageAudioAnalysisFrame(
            Mix(left.Energy, next.Energy, amount), Mix(left.Bass, next.Bass, amount),
            Mix(left.Mid, next.Mid, amount), Mix(left.High, next.High, amount), BeatPulse(time));
    }

    private double BeatPulse(double time)
    {
        if (_beatTimes.Length == 0) return 0;
        var index = Array.BinarySearch(_beatTimes, time);
        if (index < 0) index = ~index - 1;
        if (index < 0) return 0;
        var age = time - _beatTimes[index];
        return age is >= 0 and <= .3 ? Math.Exp(-age * 9) : 0;
    }

    private static StageAudioAnalysisFrame Frame(StageAudioAnalysisPoint point, double pulse) =>
        new(point.Energy, point.Bass, point.Mid, point.High, pulse);
    private static double Mix(double left, double right, double amount) => left + (right - left) * amount;

    private sealed class PointComparer : IComparer<StageAudioAnalysisPoint>
    {
        public static PointComparer Instance { get; } = new();
        public int Compare(StageAudioAnalysisPoint x, StageAudioAnalysisPoint y) =>
            x.TimeSeconds.CompareTo(y.TimeSeconds);
    }
}

}
