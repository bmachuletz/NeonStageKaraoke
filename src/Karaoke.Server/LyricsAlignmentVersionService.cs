using System.Text.Json;
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
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var reportJson = await ReadLibraryAlignmentReportAsync(songId, cancellationToken);
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
        bool preserveExistingDrafts = false)
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
        var document = LyricsDocumentImporter.Import(lyrics, analysisRunId, modelVersion);
        var json = JsonSerializer.Serialize(document, JsonOptions);
        var reportJson = await ReadAlignmentReportAsync(alignmentPath, cancellationToken);
        var version = await versions.CreateAsync(songId,
            new CreateLyricsVersionRequest(json, analysisRunId, status,
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
}
