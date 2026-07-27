using System.Collections.Concurrent;

namespace Karaoke.Server;

public sealed record StageReactionRequest(string Type, string? Sender);
public sealed record StageReactionDto(long Id, Guid EventId, string Type, string Sender, DateTimeOffset CreatedAt);

public sealed class StageReactionService
{
    private static readonly HashSet<string> Allowed = ["heart", "smile", "like", "clap", "fire"];
    private readonly object _gate = new();
    private readonly List<StageReactionDto> _recent = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastBySender = new();
    private long _nextId;

    public StageReactionDto? Add(Guid eventId, StageReactionRequest request)
    {
        var type = request.Type?.Trim().ToLowerInvariant() ?? "";
        if (!Allowed.Contains(type)) return null;
        var sender = string.IsNullOrWhiteSpace(request.Sender) ? "Gast" : request.Sender.Trim()[..Math.Min(32, request.Sender.Trim().Length)];
        var now = DateTimeOffset.UtcNow;
        var key = $"{eventId:N}:{sender.ToUpperInvariant()}";
        if (_lastBySender.TryGetValue(key, out var last) && now - last < TimeSpan.FromMilliseconds(350)) return null;
        _lastBySender[key] = now;
        lock (_gate)
        {
            var item = new StageReactionDto(++_nextId, eventId, type, sender, now);
            _recent.Add(item);
            if (_recent.Count > 300) _recent.RemoveRange(0, _recent.Count - 300);
            return item;
        }
    }

    public IReadOnlyList<StageReactionDto> Get(Guid eventId, long after)
    {
        lock (_gate) return _recent.Where(item => item.EventId == eventId && item.Id > after).Take(50).ToArray();
    }
}
