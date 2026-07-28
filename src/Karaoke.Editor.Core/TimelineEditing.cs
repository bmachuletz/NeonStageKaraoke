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

    public static void ShiftDocument(LyricsEditorDocument document, TimeSpan delta,
        TimeSpan? audioDuration = null)
    {
        if (document.Lines.Count == 0)
            throw new InvalidOperationException("Das Lyrics-Dokument enthält keine Zeilen.");
        if (delta == TimeSpan.Zero) return;
        var segments = document.Segments.ToArray();
        var earliest = segments.Min(segment => segment.Start) + delta;
        var latest = segments.Max(segment => segment.End) + delta;
        if (earliest < TimeSpan.Zero)
            throw new InvalidOperationException("Der globale Versatz würde Lyrics vor den Songanfang verschieben.");
        if (audioDuration is { } duration && duration > TimeSpan.Zero && latest > duration)
            throw new InvalidOperationException("Der globale Versatz würde Lyrics hinter das Songende verschieben.");

        foreach (var segment in segments)
        {
            segment.Start += delta;
            segment.End += delta;
            segment.IsManuallyAdjusted = true;
            segment.Origin = SegmentOrigin.ManuallyAdjusted;
            // A deliberate uniform correction does not invalidate individual
            // review decisions. The original timing remains available in the
            // immutable OriginalStart/OriginalEnd provenance fields.
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

    /// <summary>
    /// Fits one or more selected lyric segments into an exact target range. The complete selected
    /// forest is transformed with one shared affine mapping, so relative gaps and durations are
    /// preserved. Segments outside the selection are not moved.
    /// </summary>
    public static int FitSelectionToRange(LyricsEditorDocument document,
        IEnumerable<LyricSegment> selection, TimeSpan targetStart, TimeSpan targetEnd)
    {
        if (targetStart < TimeSpan.Zero)
            throw new InvalidOperationException("Der markierte Bereich darf nicht vor dem Song beginnen.");
        if (targetEnd <= targetStart)
            throw new InvalidOperationException("Der markierte Bereich besitzt keine gültige Dauer.");

        var documentSegments = document.Segments.ToHashSet();
        var selected = selection.Distinct().ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("Bitte zuerst mindestens eine Zeile, ein Wort oder eine Silbe auswählen.");
        if (selected.Any(segment => !documentSegments.Contains(segment) ||
                                    segment.Type is not (LyricSegmentType.Line or LyricSegmentType.Word or LyricSegmentType.Syllable)))
            throw new InvalidOperationException("Die Auswahl enthält kein bearbeitbares Lyrics-Segment.");

        // If a parent and one of its descendants are selected, the parent owns the transformation.
        var roots = selected.Where(segment => !selected.Any(parent => !ReferenceEquals(parent, segment) &&
            parent.DescendantsAndSelf().Contains(segment))).ToList();
        var affected = roots.SelectMany(root => root.DescendantsAndSelf()).Distinct().ToList();
        // The explicitly selected objects define the envelope. Their outer boundaries therefore
        // land exactly on the waveform selection even if an imported child is slightly malformed.
        var sourceStart = roots.Min(segment => segment.Start);
        var sourceEnd = roots.Max(segment => segment.End);
        if (sourceEnd <= sourceStart)
            throw new InvalidOperationException("Die ausgewählten Segmente besitzen keine gültige Dauer.");

        var sourceTicks = (double)(sourceEnd - sourceStart).Ticks;
        var targetTicks = (double)(targetEnd - targetStart).Ticks;
        var transformed = affected.ToDictionary(segment => segment, segment => (
            Start: targetStart + TimeSpan.FromTicks((long)Math.Round((segment.Start - sourceStart).Ticks / sourceTicks * targetTicks)),
            End: targetStart + TimeSpan.FromTicks((long)Math.Round((segment.End - sourceStart).Ticks / sourceTicks * targetTicks))));

        foreach (var root in roots)
        {
            var duration = transformed[root].End - transformed[root].Start;
            var minimum = root.Type switch
            {
                LyricSegmentType.Line => TimeSpan.FromMilliseconds(100),
                LyricSegmentType.Word => TimeSpan.FromMilliseconds(35),
                _ => TimeSpan.FromMilliseconds(25)
            };
            if (duration < minimum)
                throw new InvalidOperationException("Der markierte Bereich ist für die ausgewählten Segmente zu kurz.");
        }
        if (transformed.Any(item => item.Value.End <= item.Value.Start))
            throw new InvalidOperationException("Der markierte Bereich würde ein Untersegment auf null verkürzen.");

        var beforeErrors = ValidateHierarchy(document).Concat(ValidateLineSequence(document)).ToHashSet();
        var snapshots = documentSegments.ToDictionary(segment => segment, segment => new TimingSnapshot(
            segment.Start, segment.End, segment.Origin, segment.IsManuallyAdjusted, segment.RequiresReview));
        try
        {
            foreach (var (segment, timing) in transformed)
            {
                segment.Start = timing.Start;
                segment.End = timing.End;
                MarkAdjusted(segment);
            }

            // A partial syllable selection may change the outer word boundary. Other syllables of
            // that word stay untouched and therefore remain the natural limit of the parent word.
            var affectedWords = roots.Where(segment => segment.Type == LyricSegmentType.Syllable)
                .Select(segment => document.Lines.SelectMany(line => line.Children)
                    .First(word => word.Id == segment.ParentId))
                .Where(word => !affected.Contains(word))
                .Distinct();
            foreach (var word in affectedWords) FitParentToChildren(word);

            var newErrors = ValidateHierarchy(document).Concat(ValidateLineSequence(document))
                .Where(error => !beforeErrors.Contains(error)).ToList();
            if (newErrors.Count > 0)
                throw new InvalidOperationException("Der Zielbereich kollidiert mit einem nicht ausgewählten Segment.");
        }
        catch
        {
            foreach (var (segment, snapshot) in snapshots) snapshot.Restore(segment);
            throw;
        }

        return roots.Count;
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

    private readonly record struct TimingSnapshot(TimeSpan Start, TimeSpan End, SegmentOrigin Origin,
        bool IsManuallyAdjusted, bool RequiresReview)
    {
        public void Restore(LyricSegment segment)
        {
            segment.Start = Start;
            segment.End = End;
            segment.Origin = Origin;
            segment.IsManuallyAdjusted = IsManuallyAdjusted;
            segment.RequiresReview = RequiresReview;
        }
    }
}
