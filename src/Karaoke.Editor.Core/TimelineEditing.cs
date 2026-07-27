namespace Karaoke.Editor.Core;

public static class TimelineEditing
{
    public static void SynchronizeWordText(LyricSegment word)
    {
        if (word.Type != LyricSegmentType.Word || word.Children.Count == 0) return;
        word.Text = string.Concat(word.Children
            .Where(child => child.Type == LyricSegmentType.Syllable)
            .Select(child => child.Text));
        MarkAdjusted(word);
    }

    public static void SynchronizeLineText(LyricSegment line)
    {
        if (line.Type != LyricSegmentType.Line || line.Children.Count == 0) return;
        line.Text = string.Join(" ", line.Children
            .Where(child => child.Type == LyricSegmentType.Word)
            .Select(child => child.Text));
        MarkAdjusted(line);
    }

    public static void MoveSharedBoundary(LyricSegment left, LyricSegment right,
        TimeSpan boundary, TimeSpan minimumDuration)
    {
        EnsureSiblings(left, right);
        var minimum = left.Start + minimumDuration;
        var maximum = right.End - minimumDuration;
        if (maximum < minimum) throw new InvalidOperationException("Für diese Segmente ist keine gültige gemeinsame Grenze möglich.");
        boundary = boundary < minimum ? minimum : boundary > maximum ? maximum : boundary;
        left.End = boundary;
        right.Start = boundary;
        MarkAdjusted(left);
        MarkAdjusted(right);
    }

    public static void MoveWithChildren(LyricSegment segment, TimeSpan delta)
    {
        foreach (var item in segment.DescendantsAndSelf())
        {
            item.Start += delta;
            item.End += delta;
            MarkAdjusted(item);
        }
    }

    public static void MoveLineWithinNeighbors(LyricSegment line, TimeSpan delta,
        TimeSpan previousEnd, TimeSpan? nextStart)
    {
        if (line.Type != LyricSegmentType.Line) throw new InvalidOperationException("Nur Zeilen können zwischen Nachbarzeilen verschoben werden.");
        var minimumDelta = previousEnd - line.Start;
        var maximumDelta = nextStart is null ? TimeSpan.MaxValue : nextStart.Value - line.End;
        if (maximumDelta < minimumDelta)
            throw new InvalidOperationException("Zwischen den Nachbarzeilen ist nicht genügend Platz.");
        if (delta < minimumDelta) delta = minimumDelta;
        if (delta > maximumDelta) delta = maximumDelta;
        MoveWithChildren(line, delta);
    }

    public static void ResizeLineWithinNeighbors(LyricSegment line, TimeSpan newStart, TimeSpan newEnd,
        TimeSpan previousEnd, TimeSpan? nextStart, TimeSpan minimumDuration)
    {
        if (newStart < previousEnd) newStart = previousEnd;
        if (nextStart is { } maximum && newEnd > maximum) newEnd = maximum;
        ResizeLineContainer(line, newStart, newEnd, minimumDuration);
    }

    public static void ScaleChildren(LyricSegment parent, TimeSpan newStart, TimeSpan newEnd)
    {
        if (newEnd <= newStart) throw new ArgumentOutOfRangeException(nameof(newEnd));
        var oldDuration = (parent.End - parent.Start).TotalMilliseconds;
        if (oldDuration <= 0) throw new InvalidOperationException("Ein Segment ohne Dauer kann nicht skaliert werden.");
        foreach (var child in parent.Children)
        {
            var startRatio = (child.Start - parent.Start).TotalMilliseconds / oldDuration;
            var endRatio = (child.End - parent.Start).TotalMilliseconds / oldDuration;
            child.Start = newStart + TimeSpan.FromMilliseconds((newEnd - newStart).TotalMilliseconds * startRatio);
            child.End = newStart + TimeSpan.FromMilliseconds((newEnd - newStart).TotalMilliseconds * endRatio);
            MarkAdjusted(child);
        }
        parent.Start = newStart;
        parent.End = newEnd;
        MarkAdjusted(parent);
    }

    public static void ResizeWithDescendants(LyricSegment segment, TimeSpan newStart, TimeSpan newEnd,
        TimeSpan minimumDuration)
    {
        if (newEnd - newStart < minimumDuration)
            throw new InvalidOperationException("Der Block würde zu kurz.");
        var oldStart = segment.Start;
        var oldDuration = (segment.End - oldStart).TotalMilliseconds;
        if (oldDuration <= 0) throw new InvalidOperationException("Ein Block ohne Dauer kann nicht skaliert werden.");
        var newDuration = (newEnd - newStart).TotalMilliseconds;
        foreach (var item in segment.DescendantsAndSelf())
        {
            var startRatio = (item.Start - oldStart).TotalMilliseconds / oldDuration;
            var endRatio = (item.End - oldStart).TotalMilliseconds / oldDuration;
            item.Start = newStart + TimeSpan.FromMilliseconds(newDuration * startRatio);
            item.End = newStart + TimeSpan.FromMilliseconds(newDuration * endRatio);
            MarkAdjusted(item);
        }
    }

    public static void ResizeLineContainer(LyricSegment line, TimeSpan newStart, TimeSpan newEnd,
        TimeSpan minimumDuration)
    {
        if (line.Type != LyricSegmentType.Line) throw new InvalidOperationException("Nur Zeilen besitzen freie Außenränder.");
        if (newEnd - newStart < minimumDuration) throw new InvalidOperationException("Die Zeile würde zu kurz.");
        if (line.Children.Count == 0)
        {
            line.Start = newStart; line.End = newEnd; MarkAdjusted(line); return;
        }
        var firstChildStart = line.Children.Min(child => child.Start);
        var lastChildEnd = line.Children.Max(child => child.End);
        if (newStart <= firstChildStart && newEnd >= lastChildEnd)
        {
            line.Start = newStart; line.End = newEnd; MarkAdjusted(line); return;
        }
        var contentStart = newStart > firstChildStart ? newStart : firstChildStart;
        var contentEnd = newEnd < lastChildEnd ? newEnd : lastChildEnd;
        if (contentEnd - contentStart < minimumDuration)
            throw new InvalidOperationException("Die Zeile würde ihren Wortinhalt vollständig verdrängen.");
        ScaleDescendants(line, firstChildStart, lastChildEnd, contentStart, contentEnd);
        line.Start = newStart;
        line.End = newEnd;
        MarkAdjusted(line);
    }

    private static void ScaleDescendants(LyricSegment parent, TimeSpan oldStart, TimeSpan oldEnd,
        TimeSpan newStart, TimeSpan newEnd)
    {
        var oldDuration = (oldEnd - oldStart).TotalMilliseconds;
        var newDuration = (newEnd - newStart).TotalMilliseconds;
        if (oldDuration <= 0) throw new InvalidOperationException("Der Wortbereich besitzt keine Dauer.");
        foreach (var item in parent.Children.SelectMany(child => child.DescendantsAndSelf()))
        {
            var startRatio = (item.Start - oldStart).TotalMilliseconds / oldDuration;
            var endRatio = (item.End - oldStart).TotalMilliseconds / oldDuration;
            item.Start = newStart + TimeSpan.FromMilliseconds(newDuration * startRatio);
            item.End = newStart + TimeSpan.FromMilliseconds(newDuration * endRatio);
            MarkAdjusted(item);
        }
    }

    public static void ResizeWord(LyricSegment word, TimeSpan newStart, TimeSpan newEnd,
        TimeSpan minimumDuration)
    {
        if (word.Type != LyricSegmentType.Word) throw new InvalidOperationException("Nur Wörter können so skaliert werden.");
        if (newEnd - newStart < minimumDuration) throw new InvalidOperationException("Das Wort würde zu kurz.");
        ScaleChildren(word, newStart, newEnd);
    }

    public static void ResizeSyllable(LyricSegment syllable, LyricSegment word,
        TimeSpan newStart, TimeSpan newEnd, TimeSpan minimumDuration)
    {
        if (syllable.Type != LyricSegmentType.Syllable || syllable.ParentId != word.Id)
            throw new InvalidOperationException("Silbe und Elternwort passen nicht zusammen.");
        if (newEnd - newStart < minimumDuration) throw new InvalidOperationException("Die Silbe würde zu kurz.");
        var index = word.Children.IndexOf(syllable);
        if (index > 0 && newStart < word.Children[index - 1].End) newStart = word.Children[index - 1].End;
        if (index + 1 < word.Children.Count && newEnd > word.Children[index + 1].Start) newEnd = word.Children[index + 1].Start;
        if (newEnd - newStart < minimumDuration) throw new InvalidOperationException("Kein Platz für diese Silbenänderung.");
        syllable.Start = newStart;
        syllable.End = newEnd;
        MarkAdjusted(syllable);
        FitParentToChildren(word);
    }

    public static void MoveSyllable(LyricSegment syllable, LyricSegment word, TimeSpan delta)
    {
        var index = word.Children.IndexOf(syllable);
        if (index < 0) throw new InvalidOperationException("Die Silbe gehört nicht zu diesem Wort.");
        var start = syllable.Start + delta;
        var end = syllable.End + delta;
        if (index > 0 && start < word.Children[index - 1].End)
        {
            var correction = word.Children[index - 1].End - start;
            start += correction; end += correction;
        }
        if (index + 1 < word.Children.Count && end > word.Children[index + 1].Start)
        {
            var correction = end - word.Children[index + 1].Start;
            start -= correction; end -= correction;
        }
        syllable.Start = start;
        syllable.End = end;
        MarkAdjusted(syllable);
        FitParentToChildren(word);
    }

    public static void FitParentToChildren(LyricSegment parent)
    {
        if (parent.Children.Count == 0) return;
        parent.Start = parent.Children.Min(child => child.Start);
        parent.End = parent.Children.Max(child => child.End);
        MarkAdjusted(parent);
    }

    public static IReadOnlyList<string> ValidateHierarchy(LyricsEditorDocument document)
    {
        // LRC (10 ms), Whisper/Aligner (1 ms) und JSON können dieselbe Grenze
        // minimal unterschiedlich runden. Solche Abweichungen sind kein
        // fachlicher Timing-Konflikt und dürfen das Speichern nicht verhindern.
        var tolerance = TimeSpan.FromMilliseconds(15);
        var errors = new List<string>();
        foreach (var parent in document.Segments.Where(segment => segment.Children.Count > 0))
        {
            LyricSegment? previous = null;
            foreach (var child in parent.Children.OrderBy(segment => segment.Start))
            {
                if (child.Start < parent.Start - tolerance || child.End > parent.End + tolerance ||
                    child.End < child.Start - tolerance)
                    errors.Add($"{child.Id}: Segment liegt außerhalb des Elternsegments.");
                if (previous is not null && child.Start < previous.End - tolerance)
                    errors.Add($"{child.Id}: Segment überschneidet seinen Vorgänger.");
                previous = child;
            }
        }
        return errors;
    }

    public static IReadOnlyList<string> ValidateLineSequence(LyricsEditorDocument document)
    {
        var tolerance = TimeSpan.FromMilliseconds(1);
        var errors = new List<string>();
        LyricSegment? previous = null;
        TimeSpan previousEffectiveEnd = TimeSpan.Zero;
        foreach (var line in document.Lines.OrderBy(EffectiveStart).ThenBy(EffectiveEnd))
        {
            var effectiveStart = EffectiveStart(line);
            var effectiveEnd = EffectiveEnd(line);
            if (effectiveEnd <= effectiveStart)
                errors.Add($"{line.Id}: Zeile besitzt keine positive Dauer.");
            if (previous is not null && effectiveStart < previousEffectiveEnd - tolerance)
                errors.Add($"{line.Id}: Zeile oder ihr Wortinhalt überschneidet die vorherige Zeile um {(previousEffectiveEnd - effectiveStart).TotalMilliseconds:0} ms.");
            previous = line;
            previousEffectiveEnd = effectiveEnd;
        }
        return errors;
    }

    public static TimeSpan EffectiveStart(LyricSegment segment) =>
        segment.DescendantsAndSelf().Min(item => item.Start);

    public static TimeSpan EffectiveEnd(LyricSegment segment) =>
        segment.DescendantsAndSelf().Max(item => item.End);

    private static void EnsureSiblings(LyricSegment left, LyricSegment right)
    {
        if (left.ParentId != right.ParentId || left.Type != right.Type)
            throw new InvalidOperationException("Eine gemeinsame Grenze ist nur zwischen gleichartigen Geschwistern möglich.");
        if (left.Start > right.Start)
            throw new InvalidOperationException("Die Segmentreihenfolge darf nicht vertauscht werden.");
    }

    public static void MarkAdjusted(LyricSegment segment)
    {
        segment.IsManuallyAdjusted = true;
        segment.Origin = SegmentOrigin.ManuallyAdjusted;
        segment.RequiresReview = true;
    }
}
