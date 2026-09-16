namespace Karaoke.Server;

public sealed class OnlineOptions
{
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "LiveKit";
    public string ServerUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public string RoomPrefix { get; set; } = "neonstage-";
    public int TokenLifetimeMinutes { get; set; } = 30;
    public int ParticipantLeaseSeconds { get; set; } = 15;

    public bool IsConfigured => Enabled &&
        Provider.Equals("LiveKit", StringComparison.OrdinalIgnoreCase) &&
        Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) && uri.Scheme is "ws" or "wss" &&
        !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(ApiSecret);
}
