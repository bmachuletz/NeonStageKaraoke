using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Karaoke.Editor.Core;

namespace Karaoke.App.Desktop;

public sealed class EditorTimelineControl : Control
{
    public static readonly StyledProperty<LyricsEditorDocument?> DocumentProperty =
        AvaloniaProperty.Register<EditorTimelineControl, LyricsEditorDocument?>(nameof(Document));
    public static readonly StyledProperty<TimeSpan> PlayheadProperty =
        AvaloniaProperty.Register<EditorTimelineControl, TimeSpan>(nameof(Playhead));
    public static readonly StyledProperty<WaveformPyramid?> WaveformProperty =
        AvaloniaProperty.Register<EditorTimelineControl, WaveformPyramid?>(nameof(Waveform));
    public static readonly StyledProperty<IReadOnlyList<PitchNoteEvidence>?> PitchEvidenceProperty =
        AvaloniaProperty.Register<EditorTimelineControl, IReadOnlyList<PitchNoteEvidence>?>(nameof(PitchEvidence));
    public static readonly StyledProperty<long> RevisionProperty =
        AvaloniaProperty.Register<EditorTimelineControl, long>(nameof(Revision));
    public static readonly StyledProperty<bool> EditingEnabledProperty =
        AvaloniaProperty.Register<EditorTimelineControl, bool>(nameof(EditingEnabled), true);
    public static readonly StyledProperty<bool> ShowTechnicalTextProperty =
        AvaloniaProperty.Register<EditorTimelineControl, bool>(nameof(ShowTechnicalText));

    private readonly TimelineViewport _viewport = new(115);
    private (LyricSegment Left, LyricSegment Right, TimeSpan LeftEnd, TimeSpan RightStart,
        SegmentOrigin LeftOrigin, SegmentOrigin RightOrigin, bool LeftAdjusted, bool RightAdjusted,
        bool LeftReview, bool RightReview)? _drag;
    private TimeSpan _dragBoundary;
    private LyricSegment? _selectedSegment;
    private bool _showInitialVocalWindow;
    private TimeSpan? _loopStart;
    private TimeSpan? _loopEnd;
    private readonly TrackedWordLoop _trackedWordLoop = new();
    private LyricSegment? _contextSegment;
    private bool _selectingRange;
    private TimeSpan _rangeAnchor;
    private SegmentDragState? _segmentDrag;
    private EditSegmentTreeCommand? _segmentDragCommand;
    private MultiSegmentDragState? _multiDrag;
    private EditSegmentForestCommand? _multiDragCommand;
    private readonly HashSet<LyricSegment> _selectedSegments = [];

    static EditorTimelineControl()
    {
        ClipToBoundsProperty.OverrideDefaultValue<EditorTimelineControl>(true);
        AffectsRender<EditorTimelineControl>(DocumentProperty, PlayheadProperty, WaveformProperty,
            PitchEvidenceProperty, RevisionProperty, ShowTechnicalTextProperty);
        DocumentProperty.Changed.AddClassHandler<EditorTimelineControl>((control, change) =>
        {
            var previous = change.GetOldValue<LyricsEditorDocument?>();
            var current = change.GetNewValue<LyricsEditorDocument?>();
            if (previous?.SongId != current?.SongId)
            {
                control._viewport.Reset();
                control._showInitialVocalWindow = true;
                control.SelectedSegment = null;
                control._selectedSegments.Clear();
                control._contextSegment = null;
                control._trackedWordLoop.Clear();
            }
            else
            {
                control.SelectSegments(control._selectedSegments.ToArray());
                control.RefreshTrackedWordLoop();
            }
            control.InvalidateVisual();
        });
        RevisionProperty.Changed.AddClassHandler<EditorTimelineControl>((control, _) =>
            control.RefreshTrackedWordLoop());
        FocusableProperty.OverrideDefaultValue<EditorTimelineControl>(true);
    }

    public LyricsEditorDocument? Document { get => GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }
    public TimeSpan Playhead { get => GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
    public WaveformPyramid? Waveform { get => GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    public IReadOnlyList<PitchNoteEvidence>? PitchEvidence
    {
        get => GetValue(PitchEvidenceProperty);
        set => SetValue(PitchEvidenceProperty, value);
    }
    public long Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public bool EditingEnabled { get => GetValue(EditingEnabledProperty); set => SetValue(EditingEnabledProperty, value); }
    public bool ShowTechnicalText
    {
        get => GetValue(ShowTechnicalTextProperty);
        set => SetValue(ShowTechnicalTextProperty, value);
    }
    public CommandHistory? History { get; set; }
    public event EventHandler<TimeSpan>? PositionRequested;
    public event EventHandler<LyricSegment?>? SegmentSelected;
    public event EventHandler<(TimeSpan Start, TimeSpan End)>? RangeSelected;
    public event EventHandler<(TimeSpan Start, TimeSpan End)>? TrackedWordLoopRangeChanged;
    public event EventHandler? TrackedWordLoopCleared;
    public event EventHandler? SegmentEdited;

    public LyricSegment? SelectedSegment
    {
        get => _selectedSegment;
        set
        {
            _selectedSegment = value is null || Document is null
                ? value
                : Document.Segments.FirstOrDefault(candidate => candidate.Id == value.Id);
            InvalidateVisual();
        }
    }

    public IReadOnlyList<LyricSegment> GetSelectedSegments()
    {
        var selection = SelectedSegment is not null && _selectedSegments.Contains(SelectedSegment)
            ? _selectedSegments
            : SelectedSegment is null ? [] : [SelectedSegment];
        return LyricsSegmentClipboard.NormalizeSelection(selection);
    }

    public void SelectOnly(LyricSegment? segment)
    {
        if (segment is not null && Document is not null)
            segment = Document.Segments.FirstOrDefault(candidate => candidate.Id == segment.Id);
        _selectedSegments.Clear();
        if (segment is not null) _selectedSegments.Add(segment);
        SelectedSegment = segment;
    }

    public void SelectSegments(IEnumerable<LyricSegment> segments)
    {
        var source = Document is null
            ? segments
            : segments.Select(segment => Document.Segments.FirstOrDefault(candidate => candidate.Id == segment.Id))
                .OfType<LyricSegment>();
        var normalized = LyricsSegmentClipboard.NormalizeSelection(source);
        _selectedSegments.Clear();
        foreach (var segment in normalized) _selectedSegments.Add(segment);
        SelectedSegment = normalized.FirstOrDefault();
    }

    public (int Count, LyricSegmentType Type)? SelectAllAtCurrentLevel()
    {
        if (Document is null) return null;
        var type = SelectedSegment?.Type is LyricSegmentType.Line or LyricSegmentType.Word or LyricSegmentType.Syllable
            ? SelectedSegment.Type
            : LyricSegmentType.Line;
        var segments = type switch
        {
            LyricSegmentType.Line => Document.Lines.ToList(),
            LyricSegmentType.Word => Document.Lines.SelectMany(line => line.Children).ToList(),
            LyricSegmentType.Syllable => Document.Lines.SelectMany(line => line.Children)
                .SelectMany(word => word.Children).ToList(),
            _ => []
        };
        SelectSegments(segments);
        SegmentSelected?.Invoke(this, SelectedSegment);
        InvalidateVisual();
        return (segments.Count, type);
    }

    public int SelectContextRange(bool toRight)
    {
        if (Document is null || _contextSegment is not { Type: LyricSegmentType.Line or LyricSegmentType.Word } context)
            return 0;
        IReadOnlyList<LyricSegment> candidates;
        if (context.Type == LyricSegmentType.Line)
            candidates = Document.Lines.OrderBy(line => line.Start).ToList();
        else
        {
            var line = FindLine(context);
            if (line is null) return 0;
            candidates = line.Children.OrderBy(word => word.Start).ToList();
        }
        var index = candidates.ToList().FindIndex(segment => segment.Id == context.Id);
        if (index < 0) return 0;
        SelectSegments(toRight ? candidates.Skip(index) : candidates.Take(index + 1));
        SegmentSelected?.Invoke(this, SelectedSegment);
        InvalidateVisual();
        return _selectedSegments.Count;
    }

    public int ShiftSelection(TimeSpan delta, TimeSpan? audioDuration)
    {
        EnsureEditingEnabled();
        if (Document is null) throw new InvalidOperationException("Bitte zuerst einen Song laden.");
        if (History is null) throw new InvalidOperationException("Die Änderungshistorie ist noch nicht bereit.");
        var selected = GetSelectedSegments();
        if (selected.Count == 0)
            throw new InvalidOperationException("Bitte zuerst mindestens eine Zeile, ein Wort oder eine Silbe auswählen.");
        var roots = Document.Lines.Where(line => selected.Any(segment =>
            line.DescendantsAndSelf().Contains(segment))).Distinct().ToList();
        var changed = 0;
        History.Execute(new EditSegmentForestCommand(roots, "Lyrics-Auswahl verschieben", () =>
            changed = TimelineEditing.ShiftSelection(Document, selected, delta, audioDuration)));
        SegmentEdited?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return changed;
    }

    public int ScaleSelection(double factor, SelectionScaleAnchor anchor, TimeSpan? audioDuration)
    {
        EnsureEditingEnabled();
        if (Document is null) throw new InvalidOperationException("Bitte zuerst einen Song laden.");
        if (History is null) throw new InvalidOperationException("Die Änderungshistorie ist noch nicht bereit.");
        var selected = GetSelectedSegments();
        if (selected.Count == 0)
            throw new InvalidOperationException("Bitte zuerst mindestens eine Zeile, ein Wort oder eine Silbe auswählen.");
        var roots = Document.Lines.Where(line => selected.Any(segment =>
            line.DescendantsAndSelf().Contains(segment))).Distinct().ToList();
        var changed = 0;
        History.Execute(new EditSegmentForestCommand(roots, "Lyrics-Auswahl proportional skalieren", () =>
            changed = TimelineEditing.ScaleSelection(Document, selected, factor, anchor, audioDuration)));
        SegmentEdited?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return changed;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = Bounds;
        if (_showInitialVocalWindow && Document is { Lines.Count: > 0 } document && bounds.Width > 0)
        {
            var firstVocal = document.Lines.SelectMany(line => line.Children.Count > 0 ? line.Children : [line])
                .Select(segment => segment.Start).DefaultIfEmpty(TimeSpan.Zero).Min();
            _viewport.ShowWindow(firstVocal, TimeSpan.FromSeconds(30), bounds.Width);
            _showInitialVocalWindow = false;
        }
        context.FillRectangle(new SolidColorBrush(Color.Parse("#10131A")), bounds);
        DrawRuler(context, bounds.Width);
        DrawWaveform(context, bounds.Width);
        DrawPitchEvidence(context, bounds.Width);
        if (Document is null) { DrawEmpty(context, bounds); return; }
        DrawTracks(context, bounds);
        DrawLoopRange(context, bounds);
        var playX = _viewport.TimeToPixel(Playhead);
        if (playX is >= 0 and <= double.MaxValue)
            context.DrawLine(new Pen(Brushes.Orange, 1.5), new Point(playX, 0), new Point(playX, bounds.Height));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var point = e.GetPosition(this);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            _viewport.ZoomAt(e.Delta.Y > 0 ? 1.2 : 1 / 1.2, point.X);
        else
            _viewport.ScrollPixels(-e.Delta.Y * 90);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetPosition(this);
        var pointer = e.GetCurrentPoint(this).Properties;
        if (pointer.IsRightButtonPressed)
        {
            var contextHit = FindSegment(point);
            _contextSegment = contextHit;
            UpdateContextMenu(contextHit);
            if (contextHit is not null && !_selectedSegments.Contains(contextHit))
            {
                _selectedSegments.Clear();
                _selectedSegments.Add(contextHit);
                SelectedSegment = contextHit;
                SegmentSelected?.Invoke(this, contextHit);
            }
            // Das selbst gezeichnete Timeline-Control übernimmt die Pointer-
            // Verarbeitung vollständig. Deshalb löst Avalonia hier nicht auf
            // allen Plattformen automatisch ContextRequested aus.
            if (ContextMenu is { } contextMenu)
            {
                if (contextMenu.IsOpen) contextMenu.Close();
                contextMenu.Open(this);
            }
            e.Handled = true;
            return;
        }
        if (!pointer.IsLeftButtonPressed) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _trackedWordLoop.Clear();
            _rangeAnchor = _viewport.PixelToTime(point.X);
            _loopStart = _loopEnd = _rangeAnchor;
            _selectingRange = true;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        var boundary = FindBoundary(point);
        if (boundary is not null && EditingEnabled)
        {
            _drag = (boundary.Value.Left, boundary.Value.Right, boundary.Value.Left.End, boundary.Value.Right.Start,
                boundary.Value.Left.Origin, boundary.Value.Right.Origin,
                boundary.Value.Left.IsManuallyAdjusted, boundary.Value.Right.IsManuallyAdjusted,
                boundary.Value.Left.RequiresReview, boundary.Value.Right.RequiresReview);
            _dragBoundary = boundary.Value.Left.End;
            e.Pointer.Capture(this);
        }
        else
        {
            var hit = FindSegment(point);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
                hit is { Type: LyricSegmentType.Line or LyricSegmentType.Word or LyricSegmentType.Syllable })
            {
                if (!_selectedSegments.Add(hit)) _selectedSegments.Remove(hit);
                SelectedSegment = _selectedSegments.Contains(hit) ? hit : _selectedSegments.LastOrDefault();
                SegmentSelected?.Invoke(this, SelectedSegment);
                InvalidateVisual();
                e.Handled = true;
                return;
            }
            if (hit is not null && !_selectedSegments.Contains(hit))
            {
                _selectedSegments.Clear();
                _selectedSegments.Add(hit);
            }
            SelectedSegment = hit;
            SegmentSelected?.Invoke(this, SelectedSegment);
            if (!EditingEnabled)
            {
                PositionRequested?.Invoke(this, _viewport.PixelToTime(point.X));
                e.Handled = true;
                return;
            }
            if (SelectedSegment is { Type: LyricSegmentType.Line or LyricSegmentType.Word or LyricSegmentType.Syllable } segment &&
                FindLine(segment) is { } line)
            {
                var normalized = NormalizeSelection(_selectedSegments).ToList();
                if (normalized.Count > 1)
                {
                    _multiDrag = new(Document!, normalized, Document!.Lines.Where(candidate => normalized.Any(item =>
                            candidate.DescendantsAndSelf().Contains(item))).Distinct().ToList(),
                        _viewport.PixelToTime(point.X));
                    var multiState = _multiDrag;
                    _multiDragCommand = new EditSegmentForestCommand(multiState.Roots,
                        "Mehrere Segmente verschieben", () => ApplyMultiSegmentDrag(multiState));
                    e.Pointer.Capture(this);
                    e.Handled = true;
                    return;
                }
                var startX = _viewport.TimeToPixel(segment.Start);
                var endX = _viewport.TimeToPixel(segment.End);
                var mode = Math.Abs(point.X - startX) <= 7 ? SegmentDragMode.ResizeStart
                    : Math.Abs(point.X - endX) <= 7 ? SegmentDragMode.ResizeEnd : SegmentDragMode.Move;
                _segmentDrag = new(segment, line, FindParentWord(segment), mode,
                    _viewport.PixelToTime(point.X), segment.Start, segment.End);
                var state = _segmentDrag;
                _segmentDragCommand = new EditSegmentTreeCommand(line, mode == SegmentDragMode.Move
                    ? "Segment in der Timeline verschieben" : "Segment in der Timeline skalieren", () => ApplySegmentDrag(state));
                e.Pointer.Capture(this);
            }
            else PositionRequested?.Invoke(this, _viewport.PixelToTime(point.X));
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_selectingRange && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var current = _viewport.PixelToTime(e.GetPosition(this).X);
            _loopStart = current < _rangeAnchor ? current : _rangeAnchor;
            _loopEnd = current < _rangeAnchor ? _rangeAnchor : current;
            InvalidateVisual();
            return;
        }
        if (_segmentDrag is { } segmentDrag && _segmentDragCommand is { } segmentCommand &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            segmentDrag.Current = _viewport.PixelToTime(e.GetPosition(this).X);
            segmentCommand.Undo();
            try { segmentCommand.Execute(); }
            catch (InvalidOperationException) { segmentCommand.Undo(); }
            SegmentEdited?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }
        if (_multiDrag is { } multiDrag && _multiDragCommand is { } multiCommand &&
            e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            multiDrag.Current = _viewport.PixelToTime(e.GetPosition(this).X);
            multiCommand.Undo();
            multiCommand.Execute();
            SegmentEdited?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }
        if (_drag is not { } drag || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            UpdateHoverCursor(e.GetPosition(this));
            return;
        }
        _dragBoundary = _viewport.PixelToTime(e.GetPosition(this).X);
        TimelineEditing.MoveSharedBoundary(drag.Left, drag.Right, _dragBoundary, TimeSpan.FromMilliseconds(35));
        SegmentEdited?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        if (_segmentDrag is null && _multiDrag is null && _drag is null && !_selectingRange)
            Cursor = Cursor.Default;
        base.OnPointerExited(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_selectingRange)
        {
            _selectingRange = false;
            e.Pointer.Capture(null);
            if (_loopStart is { } start && _loopEnd is { } end && end - start >= TimeSpan.FromMilliseconds(100))
                RangeSelected?.Invoke(this, (start, end));
            InvalidateVisual();
            return;
        }
        if (_segmentDrag is not null && _segmentDragCommand is { } completedCommand)
        {
            History?.RecordExecuted(completedCommand);
            _segmentDrag = null;
            _segmentDragCommand = null;
            e.Pointer.Capture(null);
            SegmentEdited?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }
        if (_multiDrag is not null && _multiDragCommand is { } completedMultiCommand)
        {
            History?.RecordExecuted(completedMultiCommand);
            _multiDrag = null;
            _multiDragCommand = null;
            e.Pointer.Capture(null);
            SegmentEdited?.Invoke(this, EventArgs.Empty);
            InvalidateVisual();
            return;
        }
        if (_drag is not { } drag) return;
        var final = drag.Left.End;
        drag.Left.End = drag.LeftEnd;
        drag.Right.Start = drag.RightStart;
        drag.Left.Origin = drag.LeftOrigin;
        drag.Right.Origin = drag.RightOrigin;
        drag.Left.IsManuallyAdjusted = drag.LeftAdjusted;
        drag.Right.IsManuallyAdjusted = drag.RightAdjusted;
        drag.Left.RequiresReview = drag.LeftReview;
        drag.Right.RequiresReview = drag.RightReview;
        var command = new MoveSharedBoundaryCommand(drag.Left, drag.Right, final, TimeSpan.FromMilliseconds(35));
        command.Execute();
        History?.RecordExecuted(command);
        _drag = null;
        e.Pointer.Capture(null);
        SegmentEdited?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void SetLoopRange(TimeSpan? start, TimeSpan? end)
    {
        _loopStart = start;
        _loopEnd = end;
        InvalidateVisual();
    }

    public bool TryTrackContextWordLoop(out (TimeSpan Start, TimeSpan End) range)
    {
        range = default;
        var word = _contextSegment is { Type: LyricSegmentType.Word }
            ? _contextSegment
            : SelectedSegment is { Type: LyricSegmentType.Word } ? SelectedSegment : null;
        if (!_trackedWordLoop.Bind(word) ||
            !_trackedWordLoop.TryGetRange(Document, out range))
            return false;
        _loopStart = range.Start;
        _loopEnd = range.End;
        InvalidateVisual();
        return true;
    }

    public void ClearTrackedWordLoop() => _trackedWordLoop.Clear();

    private void RefreshTrackedWordLoop()
    {
        if (!_trackedWordLoop.IsBound) return;
        if (!_trackedWordLoop.TryGetRange(Document, out var range))
        {
            _loopStart = null;
            _loopEnd = null;
            InvalidateVisual();
            TrackedWordLoopCleared?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (_loopStart == range.Start && _loopEnd == range.End) return;
        _loopStart = range.Start;
        _loopEnd = range.End;
        InvalidateVisual();
        TrackedWordLoopRangeChanged?.Invoke(this, range);
    }

    private void UpdateContextMenu(LyricSegment? contextHit)
    {
        if (ContextMenu is null) return;
        foreach (var control in ContextMenu.Items.OfType<Control>())
        {
            if (Equals(control.Tag, "play-word-loop"))
                control.IsVisible = contextHit?.Type == LyricSegmentType.Word;
            if (Equals(control.Tag, "move-other-voice"))
                control.IsVisible = contextHit?.Type is LyricSegmentType.Line or LyricSegmentType.Word;
            if (Equals(control.Tag, "select-direction"))
                control.IsVisible = contextHit?.Type is LyricSegmentType.Line or LyricSegmentType.Word;
        }
    }

    public WaveformSyncResult SynchronizeSelectionToRange()
    {
        EnsureEditingEnabled();
        if (Document is null) throw new InvalidOperationException("Bitte zuerst einen Song laden.");
        if (_loopStart is not { } start || _loopEnd is not { } end || end - start < TimeSpan.FromMilliseconds(100))
            throw new InvalidOperationException("Bitte zuerst mit Shift + Ziehen einen Waveform-Bereich markieren.");
        if (History is null) throw new InvalidOperationException("Die Änderungshistorie ist noch nicht bereit.");

        var normalized = GetSelectedSegments().ToList();
        if (normalized.Count == 0)
            throw new InvalidOperationException("Bitte zuerst mindestens eine Zeile, ein Wort oder eine Silbe auswählen.");

        var roots = Document.Lines.Where(line => normalized.Any(segment =>
            line.DescendantsAndSelf().Contains(segment))).Distinct().ToList();
        var result = new WaveformSyncResult();
        var command = new EditSegmentForestCommand(roots, "Auswahl mit Waveform-Bereich synchronisieren", () =>
            result = TimelineEditing.FitSelectionToWaveformRange(Document, normalized, start, end, Waveform));
        try { History.Execute(command); }
        catch
        {
            command.Undo();
            throw;
        }

        SegmentEdited?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
        return result;
    }

    private void EnsureEditingEnabled()
    {
        if (!EditingEnabled)
            throw new InvalidOperationException(EditorLocale.German
                ? "Die Beat-Vorschau ist nicht editierbar. Schalte Karaoke-Timing aus, um den Arbeitsstand zu bearbeiten."
                : "The beat preview is read-only. Turn off Karaoke Timing to edit the working version.");
    }

    private void DrawLoopRange(DrawingContext context, Rect bounds)
    {
        if (_loopStart is not { } start || _loopEnd is not { } end) return;
        var x1 = _viewport.TimeToPixel(start);
        var x2 = _viewport.TimeToPixel(end);
        var left = Math.Max(0, Math.Min(x1, x2));
        var right = Math.Min(bounds.Width, Math.Max(x1, x2));
        if (right <= left) return;
        context.FillRectangle(new SolidColorBrush(Color.FromArgb(35, 223, 255, 40)), new Rect(left, 34, right - left, bounds.Height - 34));
        var pen = new Pen(new SolidColorBrush(Color.Parse("#DFFF28")), 1.5);
        context.DrawLine(pen, new Point(x1, 34), new Point(x1, bounds.Height));
        context.DrawLine(pen, new Point(x2, 34), new Point(x2, bounds.Height));
    }

    private void DrawRuler(DrawingContext context, double width)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#171B24")), new Rect(0, 0, width, 34));
        var range = _viewport.VisibleRange(width);
        var interval = _viewport.PixelsPerSecond >= 250 ? .5 : _viewport.PixelsPerSecond >= 80 ? 1 : 5;
        var first = Math.Floor(range.Start.TotalSeconds / interval) * interval;
        var typeface = new Typeface("Inter");
        for (var second = first; second <= range.End.TotalSeconds; second += interval)
        {
            var x = _viewport.TimeToPixel(TimeSpan.FromSeconds(second));
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#4B5363"))), new Point(x, 24), new Point(x, 34));
            var label = new FormattedText(TimeSpan.FromSeconds(second).ToString(@"m\:ss\.f"),
                System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11,
                new SolidColorBrush(Color.Parse("#AAB2C1")));
            context.DrawText(label, new Point(x + 4, 5));
        }
    }

    private void DrawTracks(DrawingContext context, Rect bounds)
    {
        var visible = _viewport.VisibleRange(bounds.Width);
        var laneCount = VoiceLaneCount();
        if (laneCount == 1)
        {
            DrawLaneBackground(context, 126, 76, "ZEILEN");
            DrawLaneBackground(context, 210, 70, "WÖRTER");
            DrawLaneBackground(context, 288, Math.Max(84, bounds.Height - 296), "SILBEN");
        }
        else
        {
            for (var lane = 0; lane < laneCount; lane++)
            {
                var group = VoiceGroupRect(lane, bounds);
                DrawLaneBackground(context, group.Y, group.Height,
                    lane == 0 ? "STIMME 1 · LEAD" : $"STIMME {lane + 1}");
            }
        }
        foreach (var line in Document!.Lines.Where(segment => segment.End >= visible.Start && segment.Start <= visible.End))
        {
            DrawSegment(context, line, SegmentTrackRect(line, bounds), Color.Parse("#3B465B"));
            foreach (var word in line.Children.Where(segment => segment.End >= visible.Start && segment.Start <= visible.End))
            {
                DrawSegment(context, word, SegmentTrackRect(word, bounds),
                    word.RequiresReview ? Color.Parse("#66502B") : Color.Parse("#31564D"));
                foreach (var syllable in word.Children.Where(segment => segment.End >= visible.Start && segment.Start <= visible.End))
                    DrawSegment(context, syllable, SegmentTrackRect(syllable, bounds),
                        syllable.Confidence is < .65 ? Color.Parse("#733F48") : Color.Parse("#334E6A"));
            }
        }
    }

    private void DrawWaveform(DrawingContext context, double width)
    {
        const double top = 38;
        const double height = 80;
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0E1720")), new Rect(0, top, width, height));
        if (Waveform is null || Waveform.Levels.Count == 0) return;
        var level = Waveform.SelectLevel(1 / _viewport.PixelsPerSecond);
        var first = Math.Max(0, (int)Math.Floor(_viewport.Offset.TotalSeconds * Waveform.SampleRate / level.SamplesPerPeak));
        var last = Math.Min(level.Peaks.Count - 1,
            (int)Math.Ceiling(_viewport.VisibleRange(width).End.TotalSeconds * Waveform.SampleRate / level.SamplesPerPeak));
        var center = top + height / 2;
        var pen = new Pen(new SolidColorBrush(Color.Parse("#4DD4C6")), 1);
        for (var index = first; index <= last; index++)
        {
            var time = TimeSpan.FromSeconds((double)index * level.SamplesPerPeak / Waveform.SampleRate);
            var x = _viewport.TimeToPixel(time);
            var peak = level.Peaks[index];
            context.DrawLine(pen, new Point(x, center - peak.Maximum * height * .45),
                new Point(x, center - peak.Minimum * height * .45));
        }
    }

    private void DrawPitchEvidence(DrawingContext context, double width)
    {
        if (PitchEvidence is not { Count: > 0 } notes) return;
        const double top = 38;
        const double height = 80;
        var visible = _viewport.VisibleRange(width);
        var visibleNotes = notes.Where(note => note.End >= visible.Start && note.Start <= visible.End).ToArray();
        if (visibleNotes.Length == 0) return;
        // Keep the vertical scale stable while scrolling; a visible-window
        // scale would make the same note jump vertically between viewports.
        var minimum = Math.Max(24, notes.Min(note => note.Midi) - 2);
        var maximum = Math.Min(108, notes.Max(note => note.Midi) + 2);
        var span = Math.Max(24, maximum - minimum);
        var center = (minimum + maximum) / 2d;
        var lower = center - span / 2d;
        foreach (var note in visibleNotes)
        {
            var x = _viewport.TimeToPixel(note.Start);
            var noteWidth = Math.Max(2, _viewport.TimeToPixel(note.End) - x);
            var normalized = Math.Clamp((note.Midi - lower) / span, 0, 1);
            var y = top + height - 4 - normalized * (height - 8);
            var alpha = (byte)Math.Clamp(55 + note.Amplitude * 120, 55, 175);
            context.FillRectangle(new SolidColorBrush(Color.FromArgb(alpha, 255, 86, 210)),
                new Rect(x, y, noteWidth, 3));
        }
        var label = new FormattedText("PITCH", System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Inter", FontStyle.Normal, FontWeight.Bold), 8,
            new SolidColorBrush(Color.FromArgb(170, 255, 86, 210)));
        context.DrawText(label, new Point(8, top + 3));
    }

    private void DrawLaneBackground(DrawingContext context, double y, double height, string label)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#141923")), new Rect(0, y, Bounds.Width, height));
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#29303C"))), new Point(0, y + height), new Point(Bounds.Width, y + height));
        var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Inter", FontStyle.Normal, FontWeight.SemiBold), 10,
            new SolidColorBrush(Color.Parse("#778195")));
        context.DrawText(text, new Point(8, y + 5));
    }

    private void DrawSegment(DrawingContext context, LyricSegment segment, Rect track, Color color)
    {
        var x = _viewport.TimeToPixel(segment.Start);
        var width = Math.Max(2, _viewport.TimeToPixel(segment.End) - x);
        var rect = new Rect(x, track.Y, width, track.Height);
        var selected = SelectedSegment?.Id == segment.Id || _selectedSegments.Contains(segment);
        context.DrawRectangle(new SolidColorBrush(color),
            new Pen(selected ? new SolidColorBrush(Color.Parse("#DFFF28")) : new SolidColorBrush(color.Lighten(.2f)), selected ? 2.5 : 1),
            rect, 4, 4);
        if (width < 18) return;
        var label = ShowTechnicalText && !string.IsNullOrWhiteSpace(segment.TechnicalText)
            ? segment.TechnicalText : segment.Text;
        var text = new FormattedText(label, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Inter"), 12, Brushes.White)
        { MaxTextWidth = Math.Max(1, width - 10), MaxTextHeight = track.Height - 6, Trimming = TextTrimming.CharacterEllipsis };
        context.DrawText(text, new Point(x + 5, track.Y + (track.Height - text.Height) / 2));
    }

    private (LyricSegment Left, LyricSegment Right)? FindBoundary(Point point)
    {
        if (Document is null) return null;
        var maximum = 7d;
        (LyricSegment Left, LyricSegment Right)? best = null;
        foreach (var word in Document.Lines.SelectMany(line => line.Children))
        {
            if (!SegmentTrackRect(word.Children.FirstOrDefault() ?? word, Bounds).Contains(point)) continue;
            for (var index = 0; index + 1 < word.Children.Count; index++)
            {
                var distance = Math.Abs(_viewport.TimeToPixel(word.Children[index].End) - point.X);
                if (distance > maximum) continue;
                maximum = distance;
                best = (word.Children[index], word.Children[index + 1]);
            }
        }
        return best;
    }

    private LyricSegment? FindSegment(Point point)
    {
        if (Document is null) return null;
        var time = _viewport.PixelToTime(point.X);
        return Document.Segments.Where(segment =>
                segment.Type is LyricSegmentType.Line or LyricSegmentType.Word or LyricSegmentType.Syllable &&
                SegmentTrackRect(segment, Bounds).Contains(point) &&
                segment.Start <= time && segment.End >= time)
            .OrderBy(segment => segment.End - segment.Start).FirstOrDefault();
    }

    private int VoiceLaneCount() => Math.Clamp(
        (Document?.Lines.Select(line => line.VoiceLane).DefaultIfEmpty(0).Max() ?? 0) + 1, 1, 4);

    private Rect VoiceGroupRect(int lane, Rect bounds)
    {
        var count = VoiceLaneCount();
        var height = Math.Max(96, (bounds.Height - 126) / count);
        return new Rect(0, 126 + lane * height, bounds.Width, height);
    }

    private Rect SegmentTrackRect(LyricSegment segment, Rect bounds)
    {
        if (VoiceLaneCount() == 1) return segment.Type switch
        {
            LyricSegmentType.Line => new Rect(0, 150, bounds.Width, 42),
            LyricSegmentType.Word => new Rect(0, 231, bounds.Width, 38),
            _ => new Rect(0, 315, bounds.Width, 44),
        };
        var line = FindLine(segment);
        var group = VoiceGroupRect(Math.Clamp(line?.VoiceLane ?? 0, 0, VoiceLaneCount() - 1), bounds);
        var usable = Math.Max(78, group.Height - 24);
        return segment.Type switch
        {
            LyricSegmentType.Line => new Rect(0, group.Y + 20, bounds.Width, Math.Max(22, usable * .27)),
            LyricSegmentType.Word => new Rect(0, group.Y + 22 + usable * .31, bounds.Width, Math.Max(22, usable * .27)),
            _ => new Rect(0, group.Y + 24 + usable * .62, bounds.Width, Math.Max(24, usable * .32)),
        };
    }

    private LyricSegment? FindLine(LyricSegment segment) => Document?.Lines.FirstOrDefault(line =>
        line.Id == segment.Id || line.DescendantsAndSelf().Any(candidate => candidate.Id == segment.Id));

    private LyricSegment? FindParentWord(LyricSegment segment) => Document?.Lines.SelectMany(line => line.Children)
        .FirstOrDefault(word => word.Id == segment.ParentId);

    private void UpdateHoverCursor(Point point)
    {
        if (FindBoundary(point) is not null)
        {
            Cursor = new Cursor(StandardCursorType.SizeWestEast);
            return;
        }
        var segment = FindSegment(point);
        if (segment is not { Type: LyricSegmentType.Line or LyricSegmentType.Word or LyricSegmentType.Syllable })
        {
            Cursor = Cursor.Default;
            return;
        }
        var atEdge = Math.Abs(point.X - _viewport.TimeToPixel(segment.Start)) <= 7 ||
                     Math.Abs(point.X - _viewport.TimeToPixel(segment.End)) <= 7;
        Cursor = new Cursor(atEdge ? StandardCursorType.SizeWestEast : StandardCursorType.SizeAll);
    }

    private void ApplySegmentDrag(SegmentDragState state)
    {
        var delta = state.Current - state.PointerStart;
        var start = state.InitialStart;
        var end = state.InitialEnd;
        if (state.Mode == SegmentDragMode.Move)
        {
            if (state.Segment.Type == LyricSegmentType.Line)
            {
                var (previousEnd, nextStart) = LineNeighborBounds(state.Segment);
                TimelineEditing.MoveLineWithinNeighbors(state.Segment, delta, previousEnd, nextStart);
            }
            else if (state.Segment.Type == LyricSegmentType.Word)
            {
                var index = state.Line.Children.IndexOf(state.Segment);
                var minimum = index > 0 ? state.Line.Children[index - 1].End : state.Line.Start;
                var maximum = index + 1 < state.Line.Children.Count ? state.Line.Children[index + 1].Start : state.Line.End;
                if (start + delta < minimum) delta = minimum - start;
                if (end + delta > maximum) delta = maximum - end;
                TimelineEditing.MoveWithChildren(state.Segment, delta);
            }
            else if (state.ParentWord is not null) TimelineEditing.MoveSyllable(state.Segment, state.ParentWord, delta);
            return;
        }
        if (state.Mode == SegmentDragMode.ResizeStart) start += delta; else end += delta;
        if (state.Segment.Type == LyricSegmentType.Line)
        {
            var (previousEnd, nextStart) = LineNeighborBounds(state.Segment);
            TimelineEditing.ResizeLineWithinNeighbors(state.Segment, start, end, previousEnd, nextStart,
                TimeSpan.FromMilliseconds(100));
        }
        else if (state.Segment.Type == LyricSegmentType.Word)
        {
            var index = state.Line.Children.IndexOf(state.Segment);
            var minimum = index > 0 ? state.Line.Children[index - 1].End : state.Line.Start;
            var maximum = index + 1 < state.Line.Children.Count ? state.Line.Children[index + 1].Start : state.Line.End;
            TimelineEditing.ResizeWord(state.Segment, start < minimum ? minimum : start,
                end > maximum ? maximum : end, TimeSpan.FromMilliseconds(35));
        }
        else if (state.ParentWord is not null)
            TimelineEditing.ResizeSyllable(state.Segment, state.ParentWord, start, end, TimeSpan.FromMilliseconds(25));
    }

    private static IEnumerable<LyricSegment> NormalizeSelection(IEnumerable<LyricSegment> selection)
    {
        var selected = selection.ToHashSet();
        foreach (var segment in selected)
            if (!selected.Any(parent => !ReferenceEquals(parent, segment) &&
                                        parent.DescendantsAndSelf().Contains(segment)))
                yield return segment;
    }

    private static void ApplyMultiSegmentDrag(MultiSegmentDragState state)
    {
        var delta = state.Current - state.PointerStart;
        var selected = state.Segments.ToHashSet();
        var minimumDelta = TimeSpan.MinValue;
        var maximumDelta = TimeSpan.MaxValue;
        foreach (var segment in state.Segments)
        {
            var line = state.Roots.First(root => root.DescendantsAndSelf().Contains(segment));
            List<LyricSegment> siblings;
            TimeSpan outerStart;
            TimeSpan? outerEnd;
            if (segment.Type == LyricSegmentType.Line)
            {
                siblings = state.Document.Lines.Where(candidate => candidate.VoiceLane == segment.VoiceLane)
                    .OrderBy(TimelineEditing.EffectiveStart).ToList();
                outerStart = TimeSpan.Zero;
                outerEnd = null;
            }
            else if (segment.Type == LyricSegmentType.Word)
            {
                siblings = line.Children;
                outerStart = line.Start;
                outerEnd = line.End;
            }
            else
            {
                var word = line.Children.First(parent => parent.Id == segment.ParentId);
                siblings = word.Children;
                outerStart = line.Start;
                outerEnd = line.End;
            }

            var index = siblings.IndexOf(segment);
            var effectiveStart = segment.Type == LyricSegmentType.Line
                ? TimelineEditing.EffectiveStart(segment) : segment.Start;
            var effectiveEnd = segment.Type == LyricSegmentType.Line
                ? TimelineEditing.EffectiveEnd(segment) : segment.End;
            if (index == 0)
                minimumDelta = Max(minimumDelta, outerStart - effectiveStart);
            else if (!selected.Contains(siblings[index - 1]))
                minimumDelta = Max(minimumDelta,
                    (segment.Type == LyricSegmentType.Line
                        ? TimelineEditing.EffectiveEnd(siblings[index - 1])
                        : siblings[index - 1].End) - effectiveStart);

            if (index + 1 < siblings.Count && !selected.Contains(siblings[index + 1]))
                maximumDelta = Min(maximumDelta,
                    (segment.Type == LyricSegmentType.Line
                        ? TimelineEditing.EffectiveStart(siblings[index + 1])
                        : siblings[index + 1].Start) - effectiveEnd);
            else if (index + 1 == siblings.Count && outerEnd is { } maximum)
                maximumDelta = Min(maximumDelta, maximum - effectiveEnd);
        }
        if (delta < minimumDelta) delta = minimumDelta;
        if (delta > maximumDelta) delta = maximumDelta;

        var affectedWords = new HashSet<LyricSegment>();
        foreach (var segment in state.Segments)
        {
            if (segment.Type is LyricSegmentType.Line or LyricSegmentType.Word)
                TimelineEditing.MoveWithChildren(segment, delta);
            else
            {
                segment.Start += delta;
                segment.End += delta;
                TimelineEditing.MarkAdjusted(segment);
                var word = state.Roots.SelectMany(root => root.Children)
                    .First(parent => parent.Id == segment.ParentId);
                affectedWords.Add(word);
            }
        }
        foreach (var word in affectedWords) TimelineEditing.FitParentToChildren(word);
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;
    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private (TimeSpan PreviousEnd, TimeSpan? NextStart) LineNeighborBounds(LyricSegment line)
    {
        var ordered = Document?.Lines.Where(candidate => candidate.VoiceLane == line.VoiceLane)
            .OrderBy(candidate => candidate.Start).ThenBy(candidate => candidate.End).ToList() ?? [];
        var index = ordered.IndexOf(line);
        return (index > 0 ? ordered[index - 1].End : TimeSpan.Zero,
            index >= 0 && index + 1 < ordered.Count ? ordered[index + 1].Start : null);
    }

    private enum SegmentDragMode { Move, ResizeStart, ResizeEnd }
    private sealed class SegmentDragState(LyricSegment segment, LyricSegment line, LyricSegment? parentWord,
        SegmentDragMode mode, TimeSpan pointerStart, TimeSpan initialStart, TimeSpan initialEnd)
    {
        public LyricSegment Segment { get; } = segment;
        public LyricSegment Line { get; } = line;
        public LyricSegment? ParentWord { get; } = parentWord;
        public SegmentDragMode Mode { get; } = mode;
        public TimeSpan PointerStart { get; } = pointerStart;
        public TimeSpan InitialStart { get; } = initialStart;
        public TimeSpan InitialEnd { get; } = initialEnd;
        public TimeSpan Current { get; set; } = pointerStart;
    }
    private sealed class MultiSegmentDragState(LyricsEditorDocument document, IReadOnlyList<LyricSegment> segments,
        IReadOnlyList<LyricSegment> roots, TimeSpan pointerStart)
    {
        public LyricsEditorDocument Document { get; } = document;
        public IReadOnlyList<LyricSegment> Segments { get; } = segments;
        public IReadOnlyList<LyricSegment> Roots { get; } = roots;
        public TimeSpan PointerStart { get; } = pointerStart;
        public TimeSpan Current { get; set; } = pointerStart;
    }

    private static void DrawEmpty(DrawingContext context, Rect bounds)
    {
        var text = new FormattedText("Song auswählen, um das KI-Alignment zu prüfen",
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Inter"), 18, new SolidColorBrush(Color.Parse("#7E8798")));
        context.DrawText(text, new Point((bounds.Width - text.Width) / 2, (bounds.Height - text.Height) / 2));
    }
}

internal static class EditorColorExtensions
{
    public static Color Lighten(this Color color, float amount) => Color.FromArgb(color.A,
        (byte)Math.Clamp(color.R + 255 * amount, 0, 255),
        (byte)Math.Clamp(color.G + 255 * amount, 0, 255),
        (byte)Math.Clamp(color.B + 255 * amount, 0, 255));
}
