using Microsoft.AspNetCore.SignalR;

namespace Karaoke.Server;

public sealed class KaraokeHub : Hub;

public static class KaraokeHubEvents
{
    public const string QueueChanged = "QueueChanged";
    public const string PlaybackPositionChanged = "PlaybackPositionChanged";
    public const string ScanStatusChanged = "ScanStatusChanged";
}
