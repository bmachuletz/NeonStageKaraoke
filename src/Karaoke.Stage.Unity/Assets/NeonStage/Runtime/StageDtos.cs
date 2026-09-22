using System;

namespace NeonStage.Stage
{

[Serializable]
public sealed class SongDto
{
    public string id = "";
    public string title = "";
    public string artist = "";
    public double durationSeconds;
}

[Serializable]
public sealed class QueueEntryDto
{
    public string id = "";
    public SongDto song = new();
    public string requestedBy = "";
    public string locationId = "";
    public string startedAt = "";
    public bool singerControlsMix;
    public double singerMicrophoneVolume = 1;
}

[Serializable]
public sealed class PlaybackStateDto
{
    public bool isRunning;
    public QueueEntryDto? current;
    public bool isPaused;
    public string position = "00:00:00";
    public long revision;
    public QueueEntryDto[] queue = Array.Empty<QueueEntryDto>();
}

[Serializable]
public sealed class StemAvailabilityDto
{
    public bool hasInstrumental;
    public bool hasVocals;
}

[Serializable]
public sealed class OnlineConfigurationDto
{
    public bool enabled;
    public string provider = "";
    public string serverUrl = "";
    public string status = "";
}

[Serializable]
public sealed class OnlineParticipantDto
{
    public string participantId = "";
    public string displayName = "";
    public int role;
}

[Serializable]
public sealed class OnlineRoomSessionDto
{
    public bool enabled;
    public string provider = "";
    public string serverUrl = "";
    public string roomId = "";
    public string participantId = "";
    public int role;
    public string accessToken = "";
    public string expiresAt = "";
    public OnlineParticipantDto[] participants = Array.Empty<OnlineParticipantDto>();
    public bool allowConversation;
}

[Serializable]
public sealed class OnlineRoomStateDto
{
    public string roomId = "";
    public string participantId = "";
    public int role;
    public OnlineParticipantDto[] participants = Array.Empty<OnlineParticipantDto>();
    public bool allowConversation;
}

[Serializable]
public sealed class OnlineJoinRequestDto
{
    public string roomId = "";
    public string participantId = "";
    public string displayName = "";
    public int role;
    public string password = "";
    public string locationId = "";
}

[Serializable]
public sealed class OnlineStageDto
{
    public string roomId = "";
    public string name = "";
    public bool hasPassword;
    public int participantCount;
    public bool allowConversation;
}

[Serializable]
public sealed class OnlineStageListDto { public OnlineStageDto[] items = Array.Empty<OnlineStageDto>(); }

[Serializable]
public sealed class OnlineRoleRequestDto { public int role; }

[Serializable]
public sealed class SongVideoInfoDto
{
    public int offsetMilliseconds;
    public string updatedAt = "";
}

[Serializable]
public sealed class KaraokeEventDto
{
    public string id = "";
    public string name = "";
    public string inviteToken = "";
    public string description = "";
    public string stageThemeId = "standard";
    public bool isActive;
    public bool isOnline;
    public bool hasOnlinePassword;
    public bool allowConversation;
}

[Serializable]
public sealed class StageLauncherDto
{
    public string id = "";
    public string name = "";
    public string inviteToken = "";
    public bool isDirect;
    public bool isOnline;
    public bool hasOnlinePassword;
    public bool hasImage;
    public string stageThemeId = "standard";
    [NonSerialized] public UnityEngine.Texture2D? image;
}

[Serializable]
public sealed class StageLauncherListDto { public StageLauncherDto[] items = Array.Empty<StageLauncherDto>(); }

[Serializable]
public sealed class StageReactionDto { public long id; public string type = ""; public string sender = ""; }
[Serializable]
public sealed class StageReactionListDto { public StageReactionDto[] items = Array.Empty<StageReactionDto>(); }

[Serializable]
public sealed class CreateKaraokeEventDto
{
    public string name = "";
    public string startsAt = "";
    public string description = "";
    public string stageThemeId = "standard";
}

[Serializable]
public sealed class PlaybackControllerRequestDto
{
    public string clientId = "";
    public string clientName = "Neon Stage Unity";
    public bool force;
}

[Serializable]
public sealed class PlaybackControllerDto
{
    public bool ownsControl;
    public string controllerId = "";
    public string controllerName = "";
}

[Serializable]
public sealed class LyricsDto
{
    public LyricsLineDto[] lines = Array.Empty<LyricsLineDto>();
    public bool hasUltraStarTimingHeritage;
    public MusicalHighlightSettingsDto musicalHighlight = new();
    public KaraokeColorSettingsDto karaokeColors = new();
}

[Serializable]
public sealed class KaraokeColorSettingsDto
{
    public string unsungColor = "#FFFFFF";
    public string sungColor = "#DFFF28";
    public string glowColor = "#FF5008";
    public int outlineStrength = 55;
}

[Serializable]
public sealed class SongVisualizationDto
{
    public VisualizationFrameDto[] frames = Array.Empty<VisualizationFrameDto>();
}

[Serializable]
public sealed class VisualizationFrameDto
{
    public double timeSeconds;
    public double energy;
    public double bass;
    public double mid;
    public double high;
    public bool beat;
}

[Serializable]
public sealed class LyricsLineDto
{
    public string start = "00:00:00";
    public string text = "";
    public string end = "";
    public int index;
    public LyricsWordDto[] words = Array.Empty<LyricsWordDto>();
    public int holdAfterMilliseconds = -1;
    public string stageEffect = "Automatic";
    public int voiceLane;
    public string voiceLabel = "";
    public bool karaokeTimingLocked;
}

[Serializable]
public sealed class LyricsWordDto
{
    public string start = "00:00:00";
    public string text = "";
    public string end = "";
    public int index;
    public LyricsSyllableDto[] syllables = Array.Empty<LyricsSyllableDto>();
    public float syllableConfidence;
    public bool karaokeTimingLocked;
}

[Serializable]
public sealed class LyricsSyllableDto
{
    public string start = "00:00:00";
    public string text = "";
    public string end = "";
    public int index;
    public float confidence;
    public bool karaokeTimingLocked;
    public LyricsNoteEvidenceDto[] notes = Array.Empty<LyricsNoteEvidenceDto>();
}

[Serializable]
public sealed class LyricsNoteEvidenceDto
{
    public string start = "00:00:00";
    public string end = "00:00:00";
    public int midi;
    public float confidence;
}

[Serializable]
public sealed class MusicalHighlightSettingsDto
{
    public bool enabled;
    public int timelineVersion = 1;
}

[Serializable]
public sealed class StageTimingSampleDto
{
    public string capturedAt = "";
    public string deviceId = "";
    public string songId = "";
    public double lyricsPositionSeconds;
    public double dspPositionSeconds;
    public double masterSamplePositionSeconds;
    public double vocalSamplePositionSeconds;
    public double sampleClockCorrectionSeconds;
    public double stemDifferenceSeconds;
    public int dspBufferLength;
    public int dspBufferCount;
    public int outputSampleRate;
    public double estimatedOutputLatencySeconds;
    public double appliedOutputLatencySeconds;
    public bool playing;
}
}
