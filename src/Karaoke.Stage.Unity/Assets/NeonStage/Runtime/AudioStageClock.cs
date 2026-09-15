using NeonStage.Timing;
using UnityEngine;

namespace NeonStage.Stage
{

/// <summary>Normal and editor-test clock backed by the Stage audio timeline.</summary>
public sealed class AudioStageClock : IStageClock
{
    private readonly StageAudioEngine _audio;

    public AudioStageClock(StageAudioEngine audio) => _audio = audio;

    public StageClockFrame Capture() => new(
        _audio.PositionSeconds,
        _audio.LyricsPositionSeconds,
        _audio.IsPlaying,
        Time.unscaledDeltaTime);
}

}
