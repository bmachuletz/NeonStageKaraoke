using System;
using System.Collections.Generic;
using System.Linq;

namespace NeonStage.Presentation
{

/// <summary>
/// Deterministic, renderer-independent lyrics presentation used by both the
/// Unity stage and the desktop editor. This file deliberately has no Unity,
/// Avalonia, networking or audio dependencies.
/// </summary>
public sealed class StagePresentationEngine
{
    /// <summary>
    /// Small visual-only lead used by the stage. It compensates human visual
    /// reaction time, not decoder latency, and never changes stored timings.
    /// </summary>
    public const double PerceptualHighlightLeadSeconds = 0.08;

    private const double SingingPauseSeconds = 0.8;
    private const double CountdownPauseSeconds = 3.0;
    private const double EntryCueSeconds = 1.65;
    private const double PerceptualPostRollSeconds = 0.65;
    private const double TransitionSeconds = 0.28;
    private const int MaxSectionLines = 3;
    private const int MaxSectionCharacters = 125;
    private const int MaxVisibleLines = 8;
    private const int MaxVisualRowUnits = 48;
    private const int MinVisualRowUnits = 14;

    private readonly List<StagePresentationLine> _lines;
    private readonly Dictionary<StagePresentationWord, KaraokeHighlightTimeline> _highlightTimelines =
        new Dictionary<StagePresentationWord, KaraokeHighlightTimeline>();
    private readonly KaraokeHighlightTimelineOptions _highlightOptions;
    private readonly List<Section> _sections = new List<Section>();
    private readonly double _highlightLeadSeconds;
    private readonly bool _karaokeTimingEnabled;

    public StagePresentationEngine(IReadOnlyList<StagePresentationLine> lines, double highlightLeadSeconds = 0,
        bool karaokeTimingEnabled = false, IReadOnlyList<double>? beatTimes = null,
        KaraokeHighlightTimelineOptions? highlightOptions = null)
    {
        _highlightLeadSeconds = Math.Max(0, Math.Min(.25, highlightLeadSeconds));
        _karaokeTimingEnabled = karaokeTimingEnabled;
        _highlightOptions = highlightOptions ?? KaraokeHighlightTimelineOptions.Default;
        _lines = Normalize(karaokeTimingEnabled ? ProjectKaraokeTiming(lines, beatTimes) : lines);
        BuildHighlightTimelines();
        BuildSections();
    }

    /// <summary>
    /// Produces a non-destructive karaoke geometry from acoustic alignment.
    /// Boundaries near the musical pulse (including quarter-beat subdivisions)
    /// are quantized while boundaries without a plausible pulse stay acoustic.
    /// Callers must not use this for authored UltraStar timing.
    /// </summary>
    public static IReadOnlyList<StagePresentationLine> ProjectKaraokeTiming(
        IReadOnlyList<StagePresentationLine> lines, IReadOnlyList<double>? beatTimes)
    {
        if (beatTimes == null || beatTimes.Count < 2) return lines;
        var grid = MusicalGrid(beatTimes);
        if (grid.Count == 0) return lines;
        var projected = new List<StagePresentationLine>(lines.Count);
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.Words.Count == 0) { projected.Add(line); continue; }
            var words = new List<StagePresentationWord>(line.Words.Count);
            var previousEnd = double.NegativeInfinity;
            for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
            {
                var word = line.Words[wordIndex];
                var start = word.Start;
                var end = word.End;
                if (!word.KaraokeTimingLocked)
                {
                    start = SnapBoundary(start, grid, wordIndex == 0 ? .14 : .105);
                    end = SnapBoundary(end, grid, .12);
                    start = Math.Max(start, previousEnd);
                    end = Math.Max(start + .035, end);
                    if (wordIndex + 1 < line.Words.Count)
                        end = Math.Min(end, Math.Max(start + .035, line.Words[wordIndex + 1].Start));
                }
                var syllables = ProjectSyllables(word.Syllables, start, end, grid);
                words.Add(new StagePresentationWord(start, end, word.Text, syllables, word.SyllableConfidence,
                    word.KaraokeTimingLocked, word.Notes));
                previousEnd = end;
            }
            var vocalStart = words[0].Start;
            var vocalEnd = words[words.Count - 1].End;
            projected.Add(new StagePresentationLine(
                line.KaraokeTimingLocked ? line.Start : Math.Min(line.Start, vocalStart),
                line.KaraokeTimingLocked ? line.End : Math.Max(line.End, vocalEnd), line.Text, words,
                line.HoldAfterSeconds, line.StageEffect, line.VoiceLane, line.VoiceLabel,
                line.KaraokeTimingLocked));
        }
        return projected;
    }

    private static IReadOnlyList<StagePresentationSyllable> ProjectSyllables(
        IReadOnlyList<StagePresentationSyllable> source, double wordStart, double wordEnd,
        IReadOnlyList<double> grid)
    {
        if (source.Count == 0) return source;
        var result = new List<StagePresentationSyllable>(source.Count);
        var previousEnd = wordStart;
        for (var index = 0; index < source.Count; index++)
        {
            var item = source[index];
            var start = item.Start;
            var end = item.End;
            if (!item.KaraokeTimingLocked)
            {
                start = index == 0 ? wordStart : SnapBoundary(start, grid, .075);
                end = index == source.Count - 1 ? wordEnd : SnapBoundary(end, grid, .075);
                start = Math.Max(previousEnd, Math.Min(start, wordEnd - .02));
                end = Math.Max(start + .02, Math.Min(end, wordEnd));
            }
            result.Add(new StagePresentationSyllable(start, end, item.Text, item.Confidence,
                item.KaraokeTimingLocked, item.Notes));
            previousEnd = end;
        }
        return result;
    }

    private static List<double> MusicalGrid(IReadOnlyList<double> beats)
    {
        var onsets = new List<double>();
        var previousFrame = double.NegativeInfinity;
        for (var index = 0; index < beats.Count; index++)
        {
            var frame = beats[index];
            if (!double.IsFinite(frame) || frame < 0) continue;
            if (onsets.Count == 0 || frame > previousFrame + .16) onsets.Add(frame);
            previousFrame = frame;
        }
        if (onsets.Count < 3) return new List<double>();

        // Visualization analysis marks a short run of frames for one impact.
        // Fold sparse impact distances into a musically plausible 75–200 BPM
        // range, then use the median so fills and missing beats do not dominate.
        var periods = new List<double>();
        for (var index = 1; index < onsets.Count; index++)
        {
            var period = onsets[index] - onsets[index - 1];
            if (period < .16 || period > 8) continue;
            while (period > .8) period /= 2;
            while (period < .3) period *= 2;
            periods.Add(period);
        }
        if (periods.Count < 2) return new List<double>();
        periods.Sort();
        var beatPeriod = periods[periods.Count / 2];

        // Pick the phase that explains the largest number of detected impacts.
        var anchor = onsets[0];
        var bestError = double.MaxValue;
        for (var candidateIndex = 0; candidateIndex < Math.Min(onsets.Count, 48); candidateIndex++)
        {
            var candidate = onsets[candidateIndex];
            var error = 0d;
            for (var onsetIndex = 0; onsetIndex < onsets.Count; onsetIndex++)
            {
                var cycles = Math.Round((onsets[onsetIndex] - candidate) / beatPeriod);
                error += Math.Abs(onsets[onsetIndex] - (candidate + cycles * beatPeriod));
            }
            if (error < bestError) { bestError = error; anchor = candidate; }
        }

        var subdivision = beatPeriod / 4d;
        while (anchor - subdivision >= 0) anchor -= subdivision;
        var grid = new List<double>((int)Math.Ceiling((onsets[onsets.Count - 1] + beatPeriod) / subdivision));
        for (var time = anchor; time <= onsets[onsets.Count - 1] + beatPeriod; time += subdivision)
            grid.Add(time);
        return grid;
    }

    private static double SnapBoundary(double value, IReadOnlyList<double> grid, double tolerance)
    {
        var low = 0;
        var high = grid.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (grid[middle] < value) low = middle + 1;
            else high = middle - 1;
        }
        var best = value;
        var distance = tolerance + .001;
        for (var index = Math.Max(0, low - 1); index <= Math.Min(grid.Count - 1, low); index++)
        {
            var candidateDistance = Math.Abs(grid[index] - value);
            if (candidateDistance < distance) { distance = candidateDistance; best = grid[index]; }
        }
        return distance <= tolerance ? best : value;
    }

    public StagePresentationFrame Evaluate(double positionSeconds)
    {
        if (_sections.Count == 0)
            return new StagePresentationFrame(-1, Array.Empty<StagePresentationVisualLine>(), 1, false, 0, false);

        var sectionIndex = VisibleSection(positionSeconds);
        var section = _sections[sectionIndex];
        // Pagination, entry cues and transitions remain on the canonical audio
        // clock. Only the progressive glyph fill gets the perceptual lead.
        // The legacy perceptual mode advances every glyph by one constant.
        // Karaoke mode instead applies a bounded, local lead per word below:
        // phrase entries and dense passages need more preparation than slow,
        // already established phrases. The canonical audio clock is untouched.
        var highlightPositionSeconds = _karaokeTimingEnabled
            ? positionSeconds
            : positionSeconds + _highlightLeadSeconds;
        var rows = new List<StagePresentationVisualLine>();
        for (var index = section.FirstLine; index <= section.LastLine; index++)
        {
            var wrapped = WrapForStage(_lines[index]);
            for (var rowIndex = 0; rowIndex < wrapped.Count && rows.Count < MaxVisibleLines; rowIndex++)
            {
                var row = wrapped[rowIndex];
                rows.Add(new StagePresentationVisualLine(
                    row.Text,
                    LineProgress(row, highlightPositionSeconds, _highlightLeadSeconds, _karaokeTimingEnabled),
                    SingingPace(row, highlightPositionSeconds),
                    row.Source.VoiceLane,
                    row.Source.VoiceLabel,
                    row.Source.StageEffect));
            }
        }

        var alpha = 1d;
        if (sectionIndex > 0)
        {
            var activation = ActivationTime(sectionIndex);
            var fadeInEnd = Math.Min(activation + TransitionSeconds, section.VocalStart);
            alpha = fadeInEnd <= activation
                ? 1
                : Clamp((positionSeconds - activation) / (fadeInEnd - activation));
        }
        if (sectionIndex + 1 < _sections.Count)
        {
            var transition = TransitionTime(sectionIndex);
            var fadeStart = Math.Max(section.VocalEnd, transition - TransitionSeconds);
            if (positionSeconds >= fadeStart && transition > fadeStart)
                alpha = Math.Min(alpha, Clamp((transition - positionSeconds) / (transition - fadeStart)));
        }

        var cueRemaining = section.VocalStart - positionSeconds;
        var cueAllowed = section.HasPauseBefore || sectionIndex == 0;
        var showCue = cueAllowed && cueRemaining > 0 && cueRemaining <= EntryCueSeconds;
        var showCountdown = cueAllowed &&
            (section.PauseBeforeSeconds >= CountdownPauseSeconds || sectionIndex == 0);
        return new StagePresentationFrame(sectionIndex, rows, alpha, showCue, cueRemaining, showCountdown);
    }

    private static List<StagePresentationLine> Normalize(IReadOnlyList<StagePresentationLine> source)
    {
        var result = new List<StagePresentationLine>();
        for (var lineIndex = 0; lineIndex < source.Count; lineIndex++)
        {
            var line = source[lineIndex];
            if (line == null || string.IsNullOrWhiteSpace(line.Text)) continue;
            var lineStart = line.Start;
            var lineEnd = line.End;
            var words = new List<StagePresentationWord>();
            var validWords = true;
            var previousWordEnd = lineStart;
            for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
            {
                var word = line.Words[wordIndex];
                if (word == null || string.IsNullOrWhiteSpace(word.Text) || word.End <= word.Start ||
                    (words.Count > 0 && word.Start < previousWordEnd - .002))
                {
                    validWords = false;
                    break;
                }

                var syllables = new List<StagePresentationSyllable>();
                var validSyllables = true;
                var previousSyllableEnd = word.Start;
                for (var syllableIndex = 0; syllableIndex < word.Syllables.Count; syllableIndex++)
                {
                    var syllable = word.Syllables[syllableIndex];
                    if (syllable == null)
                    {
                        validSyllables = false;
                        break;
                    }
                    var start = Math.Max(word.Start, syllable.Start);
                    var end = Math.Min(word.End, syllable.End);
                    if (string.IsNullOrWhiteSpace(syllable.Text) || end <= start ||
                        start < previousSyllableEnd - .002)
                    {
                        validSyllables = false;
                        break;
                    }
                    syllables.Add(new StagePresentationSyllable(start, end, syllable.Text, syllable.Confidence,
                        syllable.KaraokeTimingLocked, syllable.Notes));
                    previousSyllableEnd = end;
                }
                if (!validSyllables) syllables.Clear();
                words.Add(new StagePresentationWord(word.Start, word.End, word.Text, syllables,
                    word.SyllableConfidence, word.KaraokeTimingLocked, word.Notes));
                previousWordEnd = word.End;
            }
            if (!validWords) words.Clear();
            if (lineEnd <= lineStart && words.Count > 0)
            {
                lineStart = words[0].Start;
                lineEnd = words[words.Count - 1].End;
            }
            if (lineEnd <= lineStart) continue;
            result.Add(new StagePresentationLine(lineStart, lineEnd, line.Text, words,
                line.HoldAfterSeconds, line.StageEffect, Math.Max(0, line.VoiceLane), line.VoiceLabel,
                line.KaraokeTimingLocked));
        }
        result.Sort((left, right) => left.Start.CompareTo(right.Start));
        return result;
    }

    private void BuildHighlightTimelines()
    {
        for (var lineIndex = 0; lineIndex < _lines.Count; lineIndex++)
        {
            var line = _lines[lineIndex];
            for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
            {
                var timeline = KaraokeHighlightTimeline.Create(line.Words[wordIndex], _highlightOptions);
                if (timeline is not null) _highlightTimelines[line.Words[wordIndex]] = timeline;
                if (timeline is not null) _highlightTimelines[line.Words[wordIndex]] = timeline;
            }
        }
}

    private void BuildSections()
    {
        if (_lines.Count == 0) return;
        var first = 0;
        var activeVocalEnd = VocalEnd(_lines[0]);
        var pauseBefore = VocalStart(_lines[0]);
        for (var index = 1; index < _lines.Count; index++)
        {
            var vocalStart = VocalStart(_lines[index]);
            var overlapsActiveVoice = vocalStart < activeVocalEnd;
            if (overlapsActiveVoice || vocalStart - activeVocalEnd < SingingPauseSeconds)
            {
                activeVocalEnd = Math.Max(activeVocalEnd, VocalEnd(_lines[index]));
                continue;
            }
            AddSectionPages(first, index - 1, first > 0 || pauseBefore >= SingingPauseSeconds, pauseBefore);
            pauseBefore = Math.Max(0, vocalStart - activeVocalEnd);
            first = index;
            activeVocalEnd = VocalEnd(_lines[index]);
        }
        AddSectionPages(first, _lines.Count - 1, first > 0 || pauseBefore >= SingingPauseSeconds, pauseBefore);
    }

    private void AddSectionPages(int first, int last, bool hasPauseBefore, double pauseBefore)
    {
        var pages = new List<PageRange>();
        var pageFirst = first;
        var characters = 0;
        var visualRows = 0;
        for (var index = first; index <= last; index++)
        {
            var nextCharacters = characters + _lines[index].Text.Length;
            var nextVisualRows = WrapForStage(_lines[index]).Count;
            var overlapsVisiblePage = index > pageFirst &&
                VocalStart(_lines[index]) < SectionVocalEnd(pageFirst, index - 1);
            var exceedsCapacity = visualRows + nextVisualRows > MaxVisibleLines;
            var exceedsNormalPage = !overlapsVisiblePage &&
                (index - pageFirst >= MaxSectionLines || nextCharacters > MaxSectionCharacters);
            if (index > pageFirst && (exceedsCapacity || exceedsNormalPage))
            {
                pages.Add(new PageRange(pageFirst, index - 1));
                pageFirst = index;
                characters = 0;
                visualRows = 0;
            }
            characters += _lines[index].Text.Length;
            visualRows += nextVisualRows;
        }
        pages.Add(new PageRange(pageFirst, last));

        // A greedy three-line page can leave its final line active while the
        // following page already wants to appear for pre-roll. Move that final
        // line to the next page when a still readable four-line page can carry
        // it. This keeps the singer's current line and the upcoming context on
        // the same page instead of flashing the current line away.
        for (var index = 0; index + 1 < pages.Count; index++)
        {
            var current = pages[index];
            var next = pages[index + 1];
            if (current.Last <= current.First ||
                DisplayStart(_lines[next.First]) >= VocalEnd(_lines[current.Last]) ||
                !CanCarryTrailingLine(current.Last, next.Last)) continue;
            current.Last--;
            next.First--;
        }

        for (var index = 0; index < pages.Count; index++)
            _sections.Add(CreateSection(pages[index].First, pages[index].Last,
                index == 0 && hasPauseBefore, index == 0 ? pauseBefore : 0));
    }

    private bool CanCarryTrailingLine(int first, int last)
    {
        if (last - first + 1 > 4) return false;
        var characters = 0;
        var rows = 0;
        for (var index = first; index <= last; index++)
        {
            characters += _lines[index].Text.Length;
            rows += WrapForStage(_lines[index]).Count;
        }
        return characters <= MaxSectionCharacters && rows <= MaxVisibleLines;
    }

    private Section CreateSection(int first, int last, bool hasPauseBefore, double pauseBefore)
    {
        var presentationStart = DisplayStart(_lines[first]);
        var vocalStart = VocalStart(_lines[first]);
        var vocalEnd = VocalEnd(_lines[first]);
        var presentationEnd = DisplayEnd(_lines[first]);
        for (var index = first + 1; index <= last; index++)
        {
            presentationStart = Math.Min(presentationStart, DisplayStart(_lines[index]));
            vocalStart = Math.Min(vocalStart, VocalStart(_lines[index]));
            vocalEnd = Math.Max(vocalEnd, VocalEnd(_lines[index]));
            presentationEnd = Math.Max(presentationEnd, DisplayEnd(_lines[index]));
        }
        return new Section(first, last, presentationStart, vocalStart, vocalEnd, presentationEnd,
            hasPauseBefore, pauseBefore);
    }

    private double SectionVocalEnd(int first, int last)
    {
        var result = VocalEnd(_lines[first]);
        for (var index = first + 1; index <= last; index++) result = Math.Max(result, VocalEnd(_lines[index]));
        return result;
    }

    private int VisibleSection(double position)
    {
        for (var index = 0; index < _sections.Count - 1; index++)
            if (position < ActivationTime(index + 1)) return index;
        return _sections.Count - 1;
    }

    private double ActivationTime(int index) => index <= 0
        ? _sections[0].Start
        : Math.Max(_sections[index].Start, _sections[index - 1].VocalEnd);

    private double TransitionTime(int index)
    {
        var section = _sections[index];
        if (index + 1 >= _sections.Count) return section.PresentationEnd;
        return Math.Min(section.PresentationEnd, ActivationTime(index + 1));
    }

    private static double VocalStart(StagePresentationLine line) =>
        line.Words.Count > 0 ? line.Words[0].Start : line.Start;

    private static double VocalEnd(StagePresentationLine line) =>
        line.Words.Count > 0 ? line.Words[line.Words.Count - 1].End : line.End;

    private static double DisplayStart(StagePresentationLine line) => line.Words.Count == 0
        ? line.Start
        : Math.Max(0, Math.Min(line.Start, VocalStart(line) - EntryCueSeconds));

    private static double DisplayEnd(StagePresentationLine line) => line.Words.Count == 0
        ? line.End
        : Math.Max(line.End, VocalEnd(line) + PerceptualPostRollSeconds);

    private static List<WrappedLine> WrapForStage(StagePresentationLine line)
    {
        var result = new List<WrappedLine>();
        if (line.Words.Count == 0)
        {
            result.Add(new WrappedLine(line, line.Text, line.Start, line.End,
                Array.Empty<StagePresentationWord>()));
            return result;
        }
        var first = 0;
        while (first < line.Words.Count)
        {
            var remainingUnits = WordUnits(line.Words, first, line.Words.Count);
            if (remainingUnits <= MaxVisualRowUnits)
            {
                result.Add(CreateWrappedLine(line, first, line.Words.Count));
                break;
            }
            var rowsRemaining = Math.Max(2, (int)Math.Ceiling(remainingUnits / (double)MaxVisualRowUnits));
            var targetUnits = remainingUnits / (double)rowsRemaining;
            var bestBreak = first + 1;
            var bestScore = double.NegativeInfinity;
            for (var candidate = first + 1; candidate < line.Words.Count; candidate++)
            {
                var currentUnits = WordUnits(line.Words, first, candidate);
                if (currentUnits > MaxVisualRowUnits) break;
                var trailingUnits = WordUnits(line.Words, candidate, line.Words.Count);
                if (currentUnits < MinVisualRowUnits || trailingUnits < MinVisualRowUnits) continue;
                var previous = line.Words[candidate - 1];
                var next = line.Words[candidate];
                var score = -Math.Abs(currentUnits - targetUnits);
                if (EndsSentence(previous.Text)) score += 28;
                if (StartsSentence(next.Text) && candidate - first >= 3) score += 18;
                score += Math.Min(.45, Math.Max(0, next.Start - previous.End)) * 32;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestBreak = candidate;
                }
            }
            result.Add(CreateWrappedLine(line, first, bestBreak));
            first = bestBreak;
        }
        return result;
    }

    private static WrappedLine CreateWrappedLine(StagePresentationLine source, int first, int endExclusive)
    {
        var words = new List<StagePresentationWord>(endExclusive - first);
        var text = "";
        for (var index = first; index < endExclusive; index++)
        {
            words.Add(source.Words[index]);
            if (text.Length > 0) text += " ";
            text += source.Words[index].Text;
        }
        return new WrappedLine(source, text, words[0].Start, words[words.Count - 1].End, words);
    }

    private static int WordUnits(IReadOnlyList<StagePresentationWord> words, int first, int endExclusive)
    {
        var result = Math.Max(0, endExclusive - first - 1);
        for (var index = first; index < endExclusive; index++) result += Math.Max(1, words[index].Text.Length);
        return result;
    }

    private static bool EndsSentence(string text) => text.EndsWith(".", StringComparison.Ordinal) ||
        text.EndsWith("!", StringComparison.Ordinal) || text.EndsWith("?", StringComparison.Ordinal) ||
        text.EndsWith(";", StringComparison.Ordinal) || text.EndsWith(":", StringComparison.Ordinal);

    private static bool StartsSentence(string text)
    {
        for (var index = 0; index < text.Length; index++)
            if (char.IsLetter(text[index])) return char.IsUpper(text[index]);
        return false;
    }

    private double LineProgress(WrappedLine line, double position, double baseLead,
        bool karaokeTimingEnabled)
    {
        if (line.Words.Count == 0) return Clamp((position - line.Start) / Math.Max(.05, line.End - line.Start));
        var total = Math.Max(0, line.Words.Count - 1);
        for (var index = 0; index < line.Words.Count; index++) total += Math.Max(1, line.Words[index].Text.Length);
        total = Math.Max(1, total);
        var completed = 0d;
        for (var index = 0; index < line.Words.Count; index++)
        {
            var word = line.Words[index];
            var lead = karaokeTimingEnabled ? KaraokeLead(line, index, baseLead) : 0;
            var timeline = HighlightTimeline(word);
            var progress = timeline is not null
                ? timeline.Evaluate(position + lead)
                : WordProgress(word, position + lead);
            completed += Math.Max(1, word.Text.Length) * progress;
            if (progress < 1) break;
            if (index + 1 < line.Words.Count) completed += 1;
        }
        return Clamp(completed / total);
    }

    /// <summary>
    /// Visual-only preparation time inspired by manually authored karaoke
    /// timing. It is deliberately derived from local phrase geometry rather
    /// than written back into lyrics: exact acoustic boundaries remain the
    /// source of truth for editing, seeking and future realignment.
    /// </summary>
    private static double KaraokeLead(WrappedLine line, int index, double configuredLead)
    {
        var word = line.Words[index];
        var lead = Math.Max(.08, configuredLead);
        var phraseEntry = word.Start <= VocalStart(line.Source) + .002;
        if (phraseEntry) lead += .07;

        // A high local word rate leaves less time to read ahead. Manual
        // karaoke maps therefore tend to announce these note onsets earlier.
        var previousStart = index > 0 ? line.Words[index - 1].Start : word.Start;
        var nextStart = index + 1 < line.Words.Count ? line.Words[index + 1].Start : word.End;
        var localInterval = index + 1 < line.Words.Count
            ? nextStart - word.Start
            : index > 0 ? word.Start - previousStart : word.End - word.Start;
        if (localInterval < .28) lead += .05;
        else if (localInterval < .42) lead += .025;

        // Long notes are easy to follow once they are active, but their onset
        // is perceptually important. Give the singer a little preparation
        // without moving the acoustically measured release.
        if (word.End - word.Start > .9) lead += .015;
        return Math.Max(.08, Math.Min(.20, lead));
    }

    private static double WordProgress(StagePresentationWord word, double position)
    {
        if (word.SyllableConfidence < .62 || word.Syllables.Count == 0)
            return Clamp((position - word.Start) / Math.Max(.02, word.End - word.Start));
        var total = 0;
        for (var index = 0; index < word.Syllables.Count; index++)
            total += Math.Max(1, word.Syllables[index].Text.Length);
        var completed = 0d;
        for (var index = 0; index < word.Syllables.Count; index++)
        {
            var syllable = word.Syllables[index];
            var units = Math.Max(1, syllable.Text.Length);
            var duration = Math.Max(.02, syllable.End - syllable.Start);
            var localProgress = Clamp((position - syllable.Start) / duration);
            // A held syllable must visibly fill for its complete acoustic
            // duration so the singer is encouraged to sustain the note.
            // Very short syllables use a duration-dependent ease-out: they
            // feel like quick game steps without becoming a hard one-frame
            // jump. From 300 ms onward the progression is strictly linear.
            if (duration < .30)
            {
                var shortness = Clamp((.30 - duration) / .22);
                var exponent = 1 + 1.2 * shortness;
                localProgress = 1 - Math.Pow(1 - localProgress, exponent);
            }
            completed += units * localProgress;
            if (position < syllable.End) break;
        }
        return completed / Math.Max(1, total);
    }

    private KaraokeHighlightTimeline? HighlightTimeline(StagePresentationWord word) =>
        _highlightTimelines.TryGetValue(word, out var timeline) && timeline.HasMusicalBoundary
            ? timeline : null;

    private static double LinearWordProgress(StagePresentationWord word, double position) =>
        Clamp((position - word.Start) / Math.Max(.02, word.End - word.Start));

    public static string DescribeHighlightTimelines(IReadOnlyList<StagePresentationLine> lines,
        KaraokeHighlightTimelineOptions? options = null)
    {
        var settings = options ?? KaraokeHighlightTimelineOptions.Default;
        var result = new List<string> { $"HighlightTimelineVersion={KaraokeHighlightTimeline.Version}" };
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            for (var wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
            {
                var word = line.Words[wordIndex];
                var timeline = KaraokeHighlightTimeline.Create(word, settings);
                if (timeline is null) continue;
                var notes = string.Join(", ", word.Notes
                    .Select(note => $"{note.Start:0.000}-{note.End:0.000}/{note.Midi}"));
                result.Add($"LINE {lineIndex + 1} WORD {wordIndex + 1} '{word.Text}' NOTES [{notes}] :: " +
                           string.Join(" ", timeline.Segments.Select(segment =>
                               $"{segment.Type}:{segment.StartTime:0.000}-{segment.EndTime:0.000}/" +
                               $"{segment.StartTextProgress:0.###}->{segment.EndTextProgress:0.###}")));
            }
        }
        return string.Join("\n", result);
    }

    private static double SingingPace(WrappedLine line, double position)
    {
        if (line.Words.Count == 0) return .35;
        var word = line.Words[line.Words.Count - 1];
        for (var index = 0; index < line.Words.Count; index++)
        {
            word = line.Words[index];
            if (position <= word.End) break;
        }
        var duration = Math.Max(.04, word.End - word.Start);
        if (word.SyllableConfidence >= .62 && word.Syllables.Count > 1)
            for (var index = 0; index < word.Syllables.Count; index++)
                if (position <= word.Syllables[index].End)
                {
                    duration = Math.Max(.04, word.Syllables[index].End - word.Syllables[index].Start);
                    break;
                }
        return Clamp((1.2 - duration) / 1.08);
    }

    private static double Clamp(double value) => Math.Max(0, Math.Min(1, value));

    private sealed class WrappedLine
    {
        public WrappedLine(StagePresentationLine source, string text, double start, double end,
            IReadOnlyList<StagePresentationWord> words)
        {
            Source = source; Text = text; Start = start; End = end; Words = words;
        }
        public StagePresentationLine Source { get; }
        public string Text { get; }
        public double Start { get; }
        public double End { get; }
        public IReadOnlyList<StagePresentationWord> Words { get; }
    }

    private sealed class Section
    {
        public Section(int firstLine, int lastLine, double start, double vocalStart, double vocalEnd,
            double presentationEnd, bool hasPauseBefore, double pauseBeforeSeconds)
        {
            FirstLine = firstLine; LastLine = lastLine; Start = start; VocalStart = vocalStart;
            VocalEnd = vocalEnd; PresentationEnd = presentationEnd;
            HasPauseBefore = hasPauseBefore; PauseBeforeSeconds = pauseBeforeSeconds;
        }
        public int FirstLine { get; }
        public int LastLine { get; }
        public double Start { get; }
        public double VocalStart { get; }
        public double VocalEnd { get; }
        public double PresentationEnd { get; }
        public bool HasPauseBefore { get; }
        public double PauseBeforeSeconds { get; }
    }

    private sealed class PageRange
    {
        public PageRange(int first, int last) { First = first; Last = last; }
        public int First { get; set; }
        public int Last { get; set; }
    }
}

public sealed class StagePresentationFrame
{
    public StagePresentationFrame(int pageIndex, IReadOnlyList<StagePresentationVisualLine> lines, double alpha,
        bool showEntryCue, double entryCueRemainingSeconds, bool showEntryCountdown)
    {
        PageIndex = pageIndex; Lines = lines; Alpha = alpha; ShowEntryCue = showEntryCue;
        EntryCueRemainingSeconds = entryCueRemainingSeconds; ShowEntryCountdown = showEntryCountdown;
    }
    public int PageIndex { get; }
    public IReadOnlyList<StagePresentationVisualLine> Lines { get; }
    public double Alpha { get; }
    public bool ShowEntryCue { get; }
    public double EntryCueRemainingSeconds { get; }
    public double EntryCueProgress => ShowEntryCue ? 1 - EntryCueRemainingSeconds / 1.65 : 0;
    public bool ShowEntryCountdown { get; }
}

public sealed class StagePresentationVisualLine
{
    public StagePresentationVisualLine(string text, double progress, double pace, int voiceLane,
        string voiceLabel, string stageEffect)
    {
        Text = text; Progress = progress; Pace = pace; VoiceLane = voiceLane;
        VoiceLabel = voiceLabel; StageEffect = stageEffect;
    }
    public string Text { get; }
    public double Progress { get; }
    public double Pace { get; }
    public int VoiceLane { get; }
    public string VoiceLabel { get; }
    public string StageEffect { get; }
}

public sealed class StagePresentationLine
{
    public StagePresentationLine(double start, double end, string text, IReadOnlyList<StagePresentationWord> words,
        double? holdAfterSeconds, string stageEffect, int voiceLane, string voiceLabel,
        bool karaokeTimingLocked = false)
    {
        Start = start; End = end; Text = text ?? ""; Words = words ?? Array.Empty<StagePresentationWord>();
        HoldAfterSeconds = holdAfterSeconds; StageEffect = stageEffect ?? "Automatic";
        VoiceLane = voiceLane; VoiceLabel = voiceLabel ?? "";
        KaraokeTimingLocked = karaokeTimingLocked;
    }
    public double Start { get; }
    public double End { get; }
    public string Text { get; }
    public IReadOnlyList<StagePresentationWord> Words { get; }
    public double? HoldAfterSeconds { get; }
    public string StageEffect { get; }
    public int VoiceLane { get; }
    public string VoiceLabel { get; }
    public bool KaraokeTimingLocked { get; }
}

public sealed class StagePresentationWord
{
    public StagePresentationWord(double start, double end, string text,
        IReadOnlyList<StagePresentationSyllable> syllables, double syllableConfidence,
        bool karaokeTimingLocked = false, IReadOnlyList<StagePresentationNote>? notes = null)
    {
        Start = start; End = end; Text = text ?? ""; Syllables = syllables ?? Array.Empty<StagePresentationSyllable>();
        SyllableConfidence = syllableConfidence;
        KaraokeTimingLocked = karaokeTimingLocked;
        Notes = notes ?? (syllables ?? Array.Empty<StagePresentationSyllable>())
            .SelectMany(syllable => syllable.Notes).OrderBy(note => note.Start).ToArray();
    }
    public double Start { get; }
    public double End { get; }
    public string Text { get; }
    public IReadOnlyList<StagePresentationSyllable> Syllables { get; }
    public double SyllableConfidence { get; }
    public bool KaraokeTimingLocked { get; }
    public IReadOnlyList<StagePresentationNote> Notes { get; }
}

public sealed class StagePresentationSyllable
{
    public StagePresentationSyllable(double start, double end, string text, double confidence,
        bool karaokeTimingLocked = false, IReadOnlyList<StagePresentationNote>? notes = null)
    {
        Start = start; End = end; Text = text ?? ""; Confidence = confidence;
        KaraokeTimingLocked = karaokeTimingLocked;
        Notes = notes ?? Array.Empty<StagePresentationNote>();
    }
    public double Start { get; }
    public double End { get; }
    public string Text { get; }
    public double Confidence { get; }
    public bool KaraokeTimingLocked { get; }
    public IReadOnlyList<StagePresentationNote> Notes { get; }
}

public sealed class StagePresentationNote
{
    public StagePresentationNote(double start, double end, int midi, double confidence)
    {
        Start = start; End = end; Midi = midi; Confidence = confidence;
    }
    public double Start { get; }
    public double End { get; }
    public int Midi { get; }
    public double Confidence { get; }
}

public enum KaraokeHighlightSegmentType { Advance, Hold, Snap }

public sealed class KaraokeHighlightSegment
{
    public KaraokeHighlightSegment(KaraokeHighlightSegmentType type, double startTime, double endTime,
        double startTextProgress, double endTextProgress)
    {
        Type = type; StartTime = startTime; EndTime = endTime;
        StartTextProgress = startTextProgress; EndTextProgress = endTextProgress;
    }
    public KaraokeHighlightSegmentType Type { get; }
    public double StartTime { get; }
    public double EndTime { get; }
    public double StartTextProgress { get; }
    public double EndTextProgress { get; }
}

public sealed class KaraokeHighlightTimelineOptions
{
    public const int CurrentVersion = 1;

    public double MinimumPauseDurationSeconds { get; init; } = .055;
    public double LegatoGapToleranceSeconds { get; init; } = .035;
    public double MinimumAttackDistanceSeconds { get; init; } = .045;
    public double MinimumVoicedDurationSeconds { get; init; } = .05;
    public double MinimumNoteConfidence { get; init; } = .35;
    public double MinimumPitchChangeSemitones { get; init; } = 2;
    public double MaximumNoteOffsetSeconds { get; init; } = .08;
    public int TimelineVersion { get; init; } = CurrentVersion;

    public static KaraokeHighlightTimelineOptions Default { get; } = new();
}

public sealed class KaraokeHighlightTimeline
{
    public const int Version = 1;

    private readonly KaraokeHighlightSegment[] _segments;

    private KaraokeHighlightTimeline(KaraokeHighlightSegment[] segments) => _segments = segments;

    public IReadOnlyList<KaraokeHighlightSegment> Segments => _segments;

    public bool HasMusicalBoundary => _segments.Any(segment =>
        segment.Type is KaraokeHighlightSegmentType.Hold or KaraokeHighlightSegmentType.Snap);

    public static KaraokeHighlightTimeline? Create(StagePresentationWord word,
        KaraokeHighlightTimelineOptions options)
    {
        if (word is null || options is null || word.End <= word.Start ||
            options.TimelineVersion != KaraokeHighlightTimelineOptions.CurrentVersion) return null;

        var units = WordUnits(word);
        if (units.Length == 0 || word.Syllables.Count == 0) return null;

        var candidateBoundaries = new List<double> { word.Start };
        for (var index = 1; index < word.Syllables.Count; index++)
            candidateBoundaries.Add(word.Syllables[index].Start);
        candidateBoundaries.Add(word.End);

        foreach (var note in word.Notes)
        {
            if (note.End <= note.Start || note.Confidence < options.MinimumNoteConfidence) continue;
            if (note.Start < word.Start - options.MaximumNoteOffsetSeconds ||
                note.Start > word.End + options.MaximumNoteOffsetSeconds) continue;
            var offset = Math.Clamp(note.Start, word.Start, word.End);
            if (offset > word.Start && offset < word.End &&
                candidateBoundaries.All(candidate => Math.Abs(candidate - offset) >=
                    options.MinimumAttackDistanceSeconds))
                candidateBoundaries.Add(offset);
        }

        candidateBoundaries = candidateBoundaries
            .Where(time => time > word.Start && time < word.End)
            .Distinct()
            .OrderBy(time => time)
            .ToList();
        if (candidateBoundaries.Count == 0) return null;

        var boundaries = new List<double> { word.Start };
        var previous = word.Start;
        foreach (var candidate in candidateBoundaries)
        {
            var supportedBySyllable = word.Syllables.Any(syllable =>
                Math.Abs(syllable.Start - candidate) <= .003);
            var nearbyNotes = word.Notes.Where(note => note.Confidence >= options.MinimumNoteConfidence &&
                                                       Math.Abs(note.Start - candidate) <=
                                                       options.MaximumNoteOffsetSeconds).ToArray();
            var pitchChange = nearbyNotes.Any(note => word.Notes.Any(prior =>
                prior.End <= note.Start + .002 &&
                Math.Abs(note.Midi - prior.Midi) >= options.MinimumPitchChangeSemitones));
            var attack = nearbyNotes.Length > 0;
            var accepted = supportedBySyllable || attack || pitchChange;
            if (!accepted || candidate - previous < options.MinimumAttackDistanceSeconds) continue;
            var nextSyllable = word.Syllables.LastOrDefault(syllable => syllable.Start <= previous + .003);
            if (nextSyllable is not null && candidate > nextSyllable.End)
            {
                var maximum = nextSyllable.End - previous;
                if (maximum < options.MinimumVoicedDurationSeconds) continue;
            }
            boundaries.Add(candidate);
            previous = candidate;
        }
        if (boundaries.Count == 1) return null;
        boundaries.Add(word.End);

        var notes = word.Notes
            .Where(note => note.Confidence >= options.MinimumNoteConfidence &&
                           note.End > note.Start && note.End > word.Start && note.Start < word.End)
            .OrderBy(note => note.Start).ToArray();
        if (notes.Length == 0) return null;
        var segments = new List<KaraokeHighlightSegment>();
        for (var index = 0; index < boundaries.Count - 1; index++)
        {
            var start = boundaries[index];
            var end = boundaries[index + 1];
            var active = notes.FirstOrDefault(note =>
                Math.Max(start, note.Start) < Math.Min(end, note.End));
            var audibleEnd = active is null ? end : Math.Min(end, active.End);
            if (active is null) audibleEnd = end;
            if (audibleEnd <= start) audibleEnd = start;
            var duration = end - start;
            if (duration <= 0) continue;

            var startProgress = ProgressAt(units, start, word);
            var audibleProgress = ProgressAt(units, audibleEnd, word);
            var endProgress = ProgressAt(units, end, word);
            if (audibleEnd < end - options.MinimumPauseDurationSeconds)
            {
                if (audibleEnd - start >= options.MinimumVoicedDurationSeconds &&
                    end - audibleEnd >= options.MinimumPauseDurationSeconds)
                {
                    segments.Add(new(KaraokeHighlightSegmentType.Advance, start, audibleEnd,
                        startProgress, audibleProgress));
                    segments.Add(new(KaraokeHighlightSegmentType.Hold, audibleEnd, end,
                        audibleProgress, audibleProgress));
                    segments.Add(new(KaraokeHighlightSegmentType.Snap, end, end,
                        audibleProgress, endProgress));
                }
            }
            else if (startProgress < endProgress)
            {
                segments.Add(new(KaraokeHighlightSegmentType.Advance, start, end, startProgress, endProgress));
            }
        }
        if (segments.Count == 0) return null;
        return new(segments.ToArray());
    }

    public double Evaluate(double position)
    {
        for (var index = 0; index < _segments.Length; index++)
        {
            var segment = _segments[index];
            if (position < segment.StartTime || position < segment.EndTime)
            {
                if (segment.Type != KaraokeHighlightSegmentType.Advance) return segment.EndTextProgress;
                var duration = segment.EndTime - segment.StartTime;
                var factor = duration <= 0 ? 1 : Math.Max(0, Math.Min(1,
                    (position - segment.StartTime) / duration));
                return segment.StartTextProgress +
                       factor * (segment.EndTextProgress - segment.StartTextProgress);
            }
        }
        return _segments[^1].EndTextProgress;
    }

    private static double ProgressAt(int[] units, double time, StagePresentationWord word)
    {
        if (word.End <= word.Start) return 0;
        var factor = Math.Max(0, Math.Min(1, (time - word.Start) / (word.End - word.Start)));
        return units[units.Length - 1] * factor;
    }

    private static int[] WordUnits(StagePresentationWord word)
    {
        var result = new List<int>();
        var previousSyllableEnd = word.Start;
        foreach (var syllable in word.Syllables)
        {
            var start = Math.Max(word.Start, syllable.Start);
            var end = Math.Min(word.End, syllable.End);
            if (end <= start) continue;
            if (start > previousSyllableEnd + .003) result.Add(0);
            result.Add(Math.Max(1, syllable.Text.Length));
            previousSyllableEnd = end;
        }
        return result.ToArray();
    }
}
}
