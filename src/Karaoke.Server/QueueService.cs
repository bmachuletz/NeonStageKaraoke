using Karaoke.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

public sealed class QueueService(IOptions<KaraokeOptions> options, IHubContext<KaraokeHub> hubContext, EventRepository events)
{
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly string _connectionString = $"Data Source={Path.GetFullPath(options.Value.DatabasePath)}";

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.Value.DatabasePath))!);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS queue_entries(
                id TEXT PRIMARY KEY,
                songId TEXT NOT NULL,
                requestedBy TEXT NOT NULL,
                position INTEGER NOT NULL,
                status TEXT NOT NULL,
                addedAt TEXT NOT NULL,
                startedAt TEXT,
                finishedAt TEXT,
                locationId TEXT,
                singerControlsMix INTEGER NOT NULL DEFAULT 0,
                singerMicrophoneVolume REAL NOT NULL DEFAULT 1
            );
            CREATE INDEX IF NOT EXISTS ix_queue_status_position ON queue_entries(status, position);
            CREATE TABLE IF NOT EXISTS event_playback_state(
                eventId TEXT PRIMARY KEY,
                isRunning INTEGER NOT NULL,
                isPaused INTEGER NOT NULL,
                currentEntryId TEXT,
                positionMs INTEGER NOT NULL DEFAULT 0,
                positionUpdatedAt TEXT,
                speed REAL NOT NULL DEFAULT 1,
                revision INTEGER NOT NULL DEFAULT 0
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureQueueColumnsAsync(connection, cancellationToken);
        await events.InitializeAsync(cancellationToken);
    }

    public async Task<QueueEntryDto> AddAsync(Guid eventId, SongDto song, string requestedBy, CancellationToken cancellationToken)
        => await AddAsync(eventId, song, requestedBy, null, cancellationToken);

    public async Task<QueueEntryDto> AddAsync(Guid eventId, SongDto song, string requestedBy,
        Guid? locationId, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync(cancellationToken);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            var waitingBefore = connection.CreateCommand();
            waitingBefore.CommandText = "SELECT COUNT(*) FROM queue_entries WHERE eventId=$eventId AND status='Waiting'";
            waitingBefore.Parameters.AddWithValue("$eventId", eventId.ToString());
            var wasEmpty = Convert.ToInt64(await waitingBefore.ExecuteScalarAsync(cancellationToken)) == 0;
            var id = Guid.NewGuid();
            var addedAt = DateTimeOffset.UtcNow;
            var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO queue_entries(id,songId,requestedBy,position,status,addedAt,eventId,locationId)
                VALUES($id,$songId,$requestedBy,(SELECT COALESCE(MAX(position),0)+1 FROM queue_entries WHERE eventId=$eventId AND status='Waiting'),'Waiting',$addedAt,$eventId,$locationId)
                """;
            command.Parameters.AddWithValue("$id", id.ToString());
            command.Parameters.AddWithValue("$songId", song.Id.ToString());
            command.Parameters.AddWithValue("$requestedBy", string.IsNullOrWhiteSpace(requestedBy) ? "Gast" : requestedBy.Trim());
            command.Parameters.AddWithValue("$addedAt", addedAt.ToString("O"));
            command.Parameters.AddWithValue("$eventId", eventId.ToString());
            command.Parameters.AddWithValue("$locationId", (object?)locationId?.ToString("N") ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);

            // The first title becomes current so role automation can promote its
            // location to Singer. Playback deliberately remains stopped until
            // that Stage presses Play.
            var playback = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
            if (wasEmpty && playback.CurrentId is null)
                await AdvanceAsync(connection, eventId, null, cancellationToken, startRunning: false);

            var entry = (await GetEntryAsync(connection, id, cancellationToken))!;
            await PublishStateAsync(connection, eventId, cancellationToken);
            return entry;
        }
        finally { _mutex.Release(); }
    }

    public Task<bool> RemoveAsync(Guid eventId, Guid id, CancellationToken cancellationToken) => MutateAsync(eventId, async connection =>
    {
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE queue_entries SET status='Removed',finishedAt=$now WHERE id=$id AND eventId=$eventId AND status='Waiting'";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$eventId", eventId.ToString());
        var changed = await command.ExecuteNonQueryAsync(cancellationToken) > 0;
        if (changed) await NormalizePositionsAsync(connection, eventId, cancellationToken);
        return changed;
    }, cancellationToken);

    public Task ClearAsync(Guid eventId, CancellationToken cancellationToken) => MutateAsync(eventId, async connection =>
    {
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE queue_entries SET status='Removed',finishedAt=$now WHERE eventId=$eventId AND status='Waiting'";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$eventId", eventId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }, cancellationToken);

    public Task<bool> ReorderAsync(Guid eventId, Guid id, int requestedPosition, CancellationToken cancellationToken) => MutateAsync(eventId, async connection =>
    {
        var entries = await ReadWaitingIdsAsync(connection, eventId, cancellationToken);
        var oldIndex = entries.IndexOf(id);
        if (oldIndex < 0) return false;
        entries.RemoveAt(oldIndex);
        entries.Insert(Math.Clamp(requestedPosition - 1, 0, entries.Count), id);
        await WritePositionsAsync(connection, entries, cancellationToken);
        return true;
    }, cancellationToken);

    public Task<PlaybackStateDto> StartAsync(Guid eventId, CancellationToken cancellationToken) => MutateStateAsync(eventId, async connection =>
    {
        var state = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
        if (state.CurrentId is null)
            await AdvanceAsync(connection, eventId, null, cancellationToken);
        state = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
        if (state.CurrentId is not null)
            await SetPlaybackFlagsAsync(connection, eventId, true, false, cancellationToken);
    }, cancellationToken);

    public Task<PlaybackStateDto> StartFreshAsync(Guid eventId, CancellationToken cancellationToken) => MutateStateAsync(eventId, async connection =>
    {
        var state = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
        if (state.CurrentId is not null)
            await PutAtFrontAsync(connection, eventId, state.CurrentId.Value, cancellationToken);
        await SetCurrentAsync(connection, eventId, null, false, false, cancellationToken);
        await AdvanceAsync(connection, eventId, null, cancellationToken);
    }, cancellationToken);

    public Task<PlaybackStateDto> PauseAsync(Guid eventId, CancellationToken cancellationToken) => MutateStateAsync(eventId, async connection =>
    {
        var state = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
        if (state.CurrentId is not null && state.Running)
            await SetPlaybackFlagsAsync(connection, eventId, true, true, cancellationToken);
    }, cancellationToken);

    public Task<PlaybackStateDto> ResumeAsync(Guid eventId, CancellationToken cancellationToken) => MutateStateAsync(eventId, async connection =>
    {
        var state = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
        if (state.CurrentId is not null)
            await SetPlaybackFlagsAsync(connection, eventId, true, false, cancellationToken);
    }, cancellationToken);

    public Task<PlaybackStateDto> NextAsync(Guid eventId, CancellationToken cancellationToken) => MutateStateAsync(eventId, async connection =>
    {
        var state = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
        await AdvanceAsync(connection, eventId, state.CurrentId, cancellationToken);
    }, cancellationToken);

    public Task<PlaybackStateDto> CompleteAsync(Guid eventId, Guid expectedEntryId, CancellationToken cancellationToken) => MutateStateAsync(eventId, async connection =>
    {
        var state = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
        if (state.CurrentId != expectedEntryId) return;
        await AdvanceAsync(connection, eventId, state.CurrentId, cancellationToken);
    }, cancellationToken);

    public Task<PlaybackStateDto> SkipAsync(Guid eventId, CancellationToken cancellationToken) => MutateStateAsync(eventId, async connection =>
    {
        var state = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
        if (state.CurrentId is not null)
            await SetEntryStatusAsync(connection, state.CurrentId.Value, QueueEntryStatus.Skipped, true, cancellationToken);
        await AdvanceAsync(connection, eventId, null, cancellationToken);
    }, cancellationToken);

    public Task<PlaybackStateDto> PreviousAsync(Guid eventId, CancellationToken cancellationToken) => MutateStateAsync(eventId, async connection =>
    {
        var previous = connection.CreateCommand();
        previous.CommandText = "SELECT id FROM queue_entries WHERE eventId=$eventId AND status='Completed' ORDER BY finishedAt DESC LIMIT 1";
        previous.Parameters.AddWithValue("$eventId", eventId.ToString());
        var value = await previous.ExecuteScalarAsync(cancellationToken);
        if (value is not string previousId) return;
        var state = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
        if (state.CurrentId is not null)
            await PutAtFrontAsync(connection, eventId, state.CurrentId.Value, cancellationToken);
        await SetEntryStatusAsync(connection, Guid.Parse(previousId), QueueEntryStatus.Playing, false, cancellationToken);
        await SetCurrentAsync(connection, eventId, Guid.Parse(previousId), true, false, cancellationToken);
    }, cancellationToken);

    public Task<PlaybackStateDto> RestartAsync(Guid eventId, CancellationToken cancellationToken) => MutateStateAsync(eventId, async connection =>
    {
        var state = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
        if (state.CurrentId is null) return;
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE queue_entries SET startedAt=$now,finishedAt=NULL,status='Playing' WHERE id=$id";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", state.CurrentId.Value.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
        await SetCurrentAsync(connection, eventId, state.CurrentId, true, false, cancellationToken);
    }, cancellationToken);

    public Task<PlaybackStateDto> StopAsync(Guid eventId, CancellationToken cancellationToken) => MutateStateAsync(eventId, async connection =>
    {
        var state = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
        if (state.CurrentId is not null)
            await PutAtFrontAsync(connection, eventId, state.CurrentId.Value, cancellationToken);
        await SetCurrentAsync(connection, eventId, null, false, false, cancellationToken);
    }, cancellationToken);

    public async Task<PlaybackStateDto> GetStateAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return await ReadStateAsync(connection, eventId, cancellationToken);
    }

    public async Task<PlaybackStateDto> GetActiveStateAsync(CancellationToken cancellationToken)
    {
        return await GetStateAsync(EventRepository.DefaultEventId, cancellationToken);
    }

    public async Task<PlaybackPositionDto?> UpdatePositionAsync(Guid eventId, PlaybackPositionUpdateRequest request, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync(cancellationToken);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            var row = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
            if (row.CurrentId != request.QueueEntryId || !row.Running) return null;

            var position = TimeSpan.FromMilliseconds(Math.Clamp(request.Position.TotalMilliseconds, 0, TimeSpan.FromHours(24).TotalMilliseconds));
            var updatedAt = DateTimeOffset.UtcNow;
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE event_playback_state SET positionMs=$position,positionUpdatedAt=$updatedAt,revision=revision+1 WHERE eventId=$eventId";
            command.Parameters.AddWithValue("$eventId", eventId.ToString());
            command.Parameters.AddWithValue("$position", (long)position.TotalMilliseconds);
            command.Parameters.AddWithValue("$updatedAt", updatedAt.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            var revision = row.Revision + 1;
            var update = new PlaybackPositionDto(request.QueueEntryId, position, updatedAt, revision);
            await hubContext.Clients.All.SendAsync(KaraokeHubEvents.PlaybackPositionChanged, update, cancellationToken);
            return update;
        }
        finally { _mutex.Release(); }
    }

    public async Task<QueueEntryDto?> UpdateSingerMixAsync(Guid eventId, UpdateSingerMixRequest request,
        CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync(cancellationToken);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            var playback = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
            if (playback.CurrentId != request.QueueEntryId) return null;
            var entry = await GetEntryAsync(connection, request.QueueEntryId, cancellationToken);
            if (entry?.LocationId != request.LocationId ||
                !entry.RequestedBy.Equals(request.RequestedBy?.Trim(), StringComparison.OrdinalIgnoreCase))
                return null;
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE queue_entries SET singerControlsMix=$locked,singerMicrophoneVolume=$volume WHERE id=$id AND eventId=$eventId";
            command.Parameters.AddWithValue("$locked", request.SingerControlsMix ? 1 : 0);
            command.Parameters.AddWithValue("$volume", Math.Clamp(request.MicrophoneVolume, 0, 1));
            command.Parameters.AddWithValue("$id", request.QueueEntryId.ToString());
            command.Parameters.AddWithValue("$eventId", eventId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
            await PublishStateAsync(connection, eventId, cancellationToken);
            return await GetEntryAsync(connection, request.QueueEntryId, cancellationToken);
        }
        finally { _mutex.Release(); }
    }

    public async Task<IReadOnlySet<Guid>> GetQueuedSongIdsAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(eventId, cancellationToken);
        var ids = state.Queue.Select(entry => entry.Song.Id).ToHashSet();
        if (state.Current is not null) ids.Add(state.Current.Song.Id);
        return ids;
    }

    private async Task<T> MutateAsync<T>(Guid eventId, Func<SqliteConnection, Task<T>> mutation, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            await InitializeAsync(cancellationToken);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            var result = await mutation(connection);
            await PublishStateAsync(connection, eventId, cancellationToken);
            return result;
        }
        finally { _mutex.Release(); }
    }

    private Task<PlaybackStateDto> MutateStateAsync(Guid eventId, Func<SqliteConnection, Task> mutation, CancellationToken cancellationToken)
    {
        return MutateAsync(eventId, async connection =>
        {
            await mutation(connection);
            return await ReadStateAsync(connection, eventId, cancellationToken);
        }, cancellationToken);
    }

    private async Task PublishStateAsync(SqliteConnection connection, Guid eventId, CancellationToken cancellationToken)
    {
        var state = await ReadStateAsync(connection, eventId, cancellationToken);
        await hubContext.Clients.All.SendAsync(KaraokeHubEvents.QueueChanged, state, cancellationToken);
    }

    private static async Task AdvanceAsync(SqliteConnection connection, Guid eventId, Guid? completedId,
        CancellationToken cancellationToken, bool startRunning = true)
    {
        if (completedId is not null)
            await SetEntryStatusAsync(connection, completedId.Value, QueueEntryStatus.Completed, true, cancellationToken);
        var next = connection.CreateCommand();
        next.CommandText = "SELECT id FROM queue_entries WHERE eventId=$eventId AND status='Waiting' ORDER BY position LIMIT 1";
        next.Parameters.AddWithValue("$eventId", eventId.ToString());
        var value = await next.ExecuteScalarAsync(cancellationToken);
        if (value is not string nextId)
        {
            await SetCurrentAsync(connection, eventId, null, false, false, cancellationToken);
            return;
        }
        var id = Guid.Parse(nextId);
        await SetEntryStatusAsync(connection, id, QueueEntryStatus.Playing, false, cancellationToken);
        await SetCurrentAsync(connection, eventId, id, startRunning, false, cancellationToken);
        await NormalizePositionsAsync(connection, eventId, cancellationToken);
    }

    private static async Task PutAtFrontAsync(SqliteConnection connection, Guid eventId, Guid id, CancellationToken cancellationToken)
    {
        var shift = connection.CreateCommand();
        shift.CommandText = "UPDATE queue_entries SET position=position+1 WHERE eventId=$eventId AND status='Waiting'";
        shift.Parameters.AddWithValue("$eventId", eventId.ToString());
        await shift.ExecuteNonQueryAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE queue_entries SET status='Waiting',position=1,startedAt=NULL,finishedAt=NULL WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetEntryStatusAsync(SqliteConnection connection, Guid id, QueueEntryStatus status, bool finished, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = finished
            ? "UPDATE queue_entries SET status=$status,finishedAt=$now WHERE id=$id"
            : "UPDATE queue_entries SET status=$status,startedAt=$now,finishedAt=NULL,position=0 WHERE id=$id";
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", id.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetPlaybackFlagsAsync(SqliteConnection connection, Guid eventId, bool running, bool paused, CancellationToken cancellationToken)
    {
        await EnsurePlaybackRowAsync(connection, eventId, cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE event_playback_state SET isRunning=$running,isPaused=$paused,positionUpdatedAt=$now,revision=revision+1 WHERE eventId=$eventId";
        command.Parameters.AddWithValue("$eventId", eventId.ToString());
        command.Parameters.AddWithValue("$running", running ? 1 : 0);
        command.Parameters.AddWithValue("$paused", paused ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetCurrentAsync(SqliteConnection connection, Guid eventId, Guid? id, bool running, bool paused, CancellationToken cancellationToken)
    {
        await EnsurePlaybackRowAsync(connection, eventId, cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE event_playback_state SET currentEntryId=$id,isRunning=$running,isPaused=$paused,positionMs=0,positionUpdatedAt=$now,revision=revision+1 WHERE eventId=$eventId";
        command.Parameters.AddWithValue("$eventId", eventId.ToString());
        command.Parameters.AddWithValue("$id", (object?)id?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue("$running", running ? 1 : 0);
        command.Parameters.AddWithValue("$paused", paused ? 1 : 0);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(bool Running, bool Paused, Guid? CurrentId, TimeSpan Position, DateTimeOffset? UpdatedAt, double Speed, long Revision)> ReadPlaybackRowAsync(SqliteConnection connection, Guid eventId, CancellationToken cancellationToken)
    {
        await EnsurePlaybackRowAsync(connection, eventId, cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT isRunning,isPaused,currentEntryId,positionMs,positionUpdatedAt,speed,revision FROM event_playback_state WHERE eventId=$eventId";
        command.Parameters.AddWithValue("$eventId", eventId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (
            reader.GetInt32(0) == 1,
            reader.GetInt32(1) == 1,
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            TimeSpan.FromMilliseconds(reader.GetInt64(3)),
            reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)),
            reader.GetDouble(5),
            reader.GetInt64(6));
    }

    private static async Task<PlaybackStateDto> ReadStateAsync(SqliteConnection connection, Guid eventId, CancellationToken cancellationToken)
    {
        var row = await ReadPlaybackRowAsync(connection, eventId, cancellationToken);
        var current = row.CurrentId is null ? null : await GetEntryAsync(connection, row.CurrentId.Value, cancellationToken);
        var queue = await ReadWaitingAsync(connection, eventId, cancellationToken);
        return new(row.Running, current, queue, row.Paused, row.Position, row.UpdatedAt, row.Speed, row.Revision);
    }

    private static async Task EnsurePlaybackRowAsync(SqliteConnection connection, Guid eventId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO event_playback_state(eventId,isRunning,isPaused,currentEntryId) VALUES($eventId,0,0,NULL)";
        command.Parameters.AddWithValue("$eventId", eventId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureQueueColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(queue_entries)";
        await using (var reader = await pragma.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(1));
        foreach (var definition in new[]
        {
            "locationId TEXT",
            "singerControlsMix INTEGER NOT NULL DEFAULT 0",
            "singerMicrophoneVolume REAL NOT NULL DEFAULT 1"
        })
        {
            var name = definition[..definition.IndexOf(' ')];
            if (existing.Contains(name)) continue;
            var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE queue_entries ADD COLUMN {definition}";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task<QueueEntryDto?> GetEntryAsync(SqliteConnection connection, Guid id, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = EntrySelect + " WHERE q.id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEntry(reader) : null;
    }

    private static async Task<IReadOnlyList<QueueEntryDto>> ReadWaitingAsync(SqliteConnection connection, Guid eventId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = EntrySelect + " WHERE q.eventId=$eventId AND q.status='Waiting' ORDER BY q.position";
        command.Parameters.AddWithValue("$eventId", eventId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<QueueEntryDto>();
        while (await reader.ReadAsync(cancellationToken)) entries.Add(ReadEntry(reader));
        return entries;
    }

    private static async Task<List<Guid>> ReadWaitingIdsAsync(SqliteConnection connection, Guid eventId, CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM queue_entries WHERE eventId=$eventId AND status='Waiting' ORDER BY position";
        command.Parameters.AddWithValue("$eventId", eventId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add(Guid.Parse(reader.GetString(0)));
        return ids;
    }

    private static async Task NormalizePositionsAsync(SqliteConnection connection, Guid eventId, CancellationToken cancellationToken)
    {
        var ids = await ReadWaitingIdsAsync(connection, eventId, cancellationToken);
        await WritePositionsAsync(connection, ids, cancellationToken);
    }

    private static async Task WritePositionsAsync(SqliteConnection connection, IReadOnlyList<Guid> ids, CancellationToken cancellationToken)
    {
        for (var index = 0; index < ids.Count; index++)
        {
            var command = connection.CreateCommand();
            command.CommandText = "UPDATE queue_entries SET position=$position WHERE id=$id";
            command.Parameters.AddWithValue("$position", index + 1);
            command.Parameters.AddWithValue("$id", ids[index].ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static QueueEntryDto ReadEntry(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        new(Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetDouble(5), reader.GetInt32(6) == 1, false, reader.GetInt32(7) == 1),
        reader.GetString(8), DateTimeOffset.Parse(reader.GetString(9)), reader.GetInt32(10),
        Enum.Parse<QueueEntryStatus>(reader.GetString(11)),
        reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
        reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)),
        reader.IsDBNull(14) ? null : Guid.Parse(reader.GetString(14)),
        reader.GetInt32(15) == 1, reader.GetDouble(16));

    private const string EntrySelect = """
        SELECT q.id,s.id,s.title,s.artist,s.album,s.duration,s.hasLyrics,s.hasCover,q.requestedBy,q.addedAt,q.position,q.status,q.startedAt,q.finishedAt,q.locationId,q.singerControlsMix,q.singerMicrophoneVolume
        FROM queue_entries q JOIN songs s ON s.id=q.songId
        """;
}
