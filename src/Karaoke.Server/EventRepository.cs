using System.Security.Cryptography;
using Karaoke.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

public sealed class EventRepository(IOptions<KaraokeOptions> options)
{
    public static readonly Guid DefaultEventId = new("00000000-0000-0000-0000-000000000001");
    public const string DefaultInviteToken = "neon-stage";
    private readonly string _connectionString = $"Data Source={Path.GetFullPath(options.Value.DatabasePath)}";
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await _mutex.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct);
            var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS karaoke_events(
                    id TEXT PRIMARY KEY, name TEXT NOT NULL, inviteToken TEXT NOT NULL UNIQUE,
                    startsAt TEXT NOT NULL, endsAt TEXT, isActive INTEGER NOT NULL DEFAULT 0,
                    createdAt TEXT NOT NULL, description TEXT
                );
                INSERT OR IGNORE INTO karaoke_events(id,name,inviteToken,startsAt,endsAt,isActive,createdAt,description)
                VALUES($id,'Neon Stage',$token,$now,NULL,0,$now,'Automatisch aus der bisherigen globalen Sitzung übernommen');
                UPDATE karaoke_events SET isActive=0,description='Frühere globale Sitzung (inaktiv)'
                WHERE id=$id AND description='Automatisch aus der bisherigen globalen Sitzung übernommen';
                CREATE UNIQUE INDEX IF NOT EXISTS ix_karaoke_events_active ON karaoke_events(isActive) WHERE isActive=1;
                CREATE TABLE IF NOT EXISTS event_wishes(
                    id TEXT PRIMARY KEY,eventId TEXT NOT NULL,spotifyId TEXT NOT NULL,trackJson TEXT NOT NULL,
                    requestedBy TEXT NOT NULL,requestedAt TEXT NOT NULL,status TEXT NOT NULL,audioCandidatePath TEXT,
                    UNIQUE(eventId,spotifyId),FOREIGN KEY(eventId) REFERENCES karaoke_events(id)
                );
                """;
            command.Parameters.AddWithValue("$id", DefaultEventId.ToString());
            command.Parameters.AddWithValue("$token", DefaultInviteToken);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(ct);
            await EnsureQueueEventColumnAsync(connection, ct);
            await EnsureWishColumnsAsync(connection, ct);
            await MigrateWishesAsync(connection, ct);
        }
        finally { _mutex.Release(); }
    }

    public async Task<IReadOnlyList<KaraokeEventDto>> GetAllAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = EventSelect + " ORDER BY startsAt DESC";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<KaraokeEventDto>();
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }

    public Task<KaraokeEventDto?> GetActiveAsync(CancellationToken ct) =>
        GetSingleAsync("isActive=1", null, ct);

    public Task<KaraokeEventDto?> GetByTokenAsync(string token, CancellationToken ct) =>
        GetSingleAsync("inviteToken=$value", token, ct);

    public Task<KaraokeEventDto?> GetByIdAsync(Guid id, CancellationToken ct) =>
        GetSingleAsync("id=$value", id.ToString(), ct);

    public async Task<KaraokeEventDto> CreateAsync(CreateKaraokeEventRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Der Eventname fehlt.");
        await InitializeAsync(ct);
        var item = new KaraokeEventDto(Guid.NewGuid(), request.Name.Trim(), NewToken(), request.StartsAt,
            request.EndsAt, false, DateTimeOffset.UtcNow, request.Description?.Trim());
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO karaoke_events(id,name,inviteToken,startsAt,endsAt,isActive,createdAt,description) VALUES($id,$name,$token,$start,$end,0,$created,$description)";
        command.Parameters.AddWithValue("$id", item.Id.ToString());
        command.Parameters.AddWithValue("$name", item.Name);
        command.Parameters.AddWithValue("$token", item.InviteToken);
        command.Parameters.AddWithValue("$start", item.StartsAt.ToString("O"));
        command.Parameters.AddWithValue("$end", (object?)item.EndsAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$description", (object?)item.Description ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
        return item;
    }

    public async Task<KaraokeEventDto?> ActivateAsync(Guid id, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var clear = connection.CreateCommand(); clear.Transaction = (SqliteTransaction)transaction;
        clear.CommandText = "UPDATE karaoke_events SET isActive=0";
        await clear.ExecuteNonQueryAsync(ct);
        var activate = connection.CreateCommand(); activate.Transaction = (SqliteTransaction)transaction;
        activate.CommandText = "UPDATE karaoke_events SET isActive=1 WHERE id=$id";
        activate.Parameters.AddWithValue("$id", id.ToString());
        var changed = await activate.ExecuteNonQueryAsync(ct);
        if (changed == 0) { await transaction.RollbackAsync(ct); return null; }
        // Playback cannot continue across event boundaries.
        var stop = connection.CreateCommand(); stop.Transaction = (SqliteTransaction)transaction;
        stop.CommandText = "UPDATE playback_state SET isRunning=0,isPaused=0,currentEntryId=NULL,positionMs=0,revision=revision+1 WHERE singleton=1";
        await stop.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetByIdAsync(id, ct);
    }

    public async Task<bool> DeactivateAsync(Guid id, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var deactivate = connection.CreateCommand(); deactivate.Transaction = (SqliteTransaction)transaction;
        deactivate.CommandText = "UPDATE karaoke_events SET isActive=0 WHERE id=$id AND isActive=1";
        deactivate.Parameters.AddWithValue("$id", id.ToString());
        var changed = await deactivate.ExecuteNonQueryAsync(ct);
        if (changed > 0)
        {
            var stop = connection.CreateCommand(); stop.Transaction = (SqliteTransaction)transaction;
            stop.CommandText = "UPDATE playback_state SET isRunning=0,isPaused=0,currentEntryId=NULL,positionMs=0,revision=revision+1 WHERE singleton=1";
            await stop.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return changed > 0;
    }

    public async Task<EventDeleteResult> DeleteAsync(Guid id, bool includeOpenWishes, CancellationToken ct)
    {
        await InitializeAsync(ct);
        if (id == DefaultEventId) return EventDeleteResult.Protected;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var state = connection.CreateCommand(); state.Transaction = (SqliteTransaction)transaction;
        state.CommandText = "SELECT isActive FROM karaoke_events WHERE id=$id";
        state.Parameters.AddWithValue("$id", id.ToString());
        var active = await state.ExecuteScalarAsync(ct);
        if (active is null) { await transaction.RollbackAsync(ct); return EventDeleteResult.NotFound; }
        if (Convert.ToInt64(active) != 0) { await transaction.RollbackAsync(ct); return EventDeleteResult.Active; }
        var wishes = connection.CreateCommand(); wishes.Transaction = (SqliteTransaction)transaction;
        wishes.CommandText = "SELECT COUNT(*) FROM event_wishes WHERE eventId=$id";
        wishes.Parameters.AddWithValue("$id", id.ToString());
        var wishCount = Convert.ToInt32(await wishes.ExecuteScalarAsync(ct));
        if (wishCount > 0 && !includeOpenWishes) { await transaction.RollbackAsync(ct); return EventDeleteResult.HasWishes; }
        var queueTable = connection.CreateCommand(); queueTable.Transaction = (SqliteTransaction)transaction;
        queueTable.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='queue_entries'";
        var hasQueueTable = Convert.ToInt32(await queueTable.ExecuteScalarAsync(ct)) > 0;
        var statements = new List<string> { "DELETE FROM event_wishes WHERE eventId=$id" };
        if (hasQueueTable) statements.Add("DELETE FROM queue_entries WHERE eventId=$id");
        statements.Add("DELETE FROM karaoke_events WHERE id=$id");
        foreach (var sql in statements)
        {
            var command = connection.CreateCommand(); command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = sql; command.Parameters.AddWithValue("$id", id.ToString());
            await command.ExecuteNonQueryAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return EventDeleteResult.Deleted;
    }

    public async Task<Guid?> ResolveIdAsync(string? token, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(token) ? (await GetActiveAsync(ct))?.Id : (await GetByTokenAsync(token, ct))?.Id;

    private async Task<KaraokeEventDto?> GetSingleAsync(string predicate, string? value, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = EventSelect + " WHERE " + predicate + " LIMIT 1";
        if (value is not null) command.Parameters.AddWithValue("$value", value);
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    private static async Task EnsureQueueEventColumnAsync(SqliteConnection connection, CancellationToken ct)
    {
        var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='queue_entries'";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct)) == 0) return;
        var pragma = connection.CreateCommand(); pragma.CommandText = "PRAGMA table_info(queue_entries)";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await pragma.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1));
        if (!columns.Contains("eventId"))
        {
            var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE queue_entries ADD COLUMN eventId TEXT NOT NULL DEFAULT '{DefaultEventId}'";
            await alter.ExecuteNonQueryAsync(ct);
        }
        var index = connection.CreateCommand();
        index.CommandText = "CREATE INDEX IF NOT EXISTS ix_queue_event_status_position ON queue_entries(eventId,status,position)";
        await index.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateWishesAsync(SqliteConnection connection, CancellationToken ct)
    {
        var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='wishes'";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(ct)) == 0) return;
        var migrate = connection.CreateCommand();
        migrate.CommandText = "INSERT OR IGNORE INTO event_wishes(id,eventId,spotifyId,trackJson,requestedBy,requestedAt,status) SELECT id,$eventId,spotifyId,trackJson,requestedBy,requestedAt,status FROM wishes";
        migrate.Parameters.AddWithValue("$eventId", DefaultEventId.ToString());
        await migrate.ExecuteNonQueryAsync(ct);
        var drop = connection.CreateCommand();
        drop.CommandText = "DROP TABLE wishes";
        await drop.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureWishColumnsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(event_wishes)";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await pragma.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1));
        if (columns.Contains("audioCandidatePath")) return;
        var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE event_wishes ADD COLUMN audioCandidatePath TEXT";
        await alter.ExecuteNonQueryAsync(ct);
    }

    private static KaraokeEventDto Read(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)), reader.GetString(1),
        reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)), reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
        reader.GetInt32(5) == 1, DateTimeOffset.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7));
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private const string EventSelect = "SELECT id,name,inviteToken,startsAt,endsAt,isActive,createdAt,description FROM karaoke_events";
}

public enum EventDeleteResult { Deleted, NotFound, Active, HasWishes, Protected }
