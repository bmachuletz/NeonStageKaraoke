using Avalonia;
using Karaoke.App.Services;

namespace Karaoke.App.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is ["--audio-test", var source])
        {
            RunAudioTest(new Uri(source), null);
            return;
        }
        if (args is ["--audio-test-seek", var singleSource, var singleSeconds] &&
            double.TryParse(singleSeconds, System.Globalization.CultureInfo.InvariantCulture, out var singleSeekSeconds))
        {
            RunAudioTest(new Uri(singleSource), null, TimeSpan.FromSeconds(singleSeekSeconds), true);
            return;
        }
        if (args is ["--audio-test-sequence", var sequenceSource])
        {
            RunSeekSequence(new Uri(sequenceSource));
            return;
        }
        if (args is ["--editor-persistence-test", var persistenceSongId] &&
            Guid.TryParse(persistenceSongId, out var persistenceId))
        {
            RunEditorPersistenceTestAsync(persistenceId).GetAwaiter().GetResult();
            return;
        }
        if (args is ["--audio-test", var stemSource, var vocals])
        {
            RunAudioTest(new Uri(stemSource), new Uri(vocals));
            return;
        }
        if (args is ["--audio-test-seek", var seekStemSource, var seekVocals, var seconds] &&
            double.TryParse(seconds, System.Globalization.CultureInfo.InvariantCulture, out var seekSeconds))
        {
            RunAudioTest(new Uri(seekStemSource), new Uri(seekVocals), TimeSpan.FromSeconds(seekSeconds));
            return;
        }
        AudioPlaybackServiceFactory.Create = () => new LibVlcAudioPlaybackService();
        Build().StartWithClassicDesktopLifetime(args);
    }

    private static void RunAudioTest(Uri source, Uri? vocals, TimeSpan? initialSeek = null, bool seekOnStart = false)
    {
        using var player = new LibVlcAudioPlaybackService();
        player.StateChanged += (_, state) => Console.WriteLine(state);
        player.PlaybackFailed += (_, error) => Console.Error.WriteLine(error);
        player.PlaybackEnded += (_, _) => Console.WriteLine("EndReached");
        player.PositionChanged += (_, position) => Console.WriteLine("Position: " + position.TotalSeconds.ToString("0.00"));
        player.VocalVolume = 50;
        player.PlayAsync(source, vocals, seekOnStart ? initialSeek : null).GetAwaiter().GetResult();
        if (!seekOnStart && initialSeek is { } seek)
        {
            for (var attempt = 0; attempt < 30 && !player.IsSeekable; attempt++) Thread.Sleep(100);
            player.Seek(seek);
            Console.WriteLine($"Seek angefordert: {seek.TotalSeconds:0.000}s");
        }
        Thread.Sleep(TimeSpan.FromSeconds(12));
        Console.WriteLine($"Testende – IsPlaying={player.IsPlaying}, Position={player.Position.TotalSeconds:0.00}s, Dauer={player.Duration.TotalSeconds:0.00}s");
    }

    private static void RunSeekSequence(Uri source)
    {
        using var player = new LibVlcAudioPlaybackService { Volume = 0 };
        var targets = new[] { 45d, 8d, 32d, 4d, 18d };
        player.PlayAsync(source, startPosition: TimeSpan.FromSeconds(targets[0])).GetAwaiter().GetResult();
        foreach (var seconds in targets)
        {
            player.SeekAsync(TimeSpan.FromSeconds(seconds)).GetAwaiter().GetResult();
            var actual = player.Position.TotalSeconds;
            var difference = Math.Abs(actual - seconds);
            Console.WriteLine($"Seek {seconds:0.000}s -> {actual:0.000}s (Differenz {difference * 1000:0} ms)");
            if (difference > .12) throw new InvalidOperationException("Seek-Ziel wurde nicht synchron bestätigt.");
            Thread.Sleep(120);
        }
        player.Stop();
        Console.WriteLine("Seek-Sequenz synchron bestätigt.");
    }

    private static async Task RunEditorPersistenceTestAsync(Guid songId)
    {
        int? originalHold;
        const int marker = 9876;
        using (var first = new EditorViewModel(new LibVlcAudioPlaybackService(), loadVisualAssets: false))
        {
            await first.InitializeAsync();
            first.SelectedSong = first.Songs.First(song => song.Id == songId);
            await first.LoadSelectedSongAsync();
            var line = first.Document?.Lines.FirstOrDefault() ??
                       throw new InvalidOperationException("Keine Zeile geladen. Editorstatus: " + first.Status);
            originalHold = line.HoldAfterMilliseconds;
            line.HoldAfterMilliseconds = marker;
            first.NotifyTimelineEdit();
            await first.SaveDraftAsync();
            Console.WriteLine("SAVE-1: " + first.Status);
            if (!first.Status.Contains("Server bestätigt", StringComparison.Ordinal))
                throw new InvalidOperationException("Erster Speichervorgang wurde nicht bestätigt.");
        }

        using (var second = new EditorViewModel(new LibVlcAudioPlaybackService(), loadVisualAssets: false))
        {
            await second.InitializeAsync();
            second.SelectedSong = second.Songs.First(song => song.Id == songId);
            await second.LoadSelectedSongAsync();
            var line = second.Document?.Lines.FirstOrDefault() ??
                       throw new InvalidOperationException("Keine Zeile neu geladen. Editorstatus: " + second.Status);
            Console.WriteLine($"RELOAD-1: Hold={line.HoldAfterMilliseconds?.ToString() ?? "null"}");
            if (line.HoldAfterMilliseconds != marker)
                throw new InvalidOperationException("Gespeicherte Änderung wurde nach Neustart nicht geladen.");
            line.HoldAfterMilliseconds = originalHold;
            second.NotifyTimelineEdit();
            await second.SaveDraftAsync();
            Console.WriteLine("RESTORE: " + second.Status);
        }

        using var third = new EditorViewModel(new LibVlcAudioPlaybackService(), loadVisualAssets: false);
        await third.InitializeAsync();
        third.SelectedSong = third.Songs.First(song => song.Id == songId);
        await third.LoadSelectedSongAsync();
        var restored = third.Document?.Lines.FirstOrDefault()?.HoldAfterMilliseconds;
        Console.WriteLine($"RELOAD-2: Hold={restored?.ToString() ?? "null"}");
        if (restored != originalHold) throw new InvalidOperationException("Ursprungswert wurde nicht persistent wiederhergestellt.");
        Console.WriteLine("Editor-Persistenztest erfolgreich.");
    }

    public static AppBuilder Build() => AppBuilder.Configure<EditorApplication>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
