using Karaoke.Contracts;

namespace Karaoke.Editor.Core;

public static class StageTestLyricsMapper
{
    public static LyricsDto ToLyricsDto(LyricsEditorDocument document, SongDto song,
        bool musicalHighlightEnabled)
    {
        var lines = document.Lines.OrderBy(line => line.Start).ThenBy(line => line.End)
            .Select((line, lineIndex) => new LyricsLineDto(
                line.Start, line.Text, line.End, lineIndex,
                line.Children.Where(word => word.Type == LyricSegmentType.Word)
                    .OrderBy(word => word.Start).ThenBy(word => word.End)
                    .Select((word, wordIndex) => new LyricsWordDto(
                        word.Start, word.Text, word.End, wordIndex,
                        word.Children.Where(syllable => syllable.Type == LyricSegmentType.Syllable)
                            .OrderBy(syllable => syllable.Start).ThenBy(syllable => syllable.End)
                            .Select((syllable, syllableIndex) => new LyricsSyllableDto(
                                syllable.Start, syllable.Text, syllable.End, syllableIndex,
                                syllable.Confidence ?? 0, syllable.KaraokeTimingLocked,
                                syllable.Notes.OrderBy(note => note.Start)
                                    .Select(note => new LyricsNoteEvidenceDto(
                                        note.Start, note.End, note.Midi, note.Amplitude)).ToArray()))
                            .ToArray(),
                        word.Confidence ?? 0, word.KaraokeTimingLocked))
                    .ToArray(),
                line.HoldAfterMilliseconds, line.StageEffect.ToString(), line.VoiceLane,
                line.VoiceLabel, line.KaraokeTimingLocked))
            .ToArray();

        return new LyricsDto(document.SongId, lines, song.Artist, song.Title, song.Album,
            HasUltraStarTimingHeritage: document.HasUltraStarTimingHeritage,
            MusicalHighlight: new MusicalHighlightSettingsDto(musicalHighlightEnabled));
    }
}
