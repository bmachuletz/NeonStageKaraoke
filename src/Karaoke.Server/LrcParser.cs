using Karaoke.Contracts;
using Karaoke.Editor.Core;

namespace Karaoke.Server;

internal static class LrcParser
{
    public static LyricsDto Parse(Guid songId, IEnumerable<string> sourceLines, TimeSpan duration) =>
        EnhancedLrcLyricsImporter.Parse(songId, sourceLines, duration);
}
