using System.Text.Json;
using Karaoke.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Karaoke.Server;

public sealed class WishlistRepository(IOptions<KaraokeOptions> options, EventRepository events, ChangeFeedService changes)
{
    private readonly string _connectionString = $"Data Source={Path.GetFullPath(options.Value.DatabasePath)}";

    public async Task InitializeAsync(CancellationToken ct)
    {
        await events.InitializeAsync(ct);
    }

    public async Task<IReadOnlyList<WishDto>> GetAsync(Guid eventId, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,trackJson,requestedBy,requestedAt,status FROM event_wishes WHERE eventId=$eventId ORDER BY requestedAt DESC";
        command.Parameters.AddWithValue("$eventId", eventId.ToString());
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<WishDto>();
        while (await reader.ReadAsync(ct))
            result.Add(new(Guid.Parse(reader.GetString(0)), JsonSerializer.Deserialize<SpotifyTrackDto>(reader.GetString(1))!, reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)), reader.GetString(4)));
        return result;
    }

    public async Task<WishDto> AddAsync(Guid eventId, AddWishRequest request, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var existing = connection.CreateCommand();
        existing.CommandText = "SELECT id,trackJson,requestedBy,requestedAt,status FROM event_wishes WHERE eventId=$eventId AND spotifyId=$spotifyId";
        existing.Parameters.AddWithValue("$eventId", eventId.ToString());
        existing.Parameters.AddWithValue("$spotifyId", request.Track.Id);
        await using (var reader = await existing.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
                return new(Guid.Parse(reader.GetString(0)), JsonSerializer.Deserialize<SpotifyTrackDto>(reader.GetString(1))!, reader.GetString(2), DateTimeOffset.Parse(reader.GetString(3)), reader.GetString(4));

        var wish = new WishDto(Guid.NewGuid(), request.Track, string.IsNullOrWhiteSpace(request.RequestedBy) ? "Gast" : request.RequestedBy.Trim(), DateTimeOffset.UtcNow, "Gewünscht");
        var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO event_wishes(id,eventId,spotifyId,trackJson,requestedBy,requestedAt,status) VALUES($id,$eventId,$spotifyId,$json,$by,$at,$status)";
        insert.Parameters.AddWithValue("$id", wish.Id.ToString()); insert.Parameters.AddWithValue("$spotifyId", wish.Track.Id);
        insert.Parameters.AddWithValue("$eventId", eventId.ToString());
        insert.Parameters.AddWithValue("$json", JsonSerializer.Serialize(wish.Track)); insert.Parameters.AddWithValue("$by", wish.RequestedBy);
        insert.Parameters.AddWithValue("$at", wish.RequestedAt.ToString("O")); insert.Parameters.AddWithValue("$status", wish.Status);
        await insert.ExecuteNonQueryAsync(ct);
        changes.Publish("wishlist-changed");
        return wish;
    }

    public async Task<bool> RemoveAsync(Guid eventId, Guid id, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct);
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM event_wishes WHERE id=$id AND eventId=$eventId";
        command.Parameters.AddWithValue("$id", id.ToString());
        command.Parameters.AddWithValue("$eventId", eventId.ToString());
        var removed = await command.ExecuteNonQueryAsync(ct) > 0;
        if (removed) changes.Publish("wishlist-changed");
        return removed;
    }
}
