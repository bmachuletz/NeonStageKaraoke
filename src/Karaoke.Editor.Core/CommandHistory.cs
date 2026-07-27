namespace Karaoke.Editor.Core;

public interface IEditorCommand
{
    string Description { get; }
    void Execute();
    void Undo();
}

public sealed class CommandHistory
{
    private readonly Stack<IEditorCommand> _undo = new();
    private readonly Stack<IEditorCommand> _redo = new();
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }

    public void Execute(IEditorCommand command)
    {
        command.Execute();
        _undo.Push(command);
        _redo.Clear();
    }

    public void RecordExecuted(IEditorCommand command)
    {
        _undo.Push(command);
        _redo.Clear();
    }

    public bool Undo()
    {
        if (!_undo.TryPop(out var command)) return false;
        command.Undo();
        _redo.Push(command);
        return true;
    }

    public bool Redo()
    {
        if (!_redo.TryPop(out var command)) return false;
        command.Execute();
        _undo.Push(command);
        return true;
    }
}

public sealed class EditLineCollectionCommand(
    IList<LyricSegment> lines, string description, Action edit) : IEditorCommand
{
    private readonly LyricSegment[] _before = lines.ToArray();
    public string Description { get; } = description;
    public void Execute() => edit();
    public void Undo()
    {
        lines.Clear();
        foreach (var line in _before) lines.Add(line);
    }
}

public sealed class EditSegmentForestCommand : IEditorCommand
{
    private readonly Action _edit;
    private readonly Dictionary<LyricSegment, ForestState> _states;
    private readonly Dictionary<LyricSegment, LyricSegment[]> _children;

    public EditSegmentForestCommand(IEnumerable<LyricSegment> roots, string description, Action edit)
    {
        Description = description;
        _edit = edit;
        var segments = roots.SelectMany(root => root.DescendantsAndSelf()).Distinct().ToList();
        _states = segments.ToDictionary(segment => segment, ForestState.Capture);
        _children = segments.ToDictionary(segment => segment, segment => segment.Children.ToArray());
    }

    public string Description { get; }
    public void Execute() => _edit();
    public void Undo()
    {
        foreach (var (segment, state) in _states) state.Restore(segment);
        foreach (var (segment, children) in _children)
        {
            segment.Children.Clear();
            segment.Children.AddRange(children);
        }
    }

    private sealed record ForestState(TimeSpan Start, TimeSpan End, string Text, SegmentOrigin Origin,
        bool Adjusted, bool Reviewed, bool RequiresReview, int? HoldAfterMilliseconds, StageLineEffect StageEffect)
    {
        public static ForestState Capture(LyricSegment segment) => new(segment.Start, segment.End, segment.Text,
            segment.Origin, segment.IsManuallyAdjusted, segment.IsReviewed, segment.RequiresReview,
            segment.HoldAfterMilliseconds, segment.StageEffect);
        public void Restore(LyricSegment segment)
        {
            segment.Start = Start; segment.End = End; segment.Text = Text; segment.Origin = Origin;
            segment.IsManuallyAdjusted = Adjusted; segment.IsReviewed = Reviewed; segment.RequiresReview = RequiresReview;
            segment.HoldAfterMilliseconds = HoldAfterMilliseconds; segment.StageEffect = StageEffect;
        }
    }
}

public sealed class MoveSharedBoundaryCommand(
    LyricSegment left, LyricSegment right, TimeSpan boundary, TimeSpan minimumDuration) : IEditorCommand
{
    private readonly TimeSpan _oldLeftEnd = left.End;
    private readonly TimeSpan _oldRightStart = right.Start;
    private readonly SegmentOrigin _oldLeftOrigin = left.Origin;
    private readonly SegmentOrigin _oldRightOrigin = right.Origin;
    private readonly bool _oldLeftAdjusted = left.IsManuallyAdjusted;
    private readonly bool _oldRightAdjusted = right.IsManuallyAdjusted;
    private readonly bool _oldLeftRequiresReview = left.RequiresReview;
    private readonly bool _oldRightRequiresReview = right.RequiresReview;
    public string Description => "Gemeinsame Segmentgrenze verschieben";
    public void Execute() => TimelineEditing.MoveSharedBoundary(left, right, boundary, minimumDuration);
    public void Undo()
    {
        left.End = _oldLeftEnd;
        right.Start = _oldRightStart;
        left.Origin = _oldLeftOrigin;
        right.Origin = _oldRightOrigin;
        left.IsManuallyAdjusted = _oldLeftAdjusted;
        right.IsManuallyAdjusted = _oldRightAdjusted;
        left.RequiresReview = _oldLeftRequiresReview;
        right.RequiresReview = _oldRightRequiresReview;
    }
}

public sealed class SetReviewStateCommand(LyricSegment segment, bool reviewed) : IEditorCommand
{
    private readonly bool _oldReviewed = segment.IsReviewed;
    private readonly bool _oldRequiresReview = segment.RequiresReview;
    public string Description => reviewed ? "Segment als geprüft markieren" : "Segment erneut prüfen";

    public void Execute()
    {
        segment.IsReviewed = reviewed;
        segment.RequiresReview = !reviewed;
    }

    public void Undo()
    {
        segment.IsReviewed = _oldReviewed;
        segment.RequiresReview = _oldRequiresReview;
    }
}

/// <summary>Atomic snapshot command for hierarchical timing and structure edits.</summary>
public sealed class EditSegmentTreeCommand : IEditorCommand
{
    private readonly LyricSegment _root;
    private readonly Action _edit;
    private readonly Dictionary<LyricSegment, SegmentState> _states;
    private readonly Dictionary<LyricSegment, LyricSegment[]> _children;

    public EditSegmentTreeCommand(LyricSegment root, string description, Action edit)
    {
        _root = root;
        Description = description;
        _edit = edit;
        var segments = root.DescendantsAndSelf().ToList();
        _states = segments.ToDictionary(segment => segment, SegmentState.Capture);
        _children = segments.ToDictionary(segment => segment, segment => segment.Children.ToArray());
    }

    public string Description { get; }
    public void Execute() => _edit();
    public void Undo()
    {
        foreach (var (segment, state) in _states) state.Restore(segment);
        foreach (var (segment, children) in _children)
        {
            segment.Children.Clear();
            segment.Children.AddRange(children);
        }
    }

    private sealed record SegmentState(TimeSpan Start, TimeSpan End, string Text, SegmentOrigin Origin,
        bool Adjusted, bool Reviewed, bool RequiresReview, int? HoldAfterMilliseconds, StageLineEffect StageEffect)
    {
        public static SegmentState Capture(LyricSegment segment) => new(segment.Start, segment.End, segment.Text,
            segment.Origin, segment.IsManuallyAdjusted, segment.IsReviewed, segment.RequiresReview,
            segment.HoldAfterMilliseconds, segment.StageEffect);
        public void Restore(LyricSegment segment)
        {
            segment.Start = Start; segment.End = End; segment.Text = Text; segment.Origin = Origin;
            segment.IsManuallyAdjusted = Adjusted; segment.IsReviewed = Reviewed; segment.RequiresReview = RequiresReview;
            segment.HoldAfterMilliseconds = HoldAfterMilliseconds; segment.StageEffect = StageEffect;
        }
    }
}
