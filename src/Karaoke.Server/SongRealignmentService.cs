using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Karaoke.Contracts;
using Karaoke.Editor.Core;

namespace Karaoke.Server;

public sealed record SongRealignmentStatus(bool IsRunning, Guid? JobId, Guid? SongId, string? SongTitle,
    int Percent, string Message, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    int? ExitCode, IReadOnlyList<string> RecentOutput);

internal sealed class SongRealignmentService(IWebHostEnvironment environment, ServerSettingsService settings,
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
        lock (_gate)
        {
            if (_status.IsRunning) return false;
            _status = new(true, Guid.CreateVersion7(), songId, $"{song.Title} · {song.Artist}", 1,
                "GPU-Neuausrichtung wird gestartet …", DateTimeOffset.UtcNow, null, null, []);
        }
        var selected = request ?? new SongRealignmentRequest();
        if (!selected.IncludeEditorBasis && !selected.IncludeOriginalLyrics)
            throw new ArgumentException("Mindestens eine Alignment-Variante muss ausgewählt sein.");
        _ = Task.Run(() => RunAsync(songId, audio.Value.Path, selected));
        return true;
    }

    public bool TryStartAll()
    {
        lock (_gate)
        {
            if (_status.IsRunning) return false;
            _status = new(true, Guid.CreateVersion7(), null, "Gesamte Bibliothek", 1,
                "GPU-Neuausrichtung der gesamten Bibliothek wird gestartet …", DateTimeOffset.UtcNow, null, null, []);
        }
        _ = Task.Run(() => RunAsync(null, null, null));
        return true;
    }

    private async Task RunAsync(Guid? songId, string? audioPath, SongRealignmentRequest? request)
    {
        var output = new List<string>();
        try
        {
            Guid jobId;
            lock (_gate) jobId = _status.JobId
                                ?? throw new InvalidOperationException("Dem Alignment fehlt eine Job-ID.");
            if (songId is not null && audioPath is not null && request is not null)
            {
                await RunSongVariantsAsync(songId.Value, audioPath, request, jobId, output);
                lock (_gate) _status = _status with { IsRunning = false, Percent = 100,
                    Message = "Beide Alignment-Varianten sind als Review-Stände bereit.",
                    FinishedAt = DateTimeOffset.UtcNow, ExitCode = 0, RecentOutput = output.ToArray() };
                return;
            }

            var fingerprints = songId is null
                ? await alignmentVersions.CaptureSourceFingerprintsAsync(CancellationToken.None)
                : null;
            var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));
            var script = Path.Combine(root, "scripts", "linux", "align-library.sh");
            var start = new ProcessStartInfo("/bin/bash")
            {
                WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[] { script, "--force", "--library", settings.Get().LibraryPath })
                start.ArgumentList.Add(argument);
            if (!string.IsNullOrWhiteSpace(audioPath))
            {
                start.ArgumentList.Add("--match");
                start.ArgumentList.Add(Path.GetFileName(audioPath));
            }
            using var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, args) => Add(output, args.Data);
            process.ErrorDataReceived += (_, args) => Add(output, args.Data);
            if (!process.Start()) throw new InvalidOperationException("GPU-Alignment konnte nicht gestartet werden.");
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException($"GPU-Pipeline wurde mit Code {process.ExitCode} beendet.");
            Set(96, "Alignment wird als Lyrics-Version gesichert …");
            int createdVersions;
            if (songId is { } id)
            {
                await alignmentVersions.SnapshotAsync(id, jobId.ToString("N"), CancellationToken.None);
                createdVersions = 1;
            }
            else
            {
                createdVersions = await alignmentVersions.SnapshotChangedAsync(
                    fingerprints!, jobId.ToString("N"), CancellationToken.None);
            }
            Add(output, $"{createdVersions} Lyrics-Version(en) dauerhaft gespeichert.");
            Set(98, "Bibliothek wird aktualisiert …");
            await library.TryReindexAsync(CancellationToken.None);
            lock (_gate) _status = _status with { IsRunning = false, Percent = 100,
                Message = createdVersions == 1
                    ? "Neues Alignment ist als versionierter Review-Stand bereit."
                    : $"{createdVersions} neue Alignments sind als versionierte Review-Stände bereit.",
                FinishedAt = DateTimeOffset.UtcNow,
                ExitCode = 0, RecentOutput = output.ToArray() };
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

    private async Task RunSongVariantsAsync(Guid songId, string audioPath,
        SongRealignmentRequest request, Guid jobId, List<string> output)
    {
        var root = Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", ".."));
        var alignScript = Path.Combine(root, "scripts", "linux", "align-library.sh");
        var recognizeScript = Path.Combine(root, "scripts", "linux", "recognize-song-lyrics.sh");
        var basePath = Path.Combine(Path.GetDirectoryName(audioPath)!, Path.GetFileNameWithoutExtension(audioPath));
        var originalLyrics = basePath + ".pre-align.lrc";
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"neonstage-dual-alignment-{jobId:N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            if (request.IncludeEditorBasis)
            {
                Set(3, "Variante 1/2: letzter Editor-Stand wird vorbereitet …");
                var sourceVersion = request.SourceVersionId is { } versionId
                    ? await versions.GetAsync(songId, versionId, CancellationToken.None)
                    : await versions.GetLatestDraftAsync(songId, CancellationToken.None)
                      ?? await versions.GetRuntimeAsync(songId, CancellationToken.None);
                if (sourceVersion is null)
                    throw new InvalidOperationException("Für den Song existiert kein gespeicherter Editor-Stand.");
                var document = JsonSerializer.Deserialize<LyricsEditorDocument>(sourceVersion.DocumentJson, JsonOptions)
                               ?? throw new InvalidOperationException("Der letzte Editor-Stand ist nicht lesbar.");
                var editorLyrics = Path.Combine(temporaryRoot, "editor-basis.lrc");
                await File.WriteAllTextAsync(editorLyrics,
                    LyricsDocumentLrcExporter.ToEnhancedLrc(document), new UTF8Encoding(false));
                var editorOutput = Path.Combine(temporaryRoot, "editor-result");
                Set(6, $"Variante 1/2: Revision {sourceVersion.Revision} wird akustisch neu ausgerichtet …");
                await RunProcessAsync(root, alignScript,
                    ["--force", "--library", Path.GetDirectoryName(audioPath)!, "--match", Path.GetFileName(audioPath),
                     "--lyrics-source", editorLyrics, "--output-dir", editorOutput, "--no-reindex"], output);
                var result = Path.Combine(editorOutput, Path.GetFileName(basePath) + ".lrc");
                var report = Path.Combine(editorOutput, Path.GetFileName(basePath) + ".alignment.json");
                var version = await alignmentVersions.SnapshotFileAsync(songId, result, report,
                    $"dual-{jobId:N}:editor-basis:r{sourceVersion.Revision}", "editor-basis-acoustic-realignment",
                    CancellationToken.None);
                Add(output, $"Variante letzter Editor-Stand als Revision {version.Revision} gespeichert.");
            }

            if (request.IncludeOriginalLyrics)
            {
                if (!File.Exists(originalLyrics))
                    throw new InvalidOperationException("Die ursprüngliche LRCLIB/pre-align-Lyrics-Datei fehlt.");
                var originalOutput = Path.Combine(temporaryRoot, "lrclib-result");
                Set(request.IncludeEditorBasis ? 52 : 6,
                    "Variante 2/2: LRCLIB-Text wird auf das Volltranskript-Timing übertragen …");
                await RunProcessAsync(root, recognizeScript,
                    ["--audio", audioPath, "--canonical", originalLyrics, "--output-dir", originalOutput,
                     "--no-reindex", "--language", "auto"], output);
                var result = Path.Combine(originalOutput, Path.GetFileName(basePath) + ".lrc");
                var report = Path.Combine(originalOutput, Path.GetFileName(basePath) + ".alignment.json");
                var version = await alignmentVersions.SnapshotFileAsync(songId, result, report,
                    $"dual-{jobId:N}:lrclib-full-transcript", "lrclib-canonical-on-full-transcript",
                    CancellationToken.None);
                Add(output, $"Variante LRCLIB + Volltranskript als Revision {version.Revision} gespeichert.");
            }
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); }
            catch (IOException exception) { logger.LogWarning(exception, "Temporäre Alignment-Ausgaben konnten nicht vollständig entfernt werden."); }
        }
    }

    private async Task RunProcessAsync(string workingDirectory, string script,
        IReadOnlyList<string> arguments, List<string> output)
    {
        var start = new ProcessStartInfo("/bin/bash")
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.ArgumentList.Add(script);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, args) => Add(output, args.Data);
        process.ErrorDataReceived += (_, args) => Add(output, args.Data);
        if (!process.Start()) throw new InvalidOperationException("GPU-Alignment konnte nicht gestartet werden.");
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"GPU-Pipeline wurde mit Code {process.ExitCode} beendet.");
    }

    private void Add(List<string> output, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            output.Add(line); if (output.Count > 120) output.RemoveAt(0);
            var percent = _status.Percent;
            var marker = System.Text.RegularExpressions.Regex.Match(line, @"\b(?<value>\d{1,3})%(?!\d)");
            if (marker.Success) percent = Math.Clamp(int.Parse(marker.Groups["value"].Value), percent, 95);
            _status = _status with { Message = line.Trim(), Percent = percent, RecentOutput = output.ToArray() };
        }
    }

    private void Set(int percent, string message) { lock (_gate) _status = _status with { Percent = percent, Message = message }; }

    public Task<Karaoke.Contracts.LyricsVersionDto> SnapshotCurrentAsync(Guid songId,
        CancellationToken cancellationToken) =>
        alignmentVersions.SnapshotAsync(songId, $"manual-snapshot-{Guid.CreateVersion7():N}", cancellationToken);
}
