using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiveKit;
using LiveKit.Proto;
using NeonStage.Online;
using UnityEngine;

namespace NeonStage.Stage
{

/// <summary>LiveKit transport. It knows no server secrets and consumes only the short-lived token.</summary>
public sealed class LiveKitAudioTransport : IOnlineAudioTransport
{
    // LiveKit 2.0.0's Android ADM can block synchronously while it initializes
    // on some karaoke appliances. Keep the implementation available for a
    // later SDK upgrade, but never put the room connection behind that call.
    private static bool NativeAndroidMicrophoneEnabled => false;

    private enum BroadcastBus { Music, OriginalVocals, Microphone }

    private sealed class BroadcastSource : RtcAudioSource
    {
        private readonly OnlineBroadcastMixer _mixer;
        private readonly BroadcastBus _bus;
        private event Action<float[], int, int>? Samples;
        public override event Action<float[], int, int> AudioRead
        {
            add => Samples += value;
            remove => Samples -= value;
        }

        public BroadcastSource(OnlineBroadcastMixer mixer, BroadcastBus bus)
            : base(RtcAudioSourceType.AudioSourceCustom)
        {
            _mixer = mixer;
            _bus = bus;
        }

        public override void Start()
        {
            if (_bus == BroadcastBus.Music) _mixer.MusicRead += Forward;
            else if (_bus == BroadcastBus.OriginalVocals) _mixer.OriginalVocalRead += Forward;
            else _mixer.MicrophoneRead += Forward;
            base.Start();
        }

        public override void Stop()
        {
            base.Stop();
            if (_bus == BroadcastBus.Music) _mixer.MusicRead -= Forward;
            else if (_bus == BroadcastBus.OriginalVocals) _mixer.OriginalVocalRead -= Forward;
            else _mixer.MicrophoneRead -= Forward;
        }
        private void Forward(float[] data, int channels, int sampleRate) => Samples?.Invoke(data, channels, sampleRate);
    }

    private enum RemoteBus { Music, OriginalVocals, Microphone }

    private sealed class RemoteOutput
    {
        public RemoteOutput(GameObject outputObject, AudioSource source, AudioStream stream,
            RemoteAudioTrack track, RemoteBus bus)
        {
            Object = outputObject;
            Source = source;
            Stream = stream;
            Track = track;
            Bus = bus;
        }
        public GameObject Object { get; }
        public AudioSource Source { get; }
        public AudioStream Stream { get; }
        public RemoteAudioTrack Track { get; }
        public RemoteBus Bus { get; }
    }

    private readonly OnlineBroadcastMixer _mixer;
    private readonly Dictionary<string, RemoteOutput> _remoteOutputs = new();
    private Room? _room;
    private BroadcastSource? _musicSource;
    private BroadcastSource? _originalVocalSource;
    private BroadcastSource? _microphoneSource;
    private LocalAudioTrack? _musicTrack;
    private LocalAudioTrack? _originalVocalTrack;
    private LocalAudioTrack? _microphoneTrack;
    private PlatformAudio? _platformAudio;
    private PlatformAudioSource? _platformMicrophoneSource;
    private LocalAudioTrack? _platformMicrophoneTrack;
    private OnlineRole _role;
    private float _remoteMusicVolume = .85f;
    private float _remoteOriginalVocalVolume = .35f;
    private float _remoteMicrophoneVolume = 1f;
    private double _estimatedRemoteLatencySeconds = OnlineTimelineProjection.DefaultRemoteLatencySeconds;
    private const string TimelineTopic = "neonstage.timeline.v1";

    [Serializable]
    private sealed class TimelinePacket
    {
        public string queueEntryId = "";
        public double positionSeconds;
        public bool playing;
        public double broadcastDelaySeconds;
    }

    public LiveKitAudioTransport(OnlineBroadcastMixer mixer) => _mixer = mixer;
    public bool IsConnected => _room != null;
    public bool IsPublishing => _musicTrack != null;
    public bool UsesNativeMicrophone => _platformAudio != null;
    public int PublishedMicrophoneCount => UsesNativeMicrophone ? 1 : _mixer.ActiveMicrophoneCount;
    public double EstimatedRemoteLatencySeconds => _estimatedRemoteLatencySeconds;
    public event Action? ParticipantsChanged;
    public event Action? ConnectionLost;
    public event Action? Reconnecting;
    public event Action? Reconnected;
    public event Action<string, double, bool, double>? TimelineReceived;

    public void SetRemoteMix(float music, float originalVocals, float microphone)
    {
        _remoteMusicVolume = Mathf.Clamp01(music);
        _remoteOriginalVocalVolume = Mathf.Clamp01(originalVocals);
        _remoteMicrophoneVolume = Mathf.Clamp01(microphone);
        foreach (var output in _remoteOutputs.Values)
            output.Source.volume = VolumeFor(output.Bus);
    }

    private float VolumeFor(RemoteBus bus) => bus switch
    {
        RemoteBus.OriginalVocals => _remoteOriginalVocalVolume,
        RemoteBus.Microphone => _remoteMicrophoneVolume,
        _ => _remoteMusicVolume
    };

    public async Task ConnectAsync(OnlineTransportConnection connection, CancellationToken cancellationToken)
    {
        await DisconnectAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _role = connection.Role;
        // Unity's Microphone -> AudioSource -> OnAudioFilterRead route adds the
        // complete Unity DSP pipeline before WebRTC ever sees a sample. On the
        // Android karaoke hardware observed in production that alone is roughly
        // 85 ms (512 samples x 4 at 24 kHz), in addition to the capture device.
        // WebRTC's native ADM captures in its real-time audio thread and is the
        // only path suitable for word-synchronous remote vocals.
        if (NativeAndroidMicrophoneEnabled && _role == OnlineRole.Singer &&
            Application.platform == RuntimePlatform.Android)
        {
            _platformAudio = new PlatformAudio();
            Debug.Log("[Online] native Android WebRTC microphone initialized");
        }
        var room = new Room();
        room.TrackSubscribed += OnTrackSubscribed;
        room.TrackUnsubscribed += OnTrackUnsubscribed;
        room.ParticipantConnected += _ => ParticipantsChanged?.Invoke();
        room.ParticipantDisconnected += _ => ParticipantsChanged?.Invoke();
        room.Disconnected += _ => ConnectionLost?.Invoke();
        room.Reconnecting += _ => Reconnecting?.Invoke();
        room.Reconnected += _ => Reconnected?.Invoke();
        room.DataReceived += OnDataReceived;
        _room = room;
        Debug.Log($"[Online] LiveKit connect room={connection.RoomId} role={connection.Role}");
        var operation = room.Connect(connection.ServerUrl, connection.Token, new LiveKit.RoomOptions());
        while (!operation.IsDone) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); }
        if (operation.IsError)
        {
            await DisconnectAsync(CancellationToken.None);
            throw new InvalidOperationException("LiveKit-Verbindung fehlgeschlagen.");
        }
        Debug.Log("[Online] LiveKit connected");
    }

    public async Task PublishTimelineAsync(string queueEntryId, double positionSeconds, bool playing,
        double broadcastDelaySeconds, CancellationToken cancellationToken)
    {
        if (_room == null || _role != OnlineRole.Singer || string.IsNullOrWhiteSpace(queueEntryId)) return;
        var packet = new TimelinePacket
        {
            queueEntryId = queueEntryId,
            positionSeconds = Math.Max(0, positionSeconds),
            playing = playing,
            broadcastDelaySeconds = Math.Clamp(broadcastDelaySeconds, 0, .5)
        };
        var operation = _room.LocalParticipant.PublishData(
            Encoding.UTF8.GetBytes(JsonUtility.ToJson(packet)), reliable: false, topic: TimelineTopic);
        while (!operation.IsDone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    public async Task RefreshLatencyEstimateAsync(double outputLatencySeconds,
        CancellationToken cancellationToken)
    {
        if (_role != OnlineRole.Listener || _remoteOutputs.Count == 0) return;
        var maximumMeasuredLatency = 0d;
        foreach (var output in _remoteOutputs.Values)
        {
            var operation = ((ITrack)output.Track).GetStats();
            while (!operation.IsDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            if (operation.IsError || operation.Stats == null) continue;

            var jitterBuffer = 0d;
            var roundTrip = 0d;
            foreach (var stat in operation.Stats)
            {
                if (stat.StatsCase == RtcStats.StatsOneofCase.InboundRtp)
                {
                    var inbound = stat.InboundRtp?.Inbound;
                    if (inbound != null && inbound.JitterBufferEmittedCount > 0)
                        jitterBuffer = Math.Max(jitterBuffer,
                            inbound.JitterBufferDelay / inbound.JitterBufferEmittedCount);
                }
                else if (stat.StatsCase == RtcStats.StatsOneofCase.CandidatePair)
                {
                    var pair = stat.CandidatePair?.CandidatePair_;
                    if (pair != null && pair.CurrentRoundTripTime > 0)
                        roundTrip = Math.Max(roundTrip, pair.CurrentRoundTripTime);
                }
            }

            // Opus packetization/decoding plus LiveKit's documented 30 ms
            // Unity priming window. Half the RTT approximates the one-way leg.
            var measured = Math.Max(.02, outputLatencySeconds) + .05 +
                           Math.Max(.03, jitterBuffer) + roundTrip * .5;
            maximumMeasuredLatency = Math.Max(maximumMeasuredLatency, measured);
        }
        // The three remote buses have independent jitter buffers. Project the
        // picture/lyrics against the slowest audible bus so live vocals cannot
        // trail the highlighted word merely because the music bus arrived first.
        if (maximumMeasuredLatency > 0)
            _estimatedRemoteLatencySeconds = OnlineTimelineProjection.SmoothLatency(
                _estimatedRemoteLatencySeconds, maximumMeasuredLatency);
    }

    public async Task StartPublishingAsync(CancellationToken cancellationToken)
    {
        if (_room == null) throw new InvalidOperationException("LiveKit ist nicht verbunden.");
        if (_role != OnlineRole.Singer) throw new InvalidOperationException("Nur der Singer darf Audio senden.");
        if (_musicTrack != null) return;
        _musicSource = new BroadcastSource(_mixer, BroadcastBus.Music);
        _originalVocalSource = new BroadcastSource(_mixer, BroadcastBus.OriginalVocals);
        _musicTrack = LocalAudioTrack.CreateAudioTrack("neonstage-music", _musicSource, _room);
        _originalVocalTrack = LocalAudioTrack.CreateAudioTrack(
            "neonstage-original-vocals", _originalVocalSource, _room);

        var operation = _room.LocalParticipant.PublishTrack(_musicTrack, new TrackPublishOptions
        {
            AudioEncoding = new AudioEncoding { MaxBitrate = 128000 },
            Source = TrackSource.SourceScreenshareAudio
        });
        while (!operation.IsDone) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); }
        if (operation.IsError)
        {
            throw new InvalidOperationException("Online-Musikspur konnte nicht veröffentlicht werden.");
        }
        _musicSource.Start();

        operation = _room.LocalParticipant.PublishTrack(_originalVocalTrack, new TrackPublishOptions
        {
            AudioEncoding = new AudioEncoding { MaxBitrate = 96000 },
            Source = TrackSource.SourceScreenshareAudio
        });
        while (!operation.IsDone) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); }
        if (operation.IsError)
            throw new InvalidOperationException("Online-Originalvocal-Spur konnte nicht veröffentlicht werden.");
        _originalVocalSource.Start();

        await StartMicrophonePublishingAsync(cancellationToken);
        Debug.Log("[Online] separate music, original-vocal and microphone tracks publishing started");
    }

    public async Task StartMicrophonePublishingAsync(CancellationToken cancellationToken)
    {
        if (_room == null) throw new InvalidOperationException("LiveKit ist nicht verbunden.");
        if (_microphoneTrack != null || _platformMicrophoneTrack != null) return;
        _microphoneSource = new BroadcastSource(_mixer, BroadcastBus.Microphone);
        _microphoneTrack = LocalAudioTrack.CreateAudioTrack(
            "neonstage-microphone", _microphoneSource, _room);
        var operation = _room.LocalParticipant.PublishTrack(_microphoneTrack, new TrackPublishOptions
        {
            AudioEncoding = new AudioEncoding { MaxBitrate = 96000 },
            Source = TrackSource.SourceMicrophone
        });
        while (!operation.IsDone) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); }
        if (operation.IsError)
            throw new InvalidOperationException("Online-Mikrofonspur konnte nicht veröffentlicht werden.");
        _microphoneSource.Start();

        if (UsesNativeMicrophone)
        {
            _platformMicrophoneSource = new PlatformAudioSource(_platformAudio!);
            _platformMicrophoneTrack = LocalAudioTrack.CreateAudioTrack(
                "neonstage-microphone", _platformMicrophoneSource, _room);
            var microphoneOperation = _room.LocalParticipant.PublishTrack(
                _platformMicrophoneTrack, new TrackPublishOptions
                {
                    AudioEncoding = new AudioEncoding { MaxBitrate = 64000 },
                    Source = TrackSource.SourceMicrophone
                });
            while (!microphoneOperation.IsDone)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            if (microphoneOperation.IsError)
                throw new InvalidOperationException("Nativer Android-Mikrofontrack konnte nicht veröffentlicht werden.");
            Debug.Log("[Online] native Android microphone publishing started");
        }

        Debug.Log("[Online] microphone track publishing started");
    }

    public async Task StopMicrophonePublishingAsync(CancellationToken cancellationToken)
    {
        if (_platformMicrophoneTrack != null && _room != null)
        {
            var operation = _room.LocalParticipant.UnpublishTrack(_platformMicrophoneTrack, false);
            while (!operation.IsDone) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); }
        }
        if (_microphoneTrack != null && _room != null)
        {
            var operation = _room.LocalParticipant.UnpublishTrack(_microphoneTrack, false);
            while (!operation.IsDone) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); }
        }
        _platformMicrophoneTrack = null;
        _platformMicrophoneSource?.Dispose();
        _platformMicrophoneSource = null;
        _microphoneTrack = null;
        StopAndDispose(ref _microphoneSource);
        _mixer.StopMicrophone();
        Debug.Log("[Online] microphone track publishing stopped");
    }

    public async Task StopPublishingAsync(CancellationToken cancellationToken)
    {
        if (_musicTrack != null || _originalVocalTrack != null || _microphoneTrack != null ||
            _platformMicrophoneTrack != null)
            Debug.Log("[Online] stopping audio publishing");
        await StopMicrophonePublishingAsync(cancellationToken);
        foreach (var track in new[] { _originalVocalTrack, _musicTrack })
        {
            if (track == null || _room == null) continue;
            var operation = _room.LocalParticipant.UnpublishTrack(track, false);
            while (!operation.IsDone) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); }
        }
        _musicTrack = null;
        _originalVocalTrack = null;
        StopAndDispose(ref _musicSource);
        StopAndDispose(ref _originalVocalSource);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await StopPublishingAsync(cancellationToken);
        foreach (var output in _remoteOutputs.Values)
        {
            output.Stream.Dispose();
            UnityEngine.Object.Destroy(output.Object);
        }
        _remoteOutputs.Clear();
        if (_room != null)
        {
            _room.Disconnect();
            _room.Dispose();
            _room = null;
        }
        _platformAudio?.Dispose();
        _platformAudio = null;
    }

    private void OnTrackSubscribed(IRemoteTrack track, RemoteTrackPublication publication, RemoteParticipant participant)
    {
        if (_role != OnlineRole.Listener || track is not RemoteAudioTrack audio || _remoteOutputs.ContainsKey(track.Sid)) return;
        var outputObject = new GameObject($"Online Remote Audio: {participant.Identity}");
        var source = outputObject.AddComponent<AudioSource>();
        var bus = publication.Name switch
        {
            "neonstage-original-vocals" => RemoteBus.OriginalVocals,
            "neonstage-microphone" => RemoteBus.Microphone,
            _ => RemoteBus.Music
        };
        source.volume = VolumeFor(bus);
        _remoteOutputs[track.Sid] = new RemoteOutput(outputObject, source,
            new AudioStream(audio, source), audio, bus);
        Debug.Log($"[Online] remote audio subscribed participant={participant.Identity} " +
                  $"track={publication.Name} bus={bus} volume={source.volume:0.00}");
    }

    private void OnDataReceived(byte[] data, Participant participant, DataPacketKind kind, string topic)
    {
        if (_role != OnlineRole.Listener || topic != TimelineTopic || data == null || data.Length == 0) return;
        try
        {
            var packet = JsonUtility.FromJson<TimelinePacket>(Encoding.UTF8.GetString(data));
            if (packet == null || string.IsNullOrWhiteSpace(packet.queueEntryId)) return;
            TimelineReceived?.Invoke(packet.queueEntryId, packet.positionSeconds, packet.playing,
                packet.broadcastDelaySeconds);
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Online] invalid timeline packet: " + exception.Message);
        }
    }

    private void OnTrackUnsubscribed(IRemoteTrack track, RemoteTrackPublication publication, RemoteParticipant participant)
    {
        if (!_remoteOutputs.Remove(track.Sid, out var output)) return;
        output.Stream.Dispose();
        UnityEngine.Object.Destroy(output.Object);
        Debug.Log($"[Online] remote audio unsubscribed participant={participant.Identity}");
    }

    public void Dispose()
    {
        _musicTrack = null;
        _originalVocalTrack = null;
        _microphoneTrack = null;
        _platformMicrophoneTrack = null;
        _platformMicrophoneSource?.Dispose();
        _platformMicrophoneSource = null;
        StopAndDispose(ref _musicSource);
        StopAndDispose(ref _originalVocalSource);
        StopAndDispose(ref _microphoneSource);
        _mixer.StopMicrophone();
        foreach (var output in _remoteOutputs.Values)
        {
            output.Stream.Dispose();
            UnityEngine.Object.Destroy(output.Object);
        }
        _remoteOutputs.Clear();
        _room?.Disconnect();
        _room?.Dispose();
        _room = null;
        _platformAudio?.Dispose();
        _platformAudio = null;
    }

    private static void StopAndDispose(ref BroadcastSource? source)
    {
        if (source == null) return;
        source.Stop();
        source.Dispose();
        source = null;
    }
}
}
