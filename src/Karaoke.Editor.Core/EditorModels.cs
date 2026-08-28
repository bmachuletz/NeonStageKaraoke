using System.Text.Json.Serialization;

namespace Karaoke.Editor.Core;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LyricSegmentType { Line, Word, Syllable, Phoneme }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SegmentOrigin
{
    ImportedLineLyrics,
    GeneratedByAi,
    ManuallyCreated,
    ManuallyAdjusted,
    DerivedFromParent,
    ImportedFromServer,
    ImportedFromUltraStar
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LyricsReviewStatus
{
    Generated,
    NeedsReview,
    InReview,
    Reviewed,
    Approved,
    Published,
    Rejected,
    Superseded
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StageLineEffect { Automatic, EmberBurst, Shatter, Dissolve, Pulse }

public sealed class LyricSegment
{
    public Guid Id { get; init; }
    public Guid? ParentId { get; set; }
    public LyricSegmentType Type { get; init; }
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public string Text { get; set; } = string.Empty;
    public SegmentOrigin Origin { get; set; }
    public double? Confidence { get; set; }
    public bool IsAutomaticallyGenerated { get; init; }
    public bool IsManuallyAdjusted { get; set; }
    /// <summary>A boundary committed in the beat view; automatic projection preserves it exactly.</summary>
    public bool KaraokeTimingLocked { get; set; }
    public bool IsReviewed { get; set; }
    public bool RequiresReview { get; set; }
    public string? AnalysisRunId { get; init; }
    public string? ModelVersion { get; init; }
    public TimeSpan OriginalStart { get; init; }
    public TimeSpan OriginalEnd { get; init; }
    public string OriginalText { get; init; } = string.Empty;
    /// <summary>Optional explicit time the completed line remains visible. Null keeps the automatic stage rule.</summary>
    public int? HoldAfterMilliseconds { get; set; }
    public StageLineEffect StageEffect { get; set; } = StageLineEffect.Automatic;
    /// <summary>
    /// Independent vocal/display lane. Lane 0 is the backwards-compatible
    /// lead lane; lines in different lanes may overlap in time.
    /// </summary>
    public int VoiceLane { get; set; }
    public string? VoiceLabel { get; set; }
    /// <summary>Optional musical evidence. Singer identity remains null until diarization assigns it.</summary>
    public List<PitchNoteEvidence> Notes { get; init; } = [];
    public List<LyricSegment> Children { get; init; } = [];

    public IEnumerable<LyricSegment> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var descendant in child.DescendantsAndSelf())
                yield return descendant;
    }
}

public sealed class LyricsEditorDocument
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid SongId { get; init; }
    public Guid VersionId { get; init; } = Guid.CreateVersion7();
    public long Revision { get; set; }
    public LyricsReviewStatus Status { get; set; } = LyricsReviewStatus.Generated;
    public string? AnalysisRunId { get; init; }
    public string? PipelineVersion { get; init; }
    public string? ModelVersion { get; init; }
    public string? AudioSha256 { get; init; }
    /// <summary>
    /// True when this timing geometry originates from an UltraStar TXT. This
    /// provenance survives manual edits and derivative alignments so the
    /// perception projection never modifies already authored karaoke timing.
    /// </summary>
    public bool HasUltraStarTimingHeritage { get; set; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<LyricSegment> Lines { get; init; } = [];

    public IEnumerable<LyricSegment> Segments => Lines.SelectMany(line => line.DescendantsAndSelf());

    [JsonIgnore]
    public bool UsesUltraStarTiming => HasUltraStarTimingHeritage ||
        AnalysisRunId?.StartsWith("usdb:", StringComparison.OrdinalIgnoreCase) == true ||
        ModelVersion?.Contains("UltraStar", StringComparison.OrdinalIgnoreCase) == true ||
        Segments.Any(segment => segment.Origin == SegmentOrigin.ImportedFromUltraStar);
}
