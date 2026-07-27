namespace Karaoke.Editor.Core;

public sealed class ReplaceLyricsDocumentCommand(
    LyricsEditorDocument? previous,
    LyricsEditorDocument replacement,
    Action<LyricsEditorDocument?> replace,
    string description) : IEditorCommand
{
    public string Description { get; } = description;
    public void Execute() => replace(replacement);
    public void Undo() => replace(previous);
}
