using Karaoke.Contracts;

namespace Karaoke.Server;

public sealed class PlaybackControllerService
{
    public const string HeaderName = "X-Karaoke-Controller";
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(8);
    private readonly object _sync = new();
    private Guid? _controllerId;
    private string? _controllerName;
    private DateTimeOffset _leaseExpiresAt;

    public PlaybackControllerDto Claim(PlaybackControllerRequest request)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (request.Force || _controllerId is null || _leaseExpiresAt <= now || _controllerId == request.ClientId)
            {
                _controllerId = request.ClientId;
                _controllerName = string.IsNullOrWhiteSpace(request.ClientName) ? "Karaoke-App" : request.ClientName.Trim();
                _leaseExpiresAt = now + LeaseDuration;
            }
            return Snapshot(request.ClientId);
        }
    }

    public bool Owns(Guid clientId)
    {
        lock (_sync)
            return _controllerId == clientId && _leaseExpiresAt > DateTimeOffset.UtcNow;
    }

    public void Release(Guid clientId)
    {
        lock (_sync)
        {
            if (_controllerId != clientId) return;
            _controllerId = null;
            _controllerName = null;
            _leaseExpiresAt = DateTimeOffset.MinValue;
        }
    }

    private PlaybackControllerDto Snapshot(Guid requesterId) =>
        new(_controllerId == requesterId && _leaseExpiresAt > DateTimeOffset.UtcNow, _controllerId, _controllerName, _leaseExpiresAt);
}
