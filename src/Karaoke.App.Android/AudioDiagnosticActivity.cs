#if DEBUG
using Android.App;
using Android.OS;
using Android.Util;
using Karaoke.App.Desktop;

namespace Karaoke.App.Android;

[Activity(Name = "de.neonstage.karaoke.AudioDiagnosticActivity", Exported = true,
    Theme = "@style/MyTheme.NoActionBar")]
public sealed class AudioDiagnosticActivity : Activity
{
    private const string Tag = "NeonStageAudioTest";
    private LibVlcAudioPlaybackService? _player;
    private readonly CancellationTokenSource _lifetime = new();

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _player = new LibVlcAudioPlaybackService { Volume = 70, VocalVolume = 35 };
        _player.StateChanged += (_, state) => Log.Info(Tag, "STATE " + state);
        _player.PlaybackStarted += (_, _) => Log.Info(Tag, "EVENT PlaybackStarted");
        _player.PlaybackEnded += (_, _) => Log.Info(Tag, "EVENT PlaybackEnded");
        _player.PlaybackFailed += (_, error) => Log.Error(Tag, "EVENT PlaybackFailed: " + error);
        _ = RunAsync(_lifetime.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            var player = _player ?? throw new InvalidOperationException("Audioplayer wurde nicht initialisiert.");
            var instrumental = Intent?.GetStringExtra("instrumental") ?? throw new InvalidOperationException("instrumental fehlt");
            var vocals = Intent?.GetStringExtra("vocals");
            Log.Info(Tag, $"START instrumental={instrumental} vocals={vocals}");
            await player.PlayAsync(new Uri(instrumental), string.IsNullOrWhiteSpace(vocals) ? null : new Uri(vocals),
                cancellationToken: cancellationToken);

            await ObserveAsync("PLAY", 8, cancellationToken);
            var beforePause = player.Position;
            player.Pause();
            await Task.Delay(1000, cancellationToken);
            var pauseDrift = Math.Abs((player.Position - beforePause).TotalMilliseconds);
            Log.Info(Tag, $"CHECK pause-drift-ms={pauseDrift:0}");

            player.Resume();
            await ObserveAsync("RESUME", 4, cancellationToken);
            await player.SeekAsync(TimeSpan.FromSeconds(20), cancellationToken);
            Log.Info(Tag, "ACTION seek=20.000");
            await ObserveAsync("SEEK", 8, cancellationToken);
            Log.Info(Tag, $"SUCCESS final={player.Position.TotalSeconds:0.000} playing={player.IsPlaying} seekable={player.IsSeekable}");
        }
        catch (Exception exception)
        {
            Log.Error(Tag, "FAILED " + exception);
        }
        finally
        {
            RunOnUiThread(Finish);
        }
    }

    private async Task ObserveAsync(string phase, int samples, CancellationToken cancellationToken)
    {
        for (var index = 0; index < samples; index++)
        {
            await Task.Delay(500, cancellationToken);
            Log.Info(Tag, $"{phase} position={_player!.Position.TotalSeconds:0.000} playing={_player.IsPlaying} seekable={_player.IsSeekable}");
        }
    }

    protected override void OnDestroy()
    {
        _lifetime.Cancel();
        _player?.Dispose();
        _lifetime.Dispose();
        base.OnDestroy();
    }
}
#endif
