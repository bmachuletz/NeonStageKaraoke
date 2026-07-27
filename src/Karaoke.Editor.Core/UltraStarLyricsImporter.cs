using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Karaoke.Contracts;

namespace Karaoke.Editor.Core;

public sealed record UltraStarSongMetadata(
    string? Title,
    string? Artist,
    string? AudioFile,
    double Bpm,
    double GapMilliseconds,
    bool Relative);

public sealed class UltraStarLyricsImport(
    UltraStarSongMetadata metadata,
    IReadOnlyList<LyricsLineDto> lines,
    int noteCount,
    IReadOnlyList<string> warnings)
{
    public UltraStarSongMetadata Metadata { get; } = metadata;
    public IReadOnlyList<LyricsLineDto> Lines { get; } = lines;
    public int NoteCount { get; } = noteCount;
    public int WordCount => Lines.Sum(line => line.Words?.Count ?? 0);
    public int SyllableCount => Lines.Sum(line => line.Words?.Sum(word => word.Syllables?.Count ?? 0) ?? 0);
    public IReadOnlyList<string> Warnings { get; } = warnings;
    public TimeSpan Start => Lines.Count == 0 ? TimeSpan.Zero : Lines.Min(line => line.Start);
    public TimeSpan End => Lines.Count == 0 ? TimeSpan.Zero : Lines.Max(line => line.End ?? line.Start);

    public LyricsDto ToLyrics(Guid songId) => new(songId, Lines, Metadata.Artist, Metadata.Title,
        Author: "UltraStar Deluxe TXT import");

    public LyricsEditorDocument ToEditorDocument(Guid songId) => LyricsDocumentImporter.Import(ToLyrics(songId),
        modelVersion: "UltraStar Deluxe TXT", detailedOrigin: SegmentOrigin.ImportedFromUltraStar);

    public string ToEnhancedLrc()
    {
        var output = new StringBuilder();
        AppendMetadata(output, "ti", Metadata.Title);
        AppendMetadata(output, "ar", Metadata.Artist);
        output.AppendLine("[re:UltraStar Deluxe TXT imported by Neon Stage]");
        foreach (var line in Lines.OrderBy(item => item.Start))
        {
            output.Append('[').Append(FormatTimestamp(line.Start)).Append(']');
            var words = line.Words ?? [];
            for (var index = 0; index < words.Count; index++)
            {
                var word = words[index];
                output.Append('<').Append(FormatTimestamp(word.Start)).Append(',')
                    .Append(FormatTimestamp(word.End ?? word.Start)).Append('>')
                    .Append(word.Text);
                if (index + 1 < words.Count) output.Append(' ');
            }
            output.AppendLine();
        }
        return output.ToString();
    }

    private static void AppendMetadata(StringBuilder output, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        output.Append('[').Append(key).Append(':')
            .Append(value.Replace('\r', ' ').Replace('\n', ' ').Replace(']', ' ')).AppendLine("]");
    }

    private static string FormatTimestamp(TimeSpan value)
    {
        var safe = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        var minutes = (int)safe.TotalMinutes;
        var seconds = safe.TotalSeconds - minutes * 60;
        return FormattableString.Invariant($"{minutes:00}:{seconds:00.000}");
    }
}

public sealed class UltraStarFormatException(int lineNumber, string germanReason, string englishReason)
    : ArgumentException(lineNumber > 0 ? $"UltraStar-Zeile {lineNumber}: {germanReason}" : germanReason)
{
    public int LineNumber { get; } = lineNumber;
    public string EnglishMessage { get; } = lineNumber > 0
        ? $"UltraStar line {lineNumber}: {englishReason}"
        : englishReason;
}

public static partial class UltraStarLyricsImporter
{
    public static bool LooksLikeUltraStar(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return HeaderBpmRegex().IsMatch(value) && NoteDetectionRegex().IsMatch(value);
    }

    public static UltraStarLyricsImport Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Error(0, "Die Datei ist leer.", "The file is empty.");

        var sourceLines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var metadata = ReadMetadata(sourceLines);
        var bpm = ParseRequiredNumber(metadata, "BPM");
        if (bpm <= 0) throw Error(0, "BPM muss größer als null sein.", "BPM must be greater than zero.");
        var gap = ParseOptionalNumber(metadata, "GAP");
        var relative = metadata.TryGetValue("RELATIVE", out var relativeValue) &&
                       relativeValue.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
        if (relative && metadata.TryGetValue("VERSION", out var versionValue) &&
            TryParseNumber(versionValue, out var version) && version >= 1)
            throw Error(0, "RELATIVE ist ab UltraStar-Formatversion 1.0 nicht zulässig.",
                "RELATIVE is not valid in UltraStar format version 1.0 or newer.");

        var rawLines = new List<List<RawNote>>();
        var current = new List<RawNote>();
        var warnings = new List<string>();
        long relativeBase = 0;
        var foundEnd = false;

        for (var sourceIndex = 0; sourceIndex < sourceLines.Length; sourceIndex++)
        {
            var lineNumber = sourceIndex + 1;
            var raw = sourceLines[sourceIndex].TrimStart('\uFEFF', ' ', '\t');
            if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith('#') || raw.StartsWith("//", StringComparison.Ordinal))
                continue;
            if (EndRegex().IsMatch(raw))
            {
                FlushLine();
                foundEnd = true;
                break;
            }
            if (TrackRegex().IsMatch(raw))
                throw Error(lineNumber, "Duettspuren P1/P2 werden noch nicht unterstützt.",
                    "P1/P2 duet tracks are not supported yet.");
            if (VariableBpmRegex().IsMatch(raw))
                throw Error(lineNumber, "Variable BPM-Wechsel können noch nicht verlustfrei importiert werden.",
                    "Variable BPM changes cannot yet be imported without losing timing information.");
            var noteMatch = NoteRegex().Match(raw);
            if (noteMatch.Success)
            {
                var start = ParseInteger(noteMatch.Groups["start"].Value, lineNumber);
                var duration = ParseInteger(noteMatch.Groups["duration"].Value, lineNumber);
                if (duration < 0)
                    throw Error(lineNumber, "Eine Note besitzt eine negative Dauer.", "A note has a negative duration.");
                long absoluteBeat;
                try { absoluteBeat = checked(start + relativeBase); }
                catch (OverflowException) { throw Error(lineNumber, "Der relative Beatwert ist zu groß.", "The relative beat value is too large."); }
                if (current.Count > 0 && absoluteBeat < current[^1].Beat)
                    throw Error(lineNumber, "Noten innerhalb einer Zeile sind nicht chronologisch.",
                        "Notes inside a line are not chronological.");
                current.Add(new(noteMatch.Groups["type"].Value[0], absoluteBeat, duration,
                    noteMatch.Groups["text"].Value, lineNumber));
                continue;
            }
            var breakMatch = BreakRegex().Match(raw);
            if (breakMatch.Success)
            {
                FlushLine();
                if (relative)
                {
                    if (!breakMatch.Groups["relative"].Success)
                        throw Error(lineNumber, "Ein relativer Zeilenwechsel benötigt zwei Beatwerte.",
                            "A relative line break requires two beat values.");
                    try { relativeBase = checked(relativeBase + ParseInteger(breakMatch.Groups["relative"].Value, lineNumber)); }
                    catch (OverflowException) { throw Error(lineNumber, "Der relative Zeilenoffset ist zu groß.", "The relative line offset is too large."); }
                }
                continue;
            }
            if (raw.Length > 0 && raw[0] is ':' or '*' or 'F' or 'R' or 'G' or '-' or 'B' or 'P')
                throw Error(lineNumber, "Die Noten- oder Zeilenwechsel-Syntax ist ungültig.",
                    "The note or line-break syntax is invalid.");
            warnings.Add($"Nicht erkannte Zeile {lineNumber} wurde ignoriert.");
        }
        FlushLine();
        if (!foundEnd) warnings.Add("Der abschließende E-Marker fehlt.");
        if (rawLines.Count == 0)
            throw Error(0, "Die Datei enthält keine importierbaren Noten.", "The file contains no importable notes.");

        var zeroDurations = 0;
        var clippedNotes = 0;
        var discardedBeforeAudio = 0;
        var converted = rawLines.Select((line, index) => ConvertLine(line, index, bpm, gap,
            ref zeroDurations, ref clippedNotes, ref discardedBeforeAudio)).Where(line => line is not null)
            .Cast<LyricsLineDto>().ToList();
        EnsureNonOverlappingLines(converted, ref clippedNotes);
        if (converted.Count == 0)
            throw Error(0, "Alle Noten liegen vor dem Beginn der Audiodatei oder enthalten keinen Text.",
                "All notes are before the beginning of the audio file or contain no text.");
        if (zeroDurations > 0) warnings.Add($"{zeroDurations} Note(n) ohne Dauer wurden auf den nächsten Beat ausgedehnt.");
        if (clippedNotes > 0) warnings.Add($"{clippedNotes} überlappende Notengrenze(n) wurden kollisionsfrei gekürzt.");
        if (discardedBeforeAudio > 0) warnings.Add($"{discardedBeforeAudio} vollständig vor dem Audiobeginn liegende Note(n) wurden verworfen.");

        return new(new(Get(metadata, "TITLE"), Get(metadata, "ARTIST"),
                Get(metadata, "AUDIO") ?? Get(metadata, "MP3"), bpm, gap, relative),
            converted.Select((line, index) => line with { Index = index }).ToArray(),
            rawLines.Sum(line => line.Count), warnings);

        void FlushLine()
        {
            if (current.Count == 0) return;
            rawLines.Add(current);
            current = [];
        }
    }

    private static LyricsLineDto? ConvertLine(IReadOnlyList<RawNote> rawNotes, int lineIndex, double bpm,
        double gap, ref int zeroDurations, ref int clippedNotes, ref int discardedBeforeAudio)
    {
        var notes = new List<TimedNote>(rawNotes.Count);
        for (var index = 0; index < rawNotes.Count; index++)
        {
            var raw = rawNotes[index];
            var start = BeatToTime(raw.Beat, bpm, gap);
            var duration = raw.Duration;
            if (duration == 0) { duration = 1; zeroDurations++; }
            long endBeat;
            try { endBeat = checked(raw.Beat + duration); }
            catch (OverflowException)
            {
                throw Error(raw.LineNumber, "Die Notendauer liegt außerhalb des unterstützten Bereichs.",
                    "The note duration is outside the supported range.");
            }
            var end = BeatToTime(endBeat, bpm, gap);
            if (index + 1 < rawNotes.Count)
            {
                var nextStart = BeatToTime(rawNotes[index + 1].Beat, bpm, gap);
                if (end > nextStart)
                {
                    end = nextStart;
                    clippedNotes++;
                }
            }
            if (end <= TimeSpan.Zero) { discardedBeforeAudio++; continue; }
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;
            if (end < start) end = start;
            notes.Add(new(start, end, raw.Text, raw.LineNumber));
        }
        if (notes.Count == 0) return null;

        var words = BuildWords(notes);
        if (words.Count == 0) return null;
        var startTime = words.Min(word => word.Start);
        var endTime = words.Max(word => word.End ?? word.Start);
        if (endTime <= startTime) endTime = startTime + TimeSpan.FromMilliseconds(1);
        return new(startTime, string.Join(' ', words.Select(word => word.Text)), endTime, lineIndex, words);
    }

    private static IReadOnlyList<LyricsWordDto> BuildWords(IReadOnlyList<TimedNote> notes)
    {
        var completed = new List<MutableWord>();
        MutableWord? current = null;
        foreach (var note in notes)
        {
            var matches = TokenRegex().Matches(note.Text);
            if (matches.Count == 0)
            {
                var previous = current ?? completed.LastOrDefault();
                previous?.Extend(note.End);
                continue;
            }
            var totalWeight = matches.Sum(match => Math.Max(1, match.Length));
            var consumedWeight = 0;
            var previousEnd = 0;
            foreach (Match match in matches)
            {
                if (match.Index > previousEnd) FlushWord();
                var tokenStart = Interpolate(note.Start, note.End, consumedWeight, totalWeight);
                consumedWeight += Math.Max(1, match.Length);
                var tokenEnd = Interpolate(note.Start, note.End, consumedWeight, totalWeight);
                current ??= new();
                current.Add(match.Value, tokenStart, tokenEnd);
                previousEnd = match.Index + match.Length;
                if (previousEnd < note.Text.Length) FlushWord();
            }
        }
        FlushWord();
        return completed.Select((word, wordIndex) => word.ToDto(wordIndex)).ToArray();

        void FlushWord()
        {
            if (current is null || current.Syllables.Count == 0) { current = null; return; }
            completed.Add(current);
            current = null;
        }
    }

    private static void EnsureNonOverlappingLines(List<LyricsLineDto> lines, ref int clippedNotes)
    {
        lines.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var index = 0; index + 1 < lines.Count; index++)
        {
            var current = lines[index];
            var next = lines[index + 1];
            if ((current.End ?? current.Start) <= next.Start) continue;
            if (next.Start <= current.Start)
                throw Error(0, "Zwei UltraStar-Zeilen beginnen gleichzeitig und können nicht eindeutig dargestellt werden.",
                    "Two UltraStar lines start at the same time and cannot be displayed unambiguously.");
            var words = (current.Words ?? []).ToArray();
            if (words.Length == 0 || words[^1].Start >= next.Start)
                throw Error(0, "Zwei UltraStar-Zeilen überlappen sich inhaltlich.",
                    "Two UltraStar lines overlap in their sung content.");
            var last = words[^1];
            var syllables = (last.Syllables ?? []).ToArray();
            for (var syllableIndex = 0; syllableIndex < syllables.Length; syllableIndex++)
            {
                var syllable = syllables[syllableIndex];
                if (syllable.Start >= next.Start)
                    throw Error(0, "Zwei UltraStar-Zeilen überlappen sich in einer Silbe.",
                        "Two UltraStar lines overlap inside a syllable.");
                if ((syllable.End ?? syllable.Start) > next.Start)
                    syllables[syllableIndex] = syllable with { End = next.Start };
            }
            words[^1] = last with { End = next.Start, Syllables = syllables };
            lines[index] = current with { End = next.Start, Words = words };
            clippedNotes++;
        }
    }

    private static TimeSpan Interpolate(TimeSpan start, TimeSpan end, int weight, int totalWeight) =>
        totalWeight <= 0 ? start : start + TimeSpan.FromTicks((end - start).Ticks * weight / totalWeight);

    private static TimeSpan BeatToTime(long beat, double bpm, double gapMilliseconds)
    {
        try { return TimeSpan.FromMilliseconds(gapMilliseconds + beat * 15_000d / bpm); }
        catch (OverflowException) { throw Error(0, "Ein Beatwert liegt außerhalb des unterstützten Zeitbereichs.", "A beat value is outside the supported time range."); }
    }

    private static Dictionary<string, string> ReadMetadata(IEnumerable<string> sourceLines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in sourceLines)
        {
            var match = HeaderRegex().Match(line.TrimStart('\uFEFF', ' ', '\t'));
            if (match.Success) result[match.Groups["key"].Value.Trim()] = match.Groups["value"].Value.Trim();
        }
        return result;
    }

    private static double ParseRequiredNumber(IReadOnlyDictionary<string, string> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var value) || !TryParseNumber(value, out var result))
            throw Error(0, $"#{key} fehlt oder ist ungültig.", $"#{key} is missing or invalid.");
        return result;
    }

    private static double ParseOptionalNumber(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) && TryParseNumber(value, out var result) ? result : 0;

    private static bool TryParseNumber(string value, out double result) => double.TryParse(
        value.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out result) &&
        double.IsFinite(result);

    private static long ParseInteger(string value, int lineNumber)
    {
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)) return result;
        throw Error(lineNumber, "Ein Beatwert ist ungültig.", "A beat value is invalid.");
    }

    private static string? Get(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static UltraStarFormatException Error(int line, string german, string english) => new(line, german, english);

    private sealed record RawNote(char Type, long Beat, long Duration, string Text, int LineNumber);
    private sealed record TimedNote(TimeSpan Start, TimeSpan End, string Text, int LineNumber);

    private sealed class MutableWord
    {
        public List<MutableSyllable> Syllables { get; } = [];
        public void Add(string text, TimeSpan start, TimeSpan end) => Syllables.Add(new(text, start, end));
        public void Extend(TimeSpan end)
        {
            if (Syllables.Count > 0 && end > Syllables[^1].End) Syllables[^1].End = end;
        }
        public LyricsWordDto ToDto(int index)
        {
            var text = string.Concat(Syllables.Select(syllable => syllable.Text));
            var syllables = Syllables.Select((syllable, syllableIndex) =>
                new LyricsSyllableDto(syllable.Start, syllable.Text, syllable.End, syllableIndex, 1)).ToArray();
            return new(syllables[0].Start, text, syllables[^1].End, index, syllables, 1);
        }
    }

    private sealed class MutableSyllable(string text, TimeSpan start, TimeSpan end)
    {
        public string Text { get; } = text;
        public TimeSpan Start { get; } = start;
        public TimeSpan End { get; set; } = end;
    }

    [GeneratedRegex(@"(?im)^#BPM\s*:")]
    private static partial Regex HeaderBpmRegex();
    [GeneratedRegex(@"(?im)^\s*[:*FRG]\s+")]
    private static partial Regex NoteDetectionRegex();
    [GeneratedRegex(@"^#(?<key>[^:]+):(?<value>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderRegex();
    [GeneratedRegex(@"^(?<type>[:*FRG])\s+(?<start>-?\d+)\s+(?<duration>-?\d+)\s+-?\d+(?:\s(?<text>.*))?$", RegexOptions.CultureInvariant)]
    private static partial Regex NoteRegex();
    [GeneratedRegex(@"^-\s*(?<start>-?\d+)(?:\s+(?<relative>-?\d+))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BreakRegex();
    [GeneratedRegex(@"^B(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VariableBpmRegex();
    [GeneratedRegex(@"^P\s*[12](?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TrackRegex();
    [GeneratedRegex(@"^E\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndRegex();
    [GeneratedRegex(@"\S+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}
