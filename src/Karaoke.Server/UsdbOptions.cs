namespace Karaoke.Server;

public sealed class UsdbOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://usdb.eu";
    public string PlayerApiUrl { get; set; } = "https://player.usdb.eu/2.0.0/json.php";
    public int RequestTimeoutSeconds { get; set; } = 25;
    public int DownloadWaitSeconds { get; set; } = 21;
    public int DownloadMaximumAttempts { get; set; } = 4;
    public int DownloadRetryDelaySeconds { get; set; } = 3;
    public int MaximumCandidates { get; set; } = 5;
    public double MaximumDurationDifferenceSeconds { get; set; } = 10;
    public double MinimumTitleSimilarity { get; set; } = .90;
    public double MinimumArtistSimilarity { get; set; } = .82;
    public int SuccessfulCacheHours { get; set; } = 168;
    public int FailureCacheMinutes { get; set; } = 30;
    public string CachePath { get; set; } = "./data/usdb-cache";
    public AnimuxUsdbOptions Animux { get; set; } = new();
}

public sealed class AnimuxUsdbOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://usdb.animux.de";
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
