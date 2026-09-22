using System;
using UnityEngine;

namespace NeonStage.Stage
{

/// <summary>
/// Generates an original, sample-seamless chiptune loop at runtime. Keeping the
/// waiting music procedural avoids a licensed binary asset and lets every note
/// envelope reach zero exactly at the loop boundary.
/// </summary>
public sealed class StageIdleMusic : MonoBehaviour
{
    private const float MaximumVolume = .18f;
    private const float FadeInSeconds = 1.1f;
    private const float FadeOutSeconds = .55f;
    private AudioSource _source = null!;
    private bool _requested = true;
    private bool _suppressed;
    private bool _paused;

    private void Awake()
    {
        _source = gameObject.AddComponent<AudioSource>();
        _source.name = "NeonStage 8-bit Lobby Music";
        _source.playOnAwake = false;
        _source.loop = true;
        _source.spatialBlend = 0;
        _source.volume = 0;
        _source.clip = ComposeLoop(Mathf.Clamp(AudioSettings.outputSampleRate, 22050, 48000));
        _source.Play();
    }

    public void SetIdle(bool active, bool immediate = false)
    {
        _requested = active;
        ApplyRequestedState(immediate);
    }

    /// <summary>
    /// Test and export players never represent a lobby. Suppression is kept
    /// separately from the requested lobby state so no later status update can
    /// accidentally bring the intro music back while such a player lives.
    /// </summary>
    public void SetSuppressed(bool suppressed)
    {
        _suppressed = suppressed;
        ApplyRequestedState(immediate: true);
    }

    private void ApplyRequestedState(bool immediate)
    {
        var active = _requested && !_suppressed;
        if (active && _paused)
        {
            _source.UnPause();
            _paused = false;
        }
        if (!immediate) return;
        _source.volume = active ? MaximumVolume : 0;
        if (!active && _source.isPlaying)
        {
            _source.Pause();
            _paused = true;
        }
    }

    private void Update()
    {
        var active = _requested && !_suppressed;
        var target = active ? MaximumVolume : 0;
        var seconds = active ? FadeInSeconds : FadeOutSeconds;
        _source.volume = Mathf.MoveTowards(_source.volume, target,
            MaximumVolume * Time.unscaledDeltaTime / seconds);
        if (!active && _source.volume <= .0001f && !_paused)
        {
            _source.Pause();
            _paused = true;
        }
    }

    private void OnDestroy()
    {
        if (_source != null && _source.clip != null) Destroy(_source.clip);
    }

    private static AudioClip ComposeLoop(int sampleRate)
    {
        const float beatsPerMinute = 132f;
        const int bars = 16;
        const int stepsPerBar = 16;
        var stepSeconds = 60f / beatsPerMinute / 4f;
        var totalSteps = bars * stepsPerBar;
        var frameCount = Mathf.RoundToInt(totalSteps * stepSeconds * sampleRate);
        var samples = new float[frameCount * 2];
        // A broad late-80s/early-90s tracker progression, resolving through A
        // back into D minor without borrowing any existing melody.
        var roots = new[] { 50, 48, 46, 48, 50, 53, 55, 57 };
        var minor = new[] { true, false, false, false, true, false, true, false };
        var melody = new[]
        {
            12, -1, 15, 14, 12, -1, 10, -1, 7, 10, 12, -1, 15, 14, 12, -1,
            17, -1, 19, 17, 15, 14, 12, -1, 10, -1, 12, 14, 15, -1, 10, -1
        };
        var bassHits = new[] { 0, 3, 6, 8, 11, 14 };

        for (var step = 0; step < totalSteps; step++)
        {
            var bar = step / stepsPerBar;
            var inBar = step % stepsPerBar;
            var chordIndex = bar % roots.Length;
            var root = roots[chordIndex];
            var third = minor[chordIndex] ? 3 : 4;
            var chord = new[] { 0, third, 7, 12 };
            var start = step * stepSeconds;
            var punkSection = bar % 4 is 2 or 3;

            // Paula-like channel pair: the arpeggio jumps between the hard-ish
            // left/right positions used by classic four-channel MOD playback.
            var arpPan = inBar % 2 == 0 ? -.62f : .62f;
            AddTone(samples, frameCount, sampleRate, start, stepSeconds * .86f,
                Midi(root + 12 + chord[inBar % chord.Length]), .046f, Wave.Glass,
                arpPan, .2f, vibrato: .04f);

            if (Array.IndexOf(bassHits, inBar) >= 0)
            {
                var bassNote = root - 12 + (inBar is 6 or 14 ? 7 : 0);
                AddTone(samples, frameCount, sampleRate, start, stepSeconds * 2.15f,
                    Midi(bassNote), .115f, Wave.Saw, -.36f, .48f,
                    slideSemitones: inBar == 14 ? -1.2f : 0);
            }

            // Every second pair of bars opens into a wide punk wall: tightly
            // doubled power-chord downstrokes on the eighth-note grid. Short
            // muted strokes leave room for the tracker arpeggio while the
            // first and third beats ring out like an emphatic guitar accent.
            if (punkSection && inBar % 2 == 0)
            {
                var openStroke = inBar is 0 or 8;
                AddPowerChord(samples, frameCount, sampleRate, start,
                    stepSeconds * (openStroke ? 1.72f : .68f), root,
                    openStroke ? .082f : .061f);
            }

            // Short sampled chord stabs make the result feel like a small MOD
            // arrangement instead of a single-chip console oscillator.
            if (inBar is 0 or 8)
            {
                for (var voice = 0; voice < 3; voice++)
                    AddTone(samples, frameCount, sampleRate, start, stepSeconds * 2.7f,
                        Midi(root + 12 + chord[voice]), .025f, Wave.Triangle,
                        Mathf.Lerp(-.72f, .72f, voice / 2f), .5f);
            }

            // A quiet, long sampled layer supplies body underneath the busy
            // tracker voices without turning the lobby cue into foreground music.
            if (inBar == 0)
            {
                AddTone(samples, frameCount, sampleRate, start, stepSeconds * 15.5f,
                    Midi(root - 12), .026f, Wave.Triangle, 0, .5f, vibrato: .035f);
                for (var voice = 0; voice < 3; voice++)
                    AddTone(samples, frameCount, sampleRate, start, stepSeconds * 15.35f,
                        Midi(root + chord[voice]), .012f, Wave.Glass,
                        Mathf.Lerp(-.48f, .48f, voice / 2f), .3f, vibrato: .025f);
            }

            if (inBar % 2 == 0)
                AddHat(samples, frameCount, sampleRate, start, .027f, step * 7919,
                    inBar % 4 == 0 ? -.72f : .72f);
            if ((!punkSection && inBar is 0 or 8) ||
                (punkSection && inBar is 0 or 3 or 6 or 8 or 11 or 14))
                AddKick(samples, frameCount, sampleRate, start, punkSection ? .175f : .135f);
            if (inBar is 4 or 12)
                AddSnare(samples, frameCount, sampleRate, start,
                    punkSection ? .155f : .095f, step * 3571, .28f);
            if (punkSection && inBar == 0)
                AddCrash(samples, frameCount, sampleRate, start, .072f, step * 1237);
            if (punkSection && bar % 4 == 3 && inBar is 13 or 14 or 15)
                AddTom(samples, frameCount, sampleRate, start,
                    118f - (inBar - 13) * 19f, .105f, (inBar - 14) * .42f);

            var lead = melody[(step + (bar / 4) * 7) % melody.Length];
            if (lead >= 0 && bar % 4 is 1 or 2)
                AddTone(samples, frameCount, sampleRate, start, stepSeconds * 1.6f,
                    Midi(50 + lead), .064f, Wave.Pulse, .42f, .31f,
                    vibrato: .16f, slideSemitones: inBar == 15 ? 2f : 0);

            // A tiny tracker-style octave fill leads into each fourth bar.
            if (bar % 4 == 3 && inBar >= 12)
                AddTone(samples, frameCount, sampleRate, start, stepSeconds * .72f,
                    Midi(root + 12 + chord[(inBar - 12) % 3] + (inBar % 2) * 12),
                    .048f, Wave.Pulse, .68f, .18f, slideSemitones: -.35f);
        }

        ApplyCyclicTrackerDelay(samples, frameCount,
            Mathf.RoundToInt(stepSeconds * 3f * sampleRate));

        // Approximate the character of 8-bit PCM samples and Paula's separated
        // channels, then tame only the sharpest high-frequency edges.
        for (var channel = 0; channel < 2; channel++)
        {
            var previous = 0f;
            for (var pass = 0; pass < 3; pass++)
            {
                for (var frame = 0; frame < frameCount; frame++)
                {
                    var index = frame * 2 + channel;
                    previous += (samples[index] - previous) * .64f;
                    if (pass != 2) continue;
                    var clipped = (float)Math.Tanh(previous * 1.32f) * .72f;
                    samples[index] = Mathf.Round(clipped * 112f) / 112f;
                }
            }
        }
        var clip = AudioClip.Create("NeonStage · Tracker Lobby", frameCount, 2, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    private static void ApplyCyclicTrackerDelay(float[] samples, int frames, int delayFrames)
    {
        var dry = (float[])samples.Clone();
        var secondDelay = delayFrames * 2;
        for (var frame = 0; frame < frames; frame++)
        {
            var first = ((frame - delayFrames) % frames + frames) % frames * 2;
            var second = ((frame - secondDelay) % frames + frames) % frames * 2;
            var index = frame * 2;
            // Crossed first repeat plus a quieter same-side repeat creates
            // depth while retaining the characteristic tracker stereo field.
            samples[index] += dry[first + 1] * .115f + dry[second] * .052f;
            samples[index + 1] += dry[first] * .115f + dry[second + 1] * .052f;
        }
    }

    private enum Wave { Pulse, Saw, Triangle, Glass }

    private static void AddTone(float[] target, int frames, int rate, float startSeconds,
        float durationSeconds, float frequency, float amplitude, Wave wave, float pan,
        float duty, float vibrato = 0, float slideSemitones = 0)
    {
        var first = Mathf.RoundToInt(startSeconds * rate);
        var length = Mathf.Max(1, Mathf.RoundToInt(durationSeconds * rate));
        var attack = Mathf.Max(1, Mathf.RoundToInt(.006f * rate));
        var release = Mathf.Max(1, Mathf.RoundToInt(Mathf.Min(.055f, durationSeconds * .3f) * rate));
        var phase = 0f;
        for (var offset = 0; offset < length; offset++)
        {
            var progress = offset / (float)length;
            var envelope = Mathf.Min(1f, offset / (float)attack) *
                           Mathf.Min(1f, (length - 1 - offset) / (float)release);
            var pitch = frequency * Mathf.Pow(2f, slideSemitones * progress / 12f) *
                        (1f + Mathf.Sin(progress * Mathf.PI * 2 * 5.25f) * vibrato * .018f);
            phase += pitch / rate;
            var fraction = phase - Mathf.Floor(phase);
            var value = wave switch
            {
                Wave.Saw => fraction * 2f - 1f,
                Wave.Triangle => 1f - 4f * Mathf.Abs(fraction - .5f),
                Wave.Glass => (1f - 4f * Mathf.Abs(fraction - .5f)) * .68f +
                              Mathf.Sin(phase * Mathf.PI * 4) * .32f,
                _ => fraction < duty ? 1f : -1f
            };
            AddFrame(target, frames, first + offset, value * amplitude * Mathf.Max(0, envelope), pan);
        }
    }

    private static void AddKick(float[] target, int frames, int rate, float startSeconds, float amplitude)
    {
        var first = Mathf.RoundToInt(startSeconds * rate);
        var length = Mathf.RoundToInt(.15f * rate);
        var phase = 0f;
        for (var offset = 0; offset < length; offset++)
        {
            var progress = offset / (float)length;
            phase += Mathf.Lerp(112f, 43f, progress) / rate;
            AddFrame(target, frames, first + offset, Mathf.Sin(phase * Mathf.PI * 2) *
                Mathf.Pow(1f - progress, 2.35f) * amplitude, 0);
        }
    }

    private static void AddPowerChord(float[] target, int frames, int rate, float startSeconds,
        float durationSeconds, int rootNote, float amplitude)
    {
        var intervals = new[] { 0, 7, 12 };
        var weights = new[] { 1f, .82f, .61f };
        for (var voice = 0; voice < intervals.Length; voice++)
        {
            var frequency = Midi(rootNote + intervals[voice]);
            var strum = voice * .0028f;
            // Two slightly detuned and offset saw/pulse takes create the broad
            // double-tracked-guitar impression without using a sampled riff.
            AddTone(target, frames, rate, startSeconds + strum, durationSeconds,
                frequency * .9965f, amplitude * weights[voice], Wave.Saw,
                -.72f, .5f, vibrato: .018f, slideSemitones: -.08f);
            AddTone(target, frames, rate, startSeconds + strum + .0055f, durationSeconds,
                frequency * 1.0042f, amplitude * weights[voice] * .92f, Wave.Pulse,
                .72f, .43f, vibrato: .014f, slideSemitones: -.05f);
        }
    }

    private static void AddCrash(float[] target, int frames, int rate, float startSeconds,
        float amplitude, int seed)
    {
        var first = Mathf.RoundToInt(startSeconds * rate);
        var length = Mathf.RoundToInt(.72f * rate);
        var previous = 0f;
        for (var offset = 0; offset < length; offset++)
        {
            var progress = offset / (float)length;
            var noise = HashNoise(offset * 3 + seed);
            var high = noise - previous * .78f;
            previous = noise;
            var metal = Mathf.Sin(offset * 421f / rate * Mathf.PI * 2) * .22f +
                        Mathf.Sin(offset * 653f / rate * Mathf.PI * 2) * .16f;
            AddFrame(target, frames, first + offset, (high + metal) *
                Mathf.Pow(1f - progress, 1.35f) * amplitude, -.18f);
        }
    }

    private static void AddTom(float[] target, int frames, int rate, float startSeconds,
        float frequency, float amplitude, float pan)
    {
        var first = Mathf.RoundToInt(startSeconds * rate);
        var length = Mathf.RoundToInt(.13f * rate);
        var phase = 0f;
        for (var offset = 0; offset < length; offset++)
        {
            var progress = offset / (float)length;
            phase += Mathf.Lerp(frequency * 1.34f, frequency, progress) / rate;
            var body = Mathf.Sin(phase * Mathf.PI * 2);
            var click = HashNoise(offset + first) * Mathf.Pow(1f - progress, 12f) * .25f;
            AddFrame(target, frames, first + offset, (body + click) *
                Mathf.Pow(1f - progress, 2.15f) * amplitude, pan);
        }
    }

    private static void AddSnare(float[] target, int frames, int rate, float startSeconds,
        float amplitude, int seed, float pan)
    {
        var first = Mathf.RoundToInt(startSeconds * rate);
        var length = Mathf.RoundToInt(.12f * rate);
        for (var offset = 0; offset < length; offset++)
        {
            var progress = offset / (float)length;
            var noise = HashNoise(offset + seed);
            var tone = Mathf.Sin(offset * 178f / rate * Mathf.PI * 2);
            AddFrame(target, frames, first + offset, (noise * .74f + tone * .26f) *
                Mathf.Pow(1f - progress, 1.8f) * amplitude, pan);
        }
    }

    private static void AddHat(float[] target, int frames, int rate, float startSeconds,
        float amplitude, int seed, float pan)
    {
        var first = Mathf.RoundToInt(startSeconds * rate);
        var length = Mathf.RoundToInt(.04f * rate);
        var previous = 0f;
        for (var offset = 0; offset < length; offset++)
        {
            var noise = HashNoise(offset + seed);
            var high = noise - previous;
            previous = noise;
            AddFrame(target, frames, first + offset,
                high * (1f - offset / (float)length) * amplitude, pan);
        }
    }

    private static void AddFrame(float[] target, int frames, int frame, float value, float pan)
    {
        var normalizedPan = Mathf.Clamp(pan, -1f, 1f);
        var angle = (normalizedPan + 1f) * Mathf.PI * .25f;
        var index = ((frame % frames) + frames) % frames * 2;
        target[index] += value * Mathf.Cos(angle);
        target[index + 1] += value * Mathf.Sin(angle);
    }

    private static float HashNoise(int value)
    {
        unchecked
        {
            var hash = (uint)value;
            hash ^= hash >> 16;
            hash *= 0x7feb352d;
            hash ^= hash >> 15;
            hash *= 0x846ca68b;
            hash ^= hash >> 16;
            return (hash / (float)uint.MaxValue) * 2f - 1f;
        }
    }

    private static float Midi(int note) => 440f * Mathf.Pow(2f, (note - 69) / 12f);
}
}
