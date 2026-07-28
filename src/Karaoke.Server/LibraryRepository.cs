using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using Karaoke.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

public sealed class LibraryRepository(IOptions<KaraokeOptions> options, ILogger<LibraryRepository> logger,
    IHubContext<KaraokeHub> hubContext, ChangeFeedService changes)
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma"
    };

    private readonly KaraokeOptions _options = options.Value;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly object _statusLock = new();
    private LibraryScanStatusDto _scanStatus = EmptyStatus();

    private string ConnectionString => $"Data Source={Path.GetFullPath(_options.DatabasePath)}";

    public LibraryScanStatusDto GetScanStatus()
    {
        lock (_statusLock)
            return _scanStatus;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.DatabasePath))!);

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS songs(
                id TEXT PRIMARY KEY,
                path TEXT NOT NULL UNIQUE,
                title TEXT NOT NULL,
                artist TEXT NOT NULL,
                album TEXT NOT NULL,
                duration REAL NOT NULL,
                hasLyrics INTEGER NOT NULL,
                modified INTEGER NOT NULL,
                relativePath TEXT NOT NULL DEFAULT '',
                fileName TEXT NOT NULL DEFAULT '',
                audioFormat TEXT NOT NULL DEFAULT '',
                size INTEGER NOT NULL DEFAULT 0,
                lrcPath TEXT,
                lastIndexed TEXT NOT NULL DEFAULT '',
                searchTitle TEXT NOT NULL DEFAULT '',
                searchArtist TEXT NOT NULL DEFAULT '',
                searchAlbum TEXT NOT NULL DEFAULT '',
                searchFileName TEXT NOT NULL DEFAULT '',
                hasCover INTEGER NOT NULL DEFAULT -1
                ,reviewStatus TEXT NOT NULL DEFAULT 'InReview'
                ,hasInstrumental INTEGER NOT NULL DEFAULT 0
                ,hasVocals INTEGER NOT NULL DEFAULT 0
                ,libraryCategory TEXT NOT NULL DEFAULT 'KaraokeReady'
            );
            CREATE INDEX IF NOT EXISTS ix_songs_search ON songs(title, artist, album, fileName);
            CREATE INDEX IF NOT EXISTS ix_songs_path ON songs(path);
            """;

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1)
        {
            // Eine ältere Datenbank besitzt bereits die Tabelle und benötigt zunächst neue Spalten.
            await MigrateExistingSchemaAsync(connection, cancellationToken);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await MigrateExistingSchemaAsync(connection, cancellationToken);
    }

    public async Task<bool> TryReindexAsync(CancellationToken cancellationToken)
    {
        if (!await _scanLock.WaitAsync(0, cancellationToken))
            return false;

        try
        {
            await InitializeAsync(cancellationToken);
            SetStatus(EmptyStatus() with { IsRunning = true, StartedAt = DateTimeOffset.UtcNow });
            await PublishScanStatusAsync(cancellationToken);
            await ScanCoreAsync(cancellationToken);
            SetStatus(GetScanStatus() with
            {
                IsRunning = false,
                FinishedAt = DateTimeOffset.UtcNow,
                CurrentFile = null
            });
            await PublishScanStatusAsync(cancellationToken);
            changes.Publish("library-changed");
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(GetScanStatus() with
            {
                IsRunning = false,
                FinishedAt = DateTimeOffset.UtcNow,
                CurrentFile = null
            });
            await PublishScanStatusAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private async Task ScanCoreAsync(CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(_options.LibraryPath);
        Directory.CreateDirectory(root);
        var indexedFiles = await ReadIndexedFilesAsync(cancellationToken);
        var foundPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)));
        }
        catch (Exception exception)
        {
            IncrementStatus(status => status with { Errors = status.Errors + 1 });
            logger.LogError(exception, "Musikbibliothek {LibraryPath} konnte nicht gelesen werden", root);
            return;
        }

        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(path);
            IncrementStatus(status => status with
            {
                CheckedFiles = status.CheckedFiles + 1,
                CurrentFile = Path.GetRelativePath(root, fullPath)
            });
            await PublishScanStatusAsync(cancellationToken);

            try
            {
                var fileInfo = new FileInfo(fullPath);
                var modified = fileInfo.LastWriteTimeUtc.Ticks;
                var lrcPath = FindLyricsPath(fullPath);
                var hasUsableLyrics = lrcPath is not null && HasUsableLyrics(lrcPath);
                var hasInstrumental = StemExists(fullPath, "instrumental");
                var hasVocals = StemExists(fullPath, "vocals");
                var completeProject = hasUsableLyrics && hasInstrumental && hasVocals;
                var adoption = ReadWithoutLyricsMarker(fullPath);
                // Ein Song ist erst ein bearbeitbares Bibliotheksprojekt, wenn
                // Text und beide getrennten Arbeits-/Stage-Spuren vorhanden sind.
                // Die einzige Ausnahme ist ein ausdrücklich vom Admin
                // übernommener Audiofund. Er bleibt bis zur Nachbearbeitung
                // strikt unveröffentlicht und von der Stage ausgeschlossen.
                if (!completeProject && adoption is null) continue;
                var category = completeProject
                    ? SongLibraryCategory.KaraokeReady
                    : SongLibraryCategory.WithoutLyrics;
                var indexedLrcPath = completeProject ? lrcPath : null;
                foundPaths.Add(fullPath);
                indexedFiles.TryGetValue(fullPath, out var existing);
                var lyricsChanged = existing is not null &&
                    (!StringComparer.OrdinalIgnoreCase.Equals(existing.LrcPath, indexedLrcPath) ||
                     existing.HasLyrics != completeProject);

                if (existing is not null && existing.Modified == modified && existing.Size == fileInfo.Length &&
                    existing.HasCover >= 0 && existing.HasInstrumental == hasInstrumental &&
                    existing.HasVocals == hasVocals && existing.LibraryCategory == category)
                {
                    if (lyricsChanged)
                    {
                        await UpdateLyricsAsync(fullPath, indexedLrcPath, category, cancellationToken);
                        IncrementStatus(status => status with { UpdatedFiles = status.UpdatedFiles + 1 });
                    }
                    continue;
                }

                using var tagFile = TagLib.File.Create(fullPath);
                await UpsertAsync(
                    StableId(fullPath),
                    fullPath,
                    Path.GetRelativePath(root, fullPath),
                    Path.GetFileName(fullPath),
                    adoption?.Title ?? tagFile.Tag.Title ?? Path.GetFileNameWithoutExtension(fullPath),
                    adoption?.Artist ?? tagFile.Tag.FirstPerformer ?? string.Empty,
                    adoption?.Album ?? tagFile.Tag.Album ?? string.Empty,
                    tagFile.Properties.Duration.TotalSeconds,
                    Path.GetExtension(fullPath).TrimStart('.').ToUpperInvariant(),
                    fileInfo.Length,
                    modified,
                    indexedLrcPath,
                    CoverSidecarPath(fullPath) is not null || tagFile.Tag.Pictures.Length > 0,
                    hasInstrumental,
                    hasVocals,
                    category,
                    cancellationToken);

                IncrementStatus(status => existing is null
                    ? status with { NewFiles = status.NewFiles + 1 }
                    : status with { UpdatedFiles = status.UpdatedFiles + 1 });
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                IncrementStatus(status => status with { Errors = status.Errors + 1 });
                logger.LogWarning(exception, "Datei {FilePath} konnte nicht indexiert werden", fullPath);
            }
        }

        var removedPaths = indexedFiles.Keys.Where(path => !foundPaths.Contains(path)).ToArray();
        if (removedPaths.Length > 0)
        {
            await DeletePathsAsync(removedPaths, cancellationToken);
            IncrementStatus(status => status with { RemovedFiles = status.RemovedFiles + removedPaths.Length });
        }
    }

    private async Task<Dictionary<string, IndexedFile>> ReadIndexedFilesAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT path, modified, size, lrcPath, hasLyrics, hasCover,hasInstrumental,hasVocals,libraryCategory FROM songs";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<string, IndexedFile>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            result[reader.GetString(0)] = new(
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetInt32(4) == 1,
                reader.GetInt32(5), reader.GetInt32(6) == 1, reader.GetInt32(7) == 1,
                Enum.TryParse<SongLibraryCategory>(reader.GetString(8), out var category)
                    ? category : SongLibraryCategory.KaraokeReady);
        }
        return result;
    }

    private async Task UpsertAsync(Guid id, string path, string relativePath, string fileName,
        string title, string artist, string album, double duration, string format, long size,
        long modified, string? lrcPath, bool hasCover, bool hasInstrumental, bool hasVocals,
        SongLibraryCategory category,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO songs(id,path,title,artist,album,duration,hasLyrics,modified,relativePath,fileName,audioFormat,size,lrcPath,lastIndexed,searchTitle,searchArtist,searchAlbum,searchFileName,hasCover,reviewStatus,hasInstrumental,hasVocals,libraryCategory)
            VALUES($id,$path,$title,$artist,$album,$duration,$hasLyrics,$modified,$relativePath,$fileName,$format,$size,$lrcPath,$lastIndexed,$searchTitle,$searchArtist,$searchAlbum,$searchFileName,$hasCover,'InReview',$hasInstrumental,$hasVocals,$category)
            ON CONFLICT(path) DO UPDATE SET
                title=$title, artist=$artist, album=$album, duration=$duration,
                hasLyrics=$hasLyrics, modified=$modified, relativePath=$relativePath,
                fileName=$fileName, audioFormat=$format, size=$size, lrcPath=$lrcPath,
                lastIndexed=$lastIndexed, searchTitle=$searchTitle, searchArtist=$searchArtist,
                searchAlbum=$searchAlbum, searchFileName=$searchFileName, hasCover=$hasCover,
                hasInstrumental=$hasInstrumental, hasVocals=$hasVocals, libraryCategory=$category
            """;
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$artist", artist);
        command.Parameters.AddWithValue("$album", album);
        command.Parameters.AddWithValue("$duration", duration);
        command.Parameters.AddWithValue("$hasLyrics", lrcPath is null ? 0 : 1);
        command.Parameters.AddWithValue("$modified", modified);
        command.Parameters.AddWithValue("$relativePath", relativePath);
        command.Parameters.AddWithValue("$fileName", fileName);
        command.Parameters.AddWithValue("$format", format);
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$lrcPath", (object?)lrcPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$lastIndexed", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$searchTitle", NormalizeSearchText(title));
        command.Parameters.AddWithValue("$searchArtist", NormalizeSearchText(artist));
        command.Parameters.AddWithValue("$searchAlbum", NormalizeSearchText(album));
        command.Parameters.AddWithValue("$searchFileName", NormalizeSearchText(fileName));
        command.Parameters.AddWithValue("$hasCover", hasCover ? 1 : 0);
        command.Parameters.AddWithValue("$hasInstrumental", hasInstrumental ? 1 : 0);
        command.Parameters.AddWithValue("$hasVocals", hasVocals ? 1 : 0);
        command.Parameters.AddWithValue("$category", category.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task UpdateLyricsAsync(string path, string? lrcPath, SongLibraryCategory category,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE songs SET hasLyrics=$hasLyrics,lrcPath=$lrcPath,libraryCategory=$category,lastIndexed=$lastIndexed WHERE path=$path";
        command.Parameters.AddWithValue("$hasLyrics", lrcPath is null ? 0 : 1);
        command.Parameters.AddWithValue("$lrcPath", (object?)lrcPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", category.ToString());
        command.Parameters.AddWithValue("$lastIndexed", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$path", path);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeletePathsAsync(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var path in paths)
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = "DELETE FROM songs WHERE path=$path";
            command.Parameters.AddWithValue("$path", path);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PagedResultDto<SongDto>> SearchPageAsync(
        string? query, int page, int pageSize, CancellationToken cancellationToken, bool includeUnreleased = false)
    {
        var normalizedQuery = NormalizeSearchText(query ?? string.Empty);
        var terms = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Take(10)
            .ToArray();
        var searchWhere = terms.Length == 0
            ? "1=1"
            : string.Join(" AND ", terms.Select((_, index) =>
                $"(searchTitle LIKE $term{index} OR searchArtist LIKE $term{index} OR searchAlbum LIKE $term{index} OR searchFileName LIKE $term{index})"));
        var where = includeUnreleased ? searchWhere : $"reviewStatus='Approved' AND ({searchWhere})";
        var order = terms.Length == 0
            ? "artist COLLATE NOCASE, title COLLATE NOCASE"
            : """
              CASE
                  WHEN searchTitle = $full THEN 1000
                  WHEN searchArtist = $full THEN 800
                  WHEN searchTitle LIKE $fullPrefix THEN 600
                  WHEN searchArtist LIKE $fullPrefix THEN 450
                  WHEN searchTitle LIKE $fullContains THEN 350
                  WHEN searchFileName LIKE $fullContains THEN 250
                  ELSE 100
              END DESC,
              title COLLATE NOCASE, artist COLLATE NOCASE
              """;

        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);

        var countCommand = connection.CreateCommand();
        countCommand.CommandText = $"SELECT COUNT(*) FROM songs WHERE {where}";
        AddSearchParameters(countCommand, terms, normalizedQuery, includeRankingParameters: false);
        var totalCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        var command = connection.CreateCommand();
        command.CommandText = $"SELECT id,title,artist,album,duration,hasLyrics,hasCover,reviewStatus,hasInstrumental,hasVocals,libraryCategory FROM songs WHERE {where} ORDER BY {order} LIMIT $take OFFSET $skip";
        command.Parameters.AddWithValue("$take", pageSize);
        command.Parameters.AddWithValue("$skip", (page - 1) * pageSize);
        AddSearchParameters(command, terms, normalizedQuery, includeRankingParameters: terms.Length > 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var songs = new List<SongDto>();
        while (await reader.ReadAsync(cancellationToken))
            songs.Add(ReadSong(reader));

        return new(songs, page, pageSize, totalCount);
    }

    private static void AddSearchParameters(
        SqliteCommand command, IReadOnlyList<string> terms, string fullQuery, bool includeRankingParameters)
    {
        for (var index = 0; index < terms.Count; index++)
            command.Parameters.AddWithValue($"$term{index}", $"%{terms[index]}%");
        if (!includeRankingParameters)
            return;
        command.Parameters.AddWithValue("$full", fullQuery);
        command.Parameters.AddWithValue("$fullPrefix", $"{fullQuery}%");
        command.Parameters.AddWithValue("$fullContains", $"%{fullQuery}%");
    }

    public async Task<IReadOnlyList<SongDto>> SearchAsync(string? term, int skip, int take,
        CancellationToken cancellationToken, bool includeUnreleased = false)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        var columns = "id,title,artist,album,duration,hasLyrics,hasCover,reviewStatus,hasInstrumental,hasVocals,libraryCategory";
        var release = includeUnreleased ? "1=1" : "reviewStatus='Approved'";
        command.CommandText = string.IsNullOrWhiteSpace(term)
            ? $"SELECT {columns} FROM songs WHERE {release} ORDER BY artist,title LIMIT $take OFFSET $skip"
            : $"SELECT {columns} FROM songs WHERE {release} AND (title LIKE $query OR artist LIKE $query OR album LIKE $query OR fileName LIKE $query) ORDER BY artist,title LIMIT $take OFFSET $skip";
        command.Parameters.AddWithValue("$take", take);
        command.Parameters.AddWithValue("$skip", skip);
        if (!string.IsNullOrWhiteSpace(term))
            command.Parameters.AddWithValue("$query", $"%{term.Trim()}%");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var songs = new List<SongDto>();
        while (await reader.ReadAsync(cancellationToken))
            songs.Add(ReadSong(reader));
        return songs;
    }

    public async Task<IReadOnlyList<SongDto>> GetNewestAsync(int take, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,title,artist,album,duration,hasLyrics,hasCover,reviewStatus,hasInstrumental,hasVocals,libraryCategory FROM songs WHERE hasLyrics=1 AND reviewStatus='Approved' ORDER BY lastIndexed DESC LIMIT $take";
        command.Parameters.AddWithValue("$take", Math.Clamp(take, 1, 30));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var songs = new List<SongDto>();
        while (await reader.ReadAsync(cancellationToken)) songs.Add(ReadSong(reader));
        return songs;
    }

    public async Task<SongDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await SearchByIdAsync(id, cancellationToken);
        return result.Song;
    }

    public async Task<SongDto> AdoptWithoutLyricsAsync(string audioPath, SpotifyTrackDto track, Guid wishId,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(_options.LibraryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(audioPath);
        if (!fullPath.StartsWith(root, StringComparison.Ordinal) || !File.Exists(fullPath) ||
            !SupportedExtensions.Contains(Path.GetExtension(fullPath)))
            throw new ArgumentException("Der Audiofund liegt nicht als unterstützte Datei in der Bibliothek.");
        EnsureNoSymbolicPath(root.TrimEnd(Path.DirectorySeparatorChar), fullPath);

        var markerPath = WithoutLyricsMarkerPath(fullPath);
        var marker = new WithoutLyricsMarker(
            string.IsNullOrWhiteSpace(track.Title) ? Path.GetFileNameWithoutExtension(fullPath) : track.Title.Trim(),
            track.Artist.Trim(), track.Album.Trim(), track.SourceUrl ?? track.SpotifyUrl, wishId, DateTimeOffset.UtcNow);
        var temporary = markerPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await File.WriteAllTextAsync(temporary,
                JsonSerializer.Serialize(marker, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
                new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, markerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }

        if (!await TryReindexAsync(cancellationToken))
            throw new InvalidOperationException("Der Bibliotheksindex wird gerade aktualisiert. Übernahme bitte erneut versuchen.");
        return await GetAsync(StableId(fullPath), cancellationToken)
               ?? throw new InvalidOperationException("Der Audiofund konnte nicht in den Bibliotheksindex übernommen werden.");
    }

    public async Task<bool> WriteImportedLyricsSourceAsync(Guid id, string lyrics,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lyrics)) throw new ArgumentException("Die importierten Lyrics sind leer.");
        var indexed = await SearchByIdAsync(id, cancellationToken);
        if (indexed.Path is null) return false;
        var root = Path.GetFullPath(_options.LibraryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var audioPath = Path.GetFullPath(indexed.Path);
        if (!audioPath.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Song liegt außerhalb der konfigurierten Bibliothek.");
        EnsureNoSymbolicPath(root.TrimEnd(Path.DirectorySeparatorChar), audioPath);
        var target = Path.ChangeExtension(audioPath, ".lrc");
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await File.WriteAllTextAsync(temporary, lyrics.Trim() + Environment.NewLine,
                new UTF8Encoding(false), cancellationToken);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        changes.Publish("library-changed");
        return true;
    }

    public async Task<SongDto?> SetReviewStatusAsync(Guid id, SongReviewStatus status, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE songs SET reviewStatus=$status WHERE id=$id AND hasLyrics=1 AND hasInstrumental=1 AND hasVocals=1 AND libraryCategory='KaraokeReady'";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$status", status.ToString());
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
        changes.Publish("library-changed");
        return await GetAsync(id, cancellationToken);
    }

    public async Task<DeleteSongResultDto?> DeleteSongAsync(Guid id, CancellationToken cancellationToken)
    {
        var indexed = await SearchByIdAsync(id, cancellationToken);
        if (indexed.Path is null) return null;
        var root = Path.GetFullPath(_options.LibraryPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var audio = Path.GetFullPath(indexed.Path);
        if (!audio.StartsWith(root, StringComparison.Ordinal))
            throw new InvalidOperationException("Song liegt außerhalb der konfigurierten Bibliothek.");
        var directory = Path.GetDirectoryName(audio)!;
        var baseName = Path.GetFileNameWithoutExtension(audio);
        var files = Directory.EnumerateFiles(directory, baseName + ".*", SearchOption.TopDirectoryOnly).ToArray();

        await using (var connection = new SqliteConnection(ConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            foreach (var sql in new[] { "DELETE FROM lyrics_versions WHERE songId=$id", "DELETE FROM songs WHERE id=$id" })
            {
                var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$id", id.ToString());
                try { await command.ExecuteNonQueryAsync(cancellationToken); }
                catch (SqliteException exception) when (exception.SqliteErrorCode == 1 && sql.Contains("lyrics_versions")) { }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        var deleted = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Delete(file); deleted++; }
            catch (IOException exception) { logger.LogWarning(exception, "Songdatei {File} konnte nicht gelöscht werden", file); }
        }
        changes.Publish("library-changed");
        return new(id, deleted);
    }

    public async Task<SongVisualizationDto?> ReadVisualizationAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await SearchByIdAsync(id, cancellationToken);
        if (result.Path is null) return null;
        var path = Path.ChangeExtension(result.Path, ".visuals.json");
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            var frames = await System.Text.Json.JsonSerializer.DeserializeAsync<IReadOnlyList<VisualizationFrameDto>>(
                stream, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken);
            return frames is { Count: > 0 } ? new(id, 10, frames) : null;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            logger.LogWarning(exception, "Visualisierungsdaten {Path} konnten nicht gelesen werden", path);
            return null;
        }
    }

    public async Task<(string Path, string ContentType)?> GetAudioFileAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await SearchByIdAsync(id, cancellationToken);
        return result.Path is null || !File.Exists(result.Path)
            ? null
            : (result.Path, Mime(Path.GetExtension(result.Path)));
    }

    public async Task<StemAvailabilityDto?> GetStemAvailabilityAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await SearchByIdAsync(id, cancellationToken);
        if (result.Path is null) return null;
        return new StemAvailabilityDto(
            StemExists(result.Path, "instrumental"),
            StemExists(result.Path, "vocals"));
    }

    public async Task<(string Path, string ContentType)?> GetStemFileAsync(Guid id, string kind,
        CancellationToken cancellationToken, string? preferredFormat = null)
    {
        if (kind is not ("instrumental" or "vocals")) return null;
        var result = await SearchByIdAsync(id, cancellationToken);
        if (result.Path is null) return null;
        var oggPath = StemPath(result.Path, kind, ".ogg");
        var flacPath = StemPath(result.Path, kind, ".flac");
        if (string.Equals(preferredFormat, "flac", StringComparison.OrdinalIgnoreCase) && File.Exists(flacPath))
            return (flacPath, "audio/flac");
        if (File.Exists(oggPath)) return (oggPath, "audio/ogg");
        return File.Exists(flacPath) ? (flacPath, "audio/flac") : null;
    }

    private static bool StemExists(string audioPath, string kind) =>
        File.Exists(StemPath(audioPath, kind, ".ogg")) || File.Exists(StemPath(audioPath, kind, ".flac"));

    private static string StemPath(string audioPath, string kind, string extension) =>
        Path.Combine(Path.GetDirectoryName(audioPath)!, $"{Path.GetFileNameWithoutExtension(audioPath)}.{kind}{extension}");

    public async Task<(byte[] Data, string ContentType)?> GetCoverAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await SearchByIdAsync(id, cancellationToken);
        if (result.Path is null || !File.Exists(result.Path)) return null;
        var sidecar = CoverSidecarPath(result.Path);
        if (sidecar is not null)
        {
            var bytes = await File.ReadAllBytesAsync(sidecar, cancellationToken);
            return (bytes, "image/jpeg");
        }
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        using var tagFile = TagLib.File.Create(result.Path);
        var picture = tagFile.Tag.Pictures.FirstOrDefault();
        if (picture?.Data.Data is not { Length: > 0 } data || data.Length > 10 * 1024 * 1024) return null;
        var contentType = DetectImageContentType(data);
        if (contentType is null) return null;
        return (data, contentType);
    }

    public async Task<bool> SetCoverAsync(Guid id, Stream source, long length, CancellationToken cancellationToken)
    {
        if (length is <= 0 or > 20 * 1024 * 1024) return false;
        var result = await SearchByIdAsync(id, cancellationToken);
        if (result.Path is null) return false;
        var target = Path.Combine(Path.GetDirectoryName(result.Path)!,
            Path.GetFileNameWithoutExtension(result.Path) + ".cover.jpg");
        var upload = target + ".upload";
        var converted = target + ".converted";
        try
        {
            await using (var output = new FileStream(upload, FileMode.Create, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await source.CopyToAsync(output, cancellationToken);
            var start = new System.Diagnostics.ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true
            };
            foreach (var argument in new[]
                     {
                         "-y", "-nostdin", "-hide_banner", "-loglevel", "error", "-i", upload,
                         "-frames:v", "1", "-vf",
                         "scale=1024:1024:force_original_aspect_ratio=increase,crop=1024:1024",
                         "-map_metadata", "-1", "-q:v", "2", "-f", "image2", converted
                     }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return false;
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException) { if (!process.HasExited) process.Kill(true); throw; }
            var error = await errorTask;
            if (process.ExitCode != 0 || new FileInfo(converted) is not { Exists: true, Length: > 1024 })
            {
                logger.LogWarning("Cover für Song {SongId} konnte nicht konvertiert werden: {Error}", id, error);
                return false;
            }
            File.Move(converted, target, true);
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken);
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE songs SET hasCover=1 WHERE id=$id";
            command.Parameters.AddWithValue("$id", id.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
            changes.Publish("library-changed");
            return true;
        }
        finally
        {
            if (File.Exists(upload)) File.Delete(upload);
            if (File.Exists(converted)) File.Delete(converted);
        }
    }

    public async Task<LyricsDto?> ReadLyricsAsync(Guid id, CancellationToken cancellationToken)
    {
        var result = await SearchByIdAsync(id, cancellationToken);
        if (result.Path is null)
            return null;
        var path = FindLyricsPath(result.Path);
        if (path is null)
            return null;

        var sourceLines = await File.ReadAllLinesAsync(path, cancellationToken);
        var lyrics = LrcParser.Parse(id, sourceLines, TimeSpan.FromSeconds(result.Song?.DurationSeconds ?? 0));
        var alignmentPath = Path.Combine(Path.GetDirectoryName(result.Path)!, $"{Path.GetFileNameWithoutExtension(result.Path)}.alignment.json");
        return File.Exists(alignmentPath)
            ? await AddSyllableAlignmentAsync(lyrics, alignmentPath, cancellationToken)
            : lyrics;
    }

    private static async Task<LyricsDto> AddSyllableAlignmentAsync(
        LyricsDto lyrics, string alignmentPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(alignmentPath);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (!document.RootElement.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Array)
                return lyrics;

            var lines = lyrics.Lines.ToArray();
            var count = Math.Min(lines.Length, details.GetArrayLength());
            for (var lineIndex = 0; lineIndex < count; lineIndex++)
            {
                var detail = details[lineIndex];
                if (!detail.TryGetProperty("words", out var alignedWords) || alignedWords.ValueKind != JsonValueKind.Array)
                    continue;
                var words = (lines[lineIndex].Words ?? []).ToArray();
                var wordCount = Math.Min(words.Length, alignedWords.GetArrayLength());
                for (var wordIndex = 0; wordIndex < wordCount; wordIndex++)
                {
                    var alignedWord = alignedWords[wordIndex];
                    if (!alignedWord.TryGetProperty("syllables", out var alignedSyllables) || alignedSyllables.ValueKind != JsonValueKind.Array)
                        continue;
                    var wordStart = words[wordIndex].Start;
                    var wordEnd = words[wordIndex].End ?? wordStart;
                    var syllables = alignedSyllables.EnumerateArray().Select((syllable, index) =>
                    {
                        var rawStart = TimeSpan.FromSeconds(syllable.GetProperty("start").GetDouble());
                        var rawEnd = TimeSpan.FromSeconds(syllable.GetProperty("end").GetDouble());
                        var start = rawStart < wordStart ? wordStart : rawStart > wordEnd ? wordEnd : rawStart;
                        var end = rawEnd > wordEnd ? wordEnd : rawEnd < start ? start : rawEnd;
                        return new LyricsSyllableDto(start,
                            syllable.GetProperty("text").GetString() ?? string.Empty,
                            end, index,
                            syllable.TryGetProperty("confidence", out var confidence) ? confidence.GetDouble() : 0);
                    }).ToArray();
                    var wordConfidence = alignedWord.TryGetProperty("syllable_confidence", out var confidenceElement)
                        ? confidenceElement.GetDouble()
                        : syllables.Select(syllable => syllable.Confidence).DefaultIfEmpty().Average();
                    words[wordIndex] = words[wordIndex] with { Syllables = syllables, SyllableConfidence = wordConfidence };
                }
                lines[lineIndex] = lines[lineIndex] with { Words = words };
            }
            return lyrics with { Lines = lines };
        }
        catch (JsonException)
        {
            // A stale or partial sidecar must never make ordinary LRC lyrics unusable.
            return lyrics;
        }
        catch (IOException)
        {
            return lyrics;
        }
    }

    private async Task<(SongDto? Song, string? Path)> SearchByIdAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,title,artist,album,duration,hasLyrics,hasCover,reviewStatus,hasInstrumental,hasVocals,libraryCategory,path FROM songs WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (ReadSong(reader), reader.GetString(11))
            : (null, null);
    }

    private static SongDto ReadSong(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetDouble(4), reader.GetInt32(5) == 1, false, reader.GetInt32(6) == 1,
        Enum.TryParse<SongReviewStatus>(reader.GetString(7), out var status) ? status : SongReviewStatus.InReview,
        reader.GetInt32(8) == 1, reader.GetInt32(9) == 1,
        Enum.TryParse<SongLibraryCategory>(reader.GetString(10), out var category)
            ? category : SongLibraryCategory.KaraokeReady);

    private static async Task MigrateExistingSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["relativePath"] = "TEXT NOT NULL DEFAULT ''",
            ["fileName"] = "TEXT NOT NULL DEFAULT ''",
            ["audioFormat"] = "TEXT NOT NULL DEFAULT ''",
            ["size"] = "INTEGER NOT NULL DEFAULT 0",
            ["lrcPath"] = "TEXT",
            ["lastIndexed"] = "TEXT NOT NULL DEFAULT ''",
            ["searchTitle"] = "TEXT NOT NULL DEFAULT ''",
            ["searchArtist"] = "TEXT NOT NULL DEFAULT ''",
            ["searchAlbum"] = "TEXT NOT NULL DEFAULT ''",
            ["searchFileName"] = "TEXT NOT NULL DEFAULT ''",
            ["hasCover"] = "INTEGER NOT NULL DEFAULT -1",
            ["reviewStatus"] = "TEXT NOT NULL DEFAULT 'InReview'",
            ["hasInstrumental"] = "INTEGER NOT NULL DEFAULT 0",
            ["hasVocals"] = "INTEGER NOT NULL DEFAULT 0",
            ["libraryCategory"] = "TEXT NOT NULL DEFAULT 'KaraokeReady'"
        };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(songs)";
        await using (var reader = await pragma.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                existing.Add(reader.GetString(1));

        foreach (var column in columns.Where(column => !existing.Contains(column.Key)))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE songs ADD COLUMN {column.Key} {column.Value}";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        var select = connection.CreateCommand();
        select.CommandText = "SELECT id,title,artist,album,fileName FROM songs WHERE searchTitle='' AND (title<>'' OR artist<>'' OR album<>'' OR fileName<>'')";
        var pending = new List<(string Id, string Title, string Artist, string Album, string FileName)>();
        await using (var reader = await select.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                pending.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        foreach (var row in pending)
        {
            var update = connection.CreateCommand();
            update.CommandText = "UPDATE songs SET searchTitle=$title,searchArtist=$artist,searchAlbum=$album,searchFileName=$fileName WHERE id=$id";
            update.Parameters.AddWithValue("$title", NormalizeSearchText(row.Title));
            update.Parameters.AddWithValue("$artist", NormalizeSearchText(row.Artist));
            update.Parameters.AddWithValue("$album", NormalizeSearchText(row.Album));
            update.Parameters.AddWithValue("$fileName", NormalizeSearchText(row.FileName));
            update.Parameters.AddWithValue("$id", row.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static string NormalizeSearchText(string value)
    {
        var decomposed = value.Replace("ß", "ss", StringComparison.OrdinalIgnoreCase)
            .Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSpace = true;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }
        return builder.ToString().TrimEnd();
    }

    private static string? FindLyricsPath(string audioPath)
    {
        var expected = Path.ChangeExtension(audioPath, ".lrc");
        if (File.Exists(expected))
            return expected;
        var directory = Path.GetDirectoryName(audioPath);
        var baseName = Path.GetFileNameWithoutExtension(audioPath);
        return directory is null
            ? null
            : Directory.EnumerateFiles(directory, "*.lrc", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => string.Equals(Path.GetFileNameWithoutExtension(path), baseName, StringComparison.OrdinalIgnoreCase));
    }

    private static string? CoverSidecarPath(string audioPath)
    {
        var path = Path.Combine(Path.GetDirectoryName(audioPath)!,
            Path.GetFileNameWithoutExtension(audioPath) + ".cover.jpg");
        return File.Exists(path) ? path : null;
    }

    private static string WithoutLyricsMarkerPath(string audioPath) => Path.Combine(
        Path.GetDirectoryName(audioPath)!, Path.GetFileNameWithoutExtension(audioPath) + ".without-lyrics.json");

    private static WithoutLyricsMarker? ReadWithoutLyricsMarker(string audioPath)
    {
        var path = WithoutLyricsMarkerPath(audioPath);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<WithoutLyricsMarker>(File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void EnsureNoSymbolicPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Der Audiofund darf nicht über einen symbolischen Link eingebunden werden.");
        }
    }

    private static bool HasUsableLyrics(string lrcPath)
    {
        try
        {
            return LrcParser.Parse(Guid.Empty, File.ReadLines(lrcPath), TimeSpan.FromHours(24)).Lines
                .Any(line => !string.IsNullOrWhiteSpace(line.Text));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException)
        {
            return false;
        }
    }

    private void SetStatus(LibraryScanStatusDto status)
    {
        lock (_statusLock)
            _scanStatus = status;
    }

    private void IncrementStatus(Func<LibraryScanStatusDto, LibraryScanStatusDto> update)
    {
        lock (_statusLock)
            _scanStatus = update(_scanStatus);
    }

    private Task PublishScanStatusAsync(CancellationToken cancellationToken) =>
        hubContext.Clients.All.SendAsync(KaraokeHubEvents.ScanStatusChanged, GetScanStatus(), cancellationToken);

    private static LibraryScanStatusDto EmptyStatus() => new(false, 0, 0, 0, 0, 0, null, null, null);
    internal static Guid StableId(string value) => new(MD5.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(value).ToLowerInvariant())));
    private static string Mime(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg", ".flac" => "audio/flac", ".m4a" => "audio/mp4",
        ".aac" => "audio/aac", ".ogg" => "audio/ogg", ".opus" => "audio/opus",
        ".wav" => "audio/wav", ".wma" => "audio/x-ms-wma", _ => "application/octet-stream"
    };

    private static string? DetectImageContentType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
            return "image/png";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";
        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8)))
            return "image/gif";
        return null;
    }

    private sealed record IndexedFile(long Modified, long Size, string? LrcPath, bool HasLyrics, int HasCover,
        bool HasInstrumental, bool HasVocals, SongLibraryCategory LibraryCategory);
    private sealed record WithoutLyricsMarker(string Title, string Artist, string Album, string? SourceUrl,
        Guid WishId, DateTimeOffset AdoptedAt);
}
