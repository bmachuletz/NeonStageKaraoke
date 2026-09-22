using System.Security.Cryptography;
using System.Text;
using Karaoke.Contracts;

namespace Karaoke.Editor.Core;

public static class LyricsDocumentImporter
{
    public static LyricsEditorDocument Import(LyricsDto source, string? analysisRunId = null,
        string? modelVersion = null, SegmentOrigin detailedOrigin = SegmentOrigin.GeneratedByAi,
        bool hasUltraStarTimingHeritage = false)
    {
        var document = new LyricsEditorDocument
        {
            SongId = source.SongId,
            AnalysisRunId = analysisRunId,
            ModelVersion = modelVersion,
            Status = LyricsReviewStatus.NeedsReview,
            KaraokeColors = new KaraokeColorSettings
            {
                UnsungColor = KaraokeColorSettings.NormalizeOrDefault(source.KaraokeColors?.UnsungColor,
                    KaraokeColorSettings.DefaultUnsungColor),
                SungColor = KaraokeColorSettings.NormalizeOrDefault(source.KaraokeColors?.SungColor,
                    KaraokeColorSettings.DefaultSungColor),
                GlowColor = KaraokeColorSettings.NormalizeOrDefault(source.KaraokeColors?.GlowColor,
                    KaraokeColorSettings.DefaultGlowColor)
            },
            HasUltraStarTimingHeritage = source.HasUltraStarTimingHeritage ||
                hasUltraStarTimingHeritage || detailedOrigin == SegmentOrigin.ImportedFromUltraStar,
        };
        foreach (var line in source.Lines)
        {
            var isStructureMarker = LyricsStructureMarker.IsMarker(line.Text);
            var lineSegment = Create(source.SongId, $"line:{line.Index}", null,
                LyricSegmentType.Line, line.Start, line.End ?? line.Start,
                isStructureMarker ? string.Empty : line.Text,
                line.Words is { Count: > 0 } ? detailedOrigin : SegmentOrigin.ImportedLineLyrics,
                null, analysisRunId, modelVersion);
            lineSegment.VoiceLane = Math.Max(0, line.VoiceLane);
            lineSegment.VoiceLabel = line.VoiceLabel;
            if (isStructureMarker)
            {
                lineSegment.RequiresReview = false;
                document.Lines.Add(lineSegment);
                continue;
            }
            foreach (var word in line.Words ?? [])
            {
                var wordSegment = Create(source.SongId, $"line:{line.Index}:word:{word.Index}", lineSegment.Id,
                    LyricSegmentType.Word, word.Start, word.End ?? word.Start, word.Text,
                    detailedOrigin, word.SyllableConfidence, analysisRunId, modelVersion);
                foreach (var syllable in word.Syllables ?? [])
                    wordSegment.Children.Add(Create(source.SongId,
                        $"line:{line.Index}:word:{word.Index}:syllable:{syllable.Index}", wordSegment.Id,
                        LyricSegmentType.Syllable, syllable.Start, syllable.End ?? syllable.Start,
                        syllable.Text, detailedOrigin, syllable.Confidence,
                        analysisRunId, modelVersion));
                lineSegment.Children.Add(wordSegment);
            }
            document.Lines.Add(lineSegment);
        }
        return document;
    }

    /// <summary>Neutralizes section labels in old persisted drafts while retaining their timing boundary.</summary>
    public static int IgnoreStructureMarkers(LyricsEditorDocument document)
    {
        var ignored = 0;
        foreach (var line in document.Lines.Where(line => LyricsStructureMarker.IsMarker(line.Text)))
        {
            line.Text = string.Empty;
            line.Children.Clear();
            line.RequiresReview = false;
            line.IsReviewed = true;
            ignored++;
        }
        return ignored;
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
