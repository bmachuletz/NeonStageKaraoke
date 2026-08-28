using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Karaoke.Editor.Core;

namespace Karaoke.Server;

public sealed record AudioEdgeTiming(
    TimeSpan Duration,
    TimeSpan LeadingSilence,
    TimeSpan TrailingSilence,
    bool Measured)
{
    public TimeSpan AudibleDuration => TimeSpan.FromSeconds(Math.Max(0,
        Duration.TotalSeconds - LeadingSilence.TotalSeconds - TrailingSilence.TotalSeconds));
}

public enum UsdbTimelineHypothesis
{
    Unavailable,
    OriginalTimeline,
    ImportedAudioHasExtraEdgeSilence
}

public sealed record UsdbRecordingTimingDiagnostic(
    UsdbTimelineHypothesis Hypothesis,
    TimeSpan? DeclaredUsdbDuration,
    TimeSpan ImportedDuration,
    TimeSpan ImportedAudibleDuration,
    TimeSpan LeadingSilence,
    TimeSpan TrailingSilence,
    double FullDurationDifferenceSeconds,
    double TrimmedDurationDifferenceSeconds,
    TimeSpan SuggestedGlobalOffset,
    bool DurationCompatible,
    string Reason);

/// <summary>
/// Compares the media timeline declared by UltraStar with an imported audio
/// file. This is diagnostic only: it never rewrites lyrics or audio.
/// </summary>
public static partial class UsdbRecordingTimingAnalyzer
{
    public static UsdbRecordingTimingDiagnostic Compare(
        AudioEdgeTiming audio, UltraStarLyricsImport lyrics,
        double maximumDifferenceSeconds = 3)
    {
        if (lyrics.Metadata.DeclaredEndMilliseconds is not { } endMilliseconds ||
            endMilliseconds <= 0)
            return new(UsdbTimelineHypothesis.Unavailable, null, audio.Duration,
                audio.AudibleDuration, audio.LeadingSilence, audio.TrailingSilence,
                double.PositiveInfinity, double.PositiveInfinity, TimeSpan.Zero, false,
                "UltraStar candidate has no #END media duration");

        var declared = TimeSpan.FromMilliseconds(endMilliseconds);
        var fullDifference = Math.Abs(audio.Duration.TotalSeconds - declared.TotalSeconds);
        var trimmedDifference = Math.Abs(audio.AudibleDuration.TotalSeconds - declared.TotalSeconds);
        var trimmedWins = audio.Measured &&
                          audio.LeadingSilence + audio.TrailingSilence >= TimeSpan.FromMilliseconds(80) &&
                          trimmedDifference + .12 < fullDifference;
        var difference = trimmedWins ? trimmedDifference : fullDifference;
        var hypothesis = trimmedWins
            ? UsdbTimelineHypothesis.ImportedAudioHasExtraEdgeSilence
            : UsdbTimelineHypothesis.OriginalTimeline;
        var offset = trimmedWins ? audio.LeadingSilence : TimeSpan.Zero;
        return new(hypothesis, declared, audio.Duration, audio.AudibleDuration,
            audio.LeadingSilence, audio.TrailingSilence,
            Math.Round(fullDifference, 3), Math.Round(trimmedDifference, 3), offset,
            difference <= maximumDifferenceSeconds,
            trimmedWins
                ? "USDB #END matches the imported audio after removing measured edge silence"
                : "USDB #END is closest to the original imported-audio timeline");
    }

    public static async Task<AudioEdgeTiming> MeasureAudioAsync(
        string audioPath, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath) || duration <= TimeSpan.Zero)
            return new(duration, TimeSpan.Zero, TimeSpan.Zero, false);
        var startInfo = new ProcessStartInfo("ffmpeg")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-hide_banner", "-nostats", "-i", audioPath,
                     "-af", "silencedetect=noise=-45dB:d=0.08", "-f", "null", "-"
                 })
            startInfo.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) return new(duration, TimeSpan.Zero, TimeSpan.Zero, false);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await stderrTask;
        if (process.ExitCode != 0)
            return new(duration, TimeSpan.Zero, TimeSpan.Zero, false);
        return ParseSilenceDetect(output, duration);
    }

    internal static AudioEdgeTiming ParseSilenceDetect(string output, TimeSpan duration)
    {
        var intervals = new List<(double Start, double End)>();
        double? pendingStart = null;
        foreach (Match match in SilenceEventRegex().Matches(output))
        {
            if (!double.TryParse(match.Groups["seconds"].Value,
                    NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)) continue;
            if (match.Groups["kind"].Value.Equals("start", StringComparison.Ordinal))
                pendingStart = Math.Clamp(seconds, 0, duration.TotalSeconds);
            else if (pendingStart is { } start)
            {
                intervals.Add((start, Math.Clamp(seconds, start, duration.TotalSeconds)));
                pendingStart = null;
            }
        }
        if (pendingStart is { } trailing)
            intervals.Add((trailing, duration.TotalSeconds));
        var leadingEnd = intervals.Where(item => item.Start <= .05)
            .Select(item => item.End).DefaultIfEmpty(0).Max();
        var trailingStart = intervals.Where(item => item.End >= duration.TotalSeconds - .10)
            .Select(item => item.Start).DefaultIfEmpty(duration.TotalSeconds).Min();
        return new(duration, TimeSpan.FromSeconds(leadingEnd),
            TimeSpan.FromSeconds(Math.Max(0, duration.TotalSeconds - trailingStart)), true);
    }

    [GeneratedRegex(@"silence_(?<kind>start|end):\s*(?<seconds>-?\d+(?:\.\d+)?)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SilenceEventRegex();
}
