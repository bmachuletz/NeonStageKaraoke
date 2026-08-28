using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Karaoke.Contracts;
using Karaoke.Editor.Core;

namespace Karaoke.Server;

public sealed record SongRealignmentStatus(bool IsRunning, Guid? JobId, Guid? SongId, string? SongTitle,
    int Percent, string Message, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, IReadOnlyList<string> RecentOutput, IReadOnlyList<Guid>? SongIds = null);

internal sealed class SongRealignmentService(IWebHostEnvironment environment,
    LibraryRepository library, LyricsAlignmentVersionService alignmentVersions, LyricsVersionRepository versions,
    ILogger<SongRealignmentService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private SongRealignmentStatus _status = new(false, null, null, null, 0, "Bereit", null, null, null, []);

    public SongRealignmentStatus GetStatus() { lock (_gate) return _status; }

    public async Task<bool?> TryStartAsync(Guid songId, SongRealignmentRequest? request,
        CancellationToken cancellationToken)
    {
        var song = await library.GetAsync(songId, cancellationToken);
        var audio = await library.GetAudioFileAsync(songId, cancellationToken);
        if (song is null || audio is null) return null;
        var selected = request ?? new SongRealignmentRequest();
        ValidateSelection(selected);
        lock (_gate)
        {
            if (_status.IsRunning) return false;
            _status = new(true, Guid.CreateVersion7(), songId, $"{song.Title} · {song.Artist}", 1,
                "GPU-Neuausrichtung wird gestartet …", DateTimeOffset.UtcNow, null, null, []);
        }
        _ = Task.Run(() => RunAsync(songId, audio.Value.Path, selected));
        return true;
    }

    public bool TryStartAll(SongRealignmentRequest? request)
    {
        var selected = request ?? new SongRealignmentRequest();
        ValidateSelection(selected);
        lock (_gate)
        {
            if (_status.IsRunning) return false;
            _status = new(true, Guid.CreateVersion7(), null, "Gesamte Bibliothek", 1,
                "GPU-Neuausrichtung der gesamten Bibliothek wird gestartet …", DateTimeOffset.UtcNow, null, null, []);
        }
        _ = Task.Run(() => RunAsync(null, null, selected with { SourceVersionId = null }));
        return true;
    }

    public bool TryStartSelection(SongSelectionRealignmentRequest request)
    {
        var songIds = request.SongIds.Distinct().ToArray();
        if (songIds.Length is 0 or > 100)
            throw new ArgumentException("Die Songauswahl muss zwischen 1 und 100 Titel enthalten.");
        var selected = request.Alignment with { SourceVersionId = null };
        ValidateSelection(selected);
        lock (_gate)
        {
            if (_status.IsRunning) return false;
            _status = new(true, Guid.CreateVersion7(), null,
                $"{songIds.Length} ausgewählte Songs", 1,
                "GPU-Neuausrichtung der Auswahl wird gestartet …", DateTimeOffset.UtcNow,
                null, null, [], songIds);
        }
        _ = Task.Run(() => RunAsync(null, null, selected, songIds));
        return true;
    }

    private async Task RunAsync(Guid? songId, string? audioPath, SongRealignmentRequest? request,
        IReadOnlyList<Guid>? selectedSongIds = null)
    {
        var output = new List<string>();
        try
        {
            Guid jobId;
            lock (_gate) jobId = _status.JobId
                                ?? throw new InvalidOperationException("Dem Alignment fehlt eine Job-ID.");
            if (songId is not null && audioPath is not null && request is not null)
            {
                var result = await RunSongVariantsAsync(
                    songId.Value, audioPath, request, jobId, output, 3, 95, continueOnVariantError: true);
                if (result.Created == 0)
                    throw new InvalidOperationException("Keine der ausgewählten Alignment-Varianten konnte erstellt werden.");
                lock (_gate) _status = _status with { IsRunning = false, Percent = 100,
                    Message = result.Failed > 0
                        ? $"{result.Created} Alignment-{(result.Created == 1 ? "Variante ist" : "Varianten sind")} bereit; " +
                          $"{result.Failed} {(result.Failed == 1 ? "Variante ist" : "Varianten sind")} fehlgeschlagen."
                        : result.Created == 1
                        ? "Die ausgewählte Alignment-Variante ist als Review-Stand bereit."
                        : $"Alle {result.Created} ausgewählten Alignment-Varianten sind als Review-Stände bereit.",
                    FinishedAt = DateTimeOffset.UtcNow, ExitCode = 0, RecentOutput = output.ToArray() };
                return;
            }

            if (request is null)
                throw new InvalidOperationException("Für das Bibliotheks-Alignment fehlt die Variantenauswahl.");
            var libraryResult = await RunLibraryVariantsAsync(
                request, jobId, output, selectedSongIds);
            lock (_gate) _status = _status with { IsRunning = false, Percent = 100,
                Message = $"{(selectedSongIds is null ? "Bibliotheks-Alignment" : "Auswahl-Alignment")} beendet: " +
                          $"{libraryResult.Created} Review-Versionen, " +
                          $"{libraryResult.Failed} Fehler, {libraryResult.Skipped} Songs übersprungen.",
                FinishedAt = DateTimeOffset.UtcNow,
                ExitCode = libraryResult.Created > 0 || libraryResult.Failed == 0 ? 0 : -1,
                RecentOutput = output.ToArray() };
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Neuausrichtung für Song {SongId} fehlgeschlagen", songId);
            Add(output, exception.Message);
            lock (_gate) _status = _status with { IsRunning = false, Percent = 100,
                Message = "Neuausrichtung fehlgeschlagen.", FinishedAt = DateTimeOffset.UtcNow,
                ExitCode = -1, RecentOutput = output.ToArray() };
        }
    }

    private async Task<VariantBatchResult> RunLibraryVariantsAsync(
        SongRealignmentRequest request, Guid jobId, List<string> output,
        IReadOnlyList<Guid>? selectedSongIds = null)
    {
        var allSongs = await library.SearchAsync(
            null, 0, 100_000, CancellationToken.None, includeUnreleased: true);
        IReadOnlyList<SongDto> scopedSongs = allSongs;
        var skipped = 0;
        if (selectedSongIds is not null)
        {
            var songsById = allSongs.ToDictionary(song => song.Id);
            scopedSongs = selectedSongIds
                .Where(songsById.ContainsKey)
                .Select(id => songsById[id])
                .ToArray();
            skipped += selectedSongIds.Count - scopedSongs.Count;
        }
        var songs = scopedSongs.Where(song => song.HasLyrics).ToList();
        skipped += scopedSongs.Count - songs.Count;
        if (request.IncludeBasicPitchAb)
        {
            var eligible = new List<SongDto>();
            foreach (var song in songs)
            {
                if (await HasUltraStarHeritageAsync(song.Id))
                {
                    skipped++;
                    Add(output, $"Übersprungen: {song.Title} · {song.Artist} – UltraStar-Timing ist für den Basic-Pitch-Test ausgeschlossen.");
                    continue;
                }
                eligible.Add(song);
                if (request.MaximumSongs is { } maximum && eligible.Count >= maximum) break;
            }
            songs = eligible;
        }
        else if (request.MaximumSongs is { } maximum)
            songs = songs.Take(maximum).ToList();
        var created = 0;
        var failed = 0;
        Add(output, $"{(selectedSongIds is null ? "Bibliothek" : "Auswahl")}: " +
                    $"{songs.Count} geeignete Songs, {skipped} übersprungen.");
        for (var index = 0; index < songs.Count; index++)
        {
            var song = songs[index];
            var audio = await library.GetAudioFileAsync(song.Id, CancellationToken.None);
            if (audio is null)
            {
                skipped++;
                Add(output, $"Übersprungen: {song.Title} · {song.Artist} – Audiodatei fehlt.");
                continue;
            }
            var progressStart = 3 + (int)Math.Floor(92d * index / Math.Max(1, songs.Count));
            var progressEnd = 3 + (int)Math.Floor(92d * (index + 1) / Math.Max(1, songs.Count));
            Set(progressStart, $"Song {index + 1}/{songs.Count}: {song.Title} · {song.Artist}");
            var result = await RunSongVariantsAsync(
                song.Id, audio.Value.Path, request with { SourceVersionId = null }, jobId, output,
                progressStart, Math.Max(progressStart + 1, progressEnd), continueOnVariantError: true,
                scope: $"{index + 1}/{songs.Count}");
            created += result.Created;
            failed += result.Failed;
        }
        return new VariantBatchResult(created, failed, skipped);
    }

    private async Task<VariantBatchResult> RunSongVariantsAsync(Guid songId, string audioPath,
        SongRealignmentRequest request, Guid jobId, List<string> output,
        int progressStart, int progressEnd, bool continueOnVariantError, string? scope = null)
    {
        var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));
        var alignScript = Path.Combine(root, "scripts", "linux", "align-library.sh");
        var recognizeScript = Path.Combine(root, "scripts", "linux", "recognize-song-lyrics.sh");
        var basePath = Path.Combine(Path.GetDirectoryName(audioPath)!, Path.GetFileNameWithoutExtension(audioPath));
        var originalLyrics = basePath + ".pre-align.lrc";
        var temporaryRoot = Path.Combine(
            Path.GetTempPath(), $"neonstage-dual-alignment-{jobId:N}-{songId:N}");
        var variantCount = Convert.ToInt32(request.IncludeEditorBasis)
                           + Convert.ToInt32(request.IncludeOriginalLyrics)
                           + Convert.ToInt32(request.IncludeResearchShadow)
                           + Convert.ToInt32(request.IncludeEditorGuidance)
                           + (request.IncludeBasicPitchAb ? 2 : 0);
        var variantIndex = 0;
        var created = 0;
        var failed = 0;
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            if (request.IncludeBasicPitchAb)
            {
                var sourceVersion = request.SourceVersionId is { } abVersionId
                    ? await versions.GetAsync(songId, abVersionId, CancellationToken.None)
                    : await versions.GetLatestDraftAsync(songId, CancellationToken.None)
                      ?? await versions.GetRuntimeAsync(songId, CancellationToken.None);
                if (sourceVersion is null)
                    throw new InvalidOperationException("Für den Basic-Pitch-A/B-Test existiert kein gespeicherter Editor-Stand.");
                var document = JsonSerializer.Deserialize<LyricsEditorDocument>(
                                   sourceVersion.DocumentJson, JsonOptions)
                               ?? throw new InvalidOperationException("Der Editor-Stand für den Basic-Pitch-A/B-Test ist nicht lesbar.");
                if (document.UsesUltraStarTiming)
                    throw new InvalidOperationException(
                        "Basic-Pitch-A/B ist für Songs mit UltraStar-Timing-Herkunft ausgeschlossen.");
                var editorLyrics = Path.Combine(temporaryRoot, "basic-pitch-ab-basis.lrc");
                await File.WriteAllTextAsync(editorLyrics,
                    LyricsDocumentLrcExporter.ToEnhancedLrc(document), new UTF8Encoding(false));
                string? baselineLyrics = null;
                string? baselineReport = null;
                foreach (var (profile, label, analysisType) in new[]
                         {
                             ("standard", "A · unveränderte Kontrolle", "basic-pitch-ab-control"),
                             ("basic-pitch-postprocess", "B · Basic-Pitch-Evidenz", "basic-pitch-ab-treatment")
                         })
                {
                    var range = VariantRange(progressStart, progressEnd, variantIndex++, variantCount);
                    try
                    {
                        Set(range.Start, Prefix(scope, $"Basic Pitch A/B: {label} wird berechnet …"));
                        var outputDirectory = Path.Combine(temporaryRoot, $"basic-pitch-{profile}");
                        var arguments = new List<string>
                        {
                            "--force", "--library", Path.GetDirectoryName(audioPath)!,
                            "--match", Path.GetFileName(audioPath), "--lyrics-source",
                            profile == "standard" ? editorLyrics : baselineLyrics
                                ?? throw new InvalidOperationException("Kontrollversion A fehlt als Basis für B."),
                            "--output-dir", outputDirectory, "--reuse-stems", "--no-reindex"
                        };
                        if (profile != "standard")
                        {
                            arguments.Add("--profile");
                            arguments.Add(profile);
                            arguments.Add("--baseline-report");
                            arguments.Add(baselineReport
                                ?? throw new InvalidOperationException("Kontrollreport A fehlt als Basis für B."));
                        }
                        await RunProcessAsync(root, alignScript, arguments, output,
                            percent => MapProgress(percent, range.Start, range.End));
                        var result = Path.Combine(outputDirectory, Path.GetFileName(basePath) + ".lrc");
                        var report = Path.Combine(outputDirectory,
                            Path.GetFileName(basePath) + ".alignment.json");
                        if (profile == "standard")
                        {
                            baselineLyrics = result;
                            baselineReport = report;
                        }
                        var saved = await alignmentVersions.SnapshotFileAsync(
                            songId, result, report,
                            $"dual-{jobId:N}:{analysisType}:r{sourceVersion.Revision}", analysisType,
                            CancellationToken.None, LyricsVersionStatus.Generated,
                            preserveExistingDrafts: true, hasUltraStarTimingHeritage: false);
                        Add(output, $"Basic Pitch {label} als Revision {saved.Revision} gespeichert.");
                        created++;
                    }
                    catch (Exception exception) when (continueOnVariantError)
                    {
                        failed++;
                        Add(output, Prefix(scope, $"Basic Pitch {label} fehlgeschlagen: {exception.Message}"));
                    }
                }
            }

            if (request.IncludeEditorBasis)
            {
                var range = VariantRange(progressStart, progressEnd, variantIndex++, variantCount);
                try
                {
                    Set(range.Start, Prefix(scope, "Variante 1.2: letzter Editor-Stand wird vorbereitet …"));
                    var sourceVersion = request.SourceVersionId is { } versionId
                        ? await versions.GetAsync(songId, versionId, CancellationToken.None)
                        : await versions.GetLatestDraftAsync(songId, CancellationToken.None)
                          ?? await versions.GetRuntimeAsync(songId, CancellationToken.None);
                    if (sourceVersion is null)
                        throw new InvalidOperationException("Für den Song existiert kein gespeicherter Editor-Stand.");
                    var document = JsonSerializer.Deserialize<LyricsEditorDocument>(
                                       sourceVersion.DocumentJson, JsonOptions)
                                   ?? throw new InvalidOperationException("Der letzte Editor-Stand ist nicht lesbar.");
                    var editorLyrics = Path.Combine(temporaryRoot, "editor-basis.lrc");
                    await File.WriteAllTextAsync(editorLyrics,
                        LyricsDocumentLrcExporter.ToEnhancedLrc(document), new UTF8Encoding(false));
                    var editorOutput = Path.Combine(temporaryRoot, "editor-result");
                    Set(range.Start, Prefix(scope,
                        $"Variante 1.2: Revision {sourceVersion.Revision} erhält IPA-Mikroanalyse und Pitch-/Voicing-Ausklänge …"));
                    await RunProcessAsync(root, alignScript,
                        ["--force", "--library", Path.GetDirectoryName(audioPath)!, "--match", Path.GetFileName(audioPath),
                         "--lyrics-source", editorLyrics, "--output-dir", editorOutput,
                         "--reuse-stems", "--no-reindex"], output,
                        percent => MapProgress(percent, range.Start, range.End));
                    var result = Path.Combine(editorOutput, Path.GetFileName(basePath) + ".lrc");
                    var report = Path.Combine(editorOutput, Path.GetFileName(basePath) + ".alignment.json");
                    var version = await alignmentVersions.SnapshotFileAsync(songId, result, report,
                        $"dual-{jobId:N}:editor-basis:r{sourceVersion.Revision}",
                        "editor-basis-acoustic-realignment", CancellationToken.None,
                        LyricsVersionStatus.Generated, preserveExistingDrafts: true,
                        hasUltraStarTimingHeritage: document.UsesUltraStarTiming);
                    Add(output, $"Variante 1.2 als Revision {version.Revision} gespeichert.");
                    created++;
                }
                catch (Exception exception) when (continueOnVariantError)
                {
                    failed++;
                    Add(output, Prefix(scope, $"Variante 1.2 fehlgeschlagen: {exception.Message}"));
                }
            }

            if (request.IncludeOriginalLyrics)
            {
                var range = VariantRange(progressStart, progressEnd, variantIndex++, variantCount);
                try
                {
                    if (!File.Exists(originalLyrics))
                        throw new InvalidOperationException("Die ursprüngliche LRCLIB/pre-align-Lyrics-Datei fehlt.");
                    var originalOutput = Path.Combine(temporaryRoot, "lrclib-result");
                    Set(range.Start, Prefix(scope,
                        "Variante 2: LRCLIB-Text wird auf das Volltranskript-Timing übertragen …"));
                    await RunProcessAsync(root, recognizeScript,
                        ["--audio", audioPath, "--canonical", originalLyrics, "--output-dir", originalOutput,
                         "--no-reindex", "--language", "auto"], output,
                        percent => MapProgress(percent, range.Start, range.End));
                    var result = Path.Combine(originalOutput, Path.GetFileName(basePath) + ".lrc");
                    var report = Path.Combine(originalOutput, Path.GetFileName(basePath) + ".alignment.json");
                    var version = await alignmentVersions.SnapshotFileAsync(songId, result, report,
                        $"dual-{jobId:N}:lrclib-full-transcript", "lrclib-canonical-on-full-transcript",
                        CancellationToken.None, LyricsVersionStatus.Generated, preserveExistingDrafts: true);
                    Add(output, $"Variante 2 als Revision {version.Revision} gespeichert.");
                    created++;
                }
                catch (Exception exception) when (continueOnVariantError)
                {
                    failed++;
                    Add(output, Prefix(scope, $"Variante 2 fehlgeschlagen: {exception.Message}"));
                }
            }

            if (request.IncludeEditorGuidance)
            {
                var range = VariantRange(progressStart, progressEnd, variantIndex++, variantCount);
                try
                {
                    Set(range.Start, Prefix(scope,
                        "Referenzgestütztes Realignment: manuelle Timing-Anker werden vorbereitet …"));
                    var sourceVersion = request.SourceVersionId is { } versionId
                        ? await versions.GetAsync(songId, versionId, CancellationToken.None)
                        : await versions.GetLatestDraftAsync(songId, CancellationToken.None)
                          ?? await versions.GetRuntimeAsync(songId, CancellationToken.None);
                    if (sourceVersion is null)
                        throw new InvalidOperationException(
                            "Für den Song existiert kein gespeicherter Editor-Stand.");
                    var document = JsonSerializer.Deserialize<LyricsEditorDocument>(
                                       sourceVersion.DocumentJson, JsonOptions)
                                   ?? throw new InvalidOperationException(
                                       "Der letzte Editor-Stand ist nicht lesbar.");
                    var editorLyrics = Path.Combine(temporaryRoot, "editor-guided-basis.lrc");
                    await File.WriteAllTextAsync(editorLyrics,
                        LyricsDocumentLrcExporter.ToEnhancedLrc(document), new UTF8Encoding(false));
                    var guidedOutput = Path.Combine(temporaryRoot, "editor-guided-result");
                    Set(range.Start, Prefix(scope,
                        $"Referenzgestütztes Realignment: Revision {sourceVersion.Revision} kalibriert unbearbeitete Bereiche …"));
                    await RunProcessAsync(root, alignScript,
                        ["--force", "--library", Path.GetDirectoryName(audioPath)!,
                         "--match", Path.GetFileName(audioPath),
                         "--lyrics-source", editorLyrics, "--output-dir", guidedOutput,
                         "--reuse-stems", "--no-reindex", "--profile", "editor-guided"], output,
                        percent => MapProgress(percent, range.Start, range.End));
                    var result = Path.Combine(guidedOutput, Path.GetFileName(basePath) + ".lrc");
                    var report = Path.Combine(
                        guidedOutput, Path.GetFileName(basePath) + ".alignment.json");
                    var version = await alignmentVersions.SnapshotFileAsync(
                        songId, result, report,
                        $"dual-{jobId:N}:editor-guided:r{sourceVersion.Revision}",
                        "manual-anchor-guided-acoustic-realignment",
                        CancellationToken.None, LyricsVersionStatus.Generated,
                        preserveExistingDrafts: true,
                        hasUltraStarTimingHeritage: document.UsesUltraStarTiming);
                    Add(output,
                        $"Referenzgestütztes Realignment als Revision {version.Revision} gespeichert.");
                    created++;
                }
                catch (Exception exception) when (continueOnVariantError)
                {
                    failed++;
                    Add(output, Prefix(scope,
                        $"Referenzgestütztes Realignment fehlgeschlagen: {exception.Message}"));
                }
            }

            if (request.IncludeResearchShadow)
            {
                var range = VariantRange(progressStart, progressEnd, variantIndex, variantCount);
                try
                {
                    if (!File.Exists(originalLyrics))
                        throw new InvalidOperationException(
                            "Für den Forschungs-Schattenlauf fehlt die unveränderte pre-align-Lyrics-Datei.");
                    var shadowOutput = Path.Combine(temporaryRoot, "research-shadow-result");
                    Set(range.Start, Prefix(scope,
                        "Forschungs-Schattenlauf: Originaltext wird ohne Editor- oder Handkorrekturen neu ausgerichtet …"));
                    await RunProcessAsync(root, alignScript,
                        ["--force", "--library", Path.GetDirectoryName(audioPath)!,
                         "--match", Path.GetFileName(audioPath),
                         "--lyrics-source", originalLyrics, "--output-dir", shadowOutput,
                         "--reuse-stems", "--no-reindex", "--profile", "research-shadow"], output,
                        percent => MapProgress(percent, range.Start, range.End));
                    var result = Path.Combine(shadowOutput, Path.GetFileName(basePath) + ".lrc");
                    var report = Path.Combine(
                        shadowOutput, Path.GetFileName(basePath) + ".alignment.json");
                    var version = await alignmentVersions.SnapshotFileAsync(
                        songId, result, report,
                        $"dual-{jobId:N}:research-shadow:fresh-original",
                        "research-shadow-joint-phoneme-pitch-duration",
                        CancellationToken.None, LyricsVersionStatus.Generated,
                        preserveExistingDrafts: true);
                    Add(output,
                        $"Forschungs-Schattenlauf als Revision {version.Revision} gespeichert.");
                    created++;
                }
                catch (Exception exception) when (continueOnVariantError)
                {
                    failed++;
                    Add(output, Prefix(scope,
                        $"Forschungs-Schattenlauf fehlgeschlagen: {exception.Message}"));
                }
            }
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch (IOException exception) { logger.LogWarning(exception, "Temporäre Alignment-Ausgaben konnten nicht vollständig entfernt werden."); }
        }
        return new VariantBatchResult(created, failed, 0);
    }

    private async Task RunProcessAsync(string workingDirectory, string script,
        IReadOnlyList<string> arguments, List<string> output, Func<int, int>? progressMap = null)
    {
        var start = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) => Add(output, args.Data, progressMap);
        process.ErrorDataReceived += (_, args) => Add(output, args.Data, progressMap);
        if (!process.Start()) throw new InvalidOperationException("GPU-Alignment konnte nicht gestartet werden.");
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"GPU-Pipeline wurde mit Code {process.ExitCode} beendet.");
    }

    private void Add(List<string> output, string? line, Func<int, int>? progressMap = null)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            output.Add(line); if (output.Count > 120) output.RemoveAt(0);
            var percent = _status.Percent;
            var marker = System.Text.RegularExpressions.Regex.Match(line, @"\b(?<value>\d{1,3})%(?!\d)");
            if (marker.Success)
            {
                var reported = int.Parse(marker.Groups["value"].Value);
                var mapped = progressMap?.Invoke(reported) ?? reported;
                percent = Math.Clamp(mapped, percent, 95);
            }
            _status = _status with { Message = line.Trim(), Percent = percent, RecentOutput = output.ToArray() };
        }
    }

    private void Set(int percent, string message) { lock (_gate) _status = _status with { Percent = percent, Message = message }; }

    private static void ValidateSelection(SongRealignmentRequest request)
    {
        if (!request.IncludeEditorBasis && !request.IncludeOriginalLyrics
                && !request.IncludeResearchShadow && !request.IncludeEditorGuidance
                && !request.IncludeBasicPitchAb)
            throw new ArgumentException("Mindestens eine Alignment-Variante muss ausgewählt sein.");
        if (request.MaximumSongs is <= 0 or > 100)
            throw new ArgumentException("MaximumSongs muss zwischen 1 und 100 liegen.");
    }

    private async Task<bool> HasUltraStarHeritageAsync(Guid songId)
    {
        var version = await versions.GetLatestDraftAsync(songId, CancellationToken.None)
                      ?? await versions.GetRuntimeAsync(songId, CancellationToken.None);
        if (version is null) return false;
        try
        {
            return JsonSerializer.Deserialize<LyricsEditorDocument>(
                version.DocumentJson, JsonOptions)?.UsesUltraStarTiming == true;
        }
        catch (JsonException) { return false; }
    }

    private static (int Start, int End) VariantRange(
        int start, int end, int index, int count) =>
        (start + (int)Math.Floor((end - start) * (double)index / count),
         start + (int)Math.Floor((end - start) * (double)(index + 1) / count));

    private static int MapProgress(int percent, int start, int end) =>
        start + (int)Math.Round((end - start) * Math.Clamp(percent, 0, 100) / 100d);

    private static string Prefix(string? scope, string message) =>
        string.IsNullOrWhiteSpace(scope) ? message : $"[{scope}] {message}";

    private readonly record struct VariantBatchResult(int Created, int Failed, int Skipped);

    public Task<Karaoke.Contracts.LyricsVersionDto> SnapshotCurrentAsync(Guid songId,
        CancellationToken cancellationToken) =>
        alignmentVersions.SnapshotAsync(songId, $"manual-snapshot-{Guid.CreateVersion7():N}", cancellationToken);
}
