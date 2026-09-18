using System;
using UnityEngine;

namespace NeonStage.Stage
{

/// <summary>Reads one Unity audio bus without changing its signal.</summary>
public sealed class StageAudioTap : MonoBehaviour
{
    public event Action<float[], int, int>? AudioRead;
    public bool MuteOutput { get; set; }
    private int _sampleRate = 48000;

    private void OnEnable()
    {
        // Unity invokes OnAudioFilterRead on its audio worker. Querying
        // AudioSettings.outputSampleRate there throws on Android and aborts the
        // complete instrumental signal path. Cache it on the main thread.
        RefreshSampleRate(false);
        AudioSettings.OnAudioConfigurationChanged += RefreshSampleRate;
    }

    private void OnDisable() =>
        AudioSettings.OnAudioConfigurationChanged -= RefreshSampleRate;

    private void RefreshSampleRate(bool deviceWasChanged) =>
        _sampleRate = Math.Max(8000, AudioSettings.outputSampleRate);

    private void OnAudioFilterRead(float[] data, int channels)
    {
        AudioRead?.Invoke(data, channels, _sampleRate);
        if (MuteOutput) Array.Clear(data, 0, data.Length);
    }
}
}
