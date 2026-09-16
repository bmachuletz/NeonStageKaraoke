using System;
using UnityEngine;

namespace NeonStage.Stage
{

/// <summary>Reads one Unity audio bus without changing its signal.</summary>
public sealed class StageAudioTap : MonoBehaviour
{
    public event Action<float[], int, int>? AudioRead;
    public bool MuteOutput { get; set; }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        AudioRead?.Invoke(data, channels, AudioSettings.outputSampleRate);
        if (MuteOutput) Array.Clear(data, 0, data.Length);
    }
}
}
