namespace Karaoke.Editor.Core;

/// <summary>
/// Keeps a loop range attached to a word instead of copying its current
/// timestamps. The current segment is resolved by ID on every refresh so the
/// range follows timeline edits and document snapshots safely.
/// </summary>
public sealed class TrackedWordLoop
{
    private Guid? _wordId;

    public bool IsBound => _wordId is not null;

    public bool Bind(LyricSegment? segment)
    {
        if (segment is not { Type: LyricSegmentType.Word } || segment.End <= segment.Start)
            return false;
        _wordId = segment.Id;
        return true;
    }

    public void Clear() => _wordId = null;

    public bool TryGetRange(LyricsEditorDocument? document,
        out (TimeSpan Start, TimeSpan End) range)
    {
        range = default;
        if (_wordId is not { } wordId || document is null) return false;
        var word = document.Lines.SelectMany(line => line.Children)
            .FirstOrDefault(candidate => candidate.Id == wordId &&
                                         candidate.Type == LyricSegmentType.Word);
        if (word is null || word.End <= word.Start)
        {
            Clear();
            return false;
        }
        range = (word.Start, word.End);
        return true;
    }
}
