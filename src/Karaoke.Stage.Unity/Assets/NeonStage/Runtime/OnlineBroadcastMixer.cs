using System;
using System.Collections.Generic;
using UnityEngine;

namespace NeonStage.Stage
{

/// <summary>
/// Builds the only permitted outgoing signal: local music plus local microphones.
/// Remote WebRTC audio is deliberately never connected to either input.
/// </summary>
public sealed class OnlineBroadcastMixer : MonoBehaviour
{
    private const int MaximumMicrophoneDevices = 2;

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

        public void MixInto(float[] target, float gain = 1f)
        {
            lock (_gate)
            {
                var length = Math.Min(target.Length, _count);
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
        public MicrophoneCapture(string device, AudioSource source, StageAudioTap tap,
            SampleQueue queue, Action<float[], int, int> handler)
        {
            Device = device;
            Source = source;
            Tap = tap;
            Queue = queue;
            Handler = handler;
        }

        public string Device { get; }
        public AudioSource Source { get; }
        public StageAudioTap Tap { get; }
        public SampleQueue Queue { get; }
        public Action<float[], int, int> Handler { get; }
    }

    private StageAudioEngine _audio = null!;
    private SampleQueue _music = null!;
    private readonly List<MicrophoneCapture> _microphoneCaptures = new();
    private SampleQueue[] _microphoneQueues = Array.Empty<SampleQueue>();
    private int _queueCapacity;
    private float[] _mixed = Array.Empty<float>();

    public event Action<float[], int, int>? AudioRead;
    public bool MicrophoneActive => _microphoneCaptures.Count > 0;
    public int ActiveMicrophoneCount => _microphoneCaptures.Count;

    public void Initialize(StageAudioEngine audio)
    {
        _audio = audio;
        _queueCapacity = Math.Max(48000, AudioSettings.outputSampleRate) * 8;
        _music = new SampleQueue(_queueCapacity);
        _audio.BroadcastMusicRead += OnMusicRead;
    }

    public bool StartMicrophone()
    {
        if (MicrophoneActive) return true;
        var devices = Microphone.devices;
        if (devices.Length == 0) return false;

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
                Action<float[], int, int> handler = (data, _, _) => queue.Write(data);
                tap.AudioRead += handler;
                _microphoneCaptures.Add(new MicrophoneCapture(
                    device, source, tap, queue, handler));
                source.Play();
                Debug.Log($"[Online] microphone selected index={_microphoneCaptures.Count} device={device} " +
                          $"inputChannels={clip.channels} sampleRate={clip.frequency}");
            }
            catch (Exception exception)
            {
                try { Microphone.End(device); } catch { /* ignored: device did not start */ }
                Debug.LogWarning($"[Online] microphone start failed device={device}: {exception.Message}");
            }
        }

        _microphoneQueues = _microphoneCaptures.ConvertAll(item => item.Queue).ToArray();
        Debug.Log($"[Online] active microphone devices={_microphoneCaptures.Count}; reported devices={devices.Length}");
        return MicrophoneActive;
    }

    public void StopMicrophone()
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
    }

    private void OnMusicRead(float[] data, int channels, int sampleRate)
    {
        _music.Write(data);
        if (_mixed.Length != data.Length) _mixed = new float[data.Length];
        else Array.Clear(_mixed, 0, _mixed.Length);
        _music.MixInto(_mixed);
        var microphoneQueues = _microphoneQueues;
        // Two independent microphones can peak at the same time. Equal-power
        // attenuation preserves their perceived level while reducing clipping.
        var microphoneGain = microphoneQueues.Length > 1 ? .70710678f : 1f;
        foreach (var microphone in microphoneQueues)
            microphone.MixInto(_mixed, microphoneGain);
        for (var index = 0; index < _mixed.Length; index++) _mixed[index] = Mathf.Clamp(_mixed[index], -1f, 1f);
        AudioRead?.Invoke(_mixed, channels, sampleRate);
    }

    private void OnDestroy()
    {
        if (_audio != null) _audio.BroadcastMusicRead -= OnMusicRead;
        StopMicrophone();
    }
}
}
