using NeonStage.Presentation;

namespace Karaoke.Editor.Core;

/// <summary>Creates an editable-document-shaped, read-only timing preview.</summary>
public static class KaraokeTimingProjection
{
    public static LyricsEditorDocument Create(LyricsEditorDocument source, IReadOnlyList<double> beatTimes)
    {
        if (source.UsesUltraStarTiming) return source;
        var presentation = source.Lines.Select(ToPresentation).ToArray();
        var projected = StagePresentationEngine.ProjectKaraokeTiming(presentation, beatTimes);
        if (ReferenceEquals(projected, presentation)) return source;

        var document = CloneDocument(source);
        for (var lineIndex = 0; lineIndex < Math.Min(document.Lines.Count, projected.Count); lineIndex++)
        {
            var targetLine = document.Lines[lineIndex];
            var sourceLine = projected[lineIndex];
            if (!targetLine.KaraokeTimingLocked)
            {
                targetLine.Start = TimeSpan.FromSeconds(sourceLine.Start);
                targetLine.End = TimeSpan.FromSeconds(sourceLine.End);
            }
            var words = targetLine.Children.Where(child => child.Type == LyricSegmentType.Word).ToArray();
            for (var wordIndex = 0; wordIndex < Math.Min(words.Length, sourceLine.Words.Count); wordIndex++)
            {
                var targetWord = words[wordIndex];
                var sourceWord = sourceLine.Words[wordIndex];
                if (!targetWord.KaraokeTimingLocked)
                {
                    targetWord.Start = TimeSpan.FromSeconds(sourceWord.Start);
                    targetWord.End = TimeSpan.FromSeconds(sourceWord.End);
                }
                var syllables = targetWord.Children.Where(child => child.Type == LyricSegmentType.Syllable).ToArray();
                for (var syllableIndex = 0;
                     syllableIndex < Math.Min(syllables.Length, sourceWord.Syllables.Count); syllableIndex++)
                {
                    if (!syllables[syllableIndex].KaraokeTimingLocked)
                    {
                        syllables[syllableIndex].Start = TimeSpan.FromSeconds(sourceWord.Syllables[syllableIndex].Start);
                        syllables[syllableIndex].End = TimeSpan.FromSeconds(sourceWord.Syllables[syllableIndex].End);
                    }
                }
            }
        }
        return document;
    }

    private static StagePresentationLine ToPresentation(LyricSegment line) => new(
        line.Start.TotalSeconds, line.End.TotalSeconds, line.Text,
        line.Children.Where(child => child.Type == LyricSegmentType.Word).Select(word =>
            new StagePresentationWord(word.Start.TotalSeconds, word.End.TotalSeconds, word.Text,
                word.Children.Where(child => child.Type == LyricSegmentType.Syllable).Select(syllable =>
                    new StagePresentationSyllable(syllable.Start.TotalSeconds, syllable.End.TotalSeconds,
                        syllable.Text, syllable.Confidence ?? 0, syllable.KaraokeTimingLocked)).ToArray(),
                word.Confidence ?? 0, word.KaraokeTimingLocked)).ToArray(),
        line.HoldAfterMilliseconds is { } hold ? hold / 1000d : null, line.StageEffect.ToString(),
        line.VoiceLane, line.VoiceLabel ?? string.Empty, line.KaraokeTimingLocked);

    private static LyricsEditorDocument CloneDocument(LyricsEditorDocument source) => new()
    {
        SongId = source.SongId,
        VersionId = source.VersionId,
        Revision = source.Revision,
        Status = source.Status,
        AnalysisRunId = source.AnalysisRunId,
        PipelineVersion = source.PipelineVersion,
        ModelVersion = source.ModelVersion,
        AudioSha256 = source.AudioSha256,
        HasUltraStarTimingHeritage = source.HasUltraStarTimingHeritage,
        CreatedAt = source.CreatedAt,
        ModifiedAt = source.ModifiedAt,
        Lines = source.Lines.Select(CloneSegment).ToList()
    };

    private static LyricSegment CloneSegment(LyricSegment source) => new()
    {
        Id = source.Id,
        ParentId = source.ParentId,
        Type = source.Type,
        Start = source.Start,
        End = source.End,
        Text = source.Text,
        Origin = source.Origin,
        Confidence = source.Confidence,
        IsAutomaticallyGenerated = source.IsAutomaticallyGenerated,
        IsManuallyAdjusted = source.IsManuallyAdjusted,
        KaraokeTimingLocked = source.KaraokeTimingLocked,
        IsReviewed = source.IsReviewed,
        RequiresReview = source.RequiresReview,
        AnalysisRunId = source.AnalysisRunId,
        ModelVersion = source.ModelVersion,
        OriginalStart = source.OriginalStart,
        OriginalEnd = source.OriginalEnd,
        OriginalText = source.OriginalText,
        HoldAfterMilliseconds = source.HoldAfterMilliseconds,
        StageEffect = source.StageEffect,
        VoiceLane = source.VoiceLane,
        VoiceLabel = source.VoiceLabel,
        Children = source.Children.Select(CloneSegment).ToList()
    };
}
