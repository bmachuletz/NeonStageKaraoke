using System.Text.Json;
using System.Text.Json.Nodes;
using Karaoke.Contracts;
using Karaoke.Editor.Core;

namespace Karaoke.Server;

/// <summary>
/// Turns the files produced by the alignment pipeline into immutable editor
/// revisions. An alignment is not considered complete until its result has a
/// server-side version that can be reopened after an editor/server restart.
/// </summary>
internal sealed class LyricsAlignmentVersionService(
    LibraryRepository library,
    LyricsVersionRepository versions,
    ChangeFeedService changes)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<LyricsVersionDto> SnapshotAsync(
        Guid songId,
        string analysisRunId,
        CancellationToken cancellationToken)
    {
        var lyrics = await library.ReadLyricsAsync(songId, cancellationToken)
                     ?? throw new InvalidOperationException(
                         $"Das Alignment für Song {songId} enthält keine lesbaren Lyrics.");
        var document = LyricsDocumentImporter.Import(lyrics, analysisRunId, "gpu-aligner");
        var reportJson = await ReadLibraryAlignmentReportAsync(songId, cancellationToken);
        AlignmentPitchEvidence.AttachToSyllables(
            document, AlignmentPitchEvidence.Parse(reportJson));
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var version = await versions.CreateAsync(songId,
            new CreateLyricsVersionRequest(json, analysisRunId, LyricsVersionStatus.InReview,
                AlignmentReportJson: reportJson),
            cancellationToken);
        changes.Publish("lyrics-version-changed");
        return version;
    }

    public async Task<LyricsVersionDto> SnapshotFileAsync(Guid songId, string lrcPath, string alignmentPath,
        string analysisRunId, string modelVersion, CancellationToken cancellationToken,
        LyricsVersionStatus status = LyricsVersionStatus.InReview,
        bool preserveExistingDrafts = false,
        bool hasUltraStarTimingHeritage = false)
    {
        var song = await library.GetAsync(songId, cancellationToken)
                   ?? throw new InvalidOperationException($"Song {songId} wurde nicht gefunden.");
        if (!File.Exists(lrcPath))
            throw new InvalidOperationException($"Alignment-Ausgabe fehlt: {lrcPath}");
        var source = await File.ReadAllLinesAsync(lrcPath, cancellationToken);
        var lyrics = LrcParser.Parse(songId, source, TimeSpan.FromSeconds(song.DurationSeconds));
        if (File.Exists(alignmentPath))
            lyrics = await LibraryRepository.AddSyllableAlignmentAsync(
                lyrics, alignmentPath, cancellationToken);
        if (lyrics.Lines.Count == 0)
            throw new InvalidOperationException("Die Alignment-Ausgabe enthält keine lesbaren Lyrics.");
        var document = LyricsDocumentImporter.Import(lyrics, analysisRunId, modelVersion,
            hasUltraStarTimingHeritage: hasUltraStarTimingHeritage);
        if (File.Exists(alignmentPath))
            await RestoreManualEditorStateAsync(document, alignmentPath, cancellationToken);
        var timingConflicts = TimelineEditing.ValidateLineSequence(document);
        if (timingConflicts.Count > 0)
            status = LyricsVersionStatus.ReviewOverlaps;
        var reportJson = await PersistAlignmentArtifactsAsync(
            songId, alignmentPath, analysisRunId, cancellationToken);
        AlignmentPitchEvidence.AttachToSyllables(
            document, AlignmentPitchEvidence.Parse(reportJson));
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var version = await versions.CreateAsync(songId,
            new CreateLyricsVersionRequest(json, analysisRunId, status,
                AllowTimingConflicts: timingConflicts.Count > 0,
                PreserveExistingDrafts: preserveExistingDrafts, AlignmentReportJson: reportJson),
            cancellationToken);
        changes.Publish("lyrics-version-changed");
        return version;
    }

    public async Task<IReadOnlyDictionary<Guid, string?>> CaptureSourceFingerprintsAsync(
        CancellationToken cancellationToken)
    {
        var songs = await library.SearchAsync(null, 0, 100_000, cancellationToken, includeUnreleased: true);
        var result = new Dictionary<Guid, string?>(songs.Count);
        foreach (var song in songs)
        {
            var lyrics = await library.ReadLyricsAsync(song.Id, cancellationToken);
            result[song.Id] = lyrics is null ? null : JsonSerializer.Serialize(lyrics, JsonOptions);
        }
        return result;
    }

    public async Task<int> SnapshotChangedAsync(
        IReadOnlyDictionary<Guid, string?> before,
        string analysisRunId,
        CancellationToken cancellationToken)
    {
        var songs = await library.SearchAsync(null, 0, 100_000, cancellationToken, includeUnreleased: true);
        var created = 0;
        foreach (var song in songs)
        {
            var lyrics = await library.ReadLyricsAsync(song.Id, cancellationToken);
            if (lyrics is null) continue;
            var fingerprint = JsonSerializer.Serialize(lyrics, JsonOptions);
            if (before.TryGetValue(song.Id, out var previous) && previous == fingerprint) continue;
            await SnapshotAsync(song.Id, analysisRunId, cancellationToken);
            created++;
        }
        return created;
    }

    private async Task<string?> ReadLibraryAlignmentReportAsync(Guid songId, CancellationToken cancellationToken)
    {
        var audio = await library.GetAudioFileAsync(songId, cancellationToken);
        if (audio is null) return null;
        var path = Path.Combine(Path.GetDirectoryName(audio.Value.Path)!,
            Path.GetFileNameWithoutExtension(audio.Value.Path) + ".alignment.json");
        return await ReadAlignmentReportAsync(path, cancellationToken);
    }

    private static async Task<string?> ReadAlignmentReportAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > 20 * 1024 * 1024)
            throw new InvalidDataException($"Alignment-Bericht hat eine ungültige Größe: {path}");
        var json = await File.ReadAllTextAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Alignment-Bericht ist kein JSON-Objekt: {path}");
        return json;
    }

    private async Task<string?> PersistAlignmentArtifactsAsync(Guid songId, string reportPath,
        string analysisRunId, CancellationToken cancellationToken)
    {
        var reportJson = await ReadAlignmentReportAsync(reportPath, cancellationToken);
        if (reportJson is null) return null;
        var root = JsonNode.Parse(reportJson) as JsonObject;
        var candidates = root?["multiple_singing_voices"]?["voice_candidates"] as JsonArray;
        if (root is null || candidates is null || candidates.Count == 0) return reportJson;
        var audio = await library.GetAudioFileAsync(songId, cancellationToken);
        if (audio is null) return reportJson;

        var safeRun = string.Concat(analysisRunId.Select(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));
        var destination = Path.Combine(Path.GetDirectoryName(audio.Value.Path)!,
            ".neonstage-analysis", safeRun);
        Directory.CreateDirectory(destination);
        var persisted = new JsonArray();
        foreach (var candidate in candidates)
        {
            var name = candidate?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(reportPath)!, name));
            if (!File.Exists(source)) continue;
            var target = Path.Combine(destination, Path.GetFileName(source));
            await using (var input = File.OpenRead(source))
            await using (var output = File.Create(target))
                await input.CopyToAsync(output, cancellationToken);
            persisted.Add(Path.GetRelativePath(Path.GetDirectoryName(audio.Value.Path)!, target));
        }
        if (persisted.Count > 0)
        {
            ((JsonObject)root["multiple_singing_voices"]!)["persisted_voice_candidates"] = persisted;
            ((JsonObject)root["multiple_singing_voices"]!)["artifacts_persisted"] = true;
        }
        return root.ToJsonString(JsonOptions);
    }

    private static async Task RestoreManualEditorStateAsync(
        LyricsEditorDocument document, string alignmentPath, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(alignmentPath);
        using var report = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!report.RootElement.TryGetProperty("details", out var details) ||
            details.ValueKind != JsonValueKind.Array)
            return;
        var lineCount = Math.Min(document.Lines.Count, details.GetArrayLength());
        for (var lineIndex = 0; lineIndex < lineCount; lineIndex++)
        {
            var detail = details[lineIndex];
            var line = document.Lines[lineIndex];
            if (Boolean(detail, "manual_adjusted"))
            {
                SetTiming(line, detail, "manual_editor_start", "manual_editor_end");
                MarkManual(line);
            }
            if (!detail.TryGetProperty("words", out var words) || words.ValueKind != JsonValueKind.Array)
                continue;
            var wordCount = Math.Min(line.Children.Count, words.GetArrayLength());
            for (var wordIndex = 0; wordIndex < wordCount; wordIndex++)
            {
                var sourceWord = words[wordIndex];
                var word = line.Children[wordIndex];
                if (Boolean(sourceWord, "manual_adjusted") ||
                    Boolean(sourceWord, "editor_word_manual_adjusted"))
                {
                    SetTiming(word, sourceWord, "editor_word_start", "editor_word_end");
                    MarkManual(word);
                }
                if (!sourceWord.TryGetProperty("syllables", out var syllables) ||
                    syllables.ValueKind != JsonValueKind.Array)
                    continue;
                var syllableCount = Math.Min(word.Children.Count, syllables.GetArrayLength());
                for (var syllableIndex = 0; syllableIndex < syllableCount; syllableIndex++)
                {
                    var sourceSyllable = syllables[syllableIndex];
                    if (Boolean(sourceSyllable, "manual_adjusted") ||
                        String(sourceSyllable, "boundary_source") == "manual-editor")
                    {
                        SetTiming(word.Children[syllableIndex], sourceSyllable, "start", "end");
                        MarkManual(word.Children[syllableIndex]);
                    }
                }
            }
        }

        // The generic LRC reader derives a line's display end from the first
        // word of its successor. A protected editor line can deliberately
        // begin before that first word, so restoring its exact start would
        // otherwise leave the preceding automatic display line overlapping
        // it. Reconcile only the automatic neighbour; manual geometry remains
        // authoritative.
        for (var lineIndex = 1; lineIndex < document.Lines.Count; lineIndex++)
        {
            var previous = document.Lines[lineIndex - 1];
            var current = document.Lines[lineIndex];
            if (previous.End <= current.Start) continue;
            if (current.IsManuallyAdjusted && !previous.IsManuallyAdjusted)
                previous.End = current.Start;
            else if (previous.IsManuallyAdjusted && !current.IsManuallyAdjusted)
                current.Start = previous.End;
        }

        static bool Boolean(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) &&
            value.ValueKind is JsonValueKind.True;
        static string? String(JsonElement element, string property) =>
            element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        static void SetTiming(LyricSegment segment, JsonElement element,
            string startProperty, string endProperty)
        {
            if (element.TryGetProperty(startProperty, out var start) &&
                start.ValueKind == JsonValueKind.Number)
                segment.Start = TimeSpan.FromSeconds(start.GetDouble());
            if (element.TryGetProperty(endProperty, out var end) &&
                end.ValueKind == JsonValueKind.Number)
                segment.End = TimeSpan.FromSeconds(end.GetDouble());
        }
        static void MarkManual(LyricSegment segment)
        {
            segment.IsManuallyAdjusted = true;
            segment.Origin = SegmentOrigin.ManuallyAdjusted;
        }
    }
}
