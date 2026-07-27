using System.Security.Cryptography;
using System.Text;
using Karaoke.Contracts;

namespace Karaoke.Editor.Core;

public static class LyricsDocumentImporter
{
    public static LyricsEditorDocument Import(LyricsDto source, string? analysisRunId = null,
        string? modelVersion = null)
    {
        var document = new LyricsEditorDocument
        {
            SongId = source.SongId,
            AnalysisRunId = analysisRunId,
            ModelVersion = modelVersion,
            Status = LyricsReviewStatus.NeedsReview,
        };
        foreach (var line in source.Lines)
        {
            var lineSegment = Create(source.SongId, $"line:{line.Index}", null,
                LyricSegmentType.Line, line.Start, line.End ?? line.Start, line.Text,
                line.Words is { Count: > 0 } ? SegmentOrigin.GeneratedByAi : SegmentOrigin.ImportedLineLyrics,
                null, analysisRunId, modelVersion);
            foreach (var word in line.Words ?? [])
            {
                var wordSegment = Create(source.SongId, $"line:{line.Index}:word:{word.Index}", lineSegment.Id,
                    LyricSegmentType.Word, word.Start, word.End ?? word.Start, word.Text,
                    SegmentOrigin.GeneratedByAi, word.SyllableConfidence, analysisRunId, modelVersion);
                foreach (var syllable in word.Syllables ?? [])
                    wordSegment.Children.Add(Create(source.SongId,
                        $"line:{line.Index}:word:{word.Index}:syllable:{syllable.Index}", wordSegment.Id,
                        LyricSegmentType.Syllable, syllable.Start, syllable.End ?? syllable.Start,
                        syllable.Text, SegmentOrigin.GeneratedByAi, syllable.Confidence,
                        analysisRunId, modelVersion));
                lineSegment.Children.Add(wordSegment);
            }
            document.Lines.Add(lineSegment);
        }
        return document;
    }

    private static LyricSegment Create(Guid songId, string key, Guid? parentId,
        LyricSegmentType type, TimeSpan start, TimeSpan end, string text, SegmentOrigin origin,
        double? confidence, string? analysisRunId, string? modelVersion)
    {
        var id = StableId(songId, key);
        return new LyricSegment
        {
            Id = id,
            ParentId = parentId,
            Type = type,
            Start = start,
            End = end < start ? start : end,
            Text = text,
            OriginalStart = start,
            OriginalEnd = end < start ? start : end,
            OriginalText = text,
            Origin = origin,
            Confidence = confidence,
            IsAutomaticallyGenerated = origin == SegmentOrigin.GeneratedByAi,
            RequiresReview = true,
            AnalysisRunId = analysisRunId,
            ModelVersion = modelVersion,
        };
    }

    private static Guid StableId(Guid songId, string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{songId:N}:{key}"));
        Span<byte> id = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(id);
        return new Guid(id);
    }
}
