using System.Security.Cryptography;
using System.Text.Json;
using Karaoke.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

public sealed class EventRepository(IOptions<KaraokeOptions> options)
{
    public static readonly Guid DefaultEventId = new("00000000-0000-0000-0000-000000000001");
    public const string DefaultInviteToken = "neon-stage";
    public const string DefaultStageThemeId = "standard";
    public static IReadOnlyList<StageThemeDto> StageThemes => StageThemeCatalog.All;
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
                    createdAt TEXT NOT NULL, description TEXT, stageThemeId TEXT NOT NULL DEFAULT 'standard',
                    isOnline INTEGER NOT NULL DEFAULT 0, onlinePasswordHash TEXT, onlinePasswordSalt TEXT,
                    allowConversation INTEGER NOT NULL DEFAULT 0, stageImage BLOB, stageImageContentType TEXT
                );
                INSERT OR IGNORE INTO karaoke_events(id,name,inviteToken,startsAt,endsAt,isActive,createdAt,description)
                VALUES($id,'Direkt-Stage',$token,$now,NULL,1,$now,'Direkte lokale Bühne');
                UPDATE karaoke_events SET isActive=1,name='Direkt-Stage' WHERE id=$id;
                DROP INDEX IF EXISTS ix_karaoke_events_active;
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
            await EnsureEventColumnsAsync(connection, ct);
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
        GetByIdAsync(DefaultEventId, ct);

    public Task<KaraokeEventDto?> GetByTokenAsync(string token, CancellationToken ct) =>
        GetSingleAsync("inviteToken=$value", token, ct);

    public Task<KaraokeEventDto?> GetByIdAsync(Guid id, CancellationToken ct) =>
        GetSingleAsync("id=$value", id.ToString(), ct);

    public async Task<KaraokeEventDto> CreateAsync(CreateKaraokeEventRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("Der Eventname fehlt.");
        if (request.IsOnline && string.IsNullOrWhiteSpace(request.OnlinePassword))
            throw new ArgumentException("Für eine Online-Stage muss ein Kennwort vergeben werden.");
        if (request.IsOnline && request.OnlinePassword!.Length is < 4 or > 128)
            throw new ArgumentException("Das Online-Kennwort muss 4–128 Zeichen lang sein.");
        await InitializeAsync(ct);
        var stageThemeId = NormalizeStageThemeId(request.StageThemeId);
        var passwordSalt = request.IsOnline ? RandomNumberGenerator.GetBytes(16) : null;
        var passwordHash = passwordSalt is null ? null : HashPassword(request.OnlinePassword!, passwordSalt);
        var item = new KaraokeEventDto(Guid.NewGuid(), request.Name.Trim(), NewToken(), request.StartsAt,
            request.EndsAt, false, DateTimeOffset.UtcNow, request.Description?.Trim(), stageThemeId,
            request.IsOnline, passwordHash is not null, request.IsOnline && request.AllowConversation, false);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO karaoke_events(id,name,inviteToken,startsAt,endsAt,isActive,createdAt,description,stageThemeId,isOnline,onlinePasswordHash,onlinePasswordSalt,allowConversation) VALUES($id,$name,$token,$start,$end,0,$created,$description,$stageThemeId,$isOnline,$passwordHash,$passwordSalt,$allowConversation)";
        command.Parameters.AddWithValue("$id", item.Id.ToString());
        command.Parameters.AddWithValue("$name", item.Name);
        command.Parameters.AddWithValue("$token", item.InviteToken);
        command.Parameters.AddWithValue("$start", item.StartsAt.ToString("O"));
        command.Parameters.AddWithValue("$end", (object?)item.EndsAt?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$created", item.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$description", (object?)item.Description ?? DBNull.Value);
        command.Parameters.AddWithValue("$stageThemeId", item.StageThemeId);
        command.Parameters.AddWithValue("$isOnline", item.IsOnline ? 1 : 0);
        command.Parameters.AddWithValue("$passwordHash", (object?)passwordHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$passwordSalt", passwordSalt is null ? DBNull.Value : Convert.ToBase64String(passwordSalt));
        command.Parameters.AddWithValue("$allowConversation", item.AllowConversation ? 1 : 0);
        await command.ExecuteNonQueryAsync(ct);
        return item;
    }

    public async Task<IReadOnlyList<KaraokeEventDto>> GetAvailableOnlineAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        // In the multi-stage model a stage exists as soon as it is created.
        // isActive remains legacy event/publication metadata and must not make
        // a launcher-visible online stage impossible to join.
        command.CommandText = EventSelect + " WHERE isOnline=1 ORDER BY name";
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<KaraokeEventDto>();
        while (await reader.ReadAsync(ct)) result.Add(Read(reader));
        return result;
    }

    public async Task<KaraokeEventDto?> ValidateOnlineAccessAsync(string roomId, string? password,
        CancellationToken ct)
    {
        if (!Guid.TryParse(roomId, out var id)) return null;
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id,name,inviteToken,startsAt,endsAt,isActive,createdAt,description,stageThemeId,isOnline,onlinePasswordHash,allowConversation,CASE WHEN stageImage IS NULL THEN 0 ELSE 1 END,onlinePasswordSalt " +
            "FROM karaoke_events WHERE id=$id AND isOnline=1 LIMIT 1";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var item = Read(reader);
        if (reader.IsDBNull(10) || reader.IsDBNull(13)) return null;
        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(reader.GetString(13));
            expected = Convert.FromBase64String(reader.GetString(10));
        }
        catch (FormatException) { return null; }
        var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? string.Empty, salt, 120_000,
            HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected) ? item : null;
    }

    public async Task<KaraokeEventDto?> ActivateAsync(Guid id, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var activate = connection.CreateCommand();
        activate.CommandText = "UPDATE karaoke_events SET isActive=1 WHERE id=$id";
        activate.Parameters.AddWithValue("$id", id.ToString());
        var changed = await activate.ExecuteNonQueryAsync(ct);
        if (changed == 0) return null;
        return await GetByIdAsync(id, ct);
    }

    public async Task<bool> DeactivateAsync(Guid id, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        if (id == DefaultEventId) return false;
        var deactivate = connection.CreateCommand();
        deactivate.CommandText = "UPDATE karaoke_events SET isActive=0 WHERE id=$id AND isActive=1";
        deactivate.Parameters.AddWithValue("$id", id.ToString());
        var changed = await deactivate.ExecuteNonQueryAsync(ct);
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
        statements.Add("DELETE FROM event_playback_state WHERE eventId=$id");
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
        string.IsNullOrWhiteSpace(token) ? DefaultEventId : (await GetByTokenAsync(token, ct))?.Id;

    public async Task<IReadOnlyList<StageLauncherDto>> GetLauncherStagesAsync(CancellationToken ct) =>
        (await GetAllAsync(ct))
            .OrderByDescending(item => item.Id == DefaultEventId).ThenBy(item => item.Name)
            .Select(item => new StageLauncherDto(item.Id, item.Name, item.InviteToken,
                item.Id == DefaultEventId, item.IsOnline, item.HasOnlinePassword, item.HasImage,
                item.StageThemeId)).ToArray();

    public async Task<bool> SetImageAsync(Guid id, Stream source, long length, string? contentType,
        CancellationToken ct)
    {
        if (length is <= 0 or > 5_000_000) throw new ArgumentException("Das Stage-Bild darf höchstens 5 MB groß sein.");
        var normalizedType = contentType?.ToLowerInvariant() switch
        {
            "image/png" => "image/png", "image/jpeg" => "image/jpeg", "image/webp" => "image/webp",
            _ => throw new ArgumentException("Bitte PNG, JPEG oder WebP verwenden.")
        };
        await using var memory = new MemoryStream();
        await source.CopyToAsync(memory, ct);
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE karaoke_events SET stageImage=$image,stageImageContentType=$type WHERE id=$id";
        command.Parameters.AddWithValue("$image", memory.ToArray());
        command.Parameters.AddWithValue("$type", normalizedType);
        command.Parameters.AddWithValue("$id", id.ToString());
        return await command.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<(byte[] Data, string ContentType)?> GetImageAsync(Guid id, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT stageImage,stageImageContentType FROM karaoke_events WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) && !reader.IsDBNull(0)
            ? ((byte[])reader[0], reader.IsDBNull(1) ? "image/png" : reader.GetString(1)) : null;
    }

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

    private static async Task EnsureEventColumnsAsync(SqliteConnection connection, CancellationToken ct)
    {
        var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(karaoke_events)";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await pragma.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1));
        foreach (var definition in new[]
        {
            $"stageThemeId TEXT NOT NULL DEFAULT '{DefaultStageThemeId}'",
            "isOnline INTEGER NOT NULL DEFAULT 0",
            "onlinePasswordHash TEXT",
            "onlinePasswordSalt TEXT",
            "allowConversation INTEGER NOT NULL DEFAULT 0",
            "stageImage BLOB",
            "stageImageContentType TEXT"
        })
        {
            var name = definition[..definition.IndexOf(' ')];
            if (columns.Contains(name)) continue;
            var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE karaoke_events ADD COLUMN {definition}";
            await alter.ExecuteNonQueryAsync(ct);
        }
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
        reader.GetInt32(5) == 1, DateTimeOffset.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.IsDBNull(8) ? DefaultStageThemeId : NormalizeStageThemeId(reader.GetString(8)),
        reader.GetInt32(9) == 1, !reader.IsDBNull(10), reader.GetInt32(11) == 1,
        reader.GetInt32(12) == 1);
    private static string NormalizeStageThemeId(string? value) =>
        StageThemes.Any(theme => string.Equals(theme.Id, value?.Trim(), StringComparison.OrdinalIgnoreCase))
            ? StageThemes.First(theme => string.Equals(theme.Id, value?.Trim(), StringComparison.OrdinalIgnoreCase)).Id
            : DefaultStageThemeId;
    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string HashPassword(string password, byte[] salt) => Convert.ToBase64String(
        Rfc2898DeriveBytes.Pbkdf2(password, salt, 120_000, HashAlgorithmName.SHA256, 32));
    private const string EventSelect = "SELECT id,name,inviteToken,startsAt,endsAt,isActive,createdAt,description,stageThemeId,isOnline,onlinePasswordHash,allowConversation,CASE WHEN stageImage IS NULL THEN 0 ELSE 1 END FROM karaoke_events";
}

public enum EventDeleteResult { Deleted, NotFound, Active, HasWishes, Protected }

internal static class StageThemeCatalog
{
    private const string EnvironmentVariable = "NEONSTAGE_STAGE_THEMES_FILE";
    private static readonly Lazy<IReadOnlyList<StageThemeDto>> Themes = new(Load);
    internal static IReadOnlyList<StageThemeDto> All => Themes.Value;

    private static IReadOnlyList<StageThemeDto> Load()
    {
        var themes = new List<StageThemeDto>
        {
            new(EventRepository.DefaultStageThemeId, "Neon Stage · Standard",
                "Die bisherige Neon-Stage mit dem bekannten Grid-Hintergrund.", true)
        };
        var configured = Environment.GetEnvironmentVariable(EnvironmentVariable);
        var candidates = new[]
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "stage-themes.private.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "stage-themes.private.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "Karaoke.Server", "stage-themes.private.json")
        };
        var path = candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
        if (path is null) return themes;
        try
        {
            var privateThemes = JsonSerializer.Deserialize<PrivateStageThemeCatalog>(File.ReadAllText(path),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            foreach (var theme in privateThemes?.Themes ?? [])
            {
                if (string.IsNullOrWhiteSpace(theme.Id) || themes.Any(item =>
                        string.Equals(item.Id, theme.Id, StringComparison.OrdinalIgnoreCase))) continue;
                themes.Add(theme with { IsDefault = false });
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Private stage theme catalog could not be loaded: {exception.Message}");
        }
        return themes;
    }

    private sealed record PrivateStageThemeCatalog(IReadOnlyList<StageThemeDto> Themes);
}
