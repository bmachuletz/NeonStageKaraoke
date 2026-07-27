namespace Karaoke.App.Services;

public interface IAudioPlaybackService : IDisposable
{
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    int Volume { get; set; }
    int VocalVolume { get; set; }
    bool IsPlaying { get; }
    bool IsSeekable { get; }
    event EventHandler<TimeSpan>? PositionChanged;
    event EventHandler? PlaybackStarted;
    event EventHandler? PlaybackEnded;
    event EventHandler<string>? PlaybackFailed;
    event EventHandler<string>? StateChanged;
    Task PlayAsync(Uri source, Uri? vocalsSource = null, TimeSpan? startPosition = null, CancellationToken cancellationToken = default);
    void Pause();
    void Resume();
    void Stop();
    void Seek(TimeSpan position);
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
}

public static class AudioPlaybackServiceFactory
{
    public static Func<IAudioPlaybackService> Create { get; set; } = () => new UnsupportedAudioPlaybackService();
}

internal sealed class UnsupportedAudioPlaybackService : IAudioPlaybackService
{
    public TimeSpan Position => TimeSpan.Zero;
    public TimeSpan Duration => TimeSpan.Zero;
    public int Volume { get; set; } = 100;
    public int VocalVolume { get; set; }
    public bool IsPlaying => false;
    public bool IsSeekable => false;
    public event EventHandler<TimeSpan>? PositionChanged { add { } remove { } }
    public event EventHandler? PlaybackStarted { add { } remove { } }
    public event EventHandler? PlaybackEnded { add { } remove { } }
    public event EventHandler<string>? PlaybackFailed;
    public event EventHandler<string>? StateChanged { add { } remove { } }
    public Task PlayAsync(Uri source, Uri? vocalsSource = null, TimeSpan? startPosition = null, CancellationToken cancellationToken = default)
    {
        PlaybackFailed?.Invoke(this, "Audioausgabe ist auf dieser Plattform noch nicht eingerichtet.");
        return Task.CompletedTask;
    }
    public void Pause() { }
    public void Resume() { }
    public void Stop() { }
    public void Seek(TimeSpan position) { }
    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Dispose() { }
}
