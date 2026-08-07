using System.Text.Json;
using Karaoke.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

internal sealed class LyricsVersionRepository(IOptions<KaraokeOptions> options)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(options.Value.DatabasePath)
    }.ToString();
    private readonly SemaphoreSlim _initialization = new(1, 1);
    private bool _initialized;

    public async Task<IReadOnlyList<LyricsVersionSummaryDto>> GetAllAsync(Guid songId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,songId,revision,status,analysisRunId,createdAt,updatedAt,alignmentReportJson IS NOT NULL FROM lyrics_versions WHERE songId=$song ORDER BY revision DESC, createdAt DESC";
        command.Parameters.AddWithValue("$song", songId.ToString());
        var result = new List<LyricsVersionSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadSummary(reader));
        return result;
    }

    public async Task<LyricsVersionDto?> GetAsync(Guid songId, Guid versionId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,songId,revision,status,documentJson,analysisRunId,createdAt,updatedAt,alignmentReportJson FROM lyrics_versions WHERE id=$id AND songId=$song";
        command.Parameters.AddWithValue("$id", versionId.ToString());
        command.Parameters.AddWithValue("$song", songId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    public async Task<LyricsVersionDto?> GetLatestDraftAsync(Guid songId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,songId,revision,status,documentJson,analysisRunId,createdAt,updatedAt,alignmentReportJson FROM lyrics_versions WHERE songId=$song AND status NOT IN ('Superseded','Rejected','Generated') ORDER BY updatedAt DESC LIMIT 1";
        command.Parameters.AddWithValue("$song", songId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    public async Task<LyricsVersionDto?> GetRuntimeAsync(Guid songId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,songId,revision,status,documentJson,analysisRunId,createdAt,updatedAt,alignmentReportJson FROM lyrics_versions WHERE songId=$song AND status='Published' ORDER BY updatedAt DESC LIMIT 1";
        command.Parameters.AddWithValue("$song", songId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadVersion(reader) : null;
    }

    public async Task<LyricsVersionDto> CreateAsync(Guid songId, CreateLyricsVersionRequest request, CancellationToken ct)
    {
        ValidateDocument(songId, request.DocumentJson, request.AllowTimingConflicts);
        ValidateAlignmentReport(request.AlignmentReportJson);
        await EnsureInitializedAsync(ct);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (!request.PreserveExistingDrafts)
        {
            var archiveDrafts = connection.CreateCommand();
            archiveDrafts.Transaction = (SqliteTransaction)transaction;
            archiveDrafts.CommandText = "UPDATE lyrics_versions SET status='Superseded' WHERE songId=$song AND status NOT IN ('Published','Superseded','Rejected')";
            archiveDrafts.Parameters.AddWithValue("$song", songId.ToString());
            await archiveDrafts.ExecuteNonQueryAsync(ct);
        }
        var revision = await NextRevisionAsync(connection, (SqliteTransaction)transaction, songId, ct);
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "INSERT INTO lyrics_versions(id,songId,revision,status,documentJson,analysisRunId,alignmentReportJson,createdAt,updatedAt) VALUES($id,$song,$revision,$status,$json,$analysis,$report,$created,$updated)";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$song", songId.ToString());
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$status", request.Status.ToString());
        command.Parameters.AddWithValue("$json", request.DocumentJson);
        command.Parameters.AddWithValue("$analysis", (object?)request.AnalysisRunId ?? DBNull.Value);
        command.Parameters.AddWithValue("$report", (object?)request.AlignmentReportJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        await command.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return new(id, songId, revision, request.Status, request.DocumentJson, request.AnalysisRunId, now, now,
            request.AlignmentReportJson);
    }

    public async Task<LyricsVersionDto?> UpdateAsync(Guid songId, Guid versionId,
        UpdateLyricsVersionRequest request, CancellationToken ct)
    {
        ValidateDocument(songId, request.DocumentJson, request.AllowTimingConflicts);
        await EnsureInitializedAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // Ein Speichern überschreibt niemals einen älteren Lyrics-Stand. Der
        // bisherige Arbeitsstand wird zum schreibgeschützten Snapshot, während
        // der neue Inhalt eine eigene ID, Revision und Zeitmarke erhält.
        var supersede = connection.CreateCommand();
        supersede.Transaction = (SqliteTransaction)transaction;
        supersede.CommandText = "UPDATE lyrics_versions SET status='Superseded' WHERE id=$id AND songId=$song AND revision=$revision AND status NOT IN ('Published','Superseded','Rejected')";
        supersede.Parameters.AddWithValue("$id", versionId.ToString());
        supersede.Parameters.AddWithValue("$song", songId.ToString());
        supersede.Parameters.AddWithValue("$revision", request.ExpectedRevision);
        if (await supersede.ExecuteNonQueryAsync(ct) != 1)
        {
            await transaction.RollbackAsync(ct);
            return null;
        }

        var newId = Guid.NewGuid();
        var revision = await NextRevisionAsync(connection, (SqliteTransaction)transaction, songId, ct);
        var insert = connection.CreateCommand();
        insert.Transaction = (SqliteTransaction)transaction;
        insert.CommandText = "INSERT INTO lyrics_versions(id,songId,revision,status,documentJson,analysisRunId,alignmentReportJson,createdAt,updatedAt) SELECT $id,$song,$revision,$status,$json,analysisRunId,alignmentReportJson,$created,$updated FROM lyrics_versions WHERE id=$previousId AND songId=$song";
        insert.Parameters.AddWithValue("$id", newId.ToString());
        insert.Parameters.AddWithValue("$previousId", versionId.ToString());
        insert.Parameters.AddWithValue("$song", songId.ToString());
        insert.Parameters.AddWithValue("$revision", revision);
        insert.Parameters.AddWithValue("$status", request.Status.ToString());
        insert.Parameters.AddWithValue("$json", request.DocumentJson);
        insert.Parameters.AddWithValue("$created", now.ToString("O"));
        insert.Parameters.AddWithValue("$updated", now.ToString("O"));
        await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetAsync(songId, newId, ct) ??
               throw new InvalidOperationException("Die neue Lyrics-Version konnte nicht gelesen werden.");
    }

    public async Task<LyricsVersionDeleteResult> DeleteAsync(Guid songId, Guid versionId, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        var lookup = connection.CreateCommand();
        lookup.CommandText = "SELECT status FROM lyrics_versions WHERE id=$id AND songId=$song";
        lookup.Parameters.AddWithValue("$id", versionId.ToString());
        lookup.Parameters.AddWithValue("$song", songId.ToString());
        var status = await lookup.ExecuteScalarAsync(ct) as string;
        if (status is null) return LyricsVersionDeleteResult.NotFound;
        if (string.Equals(status, nameof(LyricsVersionStatus.Published), StringComparison.Ordinal))
            return LyricsVersionDeleteResult.Published;

        var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM lyrics_versions WHERE id=$id AND songId=$song";
        delete.Parameters.AddWithValue("$id", versionId.ToString());
        delete.Parameters.AddWithValue("$song", songId.ToString());
        return await delete.ExecuteNonQueryAsync(ct) == 1
            ? LyricsVersionDeleteResult.Deleted
            : LyricsVersionDeleteResult.NotFound;
    }

    public async Task<LyricsVersionDto?> ChangeStatusAsync(Guid songId, Guid versionId,
        long expectedRevision, LyricsVersionStatus target, CancellationToken ct,
        bool allowTimingConflicts = false)
    {
        await EnsureInitializedAsync(ct);
        var current = await GetAsync(songId, versionId, ct);
        if (current is null || current.Revision != expectedRevision || !CanTransition(current.Status, target)) return null;
        if (target == LyricsVersionStatus.Published)
            ValidateDocument(songId, current.DocumentJson, allowTimingConflicts);
        var now = DateTimeOffset.UtcNow;
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        if (target == LyricsVersionStatus.Published)
        {
            var supersede = connection.CreateCommand();
            supersede.Transaction = (SqliteTransaction)transaction;
            supersede.CommandText = "UPDATE lyrics_versions SET status='Superseded',updatedAt=$updated WHERE songId=$song AND status='Published' AND id<>$id";
            supersede.Parameters.AddWithValue("$song", songId.ToString());
            supersede.Parameters.AddWithValue("$id", versionId.ToString());
            supersede.Parameters.AddWithValue("$updated", now.ToString("O"));
            await supersede.ExecuteNonQueryAsync(ct);
        }
        var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = "UPDATE lyrics_versions SET status=$status,updatedAt=$updated WHERE id=$id AND songId=$song AND revision=$revision AND status=$currentStatus";
        command.Parameters.AddWithValue("$id", versionId.ToString());
        command.Parameters.AddWithValue("$song", songId.ToString());
        command.Parameters.AddWithValue("$revision", expectedRevision);
        command.Parameters.AddWithValue("$currentStatus", current.Status.ToString());
        command.Parameters.AddWithValue("$status", target.ToString());
        command.Parameters.AddWithValue("$updated", now.ToString("O"));
        if (await command.ExecuteNonQueryAsync(ct) != 1) { await transaction.RollbackAsync(ct); return null; }
        await transaction.CommitAsync(ct);
        return await GetAsync(songId, versionId, ct);
    }

    public async Task<int> ImportAsync(Guid songId, IReadOnlyList<LyricsVersionDto> versions,
        CancellationToken ct)
    {
        if (versions.Count == 0) return 0;
        if (versions.Count > 10_000 || versions.Any(version => version.Revision <= 0) ||
            versions.Select(version => version.Revision).Distinct().Count() != versions.Count)
            throw new InvalidDataException("Das Songpaket enthält ungültige Lyrics-Revisionen.");
        foreach (var version in versions)
            ValidateDocument(songId, version.DocumentJson, allowTimingConflicts: true);

        await EnsureInitializedAsync(ct);
        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var existing = connection.CreateCommand();
        existing.Transaction = (SqliteTransaction)transaction;
        existing.CommandText = "SELECT COUNT(*) FROM lyrics_versions WHERE songId=$song";
        existing.Parameters.AddWithValue("$song", songId.ToString());
        if (Convert.ToInt32(await existing.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture) != 0)
            throw new InvalidOperationException("Für den importierten Song existieren bereits Lyrics-Versionen.");

        foreach (var version in versions.OrderBy(version => version.Revision))
        {
            var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            ValidateAlignmentReport(version.AlignmentReportJson);
            command.CommandText = "INSERT INTO lyrics_versions(id,songId,revision,status,documentJson,analysisRunId,alignmentReportJson,createdAt,updatedAt) VALUES($id,$song,$revision,$status,$json,$analysis,$report,$created,$updated)";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$song", songId.ToString());
            command.Parameters.AddWithValue("$revision", version.Revision);
            command.Parameters.AddWithValue("$status", version.Status.ToString());
            command.Parameters.AddWithValue("$json", version.DocumentJson);
            command.Parameters.AddWithValue("$analysis", (object?)version.AnalysisRunId ?? DBNull.Value);
            command.Parameters.AddWithValue("$report", (object?)version.AlignmentReportJson ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", version.CreatedAt.ToString("O"));
            command.Parameters.AddWithValue("$updated", version.UpdatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return versions.Count;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initialization.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            var builder = new SqliteConnectionStringBuilder(_connectionString);
            Directory.CreateDirectory(Path.GetDirectoryName(builder.DataSource)!);
            await using var connection = await OpenAsync(ct);
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS lyrics_versions(
                    id TEXT PRIMARY KEY, songId TEXT NOT NULL, revision INTEGER NOT NULL,
                    status TEXT NOT NULL, documentJson TEXT NOT NULL, analysisRunId TEXT NULL,
                    alignmentReportJson TEXT NULL,
                    createdAt TEXT NOT NULL, updatedAt TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_lyrics_versions_song_updated ON lyrics_versions(songId,updatedAt DESC);
                """;
            await command.ExecuteNonQueryAsync(ct);
            await EnsureColumnAsync(connection, "lyrics_versions", "alignmentReportJson", "TEXT NULL", ct);
            _initialized = true;
        }
        finally { _initialization.Release(); }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        return connection;
    }

    private static async Task<long> NextRevisionAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid songId, CancellationToken ct)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(revision),0)+1 FROM lyrics_versions WHERE songId=$song";
        command.Parameters.AddWithValue("$song", songId.ToString());
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task EnsureColumnAsync(SqliteConnection connection, string table, string column,
        string declaration, CancellationToken ct)
    {
        var lookup = connection.CreateCommand();
        lookup.CommandText = $"PRAGMA table_info({table})";
        var exists = false;
        await using (var reader = await lookup.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
                exists |= string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase);
        if (exists) return;
        var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration}";
        await alter.ExecuteNonQueryAsync(ct);
    }

    private static void ValidateAlignmentReport(string? json)
    {
        if (json is null) return;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 20 * 1024 * 1024)
            throw new ArgumentException("Der Alignment-Bericht ist leer oder größer als 20 MiB.");
        using var report = JsonDocument.Parse(json);
        if (report.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Der Alignment-Bericht muss ein JSON-Objekt sein.");
    }

    private static void ValidateDocument(Guid songId, string json, bool allowTimingConflicts = false)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 10 * 1024 * 1024)
            throw new ArgumentException("Das Editor-Dokument ist leer oder größer als 10 MiB.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out var schema) || schema.GetInt32() != 1)
            throw new ArgumentException("Unbekannte Editor-Schemaversion.");
        if (!root.TryGetProperty("songId", out var song) || !Guid.TryParse(song.GetString(), out var parsed) || parsed != songId)
            throw new ArgumentException("Das Editor-Dokument gehört nicht zu diesem Song.");
        if (!root.TryGetProperty("lines", out var lines) || lines.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("Das Editor-Dokument enthält keine Lyrics-Zeilen.");
        TimeSpan? previousEnd = null;
        foreach (var line in lines.EnumerateArray().OrderBy(ReadEffectiveStart))
        {
            var start = ReadEffectiveStart(line);
            var end = ReadEffectiveEnd(line);
            if (end <= start)
                throw new ArgumentException("Lyrics-Zeilen müssen eine positive Dauer besitzen.");
            if (!allowTimingConflicts && previousEnd is { } boundary && start < boundary - TimeSpan.FromMilliseconds(1))
                throw new ArgumentException("Lyrics-Zeilen einschließlich ihrer Wörter und Silben dürfen sich zeitlich nicht überschneiden.");
            previousEnd = end;
        }
    }

    private static TimeSpan ReadEffectiveStart(JsonElement segment) =>
        EnumerateSegmentTree(segment).Min(item => ReadLineTime(item, "start"));

    private static TimeSpan ReadEffectiveEnd(JsonElement segment) =>
        EnumerateSegmentTree(segment).Max(item => ReadLineTime(item, "end"));

    private static IEnumerable<JsonElement> EnumerateSegmentTree(JsonElement segment)
    {
        yield return segment;
        if (!segment.TryGetProperty("children", out var children) ||
            children.ValueKind != JsonValueKind.Array) yield break;
        foreach (var child in children.EnumerateArray())
            foreach (var descendant in EnumerateSegmentTree(child))
                yield return descendant;
    }

    private static TimeSpan ReadLineTime(JsonElement line, string property)
    {
        if (!line.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String ||
            !TimeSpan.TryParse(value.GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            throw new ArgumentException($"Lyrics-Zeile enthält keine gültige {property}-Zeit.");
        return parsed;
    }

    private static bool CanTransition(LyricsVersionStatus from, LyricsVersionStatus to) => (from, to) switch
    {
        (LyricsVersionStatus.Generated or LyricsVersionStatus.NeedsReview, LyricsVersionStatus.InReview) => true,
        (LyricsVersionStatus.InReview, LyricsVersionStatus.Reviewed) => true,
        (LyricsVersionStatus.Reviewed, LyricsVersionStatus.Approved) => true,
        (LyricsVersionStatus.Generated or LyricsVersionStatus.NeedsReview or LyricsVersionStatus.InReview or
            LyricsVersionStatus.Reviewed or LyricsVersionStatus.Approved, LyricsVersionStatus.Published) => true,
        (_, LyricsVersionStatus.Rejected) when from != LyricsVersionStatus.Published => true,
        _ => false,
    };

    private static LyricsVersionSummaryDto ReadSummary(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
        Enum.Parse<LyricsVersionStatus>(reader.GetString(3)), reader.IsDBNull(4) ? null : reader.GetString(4),
        DateTimeOffset.Parse(reader.GetString(5)), DateTimeOffset.Parse(reader.GetString(6)), reader.GetBoolean(7));

    private static LyricsVersionDto ReadVersion(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
        Enum.Parse<LyricsVersionStatus>(reader.GetString(3)), reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), DateTimeOffset.Parse(reader.GetString(6)),
        DateTimeOffset.Parse(reader.GetString(7)), reader.IsDBNull(8) ? null : reader.GetString(8));
}

internal enum LyricsVersionDeleteResult { Deleted, NotFound, Published }
