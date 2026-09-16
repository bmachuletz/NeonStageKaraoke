using System;
using System.Collections.Generic;
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
    private sealed class MixSource : RtcAudioSource
    {
        private readonly OnlineBroadcastMixer _mixer;
        private event Action<float[], int, int>? Samples;
        public override event Action<float[], int, int> AudioRead
        {
            add => Samples += value;
            remove => Samples -= value;
        }

        public MixSource(OnlineBroadcastMixer mixer) : base(RtcAudioSourceType.AudioSourceCustom) => _mixer = mixer;
        public override void Start() { _mixer.AudioRead += Forward; base.Start(); }
        public override void Stop() { base.Stop(); _mixer.AudioRead -= Forward; }
        private void Forward(float[] data, int channels, int sampleRate) => Samples?.Invoke(data, channels, sampleRate);
    }

    private sealed class RemoteOutput
    {
        public RemoteOutput(GameObject outputObject, AudioStream stream)
        {
            Object = outputObject;
            Stream = stream;
        }
        public GameObject Object { get; }
        public AudioStream Stream { get; }
    }

    private readonly OnlineBroadcastMixer _mixer;
    private readonly Dictionary<string, RemoteOutput> _remoteOutputs = new();
    private Room? _room;
    private MixSource? _mixSource;
    private LocalAudioTrack? _localTrack;
    private OnlineRole _role;

    public LiveKitAudioTransport(OnlineBroadcastMixer mixer) => _mixer = mixer;
    public bool IsConnected => _room != null;
    public bool IsPublishing => _localTrack != null;
    public event Action? ParticipantsChanged;
    public event Action? ConnectionLost;
    public event Action? Reconnecting;
    public event Action? Reconnected;

    public async Task ConnectAsync(OnlineTransportConnection connection, CancellationToken cancellationToken)
    {
        await DisconnectAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _role = connection.Role;
        var room = new Room();
        room.TrackSubscribed += OnTrackSubscribed;
        room.TrackUnsubscribed += OnTrackUnsubscribed;
        room.ParticipantConnected += _ => ParticipantsChanged?.Invoke();
        room.ParticipantDisconnected += _ => ParticipantsChanged?.Invoke();
        room.Disconnected += _ => ConnectionLost?.Invoke();
        room.Reconnecting += _ => Reconnecting?.Invoke();
        room.Reconnected += _ => Reconnected?.Invoke();
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

    public async Task StartPublishingAsync(CancellationToken cancellationToken)
    {
        if (_room == null) throw new InvalidOperationException("LiveKit ist nicht verbunden.");
        if (_role != OnlineRole.Singer) throw new InvalidOperationException("Nur der Singer darf Audio senden.");
        if (_localTrack != null) return;
        _mixSource = new MixSource(_mixer);
        _localTrack = LocalAudioTrack.CreateAudioTrack("neonstage-final-mix", _mixSource, _room);
        var operation = _room.LocalParticipant.PublishTrack(_localTrack, new TrackPublishOptions
        {
            AudioEncoding = new AudioEncoding { MaxBitrate = 128000 },
            Source = TrackSource.SourceMicrophone
        });
        while (!operation.IsDone) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); }
        if (operation.IsError)
        {
            _localTrack = null;
            _mixSource.Dispose();
            _mixSource = null;
            throw new InvalidOperationException("Online-Mix konnte nicht veröffentlicht werden.");
        }
        _mixSource.Start();
        Debug.Log("[Online] final mix publishing started");
    }

    public async Task StopPublishingAsync(CancellationToken cancellationToken)
    {
        if (_localTrack != null) Debug.Log("[Online] stopping audio publishing");
        if (_localTrack != null && _room != null)
        {
            var operation = _room.LocalParticipant.UnpublishTrack(_localTrack, false);
            while (!operation.IsDone) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); }
        }
        _localTrack = null;
        _mixSource?.Stop();
        _mixSource?.Dispose();
        _mixSource = null;
        _mixer.StopMicrophone();
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
    }

    private void OnTrackSubscribed(IRemoteTrack track, RemoteTrackPublication publication, RemoteParticipant participant)
    {
        if (_role != OnlineRole.Listener || track is not RemoteAudioTrack audio || _remoteOutputs.ContainsKey(track.Sid)) return;
        var outputObject = new GameObject($"Online Remote Audio: {participant.Identity}");
        var source = outputObject.AddComponent<AudioSource>();
        _remoteOutputs[track.Sid] = new RemoteOutput(outputObject, new AudioStream(audio, source));
        Debug.Log($"[Online] remote audio subscribed participant={participant.Identity}");
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
        _localTrack = null;
        _mixSource?.Stop();
        _mixSource?.Dispose();
        _mixSource = null;
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
    }
}
}
