using NeonStage.Timing;
using UnityEngine;

namespace NeonStage.Stage
{

/// <summary>
/// Uses the singer's LiveKit timeline on listener stages. The local audio clock
/// remains the fallback for startup, offline operation and short reconnects.
/// </summary>
public sealed class OnlineSynchronizedStageClock : IStageClock
{
    private readonly StageAudioEngine _audio;
    private readonly OnlineStageController _online;

    public OnlineSynchronizedStageClock(StageAudioEngine audio, OnlineStageController online)
    {
        _audio = audio;
        _online = online;
    }

    public StageClockFrame Capture()
    {
        if (!_online.TryGetRemotePresentationPosition(_audio.DurationSeconds,
                out var position, out var playing))
            return new AudioStageClock(_audio).Capture();

        return new StageClockFrame(position, position, playing,
            playing ? Time.unscaledDeltaTime : 0);
    }
}

}
