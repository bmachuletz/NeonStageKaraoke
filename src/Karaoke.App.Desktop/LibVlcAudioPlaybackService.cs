using Karaoke.App.Services;
using LibVLCSharp.Shared;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Karaoke.App.Desktop;

internal sealed record EditorAudioOutputDevice(string? Id, string Label)
{
    public override string ToString() => Label;
}

public sealed class LibVlcAudioPlaybackService : IAudioPlaybackService
{
    internal static Action EnsurePlatformAudioSessionAudible { private get; set; } = static () => { };

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly MediaPlayer _vocalPlayer;
    private readonly string _audioOutputStatus;
    private Media? _media;
    private Media? _vocalMedia;
    private int _requestedVocalVolume;
    private int _requestedMasterVolume = 100;
    private double _requestedPlaybackRate = 1;
    private int _playGeneration;
    private readonly Timer _vocalSyncTimer;
    private int _vocalStartScheduled;
    private int _vocalAlignmentScheduled;
    private int _seekGeneration;
    private bool _vocalAwaitingAlignment;
    private volatile bool _userSeekInProgress;
    private readonly SemaphoreSlim _seekLock = new(1, 1);
    private string _lastLibVlcMessage = "keine native Diagnose";
    private bool _sourcesAreLocal;
#if !ANDROID
    private string? _configuredOutputDeviceId;
#endif

    public LibVlcAudioPlaybackService()
    {
        InitializeLibVlcRuntime();
        _libVlc = OperatingSystem.IsWindows()
            ? new LibVLC("--no-video", "--network-caching=750", "--aout=mmdevice")
            : new LibVLC("--no-video", "--network-caching=750");
        _libVlc.Log += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Message))
                _lastLibVlcMessage = $"[{eventArgs.Level}] {eventArgs.Module}: {eventArgs.Message}";
        };
        _player = new MediaPlayer(_libVlc);
        _vocalPlayer = new MediaPlayer(_libVlc);
        _audioOutputStatus = ConfigureWindowsAudioOutput(_player, _vocalPlayer);
        ApplyConfiguredWindowsOutputDevice();
        QueueWindowsAudioSessionUnmute();
        _vocalPlayer.Playing += (_, _) =>
        {
            _vocalPlayer.SetRate((float)_requestedPlaybackRate);
            if (!_vocalAwaitingAlignment) return;
            if (Interlocked.Exchange(ref _vocalAlignmentScheduled, 1) == 0)
                ThreadPool.QueueUserWorkItem(async _ => await AlignAndEnableVocalAsync(_playGeneration));
        };
        _vocalPlayer.EncounteredError += (_, _) =>
        {
            var generation = _playGeneration;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (generation == _playGeneration) DisableVocalStem();
            });
            StateChanged?.Invoke(this, "Vocal-Spur konnte nicht geladen werden – Instrumental läuft weiter");
        };
        _player.TimeChanged += (_, eventArgs) =>
        {
            PositionChanged?.Invoke(this, TimeSpan.FromMilliseconds(eventArgs.Time));
        };
        _vocalSyncTimer = new Timer(_ => SynchronizeVocals(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _player.Opening += (_, _) => StateChanged?.Invoke(this, "Audiostream wird geöffnet …");
        _player.Buffering += (_, eventArgs) => StateChanged?.Invoke(this, $"Audiostream wird gepuffert: {eventArgs.Cache:0}%");
        _player.Playing += (_, _) =>
        {
            _player.SetRate((float)_requestedPlaybackRate);
            QueueWindowsAudioSessionUnmute();
            // Nie einen zweiten LibVLC-Player innerhalb eines LibVLC-Callbacks starten:
            // beide Player teilen interne Locks und der Master kann sonst im Buffering hängen.
            var generation = _playGeneration;
            if (Interlocked.Exchange(ref _vocalStartScheduled, 1) == 0)
                ThreadPool.QueueUserWorkItem(_ => StartVocalMuted(generation));
            StateChanged?.Invoke(this, $"Wiedergabe läuft · {_audioOutputStatus}");
            PlaybackStarted?.Invoke(this, EventArgs.Empty);
            _ = EnsureAudiblePlaybackAsync(generation);
        };
        _player.Paused += (_, _) => StateChanged?.Invoke(this, "Wiedergabe pausiert");
        _player.Stopped += (_, _) => StateChanged?.Invoke(this, "Wiedergabe gestoppt");
        _player.EndReached += (_, _) => PlaybackEnded?.Invoke(this, EventArgs.Empty);
        _player.EncounteredError += (_, _) => PlaybackFailed?.Invoke(this, "Der Audiostream konnte nicht wiedergegeben werden.");
    }

    internal static void InitializeLibVlcRuntime()
    {
        ConfigureNativeLibraryResolver();
        ConfigureMacVlcPlugins();
        if (OperatingSystem.IsMacOS())
        {
            var bundledDirectory = Path.Combine(AppContext.BaseDirectory, "vlc", "lib");
            if (File.Exists(Path.Combine(bundledDirectory, "libvlc.dylib")))
            {
                Core.Initialize(bundledDirectory);
                return;
            }
        }
        Core.Initialize();
    }

    private static void ConfigureNativeLibraryResolver()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, ResolveLibVlc);
        }
        catch (InvalidOperationException)
        {
            // Ein weiterer Player hat den Resolver in diesem Prozess bereits eingerichtet.
        }
    }

    private static IntPtr ResolveLibVlc(string libraryName, Assembly assembly, DllImportSearchPath? searchPath) =>
        libraryName != "libvlc"
            ? IntPtr.Zero
            : OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libvlc.so.5", assembly, searchPath, out var linuxHandle)
                ? linuxHandle
                : OperatingSystem.IsMacOS() && TryLoadMacLibVlc(out var macHandle)
                    ? macHandle
                    : IntPtr.Zero;

    private static bool TryLoadMacLibVlc(out IntPtr handle)
    {
        var configured = Environment.GetEnvironmentVariable("NEONSTAGE_LIBVLC_PATH")?.Trim();
        var candidates = new[]
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "vlc", "lib", "libvlc.dylib"),
            "/Applications/VLC.app/Contents/MacOS/lib/libvlc.dylib"
        };
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
            if (NativeLibrary.TryLoad(candidate, out handle)) return true;
        }
        handle = IntPtr.Zero;
        return false;
    }

    private static void ConfigureMacVlcPlugins()
    {
        if (!OperatingSystem.IsMacOS() ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VLC_PLUGIN_PATH"))) return;
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "vlc", "plugins"),
                     "/Applications/VLC.app/Contents/MacOS/plugins",
                     "/Applications/VLC.app/Contents/MacOS/lib/vlc/plugins"
                 })
        {
            if (!Directory.Exists(candidate)) continue;
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", candidate);
            return;
        }
    }

    private static string ConfigureWindowsAudioOutput(MediaPlayer player, MediaPlayer vocalPlayer)
    {
        if (!OperatingSystem.IsWindows()) return "System-Audioausgabe";

        // VLC 3 kann auf Windows noch auf alte oder unvollständig installierte
        // Ausgabemodule zurückfallen. MMDevice folgt dem Windows-Standardgerät
        // (inklusive eines zur Laufzeit gewechselten USB-/Bluetooth-Geräts).
        // DirectSound bleibt der Fallback für ältere Windows-Installationen.
        foreach (var module in new[] { "mmdevice", "wasapi", "directsound", "waveout" })
        {
            var masterConfigured = player.SetAudioOutput(module);
            var vocalConfigured = vocalPlayer.SetAudioOutput(module);
            if (masterConfigured && vocalConfigured)
                return $"Windows-Audio: {module}";
        }

        // Die automatische VLC-Auswahl darf weiterhin funktionieren. Der Status
        // macht einen fehlenden Plugin-Ordner in portablen Builds aber sichtbar.
        return "Windows-Audio: VLC-Systemstandard (mmdevice-Plugin nicht verfügbar)";
    }

    internal static IReadOnlyList<EditorAudioOutputDevice> GetWindowsAudioOutputDevices()
    {
        var result = new List<EditorAudioOutputDevice>
        {
            new(null, "Windows-Standardgerät (empfohlen)")
        };
        if (!OperatingSystem.IsWindows()) return result;
        InitializeLibVlcRuntime();
        using var libVlc = new LibVLC("--no-video", "--aout=mmdevice");
        foreach (var device in libVlc.AudioOutputDevices("mmdevice") ?? [])
        {
            if (string.IsNullOrWhiteSpace(device.DeviceIdentifier) ||
                result.Any(item => item.Id == device.DeviceIdentifier)) continue;
            result.Add(new(device.DeviceIdentifier,
                string.IsNullOrWhiteSpace(device.Description) ? device.DeviceIdentifier : device.Description));
        }
        return result;
    }

    private void ApplyConfiguredWindowsOutputDevice()
    {
        if (!OperatingSystem.IsWindows()) return;
#if ANDROID
        return;
#else
        var configured = EditorAudioSettings.Load().OutputDeviceId;
        if (configured == _configuredOutputDeviceId) return;
        _configuredOutputDeviceId = configured;
        if (string.IsNullOrWhiteSpace(configured)) return;
        _player.SetOutputDevice(configured, "mmdevice");
        _vocalPlayer.SetOutputDevice(configured, "mmdevice");
#endif
    }

    public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(_player.Time, 0));
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Math.Max(_player.Length, 0));
    public int Volume
    {
        get => _requestedMasterVolume;
        set
        {
            _requestedMasterVolume = Math.Clamp(value, 0, 100);
            if (!_vocalAwaitingAlignment) _player.Volume = _requestedMasterVolume;
            if (_requestedMasterVolume > 0 && _player.IsPlaying)
                QueueWindowsAudioSessionUnmute();
        }
    }
    public int VocalVolume
    {
        get => _requestedVocalVolume;
        set
        {
            _requestedVocalVolume = Math.Clamp(value, 0, 100);
            if (_vocalPlayer.IsPlaying) _vocalPlayer.Volume = _requestedVocalVolume;
        }
    }
    public double PlaybackRate
    {
        get => _requestedPlaybackRate;
        set
        {
            _requestedPlaybackRate = Math.Clamp(value, .1, 1);
            _player.SetRate((float)_requestedPlaybackRate);
            _vocalPlayer.SetRate((float)_requestedPlaybackRate);
        }
    }
    public bool IsPlaying => _player.IsPlaying;
    public bool IsSeekable => _player.IsSeekable;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? PlaybackStarted;
    public event EventHandler? PlaybackEnded;
    public event EventHandler<string>? PlaybackFailed;
    public event EventHandler<string>? StateChanged;

    public async Task PlayAsync(Uri source, Uri? vocalsSource = null, TimeSpan? startPosition = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Der Editor reicht auch am Songanfang explizit 00:00.000 durch. Dafür
        // ist kein Decoder-Seek nötig. Insbesondere VLC unter Windows konnte
        // andernfalls bereits laufen, während der zum Seek gesetzte Mute-Zustand
        // nicht wieder freigegeben wurde: Video und Text liefen, Audio blieb still.
        var initialSeek = startPosition is { } requested && requested > TimeSpan.FromMilliseconds(30)
            ? requested
            : (TimeSpan?)null;
        Interlocked.Increment(ref _playGeneration);
        Interlocked.Increment(ref _seekGeneration);
        _player.Stop();
        _vocalPlayer.Stop();
        _media?.Dispose();
        _vocalMedia?.Dispose();
        _vocalMedia = null;
        _userSeekInProgress = initialSeek is not null;
        _vocalAwaitingAlignment = initialSeek is not null;
        Interlocked.Exchange(ref _vocalStartScheduled, 0);
        Interlocked.Exchange(ref _vocalAlignmentScheduled, 0);
        _player.Mute = false;
        _vocalPlayer.Mute = false;
        _player.Volume = initialSeek is null ? _requestedMasterVolume : 0;
        _media = new Media(_libVlc, source);
        _sourcesAreLocal = source.IsFile && (vocalsSource is null || vocalsSource.IsFile);
        if (vocalsSource is not null)
        {
            _vocalMedia = new Media(_libVlc, vocalsSource);
            _vocalPlayer.Volume = 0;
        }
        if (!_player.Play(_media))
            throw new InvalidOperationException("Der Audiostream konnte nicht gestartet werden.");
        _player.SetRate((float)_requestedPlaybackRate);
        var generation = _playGeneration;
        WatchPlaybackStart(generation);
        if (initialSeek is not null)
        {
            for (var attempt = 0; attempt < 60 && generation == _playGeneration &&
                 (!_player.IsSeekable || !_player.IsPlaying); attempt++)
                await Task.Delay(50, cancellationToken);
            if (generation == _playGeneration && _player.IsSeekable)
                await SeekAsync(initialSeek.Value, cancellationToken);
            else if (generation == _playGeneration)
            {
                _userSeekInProgress = false;
                _vocalAwaitingAlignment = false;
                _player.Volume = _requestedMasterVolume;
            }
        }
    }

    private async Task EnsureAudiblePlaybackAsync(int generation)
    {
        // Sicherheitsnetz für Windows-Audiotreiber: ein abgebrochener initialer
        // Seek darf den Master nicht dauerhaft stumm lassen. Bei einem bewusst
        // stummen Stage-Test ist _requestedMasterVolume bereits 0 und bleibt 0.
        // Die CoreAudio-Sitzung entsteht bei einigen Treibern erst etwas nach
        // dem Playing-Event. Zweimaliges Prüfen deckt sowohl schnelle interne
        // Geräte als auch USB-/Bluetooth-Ausgänge ab.
        await Task.Delay(250);
        if (generation == _playGeneration && _player.IsPlaying && _requestedMasterVolume > 0)
            QueueWindowsAudioSessionUnmute();
        await Task.Delay(TimeSpan.FromMilliseconds(750));
        if (generation == _playGeneration && _player.IsPlaying && _requestedMasterVolume > 0)
            QueueWindowsAudioSessionUnmute();
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (generation != _playGeneration || !_player.IsPlaying || _requestedMasterVolume <= 0 ||
            _userSeekInProgress || _player.Volume > 0) return;
        _vocalAwaitingAlignment = false;
        _player.Mute = false;
        _player.Volume = _requestedMasterVolume;
        StateChanged?.Invoke(this,
            $"Windows-Audio reaktiviert · {_audioOutputStatus} · Pegel {_player.Volume}%");
    }

    private void QueueWindowsAudioSessionUnmute()
    {
        // Ein bewusst stummer Stage-Test setzt den angeforderten Pegel auf 0.
        // In diesem Zustand darf die Windows-Sitzung nicht verändert werden.
        if (!OperatingSystem.IsWindows() || _requestedMasterVolume <= 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                EnsurePlatformAudioSessionAudible();
            }
            catch
            {
                // Die Wiedergabe darf nicht an einer optionalen CoreAudio-
                // Reparatur scheitern; VLCs normaler Ausgabepfad bleibt aktiv.
            }
        });
    }

    private async void WatchPlaybackStart(int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (generation != _playGeneration || _player.IsPlaying) return;
        var diagnostic = _lastLibVlcMessage.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (diagnostic.Length > 180) diagnostic = diagnostic[..180] + "…";
        StateChanged?.Invoke(this,
            $"Audio hängt: VLC={_player.State}, seekbar={_player.IsSeekable}; {diagnostic}");
    }

    private void DisableVocalStem()
    {
        _vocalPlayer.Stop();
        _vocalMedia?.Dispose();
        _vocalMedia = null;
        _vocalAwaitingAlignment = false;
        _userSeekInProgress = false;
    }

    private void StartVocalMuted(int generation)
    {
        if (generation != _playGeneration || _vocalMedia is null) return;
        _vocalPlayer.Volume = 0;
        _vocalPlayer.SetRate((float)_requestedPlaybackRate);
        _vocalAwaitingAlignment = true;
        if (!_vocalPlayer.Play(_vocalMedia))
            DisableVocalStem();
    }

    public void Pause() { _player.SetPause(true); _vocalPlayer.SetPause(true); }
    public void Resume() { _player.SetPause(false); _vocalPlayer.SetPause(false); }
    public void Stop()
    {
        Interlocked.Increment(ref _playGeneration);
        Interlocked.Increment(ref _seekGeneration);
        _vocalAwaitingAlignment = false;
        _player.Stop();
        _vocalPlayer.Stop();
    }
    public void Seek(TimeSpan position)
        => _ = SeekAsync(position);

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        var milliseconds = Math.Max(0, (long)position.TotalMilliseconds);
        var resume = _player.IsPlaying;
        var seekGeneration = Interlocked.Increment(ref _seekGeneration);
        var playGeneration = _playGeneration;
        await _seekLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsCurrentMasterSeek(playGeneration, seekGeneration)) return;
            _userSeekInProgress = true;
            if (_sourcesAreLocal && _vocalMedia is null)
            {
                await SeekSingleLocalAsync(playGeneration, seekGeneration, milliseconds, resume, cancellationToken);
                return;
            }
        // Zwei unabhängige HTTP/Vorbis-Decoder dürfen während des Sprungs keine
        // alten Puffer hörbar ausgeben. Beide werden gemeinsam pausiert und
        // stummgeschaltet, anschließend auf derselben Zielzeit neu angefahren.
        _player.Volume = 0;
        _vocalPlayer.Volume = 0;
        _vocalAwaitingAlignment = true;
        _player.SetPause(true);
        if (_vocalMedia is not null) _vocalPlayer.SetPause(true);
        _player.Time = milliseconds;
        if (_vocalMedia is not null) _vocalPlayer.Time = milliseconds;
            await StabilizeUserSeekAsync(playGeneration, seekGeneration, milliseconds, resume, cancellationToken);
        }
        finally
        {
            if (IsCurrentMasterSeek(playGeneration, seekGeneration)) _userSeekInProgress = false;
            _seekLock.Release();
        }
    }

    private async Task SeekSingleLocalAsync(int playGeneration, int seekGeneration, long target, bool resume,
        CancellationToken cancellationToken)
    {
        _player.Volume = 0;
        _player.Time = target;
        var committed = false;
        // PCM/WAV ist samplegenau seekbar. Entscheidend ist trotzdem, nicht den
        // unmittelbar nach Set(Time) noch alten VLC-Wert als Erfolg zu werten.
        // Das war besonders bei Rückwärtssprüngen die Ursache für Desynchronität.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(10, cancellationToken);
            if (!IsCurrentMasterSeek(playGeneration, seekGeneration)) return;
            var current = Math.Max(0, _player.Time);
            if (Math.Abs(current - target) <= (resume ? 80 : 35)) { committed = true; break; }
            if (attempt is 12 or 24) _player.Time = target;
        }
        if (!committed)
        {
            _player.Stop();
            _player.Volume = _requestedMasterVolume;
            throw new InvalidOperationException(
                $"VLC hat die Zielposition {TimeSpan.FromMilliseconds(target):mm\\:ss\\.fff} nicht bestätigt; Wiedergabe wurde zum Schutz der Synchronität gestoppt.");
        }
        if (!IsCurrentMasterSeek(playGeneration, seekGeneration)) return;
        _vocalAwaitingAlignment = false;
        // Lokale PCM-Arbeitsdateien benötigen kein Fade-in.
        _player.Volume = _requestedMasterVolume;
    }

    private async Task StabilizeUserSeekAsync(int playGeneration, int seekGeneration, long target, bool resume,
        CancellationToken cancellationToken)
    {
        await Task.Delay(90, cancellationToken);
        if (!IsCurrentMasterSeek(playGeneration, seekGeneration)) return;
        _player.Time = target;
        if (_vocalMedia is not null) _vocalPlayer.Time = target;
        // Auch pausierte Sprünge werden stumm kurz vorgerollt. Erst dadurch
        // verwirft VLC zuverlässig die Decoderframes der vorherigen Position.
        _player.SetPause(false);
        if (_vocalMedia is not null) _vocalPlayer.SetPause(false);
        // HTTP- und MP3-Decoder melden Seekable oft bevor das neue Samplefenster
        // tatsächlich stabil läuft. Erst nach mehreren fortlaufenden Messungen
        // hörbar schalten; damit kann kein Rest des alten Puffers durchrutschen.
        var stableSamples = 0;
        var previousMaster = -1L;
        for (var attempt = 0; attempt < 30 && stableSamples < 3; attempt++)
        {
            await Task.Delay(70, cancellationToken);
            if (!IsCurrentMasterSeek(playGeneration, seekGeneration)) return;
            var master = Math.Max(0, _player.Time);
            var masterReady = master >= target - 140 && master > previousMaster + 5;
            var stemsReady = _vocalMedia is null || Math.Abs(_vocalPlayer.Time - master) <= 45;
            stableSamples = masterReady && stemsReady ? stableSamples + 1 : 0;
            if (_vocalMedia is not null && Math.Abs(_vocalPlayer.Time - master) > 65)
                _vocalPlayer.Time = master;
            previousMaster = master;
        }
        if (!resume)
        {
            _player.SetPause(true);
            if (_vocalMedia is not null) _vocalPlayer.SetPause(true);
        }
        _vocalAwaitingAlignment = false;
        // Kurzes Fade-in vermeidet Klicks und halb gefüllte Decoderframes.
        for (var step = 1; step <= 5; step++)
        {
            if (!IsCurrentMasterSeek(playGeneration, seekGeneration)) return;
            _player.Volume = _requestedMasterVolume * step / 5;
            if (_vocalMedia is not null) _vocalPlayer.Volume = _requestedVocalVolume * step / 5;
            await Task.Delay(28, cancellationToken);
        }
    }

    private bool IsCurrentMasterSeek(int playGeneration, int seekGeneration) =>
        playGeneration == _playGeneration && seekGeneration == _seekGeneration;

    private async Task StabilizeSeekAsync(int playGeneration, int seekGeneration)
    {
        await Task.Delay(140);
        if (!IsCurrentSeek(playGeneration, seekGeneration)) return;
        _vocalPlayer.Time = Math.Max(0, _player.Time);
        await Task.Delay(120);
        if (!IsCurrentSeek(playGeneration, seekGeneration)) return;
        if (Math.Abs(_vocalPlayer.Time - _player.Time) > 45)
        {
            _vocalPlayer.Time = Math.Max(0, _player.Time);
            await Task.Delay(80);
        }
        if (!IsCurrentSeek(playGeneration, seekGeneration)) return;
        _vocalAwaitingAlignment = false;
        _player.Volume = _requestedMasterVolume;
        _vocalPlayer.Volume = _requestedVocalVolume;
    }

    private bool IsCurrentSeek(int playGeneration, int seekGeneration) =>
        playGeneration == _playGeneration && seekGeneration == _seekGeneration && _vocalMedia is not null;

    private async Task AlignAndEnableVocalAsync(int generation)
    {
        if (generation != _playGeneration || _vocalMedia is null || _userSeekInProgress) return;
        _vocalPlayer.Time = Math.Max(0, _player.Time);
        await Task.Delay(150);
        if (generation != _playGeneration || _vocalMedia is null || _userSeekInProgress) return;
        // Während des ersten Seek-Vorgangs ist der Master weitergelaufen. Unmittelbar
        // vor dem Einblenden noch einmal angleichen, damit kein verspäteter Gesang hörbar wird.
        _vocalPlayer.Time = Math.Max(0, _player.Time);
        await Task.Delay(40);
        if (generation != _playGeneration || _vocalMedia is null || _userSeekInProgress) return;
        _vocalAwaitingAlignment = false;
        _vocalPlayer.Volume = _requestedVocalVolume;
    }

    private void SynchronizeVocals()
    {
        // Lokale, gleich erzeugte Stems laufen auf derselben stabilen Clock.
        // Periodische Hard-Seeks waren im Editor als abgehackter Ton hörbar.
        if (_sourcesAreLocal) return;
        if (_vocalMedia is null || !_vocalPlayer.IsPlaying || _vocalAwaitingAlignment) return;
        var masterTime = _player.Time;
        if (Math.Abs(_vocalPlayer.Time - masterTime) <= 90) return;
        _vocalPlayer.Volume = 0;
        _vocalAwaitingAlignment = true;
        _vocalPlayer.Time = masterTime;
        var seekGeneration = Interlocked.Increment(ref _seekGeneration);
        var playGeneration = _playGeneration;
        ThreadPool.QueueUserWorkItem(async _ => await StabilizeSeekAsync(playGeneration, seekGeneration));
    }

    public void Dispose()
    {
        _player.Stop();
        _vocalPlayer.Stop();
        _media?.Dispose();
        _vocalMedia?.Dispose();
        _vocalSyncTimer.Dispose();
        _vocalPlayer.Dispose();
        _player.Dispose();
        _libVlc.Dispose();
    }
}
