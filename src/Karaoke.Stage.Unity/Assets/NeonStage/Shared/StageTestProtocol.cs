using System;

namespace NeonStage.Testing
{

public static class StageTestProtocol
{
    public const int Version = 1;
    public const string Hello = "hello";
    public const string ReplaceSongState = "replaceSongState";
    public const string UpdateLyrics = "updateLyrics";
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Stop = "stop";
    public const string Seek = "seek";
    public const string Clock = "clock";
    public const string Ready = "ready";
    public const string Applied = "applied";
    public const string PlaybackState = "playbackState";
    public const string Error = "error";
    public const string Shutdown = "shutdown";
    public const string BeginExport = "beginExport";
    public const string CancelExport = "cancelExport";
    public const string ExportProgress = "exportProgress";
    public const string ExportComplete = "exportComplete";

    public static bool IsCompatibleHello(StageTestMessage? message, string sessionId, string token) =>
        message != null && message.type == Hello && message.protocolVersion == Version &&
        string.Equals(message.sessionId, sessionId, StringComparison.Ordinal) &&
        string.Equals(message.token, token, StringComparison.Ordinal);
}

/// <summary>One newline-delimited JSON message on the editor/Stage loopback connection.</summary>
[Serializable]
public sealed class StageTestMessage
{
    public string type = "";
    public int protocolVersion = StageTestProtocol.Version;
    public string sessionId = "";
    public string token = "";
    public string stageVersion = "";
    public long revision;
    public string stateJson = "";
    public double positionSeconds;
    public bool playing;
    public string details = "";
    public string outputPath = "";
    public int width;
    public int height;
    public int framesPerSecond;
    public int frame;
    public int totalFrames;
}

[Serializable]
public sealed class StageTestSongState
{
    public string songId = "";
    public string title = "";
    public string artist = "";
    public string serverUrl = "";
    public string stageThemeId = "standard";
    public string lyricsJson = "";
    public double positionSeconds;
    public bool playing;
    public double durationSeconds;
}

/// <summary>Shared monotonic ordering rule. The Stage never applies stale editor snapshots.</summary>
public sealed class StageTestRevisionGate
{
    public long AppliedRevision { get; private set; }

    public bool TryApply(long revision)
    {
        if (revision <= AppliedRevision) return false;
        AppliedRevision = revision;
        return true;
    }
}

/// <summary>Deterministic protocol state used by tests and diagnostic clients.</summary>
public sealed class StageTestReplicaState
{
    private readonly StageTestRevisionGate _revisions = new();
    public long AppliedRevision => _revisions.AppliedRevision;
    public string StateJson { get; private set; } = "";
    public double PositionSeconds { get; private set; }
    public bool Playing { get; private set; }

    public bool Apply(StageTestMessage message)
    {
        switch (message.type)
        {
            case StageTestProtocol.ReplaceSongState:
            case StageTestProtocol.UpdateLyrics:
                if (!_revisions.TryApply(message.revision)) return false;
                StateJson = message.stateJson;
                return true;
            case StageTestProtocol.Play:
                PositionSeconds = Math.Max(0, message.positionSeconds);
                Playing = true;
                return true;
            case StageTestProtocol.Pause:
                PositionSeconds = Math.Max(0, message.positionSeconds);
                Playing = false;
                return true;
            case StageTestProtocol.Stop:
                PositionSeconds = 0;
                Playing = false;
                return true;
            case StageTestProtocol.Seek:
            case StageTestProtocol.Clock:
                PositionSeconds = Math.Max(0, message.positionSeconds);
                if (message.type == StageTestProtocol.Clock) Playing = message.playing;
                return true;
            default:
                return false;
        }
    }
}

}
