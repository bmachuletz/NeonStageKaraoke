using System;
using System.Threading.Tasks;
using NeonStage.Presentation;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonStage.Stage
{

/// <summary>
/// Zwei AudioSources werden auf derselben Unity-DSP-Zeit gestartet. Das ist die
/// gemeinsame Zeitbasis für Instrumental, Vocals, Lyrics und spätere Scoring-Events.
/// </summary>
public sealed class StageAudioEngine : MonoBehaviour
{
    private AudioSource _master = null!;
    private AudioSource _vocals = null!;
    private int _generation;
    private double _dspAnchor;
    private double _timelineAnchor;
    private double _pausedPosition;
    private bool _clockRunning;
    private bool _playbackStarted;
    private bool _sourcesStarted;
    private bool _completionRaised;
    private double _sampleClockCorrection;
    private double? _configuredOutputLatencySeconds;
    private readonly float[] _waveform = new float[256];

    public event Action? PlaybackEnded;
    public event Action<float[], int, int>? BroadcastMusicRead;
    public event Action<float[], int, int>? BroadcastVocalRead;
    public string Status { get; private set; } = "Audio bereit";
    public bool IsPlaying => _master != null && _master.isPlaying;
    public bool HasClip => _master != null && _master.clip != null;
    public bool HasEnded => _completionRaised;
    public double DurationSeconds => HasClip ? _master.clip.length : 0;
    public bool LocalOutputMuted
    {
        get => _master != null && _master.mute;
        set
        {
            if (_master != null) _master.mute = value;
            if (_vocals != null) _vocals.mute = value;
        }
    }
    public float MusicVolume
    {
        get => _master != null ? _master.volume : 0.85f;
        set { if (_master != null) _master.volume = Mathf.Clamp01(value); }
    }
    public float VocalVolume
    {
        get => _vocals != null ? _vocals.volume : 0.35f;
        set { if (_vocals != null) _vocals.volume = Mathf.Clamp01(value); }
    }
    public double PositionSeconds
    {
        get
        {
            if (!HasClip) return 0;
            var position = _clockRunning
                ? _timelineAnchor + Math.Max(0, AudioSettings.dspTime - _dspAnchor) + _sampleClockCorrection
                : _pausedPosition;
            return Math.Clamp(position, 0, DurationSeconds);
        }
    }
    public double EstimatedOutputLatencySeconds
    {
        get
        {
            AudioSettings.GetDSPBufferSize(out var bufferLength, out var bufferCount);
            return StageTimingCompensation.EstimateOutputLatencySeconds(
                bufferLength, bufferCount, AudioSettings.outputSampleRate);
        }
    }
    public double AppliedOutputLatencySeconds =>
        _configuredOutputLatencySeconds ?? EstimatedOutputLatencySeconds;
    public double LyricsPositionSeconds => StageTimingCompensation.LyricsPositionSeconds(
        PositionSeconds, DurationSeconds, AppliedOutputLatencySeconds);
    public bool UsesAutomaticOutputLatency => _configuredOutputLatencySeconds is null;

    public void ConfigureOutputLatencySeconds(double? seconds) =>
        _configuredOutputLatencySeconds = seconds is null
            ? null
            : Math.Clamp(seconds.Value, 0, StageTimingCompensation.MaximumOutputLatencySeconds);

    public StageTimingSampleDto CaptureTiming(string deviceId, string songId)
    {
        AudioSettings.GetDSPBufferSize(out var bufferLength, out var bufferCount);
        var dspPosition = _clockRunning
            ? _timelineAnchor + Math.Max(0, AudioSettings.dspTime - _dspAnchor)
            : _pausedPosition;
        var masterPosition = HasClip && _master.clip.frequency > 0
            ? (double)_master.timeSamples / _master.clip.frequency : 0;
        var vocalPosition = _vocals?.clip != null && _vocals.clip.frequency > 0
            ? (double)_vocals.timeSamples / _vocals.clip.frequency : masterPosition;
        return new StageTimingSampleDto
        {
            capturedAt = DateTime.UtcNow.ToString("O"),
            deviceId = deviceId,
            songId = songId,
            lyricsPositionSeconds = LyricsPositionSeconds,
            dspPositionSeconds = dspPosition,
            masterSamplePositionSeconds = masterPosition,
            vocalSamplePositionSeconds = vocalPosition,
            sampleClockCorrectionSeconds = _sampleClockCorrection,
            stemDifferenceSeconds = vocalPosition - masterPosition,
            dspBufferLength = bufferLength,
            dspBufferCount = bufferCount,
            outputSampleRate = AudioSettings.outputSampleRate,
            estimatedOutputLatencySeconds = EstimatedOutputLatencySeconds,
            appliedOutputLatencySeconds = AppliedOutputLatencySeconds,
            playing = IsPlaying
        };
    }

    public void GetSpectrum(float[] target)
    {
        if (target == null || target.Length == 0) return;
        Array.Clear(target, 0, target.Length);
        if (_master == null || !_master.isPlaying) return;
        _master.GetSpectrumData(target, 0, FFTWindow.BlackmanHarris);
        _master.GetOutputData(_waveform, 0);
        var rms = 0f;
        for (var i = 0; i < _waveform.Length; i++) rms += _waveform[i] * _waveform[i];
        rms = Mathf.Sqrt(rms / _waveform.Length);
        // Streaming decoders on some Linux/Android devices expose an empty FFT
        // after their initial buffer. The actual output waveform remains valid;
        // feed its envelope into the low bands as a stable visual fallback.
        for (var i = 0; i < Mathf.Min(18, target.Length); i++)
            target[i] = Mathf.Max(target[i], rms * Mathf.Lerp(.055f, .012f, i / 17f));
        if (_vocals == null || !_vocals.isPlaying || _vocals.volume <= .001f) return;
        var vocals = new float[target.Length];
        _vocals.GetSpectrumData(vocals, 0, FFTWindow.BlackmanHarris);
        for (var i = 0; i < target.Length; i++) target[i] += vocals[i] * _vocals.volume;
    }

    private void Awake()
    {
        var musicBus = new GameObject("Music Bus");
        musicBus.transform.SetParent(transform, false);
        var vocalBus = new GameObject("Vocal Reference Bus");
        vocalBus.transform.SetParent(transform, false);
        _master = musicBus.AddComponent<AudioSource>();
        _vocals = vocalBus.AddComponent<AudioSource>();
        musicBus.AddComponent<StageAudioTap>().AudioRead +=
            (samples, channels, rate) => BroadcastMusicRead?.Invoke(samples, channels, rate);
        vocalBus.AddComponent<StageAudioTap>().AudioRead +=
            (samples, channels, rate) => BroadcastVocalRead?.Invoke(samples, channels, rate);
        _master.playOnAwake = false;
        _vocals.playOnAwake = false;
        _master.volume = 0.85f;
        _vocals.volume = 0.35f;
    }

    private void Update()
    {
        if (!_clockRunning || !HasClip) return;
        if (!_master.isPlaying)
        {
            if (!_playbackStarted || _completionRaised) return;
            var endPosition = _timelineAnchor + Math.Max(0, AudioSettings.dspTime - _dspAnchor);
            if (endPosition < DurationSeconds - .12) return;
            _pausedPosition = DurationSeconds;
            _clockRunning = false;
            _completionRaised = true;
            PlaybackEnded?.Invoke();
            return;
        }

        _playbackStarted = true;
        if (_master.clip.frequency <= 0) return;
        var dspPosition = _timelineAnchor + Math.Max(0, AudioSettings.dspTime - _dspAnchor);
        var samplePosition = (double)_master.timeSamples / _master.clip.frequency;
        var target = Math.Clamp(samplePosition - dspPosition, -.35, .35);
        // Follow decoder drift by at most a few milliseconds per frame. This
        // removes streaming-buffer offsets without making lyric motion jitter.
        _sampleClockCorrection = MoveTowards(_sampleClockCorrection, target, Time.unscaledDeltaTime * .12);
    }

    public async Task PlayAsync(string masterUrl, string? vocalsUrl)
    {
        await LoadPausedAsync(masterUrl, vocalsUrl);
        Resume();
    }

    public async Task LoadPausedAsync(string masterUrl, string? vocalsUrl)
    {
        var generation = ++_generation;
        Stop();
        Status = StageLocale.Text("Audiostream wird geladen …", "Loading audio stream …");

        var masterTask = LoadClipAsync(masterUrl, masterUrl.Contains("/stems/", StringComparison.OrdinalIgnoreCase)
            ? AudioType.OGGVORBIS : AudioType.MPEG);
        var vocalTask = string.IsNullOrWhiteSpace(vocalsUrl)
            ? Task.FromResult<AudioClip?>(null)
            : LoadClipAsync(vocalsUrl, AudioType.OGGVORBIS);

        var clips = await Task.WhenAll(masterTask, vocalTask);
        if (generation != _generation) return;
        if (clips[0] == null) throw new InvalidOperationException("Unity konnte die Masterspur nicht decodieren.");

        _master.clip = clips[0];
        _vocals.clip = clips[1];
        _timelineAnchor = 0;
        _pausedPosition = 0;
        _dspAnchor = AudioSettings.dspTime;
        _clockRunning = false;
        _playbackStarted = false;
        _sourcesStarted = false;
        _completionRaised = false;
        _sampleClockCorrection = 0;
        Status = StageLocale.Text("Audio bereit", "Audio ready");
    }

    private static async Task<AudioClip?> LoadClipAsync(string url, AudioType audioType)
    {
        using var request = UnityWebRequestMultimedia.GetAudioClip(url, audioType);
        var handler = (DownloadHandlerAudioClip)request.downloadHandler;
        handler.streamAudio = true;
        await request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success)
            throw new InvalidOperationException($"{StageLocale.Text("Audio-HTTP-Fehler", "Audio HTTP error")}: {request.responseCode} {request.error}");
        var clip = handler.audioClip;
        if (clip == null || clip.loadState == AudioDataLoadState.Failed || clip.frequency <= 0 || clip.samples <= 0)
            throw new InvalidOperationException($"Unity konnte das Audioformat nicht decodieren ({audioType}).");
        return clip;
    }

    public void Pause()
    {
        _pausedPosition = PositionSeconds;
        _clockRunning = false;
        _master.Pause();
        _vocals.Pause();
        Status = StageLocale.Text("Wiedergabe pausiert", "Playback paused");
    }

    public void Resume()
    {
        if (!HasClip || _completionRaised) return;
        if (!_sourcesStarted)
        {
            var dspStart = AudioSettings.dspTime + 0.1;
            _master.time = (float)_pausedPosition;
            if (_vocals.clip != null) _vocals.time = (float)_pausedPosition;
            _master.PlayScheduled(dspStart);
            if (_vocals.clip != null) _vocals.PlayScheduled(dspStart);
            _dspAnchor = dspStart;
            _sourcesStarted = true;
        }
        else
        {
            _master.UnPause();
            _vocals.UnPause();
            _dspAnchor = AudioSettings.dspTime;
        }
        _timelineAnchor = _pausedPosition;
        _clockRunning = true;
        _sampleClockCorrection = 0;
        Status = StageLocale.Text("Wiedergabe läuft", "Playback running");
    }

    public void Seek(double seconds)
    {
        var maximum = DurationSeconds > 0 ? Math.Max(0, DurationSeconds - 0.05) : double.MaxValue;
        var position = (float)Math.Clamp(seconds, 0, maximum);
        _master.time = position;
        if (_vocals.clip != null) _vocals.time = position;
        _pausedPosition = position;
        _timelineAnchor = position;
        _dspAnchor = AudioSettings.dspTime;
        _completionRaised = false;
        _sampleClockCorrection = 0;
    }

    public void Stop()
    {
        _master?.Stop();
        _vocals?.Stop();
        _clockRunning = false;
        _playbackStarted = false;
        _sourcesStarted = false;
        _completionRaised = false;
        _pausedPosition = 0;
        _timelineAnchor = 0;
        _sampleClockCorrection = 0;
    }

    private static double MoveTowards(double current, double target, double maximumDelta) =>
        Math.Abs(target - current) <= maximumDelta ? target : current + Math.Sign(target - current) * maximumDelta;
}
}
