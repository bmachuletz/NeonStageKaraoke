namespace Karaoke.Server;

public sealed class QobuzOptions
{
    public bool Enabled { get; set; }
    public string AppId { get; set; } = string.Empty;
    public string AppSecret { get; set; } = string.Empty;
    public string UserAuthToken { get; set; } = string.Empty;
    public int FormatId { get; set; } = 6;
    public string ApiBaseUrl { get; set; } = "https://www.qobuz.com/api.json/0.2";
}
