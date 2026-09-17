using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Karaoke.Contracts;
using Karaoke.Editor.Core;

namespace Karaoke.Server;

public sealed record SongRealignmentStatus(bool IsRunning, Guid? JobId, Guid? SongId, string? SongTitle,
    int Percent, string Message, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, IReadOnlyList<string> RecentOutput, IReadOnlyList<Guid>? SongIds = null);

/// <summary>
/// Product alignment orchestration. There is deliberately one forced-alignment
/// implementation: EasyAligner Direct. Full transcription is owned by
/// <see cref="SongLyricsRecognitionService"/> and feeds its recognized text back
/// through the same EasyAligner entry point.
/// </summary>
internal sealed class SongRealignmentService(IWebHostEnvironment environment,
    LibraryRepository library, LyricsAlignmentVersionService alignmentVersions,
    LyricsVersionRepository versions, ReplacementLyricsService replacementLyrics,
    ILogger<SongRealignmentService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();
    private SongRealignmentStatus _status = new(false, null, null, null, 0, "Bereit", null, null, null, []);
    private CancellationTokenSource? _runCancellation;
    private Process? _activeProcess;

    public SongRealignmentStatus GetStatus() { lock (_gate) return _status; }

    public async Task<bool?> TryStartAsync(Guid songId, SongRealignmentRequest? request,
        CancellationToken cancellationToken)
    {
        var song = await library.GetAsync(songId, cancellationToken);
        var audio = await library.GetAudioFileAsync(songId, cancellationToken);
        if (song is null || audio is null) return null;
        var selected = request ?? new SongRealignmentRequest();
        Validate(selected);
        if (!Begin(songId, $"{song.Title} · {song.Artist}", null,
                "EasyAligner wird gestartet …", out var jobId, out var runCancellation)) return false;
        _ = Task.Run(() => RunSingleAsync(songId, audio.Value.Path, selected.SourceVersionId,
            jobId, runCancellation));
        return true;
    }

    internal async Task<bool?> TryStartReplacementLyricsAsync(Guid songId,
        RetrievedLyricsSelection selected, CancellationToken cancellationToken)
    {
        if (selected.IsFullTranscript)
            throw new ArgumentException("Volltranskripte gehören in den Transkriptions-Worker.");
        var song = await library.GetAsync(songId, cancellationToken);
        var audio = await library.GetAudioFileAsync(songId, cancellationToken);
        if (song is null || audio is null) return null;
        if (!Begin(songId, $"{song.Title} · {song.Artist}", null,
                $"Neue Lyrics von {selected.Source} werden für EasyAligner vorbereitet …",
                out var jobId, out var runCancellation)) return false;
        _ = Task.Run(() => RunReplacementAsync(songId, audio.Value.Path, selected,
            jobId, runCancellation));
        return true;
    }

    public bool TryStartAll(SongRealignmentRequest? request)
    {
        var selected = request ?? new SongRealignmentRequest();
        Validate(selected);
        if (!Begin(null, "Gesamte Bibliothek", null,
                "EasyAligner für die gesamte Bibliothek wird gestartet …",
                out var jobId, out var cancellation)) return false;
        _ = Task.Run(() => RunLibraryAsync(selected, jobId, null,
            refreshLyricsAutomatically: true, cancellation));
        return true;
    }

    public bool TryStartSelection(SongSelectionRealignmentRequest request)
    {
        var songIds = request.SongIds.Distinct().ToArray();
        if (songIds.Length is 0 or > 100)
            throw new ArgumentException("Die Songauswahl muss zwischen 1 und 100 Titel enthalten.");
        Validate(request.Alignment);
        if (!Begin(null, $"{songIds.Length} ausgewählte Songs", songIds,
                "EasyAligner für die Auswahl wird gestartet …",
                out var jobId, out var cancellation)) return false;
        _ = Task.Run(() => RunLibraryAsync(
            request.Alignment with { SourceVersionId = null }, jobId, songIds,
            refreshLyricsAutomatically: false, cancellation));
        return true;
    }

    public bool TryCancel()
    {
        CancellationTokenSource? cancellation;
        Process? process;
        lock (_gate)
        {
            if (!_status.IsRunning) return false;
            cancellation = _runCancellation;
            process = _activeProcess;
            _status = _status with { Message = "EasyAligner wird abgebrochen …" };
        }
        cancellation?.Cancel();
        if (process is not null) _ = Task.Run(() => StopProcessAsync(process));
        return true;
    }

    private bool Begin(Guid? songId, string label, IReadOnlyList<Guid>? songIds, string message,
        out Guid jobId, out CancellationToken cancellation)
    {
        lock (_gate)
        {
            if (_status.IsRunning)
            {
                jobId = default;
                cancellation = default;
                return false;
            }
            _runCancellation?.Dispose();
            _runCancellation = new CancellationTokenSource();
            jobId = Guid.CreateVersion7();
            cancellation = _runCancellation.Token;
            _status = new(true, jobId, songId, label, 1, message,
                DateTimeOffset.UtcNow, null, null, [], songIds);
            return true;
        }
    }

    private async Task RunSingleAsync(Guid songId, string audioPath, Guid? sourceVersionId,
        Guid jobId, CancellationToken cancellationToken)
    {
        var output = new List<string>();
        try
        {
            await AlignSongAsync(songId, audioPath, sourceVersionId, jobId, output,
                3, 95, null, cancellationToken);
            Complete("EasyAligner-Ergebnis ist als neuer Review-Stand bereit.", output, 0);
        }
        catch (OperationCanceledException)
        {
            Add(output, "EasyAligner wurde abgebrochen.");
            Complete("EasyAligner wurde abgebrochen.", output, -2);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "EasyAligner für Song {SongId} fehlgeschlagen", songId);
            Add(output, exception.Message);
            Complete("EasyAligner ist fehlgeschlagen.", output, -1);
        }
    }

    private async Task RunReplacementAsync(Guid songId, string audioPath,
        RetrievedLyricsSelection selected, Guid jobId, CancellationToken cancellationToken)
    {
        var output = new List<string>();
        try
        {
            Set(2, $"Ausgewählte Lyrics von {selected.Source} werden als Alignment-Basis gespeichert …");
            if (!await library.WriteRetrievedLyricsSourceAsync(songId, selected, cancellationToken))
                throw new InvalidOperationException("Der Song wurde beim Speichern der Lyrics nicht gefunden.");
            Add(output, $"Lyrics-Quelle: {selected.Source} #{selected.SourceId} · {selected.Label}");
            await AlignSongAsync(songId, audioPath, null, jobId, output, 3, 95, null, cancellationToken);
            Complete($"Neue Lyrics von {selected.Source} wurden mit EasyAligner ausgerichtet.", output, 0);
        }
        catch (OperationCanceledException)
        {
            Add(output, "EasyAligner-Auftrag wurde abgebrochen.");
            Complete("EasyAligner wurde abgebrochen.", output, -2);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "EasyAligner mit neuen Lyrics für Song {SongId} fehlgeschlagen", songId);
            Add(output, exception.Message);
            Complete("Neue Lyrics konnten nicht ausgerichtet werden.", output, -1);
        }
    }

    private async Task RunLibraryAsync(SongRealignmentRequest request, Guid jobId,
        IReadOnlyList<Guid>? selectedSongIds, bool refreshLyricsAutomatically,
        CancellationToken cancellationToken)
    {
        var output = new List<string>();
        try
        {
            var all = await library.SearchAsync(null, 0, 100_000, CancellationToken.None,
                includeUnreleased: true);
            IReadOnlyList<SongDto> scope = all;
            var skipped = 0;
            if (selectedSongIds is not null)
            {
                var byId = all.ToDictionary(song => song.Id);
                scope = selectedSongIds.Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
                skipped += selectedSongIds.Count - scope.Count;
            }
            var songs = scope.Where(song => song.HasLyrics).ToList();
            skipped += scope.Count - songs.Count;
            if (request.MaximumSongs is { } maximum) songs = songs.Take(maximum).ToList();
            var created = 0;
            var failed = 0;
            Add(output, $"{songs.Count} Songs mit Lyrics, {skipped} übersprungen.");
            for (var index = 0; index < songs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var song = songs[index];
                var audio = await library.GetAudioFileAsync(song.Id, CancellationToken.None);
                if (audio is null) { skipped++; continue; }
                var start = 3 + (int)Math.Floor(92d * index / Math.Max(1, songs.Count));
                var end = 3 + (int)Math.Floor(92d * (index + 1) / Math.Max(1, songs.Count));
                try
                {
                    var scopeLabel = $"{index + 1}/{songs.Count}";
                    if (refreshLyricsAutomatically)
                    {
                        Set(start, Prefix(scopeLabel,
                            "Beste Lyrics-Quelle wird automatisch ermittelt …"));
                        var best = await replacementLyrics.RetrieveBestMatchAsync(song, cancellationToken);
                        if (best is not null)
                        {
                            if (!await library.WriteRetrievedLyricsSourceAsync(
                                    song.Id, best.Lyrics, cancellationToken))
                                throw new InvalidOperationException(
                                    "Der Song wurde beim Speichern der Lyrics nicht gefunden.");
                            Add(output, Prefix(scopeLabel,
                                $"Lyrics automatisch gewählt: {best.Lyrics.Source} " +
                                $"#{best.Lyrics.SourceId} · Treffer {best.Score:F1}% · " +
                                best.Lyrics.Label));
                        }
                        else
                        {
                            Add(output, Prefix(scopeLabel,
                                "Kein geeigneter Provider-Treffer; vorhandene Lyrics-Basis bleibt erhalten."));
                        }
                    }
                    await AlignSongAsync(song.Id, audio.Value.Path, null, jobId, output,
                        start, Math.Max(start + 1, end), scopeLabel, cancellationToken);
                    created++;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failed++;
                    Add(output, $"[{index + 1}/{songs.Count}] fehlgeschlagen: {exception.Message}");
                }
            }
            Complete($"EasyAligner beendet: {created} Review-Versionen, {failed} Fehler, {skipped} übersprungen.",
                output, created > 0 || failed == 0 ? 0 : -1);
        }
        catch (OperationCanceledException)
        {
            Add(output, "EasyAligner-Warteschlange wurde abgebrochen.");
            Complete("EasyAligner wurde abgebrochen.", output, -2);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "EasyAligner-Bibliothekslauf fehlgeschlagen");
            Add(output, exception.Message);
            Complete("EasyAligner-Bibliothekslauf ist fehlgeschlagen.", output, -1);
        }
    }

    private async Task AlignSongAsync(Guid songId, string audioPath, Guid? sourceVersionId,
        Guid jobId, List<string> output, int progressStart, int progressEnd, string? scope,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));
        var script = Path.Combine(root, "scripts", "linux", "align-library.sh");
        var basePath = Path.Combine(Path.GetDirectoryName(audioPath)!, Path.GetFileNameWithoutExtension(audioPath));
        var temporary = Path.Combine(Path.GetTempPath(), $"neonstage-easyaligner-{jobId:N}-{songId:N}");
        Directory.CreateDirectory(temporary);
        try
        {
            var lyricsSource = basePath + ".pre-align.lrc";
            long? sourceRevision = null;
            if (sourceVersionId is { } versionId)
            {
                var source = await versions.GetAsync(songId, versionId, CancellationToken.None)
                    ?? throw new InvalidOperationException("Der gewählte Lyrics-Stand wurde nicht gefunden.");
                var document = JsonSerializer.Deserialize<LyricsEditorDocument>(source.DocumentJson, JsonOptions)
                    ?? throw new InvalidOperationException("Der gewählte Lyrics-Stand ist nicht lesbar.");
                lyricsSource = Path.Combine(temporary, "selected-lyrics.lrc");
                await File.WriteAllTextAsync(lyricsSource, LyricsDocumentLrcExporter.ToEnhancedLrc(document),
                    new UTF8Encoding(false), cancellationToken);
                sourceRevision = source.Revision;
            }
            if (!File.Exists(lyricsSource))
                throw new InvalidOperationException("Für EasyAligner fehlt eine gewählte Lyrics-Quelle.");
            var resultDirectory = Path.Combine(temporary, "result");
            Set(progressStart, Prefix(scope, "EasyAligner: globaler DE/EN-CTC-Pfad wird berechnet …"));
            await RunProcessAsync(root, script,
                ["--force", "--library", Path.GetDirectoryName(audioPath)!, "--match", Path.GetFileName(audioPath),
                 "--lyrics-source", lyricsSource, "--output-dir", resultDirectory,
                 "--reuse-stems", "--no-reindex"], output,
                percent => MapProgress(percent, progressStart, progressEnd), cancellationToken);
            var result = Path.Combine(resultDirectory, Path.GetFileName(basePath) + ".lrc");
            var report = Path.Combine(resultDirectory, Path.GetFileName(basePath) + ".alignment.json");
            var version = await alignmentVersions.SnapshotFileAsync(songId, result, report,
                $"easyaligner-{jobId:N}" + (sourceRevision is null ? ":lyrics-source" : $":r{sourceRevision}"),
                "easyaligner-global-viterbi-direct", CancellationToken.None,
                LyricsVersionStatus.Generated, preserveExistingDrafts: true);
            Add(output, Prefix(scope, $"EasyAligner als Revision {version.Revision} gespeichert."));
        }
        finally
        {
            try { Directory.Delete(temporary, recursive: true); }
            catch (IOException exception)
            {
                logger.LogWarning(exception, "Temporäre EasyAligner-Ausgaben konnten nicht entfernt werden.");
            }
        }
    }

    private async Task RunProcessAsync(string workingDirectory, string script,
        IReadOnlyList<string> arguments, List<string> output, Func<int, int> progressMap,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        if (!process.Start()) throw new InvalidOperationException("EasyAligner konnte nicht gestartet werden.");
        lock (_gate) _activeProcess = process;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try { await process.WaitForExitAsync(cancellationToken); }
        catch (OperationCanceledException) { await StopProcessAsync(process); throw; }
        finally { lock (_gate) if (ReferenceEquals(_activeProcess, process)) _activeProcess = null; }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"EasyAligner wurde mit Code {process.ExitCode} beendet.");
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        catch (InvalidOperationException) { }
    }

    private void Add(List<string> output, string? line, Func<int, int>? progressMap = null)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            output.Add(line);
            if (output.Count > 120) output.RemoveAt(0);
            var percent = _status.Percent;
            var marker = System.Text.RegularExpressions.Regex.Match(line, @"\b(?<value>\d{1,3})%(?!\d)");
            if (marker.Success)
                percent = Math.Clamp(progressMap?.Invoke(int.Parse(marker.Groups["value"].Value))
                                     ?? int.Parse(marker.Groups["value"].Value), percent, 95);
            _status = _status with { Message = line.Trim(), Percent = percent, RecentOutput = output.ToArray() };
        }
    }

    private void Set(int percent, string message)
    {
        lock (_gate) _status = _status with { Percent = percent, Message = message };
    }

    private void Complete(string message, List<string> output, int exitCode)
    {
        lock (_gate) _status = _status with
        {
            IsRunning = false, Percent = 100, Message = message,
            FinishedAt = DateTimeOffset.UtcNow, ExitCode = exitCode, RecentOutput = output.ToArray()
        };
    }

    private static void Validate(SongRealignmentRequest request)
    {
        if (request.MaximumSongs is <= 0 or > 100)
            throw new ArgumentException("MaximumSongs muss zwischen 1 und 100 liegen.");
    }

    private static int MapProgress(int percent, int start, int end) =>
        start + (int)Math.Round((end - start) * Math.Clamp(percent, 0, 100) / 100d);
    private static string Prefix(string? scope, string message) =>
        string.IsNullOrWhiteSpace(scope) ? message : $"[{scope}] {message}";

    public Task<LyricsVersionDto> SnapshotCurrentAsync(Guid songId, CancellationToken cancellationToken) =>
        alignmentVersions.SnapshotAsync(songId, $"manual-snapshot-{Guid.CreateVersion7():N}", cancellationToken);
}
