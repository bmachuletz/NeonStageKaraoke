using NeonStage.Presentation;

namespace Karaoke.Editor.Core;

/// <summary>
/// Editor adapter for the exact presentation engine compiled into Unity. This
/// class only maps editor segments; all timing, wrapping, section, cue and
/// progress decisions live in the shared engine.
/// </summary>
public sealed class StageLyricsPreview
{
    private readonly StagePresentationEngine _engine;

    public StageLyricsPreview(LyricsEditorDocument document, bool perceptualLeadEnabled = false,
        bool karaokeTimingEnabled = false)
    {
        _engine = new StagePresentationEngine(document.Lines.Select(MapLine).ToArray(),
            perceptualLeadEnabled && !document.UsesUltraStarTiming
                ? StagePresentationEngine.PerceptualHighlightLeadSeconds : 0,
            karaokeTimingEnabled && !document.UsesUltraStarTiming);
    }

    public StagePreviewFrame Evaluate(TimeSpan position)
    {
        var frame = _engine.Evaluate(position.TotalSeconds);
        return new StagePreviewFrame(
            frame.Lines.Select(line => new StagePreviewLine(line.Text, line.Progress, line.Pace,
                line.VoiceLane, line.VoiceLabel, line.StageEffect)).ToArray(),
            frame.ShowEntryCue,
            frame.EntryCueProgress,
            frame.Alpha,
            frame.PageIndex);
    }

    private static StagePresentationLine MapLine(LyricSegment line)
    {
        var words = line.Children.Where(child => child.Type == LyricSegmentType.Word)
            .Select(word => new StagePresentationWord(
                word.Start.TotalSeconds,
                word.End.TotalSeconds,
                word.Text,
                word.Children.Where(child => child.Type == LyricSegmentType.Syllable)
                    .Select(syllable => new StagePresentationSyllable(
                        syllable.Start.TotalSeconds,
                        syllable.End.TotalSeconds,
                        syllable.Text,
                        syllable.Confidence ?? 0,
                        syllable.KaraokeTimingLocked)).ToArray(),
                word.Confidence ?? 0,
                word.KaraokeTimingLocked))
            .ToArray();
        return new StagePresentationLine(
            line.Start.TotalSeconds,
            line.End.TotalSeconds,
            line.Text,
            words,
            line.HoldAfterMilliseconds is { } hold ? hold / 1000d : null,
            line.StageEffect.ToString(),
            line.VoiceLane,
            line.VoiceLabel ?? string.Empty,
            line.KaraokeTimingLocked);
    }
}

public sealed record StagePreviewFrame(IReadOnlyList<StagePreviewLine> Lines, bool ShowEntryCue,
    double EntryCueProgress, double Alpha, int PageIndex = -1);
public sealed record StagePreviewLine(string Text, double Progress, double Pace, int VoiceLane = 0,
    string VoiceLabel = "", string StageEffect = "Automatic");
