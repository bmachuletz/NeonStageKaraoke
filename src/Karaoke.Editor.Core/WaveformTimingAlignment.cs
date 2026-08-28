namespace Karaoke.Editor.Core;

public readonly record struct WaveformSyncResult(
    int SegmentCount,
    int AcousticBoundaries,
    bool UsedWaveform);

internal static class WaveformTimingAlignment
{
    private const double MinimumDynamicRange = 0.055;

    public static int Refine(IReadOnlyList<LyricSegment> roots, WaveformPyramid? waveform)
    {
        if (waveform is null || waveform.Levels.Count == 0) return 0;
        var groups = BuildGroups(roots);
        var adjusted = 0;
        foreach (var group in groups)
            adjusted += RefineGroup(group, waveform);
        return adjusted;
    }

    private static IReadOnlyList<IReadOnlyList<LyricSegment>> BuildGroups(
        IReadOnlyList<LyricSegment> roots)
    {
        var groups = new List<IReadOnlyList<LyricSegment>>();

        foreach (var line in roots.Where(root => root.Type == LyricSegmentType.Line))
        {
            var words = line.Children.Where(child => child.Type == LyricSegmentType.Word)
                .OrderBy(child => child.Start).ToList();
            if (words.Count > 1) groups.Add(words);
            foreach (var word in words)
            {
                var syllables = word.Children.Where(child => child.Type == LyricSegmentType.Syllable)
                    .OrderBy(child => child.Start).ToList();
                if (syllables.Count > 1) groups.Add(syllables);
            }
        }

        foreach (var words in roots.Where(root => root.Type == LyricSegmentType.Word)
                     .GroupBy(root => root.ParentId))
        {
            var ordered = words.OrderBy(word => word.Start).ToList();
            if (ordered.Count > 1) groups.Add(ordered);
            foreach (var word in ordered)
            {
                var syllables = word.Children.Where(child => child.Type == LyricSegmentType.Syllable)
                    .OrderBy(child => child.Start).ToList();
                if (syllables.Count > 1) groups.Add(syllables);
            }
        }

        foreach (var syllables in roots.Where(root => root.Type == LyricSegmentType.Syllable)
                     .GroupBy(root => root.ParentId))
        {
            var ordered = syllables.OrderBy(syllable => syllable.Start).ToList();
            if (ordered.Count > 1) groups.Add(ordered);
        }

        return groups;
    }

    private static int RefineGroup(IReadOnlyList<LyricSegment> segments,
        WaveformPyramid waveform)
    {
        var adjusted = 0;
        for (var index = 0; index + 1 < segments.Count; index++)
        {
            var left = segments[index];
            var right = segments[index + 1];
            if (left.End <= left.Start || right.End <= right.Start || right.End <= left.Start)
                continue;

            var minimumDuration = left.Type == LyricSegmentType.Syllable
                ? TimeSpan.FromMilliseconds(25)
                : TimeSpan.FromMilliseconds(35);
            var lower = left.Start + minimumDuration;
            var upper = right.End - minimumDuration;
            if (upper <= lower) continue;

            var expected = left.End + TimeSpan.FromTicks((right.Start - left.End).Ticks / 2);
            var localSpan = Math.Max(0.12, (right.End - left.Start).TotalSeconds);
            var maximumRadius = left.Type == LyricSegmentType.Syllable ? 0.34 : 0.72;
            var radius = TimeSpan.FromSeconds(Math.Clamp(localSpan * 0.24, 0.08, maximumRadius));
            var searchStart = expected - radius > lower ? expected - radius : lower;
            var searchEnd = expected + radius < upper ? expected + radius : upper;
            if (searchEnd <= searchStart) continue;

            var gap = FindAcousticGap(waveform, searchStart, searchEnd, expected,
                left.Type == LyricSegmentType.Syllable ? 0.075 : 0.14);
            if (gap is null) continue;

            var newLeftEnd = gap.Value.Start < lower ? lower : gap.Value.Start;
            var newRightStart = gap.Value.End > upper ? upper : gap.Value.End;
            if (newRightStart < newLeftEnd)
            {
                var center = newLeftEnd + TimeSpan.FromTicks((newRightStart - newLeftEnd).Ticks / 2);
                newLeftEnd = newRightStart = center;
            }
            if (Math.Abs((newLeftEnd - left.End).TotalMilliseconds) < 5 &&
                Math.Abs((newRightStart - right.Start).TotalMilliseconds) < 5)
                continue;

            ResizeOuterBoundary(left, left.Start, newLeftEnd);
            ResizeOuterBoundary(right, newRightStart, right.End);
            adjusted++;
        }
        return adjusted;
    }

    private static void ResizeOuterBoundary(LyricSegment segment, TimeSpan start, TimeSpan end)
    {
        if (end <= start) return;
        if (segment.Children.Count > 0)
            TimelineEditing.ScaleChildren(segment, start, end);
        else
        {
            segment.Start = start;
            segment.End = end;
            TimelineEditing.MarkAdjusted(segment);
        }
    }

    private static (TimeSpan Start, TimeSpan End)? FindAcousticGap(WaveformPyramid waveform,
        TimeSpan start, TimeSpan end, TimeSpan expected, double maximumGapSeconds)
    {
        var level = waveform.Levels[0];
        var first = Math.Clamp((int)Math.Floor(start.TotalSeconds * waveform.SampleRate /
                                               level.SamplesPerPeak), 0, level.Peaks.Count - 1);
        var last = Math.Clamp((int)Math.Ceiling(end.TotalSeconds * waveform.SampleRate /
                                               level.SamplesPerPeak), first, level.Peaks.Count - 1);
        if (last - first < 4) return null;

        var values = new double[last - first + 1];
        for (var offset = 0; offset < values.Length; offset++)
        {
            var peak = level.Peaks[first + offset];
            values[offset] = Math.Max(Math.Abs(peak.Minimum), Math.Abs(peak.Maximum));
        }
        values = Smooth(values, Math.Max(1, (int)Math.Round(
            waveform.SampleRate * 0.012 / level.SamplesPerPeak)));
        var sorted = values.Order().ToArray();
        var floor = Percentile(sorted, 0.15);
        var ceiling = Percentile(sorted, 0.90);
        var scale = Math.Max(1e-6, Percentile(sorted, 0.97));
        if ((ceiling - floor) / scale < MinimumDynamicRange) return null;

        var expectedIndex = (int)Math.Round(expected.TotalSeconds * waveform.SampleRate /
                                            level.SamplesPerPeak) - first;
        expectedIndex = Math.Clamp(expectedIndex, 0, values.Length - 1);
        var bestIndex = 0;
        var bestScore = double.MaxValue;
        for (var index = 0; index < values.Length; index++)
        {
            var amplitude = Math.Clamp((values[index] - floor) / Math.Max(1e-6, ceiling - floor), 0, 2);
            var distance = Math.Abs(index - expectedIndex) / (double)Math.Max(1, values.Length - 1);
            var score = amplitude * 0.78 + distance * 0.22;
            if (score >= bestScore) continue;
            bestScore = score;
            bestIndex = index;
        }

        var quietThreshold = floor + (ceiling - floor) * 0.20;
        var maximumGapPeaks = Math.Max(1, (int)Math.Round(
            maximumGapSeconds * waveform.SampleRate / level.SamplesPerPeak));
        var gapFirst = bestIndex;
        var gapLast = bestIndex;
        while (gapFirst > 0 && bestIndex - gapFirst < maximumGapPeaks / 2 &&
               values[gapFirst - 1] <= quietThreshold) gapFirst--;
        while (gapLast + 1 < values.Length && gapLast - bestIndex < maximumGapPeaks / 2 &&
               values[gapLast + 1] <= quietThreshold) gapLast++;

        var secondsPerPeak = (double)level.SamplesPerPeak / waveform.SampleRate;
        var gapStart = TimeSpan.FromSeconds((first + gapFirst) * secondsPerPeak);
        var gapEnd = TimeSpan.FromSeconds((first + gapLast + 1) * secondsPerPeak);
        return (gapStart, gapEnd);
    }

    private static double[] Smooth(double[] values, int radius)
    {
        if (radius <= 0) return values;
        var result = new double[values.Length];
        var prefix = new double[values.Length + 1];
        for (var index = 0; index < values.Length; index++)
            prefix[index + 1] = prefix[index] + values[index];
        for (var index = 0; index < values.Length; index++)
        {
            var first = Math.Max(0, index - radius);
            var end = Math.Min(values.Length, index + radius + 1);
            result[index] = (prefix[end] - prefix[first]) / (end - first);
        }
        return result;
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var position = Math.Clamp(percentile, 0, 1) * (sorted.Count - 1);
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(sorted.Count - 1, lower + 1);
        var fraction = position - lower;
        return sorted[lower] * (1 - fraction) + sorted[upper] * fraction;
    }
}
