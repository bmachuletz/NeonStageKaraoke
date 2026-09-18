using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NeonStage.Online
{

public enum OnlineRole { Listener = 0, Singer = 1 }

public enum OnlineConnectionState
{
    Disconnected,
    Joining,
    Connected,
    Reconnecting,
    Leaving,
    Failed
}

[Flags]
public enum OnlineAudioBus
{
    None = 0,
    Music = 1,
    Microphone = 2,
    Remote = 4
}

public readonly struct OnlineAudioRoute
{
    public OnlineAudioRoute(OnlineAudioBus localOutput, OnlineAudioBus broadcastOutput)
    {
        LocalOutput = localOutput;
        BroadcastOutput = broadcastOutput;
    }

    public OnlineAudioBus LocalOutput { get; }
    public OnlineAudioBus BroadcastOutput { get; }
    public bool BroadcastContains(OnlineAudioBus bus) => (BroadcastOutput & bus) == bus;
}

public sealed class OnlineParticipantInfo
{
    public OnlineParticipantInfo(string participantId, string displayName, OnlineRole role)
    {
        ParticipantId = participantId ?? throw new ArgumentNullException(nameof(participantId));
        DisplayName = displayName ?? string.Empty;
        Role = role;
    }

    public string ParticipantId { get; }
    public string DisplayName { get; }
    public OnlineRole Role { get; }
}

public static class OnlineAudioRouting
{
    public static OnlineAudioRoute For(OnlineRole role) => role == OnlineRole.Singer
        ? new OnlineAudioRoute(OnlineAudioBus.Music | OnlineAudioBus.Microphone,
            OnlineAudioBus.Music | OnlineAudioBus.Microphone)
        : new OnlineAudioRoute(OnlineAudioBus.Remote, OnlineAudioBus.None);
}

public static class OnlineRoleAutomation
{
    public static OnlineRole ForQueueLocation(string? requestedLocationId, string? localLocationId,
        bool queueEntryAssigned)
    {
        if (!queueEntryAssigned) return OnlineRole.Listener;
        return Guid.TryParse(requestedLocationId, out var requested) &&
               Guid.TryParse(localLocationId, out var local) && requested == local
            ? OnlineRole.Singer
            : OnlineRole.Listener;
    }
}

/// <summary>
/// Projects a singer timeline beacon onto the song position that is currently
/// audible at a remote output. Transport latency is deliberately kept outside
/// the persisted lyrics and media timestamps.
/// </summary>
public readonly struct OnlineTimelineBeacon
{
    public OnlineTimelineBeacon(string queueEntryId, double positionSeconds, bool playing,
        double receivedAtSeconds) : this(queueEntryId, positionSeconds, playing,
        receivedAtSeconds, 0)
    {
    }

    public OnlineTimelineBeacon(string queueEntryId, double positionSeconds, bool playing,
        double receivedAtSeconds, double broadcastDelaySeconds)
    {
        QueueEntryId = queueEntryId;
        PositionSeconds = positionSeconds;
        Playing = playing;
        ReceivedAtSeconds = receivedAtSeconds;
        BroadcastDelaySeconds = Math.Clamp(broadcastDelaySeconds, 0, .5);
    }

    public string QueueEntryId { get; }
    public double PositionSeconds { get; }
    public bool Playing { get; }
    public double ReceivedAtSeconds { get; }
    public double BroadcastDelaySeconds { get; }
}

public static class OnlineTimelineProjection
{
    public const double DefaultRemoteLatencySeconds = .18;
    public const double MinimumRemoteLatencySeconds = .06;
    public const double MaximumRemoteLatencySeconds = 1.2;

    public static double AudiblePosition(OnlineTimelineBeacon beacon, double nowSeconds,
        double remoteLatencySeconds, double durationSeconds)
    {
        var elapsed = beacon.Playing ? Math.Max(0, nowSeconds - beacon.ReceivedAtSeconds) : 0;
        var latency = Math.Clamp(remoteLatencySeconds,
            MinimumRemoteLatencySeconds, MaximumRemoteLatencySeconds);
        return Math.Clamp(beacon.PositionSeconds + elapsed - latency - beacon.BroadcastDelaySeconds,
            0, Math.Max(0, durationSeconds));
    }

    public static double SmoothLatency(double currentSeconds, double measuredSeconds) =>
        Math.Clamp(currentSeconds + (measuredSeconds - currentSeconds) * .2,
            MinimumRemoteLatencySeconds, MaximumRemoteLatencySeconds);

    public static double SmoothPosition(double currentSeconds, double measuredSeconds)
    {
        var difference = measuredSeconds - currentSeconds;
        if (Math.Abs(difference) > .75) return measuredSeconds; // real seek/reconnect
        return currentSeconds + Math.Clamp(difference * .25, -.04, .04);
    }
}

public sealed class OnlinePartyStateMachine
{
    private readonly List<OnlineParticipantInfo> _participants = new();

    public OnlineConnectionState ConnectionState { get; private set; } = OnlineConnectionState.Disconnected;
    public OnlineRole LocalRole { get; private set; } = OnlineRole.Listener;
    public string? LastError { get; private set; }
    public IReadOnlyList<OnlineParticipantInfo> Participants => _participants;
    public OnlineAudioRoute AudioRoute => OnlineAudioRouting.For(LocalRole);

    public void BeginJoin()
    {
        if (ConnectionState is not (OnlineConnectionState.Disconnected or OnlineConnectionState.Failed))
            throw new InvalidOperationException($"Cannot join while {ConnectionState}.");
        LastError = null;
        ConnectionState = OnlineConnectionState.Joining;
    }

    public void Connected(OnlineRole role, IEnumerable<OnlineParticipantInfo>? participants = null)
    {
        if (ConnectionState is not (OnlineConnectionState.Joining or OnlineConnectionState.Reconnecting))
            throw new InvalidOperationException($"Cannot connect while {ConnectionState}.");
        LocalRole = role;
        ReplaceParticipants(participants);
        ConnectionState = OnlineConnectionState.Connected;
    }

    public void BeginReconnect()
    {
        if (ConnectionState != OnlineConnectionState.Connected) return;
        ConnectionState = OnlineConnectionState.Reconnecting;
    }

    public void Failed(string message)
    {
        LastError = string.IsNullOrWhiteSpace(message) ? "Online connection failed." : message.Trim();
        ConnectionState = OnlineConnectionState.Failed;
    }

    public void SetRole(OnlineRole role) => LocalRole = role;

    public void ReplaceParticipants(IEnumerable<OnlineParticipantInfo>? participants)
    {
        _participants.Clear();
        if (participants is null) return;
        _participants.AddRange(participants);
    }

    public bool CanBecomeSinger(string participantId) =>
        !_participants.Any(item => item.Role == OnlineRole.Singer &&
                                   !item.ParticipantId.Equals(participantId, StringComparison.Ordinal));

    public void BeginLeave()
    {
        if (ConnectionState == OnlineConnectionState.Disconnected) return;
        ConnectionState = OnlineConnectionState.Leaving;
    }

    public void Left()
    {
        _participants.Clear();
        LocalRole = OnlineRole.Listener;
        LastError = null;
        ConnectionState = OnlineConnectionState.Disconnected;
    }
}

public sealed class OnlineTransportConnection
{
    public OnlineTransportConnection(string serverUrl, string token, string roomId,
        string participantId, OnlineRole role)
    {
        ServerUrl = serverUrl;
        Token = token;
        RoomId = roomId;
        ParticipantId = participantId;
        Role = role;
    }

    public string ServerUrl { get; }
    public string Token { get; }
    public string RoomId { get; }
    public string ParticipantId { get; }
    public OnlineRole Role { get; }
}

public interface IOnlineAudioTransport : IDisposable
{
    bool IsConnected { get; }
    bool IsPublishing { get; }
    event Action? ParticipantsChanged;
    event Action? ConnectionLost;
    event Action? Reconnecting;
    event Action? Reconnected;
    Task ConnectAsync(OnlineTransportConnection connection, CancellationToken cancellationToken);
    Task StartPublishingAsync(CancellationToken cancellationToken);
    Task StopPublishingAsync(CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

}
