namespace Karaoke.Editor.Core;

/// <summary>
/// UI-independent mirror of the Unity Stage section and progress rules. Keeping
/// this model free of Avalonia/Unity types makes the editor preview testable.
/// </summary>
public sealed class StageLyricsPreview
{
    private const double SingingPauseSeconds = .8;
    private const int MaxSectionLines = 3;
    private const int MaxSectionCharacters = 125;
    private readonly IReadOnlyList<LyricSegment> _lines;
    private readonly List<Section> _sections = [];

    public StageLyricsPreview(LyricsEditorDocument document)
    {
        _lines = document.Lines.OrderBy(line => line.Start).ToList();
        BuildSections();
    }

    public StagePreviewFrame Evaluate(TimeSpan position)
    {
        if (_sections.Count == 0) return new([], false, 0, 1);
        var seconds = position.TotalSeconds;
        var sectionIndex = VisibleSection(seconds);
        var section = _sections[sectionIndex];
        var fade = 1d;
        if (sectionIndex > 0) fade = Math.Clamp((seconds - TransitionTime(sectionIndex - 1)) / .28, 0, 1);
        if (sectionIndex + 1 < _sections.Count && seconds >= TransitionTime(sectionIndex) - .28)
            fade = Math.Min(fade, Math.Clamp((TransitionTime(sectionIndex) - seconds) / .28, 0, 1));
        var lines = Enumerable.Range(section.First, section.Last - section.First + 1)
            .Select(index => new StagePreviewLine(DisplayText(_lines[index]), LineProgress(_lines[index], seconds),
                SingingPace(_lines[index], seconds))).ToList();
        var cueRemaining = section.Start - seconds;
        var showCue = (section.HasPauseBefore || sectionIndex == 0) && cueRemaining is >= 0 and <= 1.15;
        return new(lines, showCue, showCue ? 1 - cueRemaining / 1.15 : 0, fade);
    }

    private void BuildSections()
    {
        if (_lines.Count == 0) return;
        var first = 0;
        for (var index = 1; index < _lines.Count; index++)
        {
            var manualBreak = _lines[index - 1].HoldAfterMilliseconds is not null;
            if (!manualBreak && (_lines[index].Start - VocalEnd(_lines[index - 1])).TotalSeconds < SingingPauseSeconds) continue;
            var pause = first == 0 ? _lines[first].Start.TotalSeconds
                : (_lines[first].Start - VocalEnd(_lines[first - 1])).TotalSeconds;
            AddPages(first, index - 1, first > 0 || pause >= SingingPauseSeconds, pause);
            first = index;
        }
        var finalPause = first == 0 ? _lines[first].Start.TotalSeconds
            : (_lines[first].Start - VocalEnd(_lines[first - 1])).TotalSeconds;
        AddPages(first, _lines.Count - 1, first > 0 || finalPause >= SingingPauseSeconds, finalPause);
    }

    private void AddPages(int first, int last, bool hasPause, double pause)
    {
        var pageFirst = first;
        var characters = 0;
        for (var index = first; index <= last; index++)
        {
            var next = characters + _lines[index].Text.Length;
            if (index > pageFirst && (index - pageFirst >= MaxSectionLines || next > MaxSectionCharacters))
            {
                _sections.Add(CreateSection(pageFirst, index - 1, pageFirst == first && hasPause,
                    pageFirst == first ? pause : 0));
                pageFirst = index;
                characters = 0;
            }
            characters += _lines[index].Text.Length;
        }
        _sections.Add(CreateSection(pageFirst, last, pageFirst == first && hasPause, pageFirst == first ? pause : 0));
    }

    private Section CreateSection(int first, int last, bool pause, double pauseSeconds) =>
        new(first, last, _lines[first].Start.TotalSeconds, VocalEnd(_lines[last]).TotalSeconds, pause, pauseSeconds);

    private int VisibleSection(double position)
    {
        for (var index = 0; index < _sections.Count - 1; index++)
            if (position < TransitionTime(index)) return index;
        return _sections.Count - 1;
    }

    private double TransitionTime(int index)
    {
        var section = _sections[index];
        if (index + 1 >= _sections.Count) return section.VocalEnd;
        var manualHold = _lines[section.Last].HoldAfterMilliseconds;
        if (manualHold is not null)
            return Math.Min(section.VocalEnd + manualHold.Value / 1000d, _sections[index + 1].Start);
        var gap = _sections[index + 1].Start - section.VocalEnd;
        return gap < 2 ? section.VocalEnd : section.VocalEnd + Math.Min(.8, gap - 1.15);
    }

    private static double LineProgress(LyricSegment line, double position)
    {
        if (line.Children.Count == 0)
            return ClampProgress(position, line.Start.TotalSeconds, line.End.TotalSeconds);
        var total = line.Children.Sum(word => Math.Max(1, word.Text.Length) + 1);
        var completed = 0d;
        foreach (var word in line.Children)
        {
            var weight = Math.Max(1, word.Text.Length) + 1;
            var progress = WordProgress(word, position);
            completed += weight * progress;
            if (progress < 1) break;
        }
        return completed / Math.Max(1, total);
    }

    private static double WordProgress(LyricSegment word, double position)
    {
        if ((word.Confidence ?? 0) < .62 || word.Children.Count < 2)
            return ClampProgress(position, word.Start.TotalSeconds, word.End.TotalSeconds);
        var total = word.Children.Sum(syllable => Math.Max(1, syllable.Text.Length));
        var completed = 0d;
        foreach (var syllable in word.Children)
        {
            var weight = Math.Max(1, syllable.Text.Length);
            completed += weight * ClampProgress(position, syllable.Start.TotalSeconds, syllable.End.TotalSeconds);
            if (position < syllable.End.TotalSeconds) break;
        }
        return completed / Math.Max(1, total);
    }

    private static double SingingPace(LyricSegment line, double position)
    {
        if (line.Children.Count == 0) return .35;
        var word = line.Children.FirstOrDefault(candidate => position <= candidate.End.TotalSeconds) ?? line.Children[^1];
        var duration = Math.Max(.04, (word.End - word.Start).TotalSeconds);
        if ((word.Confidence ?? 0) >= .62 && word.Children.Count > 1)
        {
            var syllable = word.Children.FirstOrDefault(candidate => position <= candidate.End.TotalSeconds);
            if (syllable is not null) duration = Math.Max(.04, (syllable.End - syllable.Start).TotalSeconds);
        }
        return Math.Clamp((1.2 - duration) / 1.08, 0, 1);
    }

    private static double ClampProgress(double position, double start, double end) =>
        Math.Clamp((position - start) / Math.Max(.02, end - start), 0, 1);
    private static string DisplayText(LyricSegment line) => line.Children.Count == 0
        ? line.Text
        : string.Join(" ", line.Children.Where(word => word.Type == LyricSegmentType.Word).Select(word => word.Text));
    private static TimeSpan VocalEnd(LyricSegment line) => line.Children.Count > 0 ? line.Children[^1].End : line.End;
    private sealed record Section(int First, int Last, double Start, double VocalEnd, bool HasPauseBefore, double PauseBeforeSeconds);
}

public sealed record StagePreviewFrame(IReadOnlyList<StagePreviewLine> Lines, bool ShowEntryCue,
    double EntryCueProgress, double Alpha);
public sealed record StagePreviewLine(string Text, double Progress, double Pace);
