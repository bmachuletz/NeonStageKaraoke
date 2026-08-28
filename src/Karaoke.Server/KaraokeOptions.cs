namespace Karaoke.Server;
public sealed class KaraokeOptions
{
    public string LibraryPath { get; set; } = "./music";
    public string DatabasePath { get; set; } = "./data/karaoke.db";
    public string? PublicBaseUrl { get; set; }
}
