namespace Karaoke.Editor.Core;

public enum SelectionScaleAnchor
{
    Start,
    Center,
    End
}

public static class TimelineEditing
{
    public static IReadOnlyList<LyricSegment> MoveToOtherVoice(
        LyricsEditorDocument document, IEnumerable<LyricSegment> selection)
    {
        var selected = selection.Distinct().ToArray();
        if (selected.Length == 0 || selected.Any(item =>
                item.Type is not (LyricSegmentType.Line or LyricSegmentType.Word)))
            throw new InvalidOperationException(
                "Bitte mindestens eine Zeile oder ein Wort derselben Ebene auswählen.");
        if (selected.Select(item => item.Type).Distinct().Count() != 1)
            throw new InvalidOperationException(
                "Bitte Zeilen und Wörter nicht gemeinsam in eine andere Stimme verschieben.");

        if (selected[0].Type == LyricSegmentType.Line)
        {
            foreach (var line in selected)
            {
                if (!document.Lines.Contains(line))
                    throw new InvalidOperationException("Die ausgewählte Zeile gehört nicht zum aktuellen Song.");
                line.VoiceLane = OtherVoice(line.VoiceLane);
                line.VoiceLabel = line.VoiceLane == 0 ? "Lead Vocals" : "Backing Vocals";
                MarkAdjusted(line);
            }
            EnsureValidVoiceLanes(document);
            document.Lines.Sort(LineOrder);
            return selected;
        }

        var affected = new List<LyricSegment>();
        foreach (var sourceGroup in selected.GroupBy(word => FindLine(document, word)))
        {
            var source = sourceGroup.Key ?? throw new InvalidOperationException(
                "Ein ausgewähltes Wort besitzt keine Lyrics-Zeile.");
            var words = sourceGroup.OrderBy(word => word.Start).ToArray();
            if (words.Any(word => !source.Children.Contains(word)))
                throw new InvalidOperationException("Ein ausgewähltes Wort gehört nicht zum aktuellen Song.");
            var targetLane = OtherVoice(source.VoiceLane);
            var target = FindCompatibleTargetLine(document, source, words, targetLane)
                         ?? CreateVoiceLine(source, words, targetLane);
            if (!document.Lines.Contains(target)) document.Lines.Add(target);
            foreach (var word in words)
            {
                source.Children.Remove(word);
                word.ParentId = target.Id;
                target.Children.Add(word);
                MarkAdjusted(word);
            }
            NormalizeWordLine(target);
            affected.AddRange(words);
            if (source.Children.Count == 0)
                document.Lines.Remove(source);
            else
                NormalizeWordLine(source);
        }
        EnsureValidVoiceLanes(document);
        document.Lines.Sort(LineOrder);
        return affected;
    }

    private static int OtherVoice(int lane) => lane == 0 ? 1 : 0;

    private static LyricSegment? FindLine(LyricsEditorDocument document, LyricSegment child) =>
        document.Lines.FirstOrDefault(line => line.Id == child.ParentId ||
            line.DescendantsAndSelf().Any(item => item.Id == child.Id));

    private static LyricSegment? FindCompatibleTargetLine(LyricsEditorDocument document,
        LyricSegment source, IReadOnlyList<LyricSegment> words, int targetLane)
    {
        var start = words.Min(word => word.Start);
        var end = words.Max(word => word.End);
        return document.Lines
            .Where(line => line != source && line.VoiceLane == targetLane &&
                           line.End + TimeSpan.FromMilliseconds(350) >= start &&
                           line.Start - TimeSpan.FromMilliseconds(350) <= end)
            .Where(line => line.Children.Where(item => item.Type == LyricSegmentType.Word)
                .All(existing => words.All(moved =>
                    existing.End <= moved.Start || existing.Start >= moved.End)))
            .OrderBy(line => Math.Abs(((line.Start + line.End) / 2 - (start + end) / 2).Ticks))
            .FirstOrDefault();
    }

    private static LyricSegment CreateVoiceLine(LyricSegment source,
        IReadOnlyList<LyricSegment> words, int targetLane) => new()
    {
        Id = Guid.NewGuid(), Type = LyricSegmentType.Line,
        Start = words.Min(word => word.Start), End = words.Max(word => word.End),
        Text = string.Join(" ", words.Select(word => word.Text)),
        OriginalStart = words.Min(word => word.Start), OriginalEnd = words.Max(word => word.End),
        OriginalText = string.Join(" ", words.Select(word => word.Text)),
        Origin = SegmentOrigin.ManuallyAdjusted, IsManuallyAdjusted = true,
        RequiresReview = true, VoiceLane = targetLane,
        VoiceLabel = targetLane == 0 ? "Lead Vocals" : "Backing Vocals",
        AnalysisRunId = source.AnalysisRunId, ModelVersion = source.ModelVersion,
    };

    private static void NormalizeWordLine(LyricSegment line)
    {
        line.Children.Sort((left, right) => left.Start.CompareTo(right.Start));
        line.Start = line.Children.Min(word => word.Start);
        line.End = line.Children.Max(word => word.End);
        line.Text = string.Join(" ", line.Children.Select(word => word.Text));
        MarkAdjusted(line);
    }

    private static void EnsureValidVoiceLanes(LyricsEditorDocument document)
    {
        var conflict = ValidateLineSequence(document).FirstOrDefault();
        if (conflict is not null)
            throw new InvalidOperationException(
                "Die Zielstimme enthält in diesem Zeitraum bereits überlappenden Text. " +
                "Bitte einen größeren zusammenhängenden Bereich auswählen oder die Grenzen zuerst korrigieren.");
    }

    private static int LineOrder(LyricSegment left, LyricSegment right)
    {
        var time = left.Start.CompareTo(right.Start);
        return time != 0 ? time : left.VoiceLane.CompareTo(right.VoiceLane);
    }

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

    /// <summary>
    /// Moves the complete selected forest by one uniform offset. Descendants move with their
    /// selected roots, while objects outside the selection remain unchanged.
    /// </summary>
    public static int ShiftSelection(LyricsEditorDocument document,
        IEnumerable<LyricSegment> selection, TimeSpan delta, TimeSpan? audioDuration = null)
    {
        var (roots, sourceStart, sourceEnd) = SelectionEnvelope(document, selection);
        var targetStart = sourceStart + delta;
        var targetEnd = sourceEnd + delta;
        ValidateAudioBounds(targetStart, targetEnd, audioDuration);
        return FitSelectionToRange(document, roots, targetStart, targetEnd);
    }

    /// <summary>
    /// Scales the complete selected forest with one shared affine transform. Relative gaps,
    /// durations and descendant geometry are preserved. The requested anchor remains fixed.
    /// </summary>
    public static int ScaleSelection(LyricsEditorDocument document,
        IEnumerable<LyricSegment> selection, double factor, SelectionScaleAnchor anchor,
        TimeSpan? audioDuration = null)
    {
        if (!double.IsFinite(factor) || factor <= 0)
            throw new InvalidOperationException("Der Skalierungsfaktor muss größer als null sein.");
        var (roots, sourceStart, sourceEnd) = SelectionEnvelope(document, selection);
        var sourceDuration = sourceEnd - sourceStart;
        var targetDuration = TimeSpan.FromTicks((long)Math.Round(sourceDuration.Ticks * factor));
        if (targetDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("Die skalierte Auswahl wäre zu kurz.");

        var targetStart = anchor switch
        {
            SelectionScaleAnchor.Start => sourceStart,
            SelectionScaleAnchor.Center => sourceStart + TimeSpan.FromTicks((sourceDuration - targetDuration).Ticks / 2),
            SelectionScaleAnchor.End => sourceEnd - targetDuration,
            _ => sourceStart
        };
        var targetEnd = targetStart + targetDuration;
        ValidateAudioBounds(targetStart, targetEnd, audioDuration);
        return FitSelectionToRange(document, roots, targetStart, targetEnd);
    }

    private static (IReadOnlyList<LyricSegment> Roots, TimeSpan Start, TimeSpan End) SelectionEnvelope(
        LyricsEditorDocument document, IEnumerable<LyricSegment> selection)
    {
        var documentSegments = document.Segments.ToHashSet();
        var selected = selection.Distinct().ToList();
        if (selected.Count == 0)
            throw new InvalidOperationException("Bitte zuerst mindestens eine Zeile, ein Wort oder eine Silbe auswählen.");
        if (selected.Any(segment => !documentSegments.Contains(segment) ||
                                    segment.Type is not (LyricSegmentType.Line or LyricSegmentType.Word or LyricSegmentType.Syllable)))
            throw new InvalidOperationException("Die Auswahl enthält kein bearbeitbares Lyrics-Segment.");
        var roots = selected.Where(segment => !selected.Any(parent => !ReferenceEquals(parent, segment) &&
            parent.DescendantsAndSelf().Contains(segment))).ToArray();
        var start = roots.Min(segment => segment.Start);
        var end = roots.Max(segment => segment.End);
        if (end <= start)
            throw new InvalidOperationException("Die ausgewählten Segmente besitzen keine gültige Dauer.");
        return (roots, start, end);
    }

    private static void ValidateAudioBounds(TimeSpan start, TimeSpan end, TimeSpan? audioDuration)
    {
        if (start < TimeSpan.Zero)
            throw new InvalidOperationException("Die Transformation würde Lyrics vor den Songanfang verschieben.");
        if (audioDuration is { } duration && duration > TimeSpan.Zero && end > duration)
            throw new InvalidOperationException("Die Transformation würde Lyrics hinter das Songende verschieben.");
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

    /// <summary>
    /// Fits the selection into the marked range and then moves internal word and syllable
    /// boundaries towards nearby low-energy transitions in the vocal waveform. A waveform
    /// without useful dynamics deliberately keeps the exact affine result.
    /// </summary>
    public static WaveformSyncResult FitSelectionToWaveformRange(LyricsEditorDocument document,
        IEnumerable<LyricSegment> selection, TimeSpan targetStart, TimeSpan targetEnd,
        WaveformPyramid? waveform)
    {
        var selected = selection.Distinct().ToList();
        var roots = selected.Where(segment => !selected.Any(parent =>
            !ReferenceEquals(parent, segment) && parent.DescendantsAndSelf().Contains(segment))).ToList();
        var snapshots = document.Segments.ToDictionary(segment => segment, segment => new TimingSnapshot(
            segment.Start, segment.End, segment.Origin, segment.IsManuallyAdjusted, segment.RequiresReview));
        var beforeErrors = ValidateHierarchy(document).Concat(ValidateLineSequence(document)).ToHashSet();
        try
        {
            var count = FitSelectionToRange(document, roots, targetStart, targetEnd);
            var boundaries = WaveformTimingAlignment.Refine(roots, waveform);
            var newErrors = ValidateHierarchy(document).Concat(ValidateLineSequence(document))
                .Where(error => !beforeErrors.Contains(error)).ToList();
            if (newErrors.Count > 0)
                throw new InvalidOperationException("Die akustische Anpassung würde mit einem nicht ausgewählten Segment kollidieren.");
            return new(count, boundaries, boundaries > 0);
        }
        catch
        {
            foreach (var (segment, snapshot) in snapshots) snapshot.Restore(segment);
            throw;
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
        foreach (var lane in document.Lines.GroupBy(line => Math.Max(0, line.VoiceLane)))
        {
            LyricSegment? previous = null;
            TimeSpan previousEffectiveEnd = TimeSpan.Zero;
            foreach (var line in lane.OrderBy(EffectiveStart).ThenBy(EffectiveEnd))
            {
                var effectiveStart = EffectiveStart(line);
                var effectiveEnd = EffectiveEnd(line);
                if (effectiveEnd <= effectiveStart)
                    errors.Add($"{line.Id}: Zeile besitzt keine positive Dauer.");
                if (previous is not null && effectiveStart < previousEffectiveEnd - tolerance)
                    errors.Add($"{line.Id}: Zeile oder ihr Wortinhalt überschneidet die vorherige Zeile in Gesangsspur {lane.Key + 1} um {(previousEffectiveEnd - effectiveStart).TotalMilliseconds:0} ms.");
                previous = line;
                previousEffectiveEnd = effectiveEnd;
            }
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
