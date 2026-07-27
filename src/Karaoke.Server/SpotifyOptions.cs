namespace Karaoke.Server;

public sealed class SpotifyOptions
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string RedirectUri { get; set; } = "http://127.0.0.1:5274/api/spotify/callback";
}
