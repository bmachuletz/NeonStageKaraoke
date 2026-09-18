using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeonStage.Stage
{

/// <summary>
/// Builds isolated outgoing signals for music, original vocals and microphones.
/// Remote WebRTC audio is deliberately never connected to either input.
/// </summary>
public sealed class OnlineBroadcastMixer : MonoBehaviour
{
    private const int MaximumMicrophoneDevices = 2;
    private const double MicrophonePrimeTimeoutSeconds = 1.5;
    private const double TargetMicrophoneBufferSeconds = .035;
    private const double MinimumMicrophoneBufferSeconds = .008;
    private const double MaximumMicrophoneBufferSeconds = .2;
    private const float MusicBroadcastGain = .82f;
    private const float OriginalVocalBroadcastGain = .82f;
    private const float InitialMicrophoneBroadcastGain = 8f;
    private const float MinimumMicrophoneBroadcastGain = 2.5f;
    private const float MaximumMicrophoneBroadcastGain = 12f;
    private const float TargetMicrophoneRms = .16f;
    private const float MicrophoneNoiseFloor = .0005f;
    private const float MicrophoneFullGateLevel = .003f;
    private const float MicrophoneLimiterPeak = .94f;

    private sealed class SampleQueue
    {
        private readonly object _gate = new();
        private readonly float[] _samples;
        private int _read;
        private int _count;

        public SampleQueue(int capacity) => _samples = new float[Math.Max(4096, capacity)];

        public void Write(float[] values)
        {
            lock (_gate)
            {
                foreach (var value in values)
                {
                    if (_count == _samples.Length) { _read = (_read + 1) % _samples.Length; _count--; }
                    _samples[(_read + _count) % _samples.Length] = value;
                    _count++;
                }
            }
        }

        public void MixInto(float[] target, float gain = 1f, int retainedSamples = 0)
        {
            lock (_gate)
            {
                // Keep the requested tail queued. This is used for the music
                // alignment delay: the singer reacts to music only after the
                // device output buffer, while a native microphone captures the
                // reaction in real time.
                var length = Math.Min(target.Length, Math.Max(0, _count - retainedSamples));
                for (var index = 0; index < length; index++)
                {
                    target[index] += _samples[_read] * gain;
                    _read = (_read + 1) % _samples.Length;
                }
                _count -= length;
            }
        }

        public void Clear() { lock (_gate) { _read = 0; _count = 0; } }
    }

    private sealed class MicrophoneCapture
    {
        public MicrophoneCapture(string device, AudioClip clip, AudioSource source,
            StageAudioTap tap, SampleQueue queue, Action<float[], int, int> handler,
            double createdAt)
        {
            Device = device;
            Clip = clip;
            Source = source;
            Tap = tap;
            Queue = queue;
            Handler = handler;
            CreatedAt = createdAt;
        }

        public string Device { get; }
        public AudioClip Clip { get; }
        public AudioSource Source { get; }
        public StageAudioTap Tap { get; }
        public SampleQueue Queue { get; }
        public Action<float[], int, int> Handler { get; }
        public double CreatedAt { get; }
        public bool Started { get; set; }
    }

    private StageAudioEngine _audio = null!;
    private SampleQueue _music = null!;
    private SampleQueue _originalVocals = null!;
    private readonly List<MicrophoneCapture> _microphoneCaptures = new();
    private SampleQueue[] _microphoneQueues = Array.Empty<SampleQueue>();
    private int _queueCapacity;
    private float[] _musicOutput = Array.Empty<float>();
    private float[] _originalVocalOutput = Array.Empty<float>();
    private float[] _microphoneOutput = Array.Empty<float>();
    private bool _broadcastEnabled;
    private float _broadcastMusicDelaySeconds;
    private float _microphoneAutomaticGain = InitialMicrophoneBroadcastGain;
    private bool _microphoneRequested;
    private string _microphoneStateFingerprint = string.Empty;
    private float _nextMicrophoneDevicePoll;

    public event Action<float[], int, int>? MusicRead;
    public event Action<float[], int, int>? OriginalVocalRead;
    public event Action<float[], int, int>? MicrophoneRead;
    public event Action<int>? ActiveMicrophonesChanged;
    public bool MicrophoneActive => _microphoneCaptures.Count > 0;
    public int ActiveMicrophoneCount => _microphoneCaptures.Count;
    public double BroadcastMusicDelaySeconds => _broadcastMusicDelaySeconds;

    public void ConfigureBroadcastMusicDelay(double seconds)
    {
        _broadcastMusicDelaySeconds = (float)Math.Clamp(seconds, 0, .5);
        _music.Clear();
        _originalVocals.Clear();
        Debug.Log($"[Online] broadcast music alignment delay={_broadcastMusicDelaySeconds * 1000:0}ms");
    }

    public void SetBroadcastEnabled(bool enabled)
    {
        _broadcastEnabled = enabled;
        // Never carry samples recorded during a role transition into the new
        // broadcast. Otherwise a reconnect can begin with an audible stale tail.
        _music.Clear();
        _originalVocals.Clear();
        foreach (var microphone in _microphoneQueues) microphone.Clear();
        if (enabled) _microphoneAutomaticGain = InitialMicrophoneBroadcastGain;
    }

    public void Initialize(StageAudioEngine audio)
    {
        _audio = audio;
        _queueCapacity = Math.Max(48000, AudioSettings.outputSampleRate) * 8;
        _music = new SampleQueue(_queueCapacity);
        _originalVocals = new SampleQueue(_queueCapacity);
        _audio.BroadcastMusicRead += OnMusicRead;
        _audio.BroadcastVocalRead += OnOriginalVocalRead;
    }

    public bool StartMicrophone()
    {
        _microphoneRequested = true;
        if (MicrophoneActive) return true;
        return StartMicrophoneCaptures();
    }

    private bool StartMicrophoneCaptures()
    {
        var reportedDevices = Microphone.devices;
        var devices = SelectMicrophoneDevices(reportedDevices,
            Application.platform is RuntimePlatform.LinuxPlayer or RuntimePlatform.LinuxEditor,
            StageRuntimeSettings.SelectedMicrophones, StageRuntimeSettings.AutomaticMicrophones);
        _microphoneStateFingerprint = BuildMicrophoneStateFingerprint(reportedDevices);
        if (devices.Length == 0)
        {
            Debug.LogWarning("[Online] no usable microphone capture device found; " +
                             $"reported=[{string.Join(", ", reportedDevices)}]");
            ActiveMicrophonesChanged?.Invoke(0);
            return false;
        }

        Debug.Log($"[Online] microphone candidates selected=[{string.Join(", ", devices)}]; " +
                  $"reported=[{string.Join(", ", reportedDevices)}]");

        for (var index = 0; index < devices.Length && _microphoneCaptures.Count < MaximumMicrophoneDevices; index++)
        {
            var device = devices[index];
            try
            {
                var clip = Microphone.Start(device, true, 1, AudioSettings.outputSampleRate);
                if (clip == null)
                {
                    Debug.LogWarning($"[Online] microphone could not be started device={device}");
                    continue;
                }

                var queue = new SampleQueue(_queueCapacity);
                var microphoneObject = new GameObject($"Online Microphone {_microphoneCaptures.Count + 1} (broadcast only)");
                microphoneObject.transform.SetParent(transform, false);
                var source = microphoneObject.AddComponent<AudioSource>();
                source.loop = true;
                source.clip = clip;
                var tap = microphoneObject.AddComponent<StageAudioTap>();
                tap.MuteOutput = true;
                var drivesMicrophoneOutput = _microphoneCaptures.Count == 0;
                Action<float[], int, int> handler = (data, channels, sampleRate) =>
                {
                    queue.Write(data);
                    if (drivesMicrophoneOutput) EmitMicrophone(data.Length, channels, sampleRate);
                };
                tap.AudioRead += handler;
                _microphoneCaptures.Add(new MicrophoneCapture(device, clip,
                    source, tap, queue, handler, Time.realtimeSinceStartupAsDouble));
                Debug.Log($"[Online] microphone selected index={_microphoneCaptures.Count} device={device} " +
                          $"inputChannels={clip.channels} sampleRate={clip.frequency}; priming low-latency ring buffer");
            }
            catch (Exception exception)
            {
                try { Microphone.End(device); } catch { /* ignored: device did not start */ }
                Debug.LogWarning($"[Online] microphone start failed device={device}: {exception.Message}");
            }
        }

        _microphoneQueues = _microphoneCaptures.ConvertAll(item => item.Queue).ToArray();
        Debug.Log($"[Online] active microphone devices={_microphoneCaptures.Count}; " +
                  $"usable devices={devices.Length}; reported devices={reportedDevices.Length}; " +
                  $"sender AGC={MinimumMicrophoneBroadcastGain:0.0}-{MaximumMicrophoneBroadcastGain:0.0}x");
        ActiveMicrophonesChanged?.Invoke(_microphoneCaptures.Count);
        return MicrophoneActive;
    }

    internal static string[] SelectMicrophoneDevices(IReadOnlyList<string> reportedDevices,
        bool removeLinuxDefaultAlias, IReadOnlyList<string>? preferredDevices = null,
        bool automatic = true)
    {
        var candidates = new List<string>();
        foreach (var device in reportedDevices)
        {
            if (string.IsNullOrWhiteSpace(device) || IsMonitorSource(device)) continue;
            candidates.Add(device);
        }

        // PulseAudio/PipeWire exposes "Default Input Device" in addition to the
        // physical source names. Capturing both records the same laptop mic
        // twice and, more importantly, used to consume one of our two slots
        // before the actual Digital Microphone was reached.
        if (removeLinuxDefaultAlias && candidates.Exists(device => !IsDefaultInputAlias(device)))
            candidates.RemoveAll(IsDefaultInputAlias);

        if (!automatic)
        {
            var selected = new List<string>();
            foreach (var preferred in preferredDevices ?? Array.Empty<string>())
            foreach (var candidate in candidates)
                if (string.Equals(preferred, candidate, StringComparison.Ordinal) &&
                    !selected.Contains(candidate)) selected.Add(candidate);
            return selected.ToArray();
        }

        candidates.Sort((left, right) => MicrophonePreference(right)
            .CompareTo(MicrophonePreference(left)));
        return candidates.ToArray();
    }

    private static string BuildMicrophoneStateFingerprint(IReadOnlyList<string> devices) =>
        string.Join("\u001e", devices) + "|" + StageRuntimeSettings.AutomaticMicrophones + "|" +
        string.Join("\u001e", StageRuntimeSettings.SelectedMicrophones);

    private static bool IsMonitorSource(string device)
    {
        var value = device.ToLowerInvariant();
        return value.Contains("monitor of") || value.Contains("monitor von") ||
               value.Contains(".monitor") || value.EndsWith(" monitor");
    }

    private static bool IsDefaultInputAlias(string device)
    {
        var value = device.ToLowerInvariant();
        return value is "default" or "default input device" or "standard-eingabegerät" ||
               value.StartsWith("default input ");
    }

    private static int MicrophonePreference(string device)
    {
        var value = device.ToLowerInvariant();
        var score = 0;
        if (value.Contains("usb")) score += 400;
        if (value.Contains("digital") || value.Contains("dmic") ||
            value.Contains("internal") || value.Contains("built-in") ||
            value.Contains("array")) score += 300;
        if (value.Contains("microphone") || value.Contains("mikrofon") ||
            value.Contains(" mic")) score += 200;
        if (IsDefaultInputAlias(device)) score += 100;
        return score;
    }

    private void Update()
    {
        if (_microphoneRequested && Time.unscaledTime >= _nextMicrophoneDevicePoll)
        {
            _nextMicrophoneDevicePoll = Time.unscaledTime + 1.5f;
            var fingerprint = BuildMicrophoneStateFingerprint(Microphone.devices);
            if (!string.Equals(fingerprint, _microphoneStateFingerprint, StringComparison.Ordinal))
            {
                Debug.Log("[Online] microphone devices or selection changed; refreshing capture inputs");
                StopMicrophoneCaptures();
                StartMicrophoneCaptures();
            }
        }

        // Microphone.Start writes into a looping clip. Starting its AudioSource
        // immediately at sample zero can put the read head just *ahead* of the
        // writer. It then has to wait almost a complete one-second revolution,
        // which is perceived as a very late vocal on the remote stage. Prime on
        // Unity's main thread and start a small, explicit distance behind the
        // capture head instead. Microphone.GetPosition is main-thread-only on
        // Android, hence this deliberately does not run in OnAudioFilterRead.
        foreach (var capture in _microphoneCaptures)
        {
            if (capture.Source == null || capture.Clip == null) continue;
            var writePosition = Microphone.GetPosition(capture.Device);
            if (writePosition < 0) continue;
            var targetFrames = Math.Max(1,
                (int)Math.Round(capture.Clip.frequency * TargetMicrophoneBufferSeconds));

            if (capture.Started)
            {
                if (!capture.Source.isPlaying) continue;
                var currentBufferedFrames = writePosition - capture.Source.timeSamples;
                if (currentBufferedFrames < 0) currentBufferedFrames += capture.Clip.samples;
                var minimumFrames = capture.Clip.frequency * MinimumMicrophoneBufferSeconds;
                var maximumFrames = capture.Clip.frequency * MaximumMicrophoneBufferSeconds;
                if (currentBufferedFrames >= minimumFrames && currentBufferedFrames <= maximumFrames) continue;

                // Input and output devices may use slightly different clocks.
                // Re-anchor before a tiny underrun turns into a full ring-buffer
                // revolution (or a suspended device leaves old audio queued).
                var correctedReadPosition = writePosition - targetFrames;
                if (correctedReadPosition < 0) correctedReadPosition += capture.Clip.samples;
                capture.Source.timeSamples = correctedReadPosition;
                capture.Queue.Clear();
                Debug.LogWarning($"[Online] microphone ring buffer re-anchored device={capture.Device} " +
                                 $"previous={currentBufferedFrames * 1000d / capture.Clip.frequency:0.0}ms " +
                                 $"target={TargetMicrophoneBufferSeconds * 1000:0.0}ms");
                continue;
            }

            var timedOut = Time.realtimeSinceStartupAsDouble - capture.CreatedAt >=
                           MicrophonePrimeTimeoutSeconds;
            if (writePosition < targetFrames && !timedOut) continue;

            var bufferedFrames = Math.Min(targetFrames, Math.Max(0, writePosition));
            var readPosition = writePosition - bufferedFrames;
            if (readPosition < 0) readPosition += capture.Clip.samples;
            capture.Source.timeSamples = readPosition;
            capture.Source.Play();
            capture.Started = true;
            Debug.Log($"[Online] microphone low-latency capture started device={capture.Device} " +
                      $"buffer={bufferedFrames * 1000d / capture.Clip.frequency:0.0}ms " +
                      $"write={writePosition} read={readPosition}");
        }
    }

    public void StopMicrophone()
    {
        _microphoneRequested = false;
        StopMicrophoneCaptures();
    }

    private void StopMicrophoneCaptures()
    {
        _microphoneQueues = Array.Empty<SampleQueue>();
        foreach (var capture in _microphoneCaptures)
        {
            capture.Tap.AudioRead -= capture.Handler;
            try { Microphone.End(capture.Device); }
            catch (Exception exception)
            {
                Debug.LogWarning($"[Online] microphone stop failed device={capture.Device}: {exception.Message}");
            }
            capture.Queue.Clear();
            if (capture.Source != null) Destroy(capture.Source.gameObject);
        }
        var stopped = _microphoneCaptures.Count;
        _microphoneCaptures.Clear();
        Debug.Log($"[Online] microphones stopped count={stopped}");
        if (stopped > 0) ActiveMicrophonesChanged?.Invoke(0);
    }

    private void OnMusicRead(float[] data, int channels, int sampleRate)
    {
        if (!_broadcastEnabled) return;
        _music.Write(data);
        if (_musicOutput.Length != data.Length) _musicOutput = new float[data.Length];
        else Array.Clear(_musicOutput, 0, _musicOutput.Length);
        var retainedMusicSamples = (int)Math.Round(
            _broadcastMusicDelaySeconds * sampleRate * Math.Max(1, channels));
        _music.MixInto(_musicOutput, MusicBroadcastGain, retainedMusicSamples);
        Clamp(_musicOutput);
        MusicRead?.Invoke(_musicOutput, channels, sampleRate);
    }

    private void EmitMicrophone(int sampleCount, int channels, int sampleRate)
    {
        if (!_broadcastEnabled) return;
        if (_microphoneOutput.Length != sampleCount) _microphoneOutput = new float[sampleCount];
        else Array.Clear(_microphoneOutput, 0, _microphoneOutput.Length);
        var microphoneQueues = _microphoneQueues;
        // With two independent inputs equal-power attenuation retains stable
        // headroom. Sender-side AGC below then raises quiet karaoke microphones
        // before LiveKit encodes the dedicated microphone track.
        var microphoneGain = microphoneQueues.Length > 0
            ? 1f / Mathf.Sqrt(microphoneQueues.Length)
            : 0f;
        foreach (var microphone in microphoneQueues)
            microphone.MixInto(_microphoneOutput, microphoneGain);
        ProcessMicrophoneForBroadcast(_microphoneOutput);
        MicrophoneRead?.Invoke(_microphoneOutput, channels, sampleRate);
    }

    private void OnOriginalVocalRead(float[] data, int channels, int sampleRate)
    {
        if (!_broadcastEnabled) return;
        _originalVocals.Write(data);
        if (_originalVocalOutput.Length != data.Length) _originalVocalOutput = new float[data.Length];
        else Array.Clear(_originalVocalOutput, 0, _originalVocalOutput.Length);
        var retainedSamples = (int)Math.Round(
            _broadcastMusicDelaySeconds * sampleRate * Math.Max(1, channels));
        _originalVocals.MixInto(_originalVocalOutput, OriginalVocalBroadcastGain, retainedSamples);
        Clamp(_originalVocalOutput);
        OriginalVocalRead?.Invoke(_originalVocalOutput, channels, sampleRate);
    }

    private static void Clamp(float[] samples)
    {
        for (var index = 0; index < samples.Length; index++)
            samples[index] = Mathf.Clamp(samples[index], -1f, 1f);
    }

    private void ProcessMicrophoneForBroadcast(float[] samples)
    {
        if (samples.Length == 0) return;
        double energy = 0;
        var inputPeak = 0f;
        for (var index = 0; index < samples.Length; index++)
        {
            var absolute = Mathf.Abs(samples[index]);
            inputPeak = Mathf.Max(inputPeak, absolute);
            energy += samples[index] * samples[index];
        }

        var rms = (float)Math.Sqrt(energy / samples.Length);
        if (inputPeak <= float.Epsilon) return;
        if (rms > MicrophoneNoiseFloor)
        {
            var desiredGain = Mathf.Clamp(TargetMicrophoneRms / rms,
                MinimumMicrophoneBroadcastGain, MaximumMicrophoneBroadcastGain);
            // Turn down quickly on a loud phrase, but raise a quiet microphone
            // smoothly to avoid audible gain jumps between adjacent buffers.
            var smoothing = desiredGain < _microphoneAutomaticGain ? .65f : .14f;
            _microphoneAutomaticGain = Mathf.Lerp(_microphoneAutomaticGain, desiredGain, smoothing);
        }

        var gate = Mathf.Clamp01((rms - MicrophoneNoiseFloor) /
                                 (MicrophoneFullGateLevel - MicrophoneNoiseFloor));
        // Never hard-cut the beginning of a soft word; the bottom of the gate
        // merely suppresses amplified idle noise.
        gate = Mathf.Lerp(.12f, 1f, gate);
        var appliedGain = _microphoneAutomaticGain * gate;
        var projectedPeak = inputPeak * appliedGain;
        if (projectedPeak > MicrophoneLimiterPeak)
            appliedGain *= MicrophoneLimiterPeak / projectedPeak;

        for (var index = 0; index < samples.Length; index++)
            samples[index] *= appliedGain;
    }

    private void OnDestroy()
    {
        if (_audio != null) _audio.BroadcastMusicRead -= OnMusicRead;
        if (_audio != null) _audio.BroadcastVocalRead -= OnOriginalVocalRead;
        StopMicrophone();
    }
}
}
