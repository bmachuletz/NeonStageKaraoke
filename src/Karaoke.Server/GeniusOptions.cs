namespace Karaoke.Server;

public sealed class GeniusOptions
{
    public bool Enabled { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.genius.com/";
}
