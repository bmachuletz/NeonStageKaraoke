using Karaoke.Contracts;

namespace Karaoke.Server;

public sealed class PlaybackControllerService
{
    public const string HeaderName = "X-Karaoke-Controller";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(8);
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Lease> _leases = new();

    public PlaybackControllerDto Claim(Guid eventId, PlaybackControllerRequest request)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            _leases.TryGetValue(eventId, out var lease);
            if (request.Force || lease is null || lease.ExpiresAt <= now || lease.ControllerId == request.ClientId)
            {
                lease = new Lease(request.ClientId,
                    string.IsNullOrWhiteSpace(request.ClientName) ? "Karaoke-App" : request.ClientName.Trim(),
                    now + LeaseDuration);
                _leases[eventId] = lease;
            }
            return Snapshot(lease, request.ClientId);
        }
    }

    public bool Owns(Guid eventId, Guid clientId)
    {
        lock (_sync)
            return _leases.TryGetValue(eventId, out var lease) &&
                   lease.ControllerId == clientId && lease.ExpiresAt > DateTimeOffset.UtcNow;
    }

    public void Release(Guid eventId, Guid clientId)
    {
        lock (_sync)
        {
            if (_leases.TryGetValue(eventId, out var lease) && lease.ControllerId == clientId)
                _leases.Remove(eventId);
        }
    }

    private static PlaybackControllerDto Snapshot(Lease lease, Guid requesterId) =>
        new(lease.ControllerId == requesterId && lease.ExpiresAt > DateTimeOffset.UtcNow,
            lease.ControllerId, lease.ControllerName, lease.ExpiresAt);

    private sealed record Lease(Guid ControllerId, string ControllerName, DateTimeOffset ExpiresAt);
}
