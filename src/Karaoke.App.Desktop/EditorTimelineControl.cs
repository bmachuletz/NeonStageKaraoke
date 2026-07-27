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
    public static readonly StyledProperty<long> RevisionProperty =
        AvaloniaProperty.Register<EditorTimelineControl, long>(nameof(Revision));

    private readonly TimelineViewport _viewport = new(115);
    private (LyricSegment Left, LyricSegment Right, TimeSpan LeftEnd, TimeSpan RightStart,
        SegmentOrigin LeftOrigin, SegmentOrigin RightOrigin, bool LeftAdjusted, bool RightAdjusted,
        bool LeftReview, bool RightReview)? _drag;
    private TimeSpan _dragBoundary;
    private LyricSegment? _selectedSegment;
    private bool _showInitialVocalWindow;
    private TimeSpan? _loopStart;
    private TimeSpan? _loopEnd;
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
        AffectsRender<EditorTimelineControl>(DocumentProperty, PlayheadProperty, WaveformProperty, RevisionProperty);
        DocumentProperty.Changed.AddClassHandler<EditorTimelineControl>((control, _) =>
        {
            control._viewport.Reset();
            control._showInitialVocalWindow = true;
            control.SelectedSegment = null;
            control._selectedSegments.Clear();
            control.InvalidateVisual();
        });
        FocusableProperty.OverrideDefaultValue<EditorTimelineControl>(true);
    }

    public LyricsEditorDocument? Document { get => GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }
    public TimeSpan Playhead { get => GetValue(PlayheadProperty); set => SetValue(PlayheadProperty, value); }
    public WaveformPyramid? Waveform { get => GetValue(WaveformProperty); set => SetValue(WaveformProperty, value); }
    public long Revision { get => GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
    public CommandHistory? History { get; set; }
    public event EventHandler<TimeSpan>? PositionRequested;
    public event EventHandler<LyricSegment?>? SegmentSelected;
    public event EventHandler<(TimeSpan Start, TimeSpan End)>? RangeSelected;
    public event EventHandler? SegmentEdited;

    public LyricSegment? SelectedSegment
    {
        get => _selectedSegment;
        set { _selectedSegment = value; InvalidateVisual(); }
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
        Focus();
        var point = e.GetPosition(this);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _rangeAnchor = _viewport.PixelToTime(point.X);
            _loopStart = _loopEnd = _rangeAnchor;
            _selectingRange = true;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        var boundary = FindBoundary(point);
        if (boundary is not null)
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
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && hit is { Type: LyricSegmentType.Word or LyricSegmentType.Syllable })
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
            if (SelectedSegment is { Type: LyricSegmentType.Line or LyricSegmentType.Word or LyricSegmentType.Syllable } segment &&
                FindLine(segment) is { } line)
            {
                var normalized = NormalizeSelection(_selectedSegments).ToList();
                if (normalized.Count > 1)
                {
                    _multiDrag = new(normalized, Document!.Lines.Where(candidate => normalized.Any(item =>
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
        DrawLaneBackground(context, 126, 76, "ZEILEN");
        DrawLaneBackground(context, 210, 70, "WÖRTER");
        DrawLaneBackground(context, 288, Math.Max(84, bounds.Height - 296), "SILBEN");
        foreach (var line in Document!.Lines.Where(segment => segment.End >= visible.Start && segment.Start <= visible.End))
        {
            DrawSegment(context, line, 150, 42, Color.Parse("#3B465B"));
            foreach (var word in line.Children.Where(segment => segment.End >= visible.Start && segment.Start <= visible.End))
            {
                DrawSegment(context, word, 231, 38, word.RequiresReview ? Color.Parse("#66502B") : Color.Parse("#31564D"));
                foreach (var syllable in word.Children.Where(segment => segment.End >= visible.Start && segment.Start <= visible.End))
                    DrawSegment(context, syllable, 315, 44,
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

    private void DrawLaneBackground(DrawingContext context, double y, double height, string label)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#141923")), new Rect(0, y, Bounds.Width, height));
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#29303C"))), new Point(0, y + height), new Point(Bounds.Width, y + height));
        var text = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface("Inter", FontStyle.Normal, FontWeight.SemiBold), 10,
            new SolidColorBrush(Color.Parse("#778195")));
        context.DrawText(text, new Point(8, y + 5));
    }

    private void DrawSegment(DrawingContext context, LyricSegment segment, double y, double height, Color color)
    {
        var x = _viewport.TimeToPixel(segment.Start);
        var width = Math.Max(2, _viewport.TimeToPixel(segment.End) - x);
        var rect = new Rect(x, y, width, height);
        var selected = SelectedSegment?.Id == segment.Id || _selectedSegments.Contains(segment);
        context.DrawRectangle(new SolidColorBrush(color),
            new Pen(selected ? new SolidColorBrush(Color.Parse("#DFFF28")) : new SolidColorBrush(color.Lighten(.2f)), selected ? 2.5 : 1),
            rect, 4, 4);
        if (width < 18) return;
        var text = new FormattedText(segment.Text, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, new Typeface("Inter"), 12, Brushes.White)
        { MaxTextWidth = Math.Max(1, width - 10), MaxTextHeight = height - 6, Trimming = TextTrimming.CharacterEllipsis };
        context.DrawText(text, new Point(x + 5, y + (height - text.Height) / 2));
    }

    private (LyricSegment Left, LyricSegment Right)? FindBoundary(Point point)
    {
        if (Document is null || point.Y < 294) return null;
        var maximum = 7d;
        (LyricSegment Left, LyricSegment Right)? best = null;
        foreach (var word in Document.Lines.SelectMany(line => line.Children))
            for (var index = 0; index + 1 < word.Children.Count; index++)
            {
                var distance = Math.Abs(_viewport.TimeToPixel(word.Children[index].End) - point.X);
                if (distance > maximum) continue;
                maximum = distance;
                best = (word.Children[index], word.Children[index + 1]);
            }
        return best;
    }

    private LyricSegment? FindSegment(Point point)
    {
        if (Document is null) return null;
        IEnumerable<LyricSegment> candidates = point.Y switch
        {
            >= 150 and <= 192 => Document.Lines,
            >= 231 and <= 269 => Document.Lines.SelectMany(line => line.Children),
            >= 315 and <= 359 => Document.Lines.SelectMany(line => line.Children).SelectMany(word => word.Children),
            _ => []
        };
        var time = _viewport.PixelToTime(point.X);
        return candidates.Where(segment => segment.Start <= time && segment.End >= time)
            .OrderBy(segment => segment.End - segment.Start).FirstOrDefault();
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
            var siblings = segment.Type == LyricSegmentType.Word
                ? line.Children
                : line.Children.First(word => word.Id == segment.ParentId).Children;
            var index = siblings.IndexOf(segment);
            var lower = index > 0 && !selected.Contains(siblings[index - 1]) ? siblings[index - 1].End
                : segment.Type == LyricSegmentType.Word ? line.Start : TimeSpan.Zero;
            var upper = index + 1 < siblings.Count && !selected.Contains(siblings[index + 1]) ? siblings[index + 1].Start
                : segment.Type == LyricSegmentType.Word ? line.End : TimeSpan.MaxValue;
            minimumDelta = Max(minimumDelta, lower - segment.Start);
            if (upper != TimeSpan.MaxValue)
                maximumDelta = Min(maximumDelta, upper - segment.End);
        }
        if (delta < minimumDelta) delta = minimumDelta;
        if (delta > maximumDelta) delta = maximumDelta;
        var affectedWords = new HashSet<LyricSegment>();
        foreach (var segment in state.Segments)
        {
            if (segment.Type == LyricSegmentType.Word) TimelineEditing.MoveWithChildren(segment, delta);
            else
            {
                segment.Start += delta;
                segment.End += delta;
                TimelineEditing.MarkAdjusted(segment);
                var word = state.Roots.SelectMany(root => root.Children).First(parent => parent.Id == segment.ParentId);
                affectedWords.Add(word);
            }
        }
        foreach (var word in affectedWords) TimelineEditing.FitParentToChildren(word);
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) => left >= right ? left : right;
    private static TimeSpan Min(TimeSpan left, TimeSpan right) => left <= right ? left : right;

    private (TimeSpan PreviousEnd, TimeSpan? NextStart) LineNeighborBounds(LyricSegment line)
    {
        var ordered = Document?.Lines.OrderBy(candidate => candidate.Start).ThenBy(candidate => candidate.End).ToList() ?? [];
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
    private sealed class MultiSegmentDragState(IReadOnlyList<LyricSegment> segments,
        IReadOnlyList<LyricSegment> roots, TimeSpan pointerStart)
    {
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
