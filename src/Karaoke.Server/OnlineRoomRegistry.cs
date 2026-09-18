using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Karaoke.Contracts;

namespace Karaoke.Server;

internal sealed class OnlineSingerConflictException(string message) : InvalidOperationException(message);

internal sealed class OnlineRoomRegistry(OnlineSettingsService configured, TimeProvider timeProvider)
{
    private sealed record Participant(string Id, string Name, OnlineRole Role, Guid? LocationId,
        bool AllowConversation, DateTimeOffset LastSeenAt);
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, Participant>> _rooms =
        new(StringComparer.OrdinalIgnoreCase);

    public OnlineRoomStateDto Join(OnlineJoinRequest request, bool allowConversation = false)
    {
        var roomId = NormalizeRoomId(request.RoomId);
        var participantId = NormalizeParticipantId(request.ParticipantId);
        var displayName = NormalizeDisplayName(request.DisplayName);
        lock (_gate)
        {
            var room = GetRoom(roomId);
            Expire(room);
            EnsureSingerAvailable(room, participantId, request.Role);
            room[participantId] = new Participant(participantId, displayName, request.Role,
                request.LocationId, allowConversation, timeProvider.GetUtcNow());
            return Snapshot(roomId, participantId, room);
        }
    }

    public OnlineRoomStateDto ChangeRole(string roomId, string participantId, OnlineRole role)
    {
        roomId = NormalizeRoomId(roomId);
        participantId = NormalizeParticipantId(participantId);
        lock (_gate)
        {
            var room = GetExistingRoom(roomId);
            Expire(room);
            if (!room.TryGetValue(participantId, out var participant))
                throw new KeyNotFoundException("Der Online-Teilnehmer ist nicht mehr verbunden.");
            EnsureSingerAvailable(room, participantId, role);
            room[participantId] = participant with { Role = role, LastSeenAt = timeProvider.GetUtcNow() };
            return Snapshot(roomId, participantId, room);
        }
    }

    public OnlineRoomStateDto Heartbeat(string roomId, string participantId)
    {
        roomId = NormalizeRoomId(roomId);
        participantId = NormalizeParticipantId(participantId);
        lock (_gate)
        {
            var room = GetExistingRoom(roomId);
            Expire(room);
            if (!room.TryGetValue(participantId, out var participant))
                throw new KeyNotFoundException("Der Online-Teilnehmer ist nicht mehr verbunden.");
            room[participantId] = participant with { LastSeenAt = timeProvider.GetUtcNow() };
            return Snapshot(roomId, participantId, room);
        }
    }

    public void Leave(string roomId, string participantId)
    {
        roomId = NormalizeRoomId(roomId);
        participantId = NormalizeParticipantId(participantId);
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room)) return;
            room.Remove(participantId);
            if (room.Count == 0) _rooms.Remove(roomId);
        }
    }

    public int ParticipantCount(string roomId)
    {
        roomId = NormalizeRoomId(roomId);
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room)) return 0;
            Expire(room);
            return room.Count;
        }
    }

    public bool IsLocationJoined(string roomId, Guid locationId)
    {
        roomId = NormalizeRoomId(roomId);
        lock (_gate)
        {
            if (!_rooms.TryGetValue(roomId, out var room)) return false;
            Expire(room);
            return room.Values.Any(item => item.LocationId == locationId);
        }
    }

    private Dictionary<string, Participant> GetRoom(string roomId)
    {
        if (_rooms.TryGetValue(roomId, out var room)) return room;
        room = new(StringComparer.Ordinal);
        _rooms.Add(roomId, room);
        return room;
    }

    private Dictionary<string, Participant> GetExistingRoom(string roomId) =>
        _rooms.TryGetValue(roomId, out var room)
            ? room
            : throw new KeyNotFoundException("Der Online-Room existiert nicht mehr.");

    private void Expire(Dictionary<string, Participant> room)
    {
        var cutoff = timeProvider.GetUtcNow().AddSeconds(
            -Math.Clamp(configured.Current.ParticipantLeaseSeconds, 6, 120));
        foreach (var id in room.Values.Where(item => item.LastSeenAt < cutoff).Select(item => item.Id).ToArray())
            room.Remove(id);
    }

    private static void EnsureSingerAvailable(Dictionary<string, Participant> room,
        string participantId, OnlineRole requested)
    {
        if (requested != OnlineRole.Singer) return;
        var singer = room.Values.FirstOrDefault(item => item.Role == OnlineRole.Singer && item.Id != participantId);
        if (singer is not null)
            throw new OnlineSingerConflictException($"{singer.Name} ist bereits Singer in diesem Room.");
    }

    private static OnlineRoomStateDto Snapshot(string roomId, string participantId,
        Dictionary<string, Participant> room)
    {
        var local = room[participantId];
        return new(roomId, participantId, local.Role, room.Values
            .OrderByDescending(item => item.Role)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(item => new OnlineParticipantDto(item.Id, item.Name, item.Role, item.LastSeenAt))
            .ToArray(), local.AllowConversation);
    }

    internal static string NormalizeRoomId(string value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (!Regex.IsMatch(normalized, "^[A-Z0-9][A-Z0-9_-]{2,31}$"))
            throw new ArgumentException("Room-ID: 3–32 Zeichen, nur A–Z, 0–9, _ und -.");
        return normalized;
    }

    private static string NormalizeParticipantId(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length is < 3 or > 80 || normalized.Any(character =>
                !char.IsLetterOrDigit(character) && character is not ('-' or '_')))
            throw new ArgumentException("Die Teilnehmer-ID ist ungültig.");
        return normalized;
    }

    private static string NormalizeDisplayName(string value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length is < 1 or > 60) throw new ArgumentException("Der Standortname ist ungültig.");
        return normalized;
    }
}

internal sealed class LiveKitTokenIssuer(OnlineSettingsService configured, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public OnlineRoomSessionDto Issue(OnlineRoomStateDto state)
    {
        var options = configured.Current;
        if (!options.IsConfigured)
            throw new InvalidOperationException("Online-Karaoke ist nicht vollständig konfiguriert.");
        var now = timeProvider.GetUtcNow();
        var expires = now.AddMinutes(Math.Clamp(options.TokenLifetimeMinutes, 5, 240));
        var liveKitRoom = options.RoomPrefix + state.RoomId.ToLowerInvariant();
        var participant = state.Participants.Single(item => item.ParticipantId == state.ParticipantId);
        var metadata = JsonSerializer.Serialize(new
        {
            neonStageRole = state.Role.ToString(),
            neonStageRoom = state.RoomId
        }, JsonOptions);
        var videoGrant = new Dictionary<string, object?>
        {
            ["roomJoin"] = true,
            ["room"] = liveKitRoom,
            ["canSubscribe"] = true,
            ["canPublish"] = state.Role == OnlineRole.Singer || state.AllowConversation,
            ["canPublishData"] = state.Role == OnlineRole.Singer
        };
        if (state.Role == OnlineRole.Listener && state.AllowConversation)
            videoGrant["canPublishSources"] = new[] { "microphone" };
        var payload = new Dictionary<string, object?>
        {
            ["iss"] = options.ApiKey,
            ["sub"] = state.ParticipantId,
            ["name"] = participant.DisplayName,
            ["nbf"] = now.AddSeconds(-5).ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["metadata"] = metadata,
            // Timeline beacons are sent by the active Singer over LiveKit's
            // data channel. Conversation listeners may publish only a track
            // explicitly marked as microphone.
            ["video"] = videoGrant
        };
        var token = Sign(payload, options.ApiSecret);
        return new(true, "LiveKit", options.ServerUrl.TrimEnd('/'), state.RoomId,
            state.ParticipantId, state.Role, token, expires, state.Participants,
            state.AllowConversation);
    }

    internal static string Sign(IReadOnlyDictionary<string, object?> payload, string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("LiveKit API secret fehlt.");
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "HS256", typ = "JWT" }));
        var body = Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
        var unsigned = header + "." + body;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return unsigned + "." + Base64Url(hmac.ComputeHash(Encoding.ASCII.GetBytes(unsigned)));
    }

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
