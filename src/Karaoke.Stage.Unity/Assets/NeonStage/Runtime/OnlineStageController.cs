using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NeonStage.Online;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonStage.Stage
{

public sealed class OnlineStageController : MonoBehaviour
{
    private StageAudioEngine _audio = null!;
    private OnlineBroadcastMixer _mixer = null!;
    private LiveKitAudioTransport? _transport;
    private readonly OnlinePartyStateMachine _state = new();
    private CancellationTokenSource _lifetime = new();
    private string _server = "";
    private string _room = "";
    private string _launcherRoom = "";
    private string _password = "";
    private string _displayName = "";
    private string _participantId = "";
    private string _locationId = "";
    private string _status = "Online-Modus wird geprüft …";
    private bool _enabled;
    private bool _panelOpen;
    private bool _busy;
    private float _nextHeartbeat;
    private float _nextAutomaticRoleAttempt;
    private float _nextTimelinePublish;
    private float _nextLatencyRefresh;
    private bool _timelinePublishRunning;
    private bool _latencyRefreshRunning;
    private bool _loadingStages;
    private bool _listenerMixLocked;
    private bool _allowConversation;
    private bool _conversationDesired;
    private bool _conversationPublishing;
    private bool _conversationChanging;
    private float _lockedMicrophoneVolume = 1f;
    private OnlineStageDto[] _availableStages = Array.Empty<OnlineStageDto>();
    private string _queueEntryId = "";
    private OnlineTimelineBeacon? _remoteTimeline;
    private Texture2D? _onlineIcon;
    private Texture2D? _offlineIcon;

    public bool IsListener => _state.ConnectionState == OnlineConnectionState.Connected &&
                              _state.LocalRole == OnlineRole.Listener;
    public bool IsConnected => _state.ConnectionState is OnlineConnectionState.Connected or
        OnlineConnectionState.Reconnecting;
    public bool ListenerMixLocked => _listenerMixLocked;
    public float LockedMicrophoneVolume => _lockedMicrophoneVolume;
    public bool IsConversationActive => IsConnected && _allowConversation && _conversationDesired;
    public event Action<OnlineRole>? RoleChanged;
    public event Action? StageLeaveRequested;
    public double RemoteLatencySeconds => _transport?.EstimatedRemoteLatencySeconds ??
                                          OnlineTimelineProjection.DefaultRemoteLatencySeconds;

    public void SetListenerMix(float music, float originalVocals, float microphone) =>
        _transport?.SetRemoteMix(_listenerMixLocked ? .85f : music,
            _listenerMixLocked ? .35f : originalVocals,
            _listenerMixLocked ? _lockedMicrophoneVolume : microphone);

    public void ConfigureListenerMixPolicy(bool locked, double microphoneVolume)
    {
        _listenerMixLocked = locked;
        _lockedMicrophoneVolume = Mathf.Clamp01((float)microphoneVolume);
        if (locked) _transport?.SetRemoteMix(.85f, .35f, _lockedMicrophoneVolume);
    }

    public void SynchronizeConversationMode(bool songPlaying)
    {
        _conversationDesired = _allowConversation && !songPlaying;
    }

    public bool IsConnectedToEvent(string? eventId) => IsConnected &&
        Guid.TryParse(eventId, out var parsed) &&
        string.Equals(_room, parsed.ToString("N"), StringComparison.OrdinalIgnoreCase);

    public void ConfigureLauncherStage(string eventId, bool online)
    {
        _launcherRoom = online && Guid.TryParse(eventId, out var id) ? id.ToString("N") : "offline";
        if (online)
        {
            SelectStage(_launcherRoom);
            _panelOpen = true;
        }
        else
        {
            _room = "";
            _panelOpen = false;
        }
    }

    public void Initialize(string server, StageAudioEngine audio, string locationId)
    {
        _server = server.TrimEnd('/');
        _audio = audio;
        _locationId = locationId;
        _mixer = gameObject.AddComponent<OnlineBroadcastMixer>();
        _mixer.Initialize(audio);
        _mixer.ActiveMicrophonesChanged += HandleActiveMicrophonesChanged;
        _displayName = PlayerPrefs.GetString("NeonStage.OnlineName", SystemInfo.deviceName);
        _participantId = PlayerPrefs.GetString("NeonStage.OnlineParticipant", Guid.NewGuid().ToString("N"));
        PlayerPrefs.SetString("NeonStage.OnlineParticipant", _participantId);
        _onlineIcon = Resources.Load<Texture2D>("OnlineBroadcastIcon");
        _offlineIcon = Resources.Load<Texture2D>("OnlineBroadcastIconOffline");
        Debug.Log($"[Online] platform={Application.platform} processor={SystemInfo.processorType} outputRate={AudioSettings.outputSampleRate}");
        _ = LoadConfigurationAsync();
    }

    private void HandleActiveMicrophonesChanged(int count)
    {
        if (_state.ConnectionState != OnlineConnectionState.Connected) return;
        if (_state.LocalRole is not (OnlineRole.Singer or OnlineRole.Listener)) return;
        _status = count switch
        {
            0 => "Verbunden · warte auf Mikrofon (Hot-Plug aktiv)",
            1 => "Verbunden · 1 Mikrofon aktiv",
            _ => $"Verbunden · {count} Mikrofone aktiv"
        };
    }

    public void ConfigureServer(string server)
    {
        if (IsConnected) return;
        _server = server.TrimEnd('/');
        _enabled = false;
        _availableStages = Array.Empty<OnlineStageDto>();
        _status = "Online-Konfiguration wird geladen …";
        _ = LoadConfigurationAsync();
    }

    private async Task LoadConfigurationAsync()
    {
        try
        {
            var configuration = await GetAsync<OnlineConfigurationDto>("/api/online/config", _lifetime.Token);
            _enabled = configuration.enabled && configuration.provider.Equals("LiveKit", StringComparison.OrdinalIgnoreCase);
            _status = _enabled ? "Bereit zum Verbinden" : configuration.status;
            if (_enabled) await RefreshStagesAsync();
        }
        catch (Exception exception) { _status = "Online-Status nicht erreichbar: " + exception.Message; }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8)) _panelOpen = !_panelOpen;
        if (_state.ConnectionState is not (OnlineConnectionState.Connected or OnlineConnectionState.Reconnecting)) return;
        if (_state.ConnectionState == OnlineConnectionState.Connected &&
            _state.LocalRole == OnlineRole.Listener && !_conversationChanging &&
            _conversationPublishing != _conversationDesired)
            _ = ApplyConversationModeAsync();
        UpdateTimelineSynchronization();
        if (Time.unscaledTime < _nextHeartbeat || _busy) return;
        _nextHeartbeat = Time.unscaledTime + 5f;
        _ = HeartbeatAsync();
    }

    private void UpdateTimelineSynchronization()
    {
        if (_transport == null || _state.ConnectionState != OnlineConnectionState.Connected) return;
        if (_state.LocalRole == OnlineRole.Singer && !_timelinePublishRunning &&
            Time.unscaledTime >= _nextTimelinePublish && !string.IsNullOrWhiteSpace(_queueEntryId))
        {
            _nextTimelinePublish = Time.unscaledTime + .2f;
            _ = PublishTimelineAsync();
        }
        else if (_state.LocalRole == OnlineRole.Listener && !_latencyRefreshRunning &&
                 Time.unscaledTime >= _nextLatencyRefresh)
        {
            _nextLatencyRefresh = Time.unscaledTime + 2f;
            _ = RefreshLatencyAsync();
        }
    }

    private async Task PublishTimelineAsync()
    {
        if (_transport == null) return;
        _timelinePublishRunning = true;
        try
        {
            await _transport.PublishTimelineAsync(_queueEntryId, _audio.PositionSeconds,
                _audio.IsPlaying, _mixer.BroadcastMusicDelaySeconds, _lifetime.Token);
        }
        catch (Exception exception) { Debug.LogWarning("[Online] timeline publish failed: " + exception.Message); }
        finally { _timelinePublishRunning = false; }
    }

    private async Task RefreshLatencyAsync()
    {
        if (_transport == null) return;
        _latencyRefreshRunning = true;
        try { await _transport.RefreshLatencyEstimateAsync(_audio.EstimatedOutputLatencySeconds, _lifetime.Token); }
        catch (Exception exception) { Debug.LogWarning("[Online] latency stats failed: " + exception.Message); }
        finally { _latencyRefreshRunning = false; }
    }

    public void HandlePointerRelease(Vector2 position, float screenWidth, float scale)
    {
        if (!IconRect(screenWidth, scale).Contains(position)) return;
        _panelOpen = !_panelOpen;
        Debug.Log("[Online] panel toggled by pointer");
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            var room = await PostAsync<OnlineRoomStateDto>($"/api/online/rooms/{_room}/participants/{_participantId}/heartbeat", "{}", _lifetime.Token);
            _state.ReplaceParticipants(room.participants.Select(item => new OnlineParticipantInfo(item.participantId,
                item.displayName, (OnlineRole)item.role)));
        }
        catch (Exception exception)
        {
            _status = "Room-Status vorübergehend nicht erreichbar: " + exception.Message;
            Debug.LogWarning("[Online] heartbeat failed: " + exception.Message);
        }
    }

    public void DrawGui(float screenWidth, float scale, Func<Rect, bool>? consumePointerClick = null)
    {
        scale = Mathf.Max(1f, scale);
        var safeArea = Screen.safeArea;
        var rightInset = Mathf.Max(0f, screenWidth - safeArea.xMax);
        var topInset = Mathf.Max(0f, Screen.height - safeArea.yMax);
        DrawIcon(IconRect(screenWidth, scale));
        if (!_panelOpen) return;
        var box = new Rect(
            screenWidth - rightInset - 390 * scale,
            topInset + 100 * scale,
            372 * scale,
            410 * scale);
        var boxStyle = ScaledStyle(GUI.skin.box, scale, 14, FontStyle.Bold);
        var labelStyle = ScaledStyle(GUI.skin.label, scale, 13);
        var fieldStyle = ScaledStyle(GUI.skin.textField, scale, 14);
        GUI.Box(box, "ONLINE KARAOKE", boxStyle);
        GUI.Label(ScaledRect(box, 18, 32, 328, 24, scale), "Verfügbare Online-Stages", labelStyle);
        var actionStyle = ScaledStyle(GUI.skin.button, scale, 12, FontStyle.Bold);
        var stageY = 60f;
        if (_launcherRoom == "offline")
            GUI.Label(ScaledRect(box, 18, stageY, 328, 34, scale), "Diese Stage ist offline", labelStyle);
        else if (_availableStages.Length == 0)
            GUI.Label(ScaledRect(box, 18, stageY, 328, 34, scale),
                _loadingStages ? "Stages werden geladen …" : "Keine Online-Stage aktiv", labelStyle);
        foreach (var stage in _availableStages
                     .Where(stage => string.IsNullOrWhiteSpace(_launcherRoom) ||
                                     string.Equals(stage.roomId, _launcherRoom, StringComparison.OrdinalIgnoreCase))
                     .Take(3))
        {
            var selected = string.Equals(stage.roomId, _room, StringComparison.OrdinalIgnoreCase);
            if (DrawButton(ScaledRect(box, 18, stageY, 328, 34, scale),
                    $"{(selected ? "●" : "○")} {stage.name} · {stage.participantCount}" +
                    (stage.allowConversation ? " · PAUSEN-TALK" : ""), actionStyle,
                    consumePointerClick)) SelectStage(stage.roomId);
            stageY += 38;
        }
        GUI.Label(ScaledRect(box, 18, 178, 80, 28, scale), "Kennwort", labelStyle);
        _password = GUI.PasswordField(ScaledRect(box, 100, 178, 246, 28, scale), _password, '*', fieldStyle);
        GUI.Label(ScaledRect(box, 18, 212, 80, 28, scale), "Standort", labelStyle);
        _displayName = GUI.TextField(ScaledRect(box, 100, 212, 246, 28, scale), _displayName, fieldStyle);
        GUI.Label(ScaledRect(box, 18, 246, 328, 42, scale), _status, labelStyle);

        GUI.enabled = _enabled && !_busy;
        var leftAction = ScaledRect(box, 18, 298, 156, 42, scale);
        var rightAction = ScaledRect(box, 190, 298, 156, 42, scale);
        if (_state.ConnectionState is OnlineConnectionState.Disconnected or OnlineConnectionState.Failed)
        {
            GUI.enabled = GUI.enabled && !string.IsNullOrWhiteSpace(_room);
            var joinAction = ScaledRect(box, 18, 298, 264, 42, scale);
            if (DrawButton(joinAction, "Online beitreten · Rolle automatisch", actionStyle,
                    consumePointerClick)) _ = JoinAsync(OnlineRole.Listener);
            GUI.enabled = _enabled && !_busy;
            if (DrawButton(ScaledRect(box, 288, 298, 58, 42, scale), "↻", actionStyle,
                    consumePointerClick)) _ = RefreshStagesAsync();
        }
        else
        {
            GUI.Label(leftAction, _state.LocalRole == OnlineRole.Singer
                ? "Automatik: SINGER"
                : "Automatik: LISTENER", labelStyle);
            if (DrawButton(rightAction, "Stage verlassen", actionStyle, consumePointerClick))
                StageLeaveRequested?.Invoke();
        }
        GUI.enabled = true;
        GUI.Label(ScaledRect(box, 18, 350, 328, 44, scale), ParticipantSummary(), labelStyle);
    }

    private void DrawIcon(Rect rect)
    {
        var state = _state.ConnectionState;
        var connected = state == OnlineConnectionState.Connected;
        var transitioning = state is OnlineConnectionState.Joining or
            OnlineConnectionState.Reconnecting or OnlineConnectionState.Leaving;
        var singer = connected && _state.LocalRole == OnlineRole.Singer;
        var accent = singer
            ? new Color(1f, .16f, .57f, 1f)
            : connected
                ? new Color(.04f, .92f, 1f, 1f)
                : transitioning
                    ? new Color(1f, .72f, .12f, 1f)
                    : state == OnlineConnectionState.Failed
                        ? new Color(1f, .28f, .24f, 1f)
                        : new Color(.55f, .58f, .64f, 1f);

        // A permanent written state and a shape-changing halo make the mode
        // readable from a distance and do not rely on subtle colour changes.
        if (connected || transitioning)
        {
            var phase = (Mathf.Sin(Time.unscaledTime * (connected ? 4.2f : 2.8f)) + 1f) * .5f;
            var expansion = Mathf.Lerp(4f, connected ? 15f : 10f, phase) * Mathf.Max(1f, rect.height / 80f);
            var halo = new Rect(rect.x - expansion, rect.y - expansion,
                rect.width + expansion * 2, rect.height + expansion * 2);
            DrawOutline(halo, new Color(accent.r, accent.g, accent.b,
                Mathf.Lerp(.2f, .75f, phase)), Mathf.Lerp(2f, 5f, phase));
        }

        DrawSolid(rect, connected
            ? new Color(.025f, .055f, .075f, .96f)
            : new Color(.055f, .06f, .075f, .96f));
        DrawOutline(rect, accent, connected ? 4f : 3f);

        var unit = Mathf.Max(1f, rect.height / 80f);
        var iconRect = new Rect(rect.xMax - 72f * unit, rect.y + 8f * unit,
            64f * unit, 64f * unit);
        var icon = connected ? _onlineIcon : _offlineIcon ?? _onlineIcon;
        if (icon != null)
        {
            var previous = GUI.color;
            GUI.color = connected ? Color.white : new Color(.72f, .74f, .78f, 1f);
            GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit, true);
            GUI.color = previous;
        }
        else
        {
            var fallbackStyle = ScaledStyle(GUI.skin.label,
                unit, 28, FontStyle.Bold);
            fallbackStyle.alignment = TextAnchor.MiddleCenter;
            fallbackStyle.normal.textColor = accent;
            GUI.Label(iconRect, connected ? "●" : "○", fallbackStyle);
        }

        var headlineStyle = ScaledStyle(GUI.skin.label, unit, 18, FontStyle.Bold);
        headlineStyle.alignment = TextAnchor.MiddleLeft;
        headlineStyle.normal.textColor = connected ? Color.white : new Color(.9f, .91f, .94f);
        var detailStyle = ScaledStyle(GUI.skin.label, unit, 10, FontStyle.Bold);
        detailStyle.alignment = TextAnchor.MiddleLeft;
        detailStyle.normal.textColor = accent;
        var textWidth = rect.width - 84f * unit;
        var headline = connected ? "ONLINE" : transitioning ? "VERBINDEN" :
            state == OnlineConnectionState.Failed ? "FEHLER" : "OFFLINE";
        var detail = connected
            ? IsConversationActive ? "PAUSEN-GESPRÄCH · MIKRO AN" :
                singer ? "SINGER · SENDET" : "LISTENER · EMPFÄNGT"
            : transitioning ? "LIVEKIT WIRD VERBUNDEN" : "NICHT VERBUNDEN";
        GUI.Label(new Rect(rect.x + 13f * unit, rect.y + 10f * unit,
            textWidth, 30f * unit), headline, headlineStyle);
        GUI.Label(new Rect(rect.x + 13f * unit, rect.y + 39f * unit,
            textWidth, 24f * unit), detail, detailStyle);
    }

    private static Rect IconRect(float screenWidth, float scale)
    {
        scale = Mathf.Max(1f, scale);
        var safeArea = Screen.safeArea;
        var rightInset = Mathf.Max(0f, screenWidth - safeArea.xMax);
        var topInset = Mathf.Max(0f, Screen.height - safeArea.yMax);
        return new Rect(
            screenWidth - rightInset - 250 * scale,
            topInset + 12 * scale,
            236 * scale,
            80 * scale);
    }

    private static void DrawSolid(Rect rect, Color color)
    {
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
        GUI.color = previous;
    }

    private static void DrawOutline(Rect rect, Color color, float thickness)
    {
        thickness = Mathf.Max(1f, thickness);
        DrawSolid(new Rect(rect.x, rect.y, rect.width, thickness), color);
        DrawSolid(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
        DrawSolid(new Rect(rect.x, rect.y, thickness, rect.height), color);
        DrawSolid(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
    }

    private static bool DrawButton(Rect rect, string text, GUIStyle style,
        Func<Rect, bool>? consumePointerClick)
    {
        var guiClicked = GUI.Button(rect, text, style);
        // IMGUI usually maps the first Android touch to a mouse click. The
        // Stage pointer path is checked as well so taps remain reliable on
        // devices and window systems where that emulation is incomplete.
        var pointerClicked = consumePointerClick?.Invoke(rect) == true;
        return GUI.enabled && (guiClicked || pointerClicked);
    }

    private static Rect ScaledRect(Rect parent, float x, float y, float width,
        float height, float scale) => new(
        parent.x + x * scale, parent.y + y * scale,
        width * scale, height * scale);

    private static GUIStyle ScaledStyle(GUIStyle source, float scale,
        int fontSize, FontStyle fontStyle = FontStyle.Normal) => new(source)
    {
        fontSize = Mathf.Max(1, Mathf.RoundToInt(fontSize * scale)),
        fontStyle = fontStyle
    };

    private string ParticipantSummary()
    {
        if (_state.Participants.Count == 0) return "Noch keine Teilnehmer";
        return string.Join("  •  ", _state.Participants.Select(item => item.DisplayName + " (" + item.Role + ")"));
    }

    private async Task RefreshStagesAsync()
    {
        if (_loadingStages) return;
        _loadingStages = true;
        try
        {
            var result = await GetAsync<OnlineStageListDto>("/api/online/stages", _lifetime.Token);
            _availableStages = result.items ?? Array.Empty<OnlineStageDto>();
            var remembered = PlayerPrefs.GetString("NeonStage.OnlineRoom", "");
            var selected = _availableStages.FirstOrDefault(stage =>
                               string.Equals(stage.roomId, _room, StringComparison.OrdinalIgnoreCase)) ??
                           _availableStages.FirstOrDefault(stage =>
                               string.Equals(stage.roomId, remembered, StringComparison.OrdinalIgnoreCase)) ??
                           _availableStages.FirstOrDefault();
            if (selected != null) SelectStage(selected.roomId);
            else { _room = ""; _password = ""; }
        }
        catch (Exception exception)
        {
            _status = "Online-Stages nicht erreichbar: " + exception.Message;
        }
        finally { _loadingStages = false; }
    }

    private void SelectStage(string roomId)
    {
        _room = roomId;
        _password = PlayerPrefs.GetString("NeonStage.OnlinePassword." + _room, "");
    }

    private async Task JoinAsync(OnlineRole role)
    {
        _busy = true;
        var registeredOnServer = false;
        try
        {
            _state.BeginJoin();
            Debug.Log($"[Online] joining room={_room} requestedRole={role}");
            if (role == OnlineRole.Singer && !await EnsureMicrophonePermissionAsync())
                throw new InvalidOperationException("Mikrofonzugriff wurde nicht erlaubt.");
            PlayerPrefs.SetString("NeonStage.OnlineName", _displayName);
            var request = new OnlineJoinRequestDto
            {
                roomId = _room, participantId = _participantId, displayName = _displayName,
                role = (int)role, password = _password, locationId = _locationId
            };
            var session = await PostAsync<OnlineRoomSessionDto>("/api/online/rooms/join", JsonUtility.ToJson(request), _lifetime.Token);
            registeredOnServer = true;
            PlayerPrefs.SetString("NeonStage.OnlineRoom", _room);
            PlayerPrefs.SetString("NeonStage.OnlinePassword." + _room, _password);
            PlayerPrefs.Save();
            await ApplySessionAsync(session);
        }
        catch (Exception exception)
        {
            if (registeredOnServer)
                await TryRemoveParticipantAsync(_room, _participantId);
            ResetLocalConnection();
            _state.Left();
            Fail(exception);
        }
        finally { _busy = false; }
    }

    public void SynchronizePlaybackOwner(string? requestedLocationId, bool queueEntryAssigned, string? queueEntryId = null)
    {
        _queueEntryId = queueEntryId ?? "";
        if (_remoteTimeline is { } timeline && timeline.QueueEntryId != _queueEntryId)
            _remoteTimeline = null;
        if (_state.ConnectionState != OnlineConnectionState.Connected || _busy) return;
        var desired = OnlineRoleAutomation.ForQueueLocation(requestedLocationId, _locationId, queueEntryAssigned);
        if (desired == _state.LocalRole || Time.unscaledTime < _nextAutomaticRoleAttempt) return;
        _nextAutomaticRoleAttempt = Time.unscaledTime + 2.5f;
        if (desired == OnlineRole.Listener)
        {
            // Stop emitting the old site's song immediately. The server role
            // handoff and LiveKit reconnect can finish asynchronously.
            _mixer.SetBroadcastEnabled(false);
            _ = ChangeRoleAsync(OnlineRole.Listener);
        }
        else
        {
            _ = BecomeSingerAsync();
        }
    }

    public bool TryGetRemotePresentationPosition(double durationSeconds, out double positionSeconds,
        out bool playing)
    {
        positionSeconds = 0;
        playing = false;
        if (!IsListener || _remoteTimeline is not { } beacon ||
            beacon.QueueEntryId != _queueEntryId) return false;
        positionSeconds = OnlineTimelineProjection.AudiblePosition(beacon,
            Time.realtimeSinceStartupAsDouble, RemoteLatencySeconds, durationSeconds);
        playing = beacon.Playing;
        return true;
    }

    private async Task BecomeSingerAsync()
    {
        if (!await EnsureMicrophonePermissionAsync()) { _status = "Mikrofonzugriff wurde nicht erlaubt."; return; }
        await ChangeRoleAsync(OnlineRole.Singer);
    }

    private async Task ChangeRoleAsync(OnlineRole role)
    {
        _busy = true;
        try
        {
            Debug.Log($"[Online] role change requested role={role}");
            var request = new OnlineRoleRequestDto { role = (int)role };
            var session = await PutAsync<OnlineRoomSessionDto>($"/api/online/rooms/{_room}/participants/{_participantId}/role", JsonUtility.ToJson(request), _lifetime.Token);
            _state.BeginReconnect();
            await ApplySessionAsync(session);
        }
        catch (Exception exception) { Fail(exception); }
        finally { _busy = false; }
    }

    private async Task ApplySessionAsync(OnlineRoomSessionDto session)
    {
        _room = session.roomId;
        _participantId = session.participantId;
        _allowConversation = session.allowConversation;
        _conversationDesired &= _allowConversation;
        var role = (OnlineRole)session.role;
        if (_transport != null) await _transport.DisconnectAsync(_lifetime.Token);
        _conversationPublishing = false;
        _transport = new LiveKitAudioTransport(_mixer);
        _transport.TimelineReceived += HandleTimelineReceived;
        _transport.ConnectionLost += () => { _state.BeginReconnect(); _status = "Verbindung verloren"; };
        _transport.Reconnecting += () => { _state.BeginReconnect(); _status = "Verbinde neu …"; };
        _transport.Reconnected += () =>
        {
            if (_state.ConnectionState == OnlineConnectionState.Reconnecting)
                _state.Connected(_state.LocalRole, _state.Participants);
            _status = "Wieder verbunden";
        };
        var liveKitOverride = StageRuntimeSettings.LiveKitServerOverride;
        var liveKitServer = string.IsNullOrWhiteSpace(liveKitOverride)
            ? session.serverUrl
            : liveKitOverride;
        await _transport.ConnectAsync(new OnlineTransportConnection(liveKitServer, session.accessToken,
            session.roomId, session.participantId, role), _lifetime.Token);
        if (_listenerMixLocked)
            _transport.SetRemoteMix(.85f, .35f, _lockedMicrophoneVolume);
        if (role == OnlineRole.Singer)
        {
            // Android uses LiveKit/WebRTC's native audio-device module. Sending
            // the microphone through Unity first adds the complete DSP buffer
            // before encoding and makes the voice trail the remote lyrics.
            if (!_transport.UsesNativeMicrophone && !_mixer.StartMicrophone())
                Debug.LogWarning("[Online] no microphone connected yet; hot-plug watcher remains active");
            // OnAudioFilterRead exposes music before Android's output buffer is
            // audible. A singer reacts to the later, audible signal. Hold only
            // the outbound music by that hardware latency so it meets the native
            // microphone at the listener, and include the same delay in timeline
            // packets so words follow the received performance.
            var microphoneBufferSeconds = _transport.UsesNativeMicrophone ? 0 : .035;
            _mixer.ConfigureBroadcastMusicDelay(
                _audio.EstimatedOutputLatencySeconds + microphoneBufferSeconds);
            await _transport.StartPublishingAsync(_lifetime.Token);
            _mixer.SetBroadcastEnabled(true);
        }
        else
        {
            _mixer.SetBroadcastEnabled(false);
        }
        var participants = session.participants.Select(item => new OnlineParticipantInfo(item.participantId,
            item.displayName, (OnlineRole)item.role)).ToArray();
        if (_state.ConnectionState is OnlineConnectionState.Joining or OnlineConnectionState.Reconnecting)
            _state.Connected(role, participants);
        else { _state.SetRole(role); _state.ReplaceParticipants(participants); }
        _audio.LocalOutputMuted = role == OnlineRole.Listener;
        RoleChanged?.Invoke(role);
        var microphoneCount = _transport.PublishedMicrophoneCount;
        _status = role == OnlineRole.Singer
            ? $"Du sendest Musik/Originalvocals + {microphoneCount} " +
              (microphoneCount == 1
                  ? (_transport.UsesNativeMicrophone
                      ? "Mikrofon-Eingang (Android Low-Latency)"
                      : "Mikrofon-Eingang (Hardware-Duomix möglich)")
                  : "Mikrofon-Eingänge")
            : "Du hörst den Remote-Mix";
        Debug.Log($"[Online] role={role} participants={participants.Length}");
    }

    private async Task ApplyConversationModeAsync()
    {
        if (_conversationChanging) return;
        _conversationChanging = true;
        try
        {
            while (_transport != null && _state.ConnectionState == OnlineConnectionState.Connected &&
                   _state.LocalRole == OnlineRole.Listener &&
                   _conversationPublishing != _conversationDesired)
            {
                if (_conversationDesired)
                {
                    if (!await EnsureMicrophonePermissionAsync())
                        throw new InvalidOperationException("Mikrofonzugriff wurde nicht erlaubt.");
                    if (!_mixer.StartMicrophone())
                        Debug.LogWarning("[Online] conversation opened without microphone; waiting for hot-plug");
                    _mixer.SetBroadcastEnabled(true);
                    await _transport.StartMicrophonePublishingAsync(_lifetime.Token);
                    _conversationPublishing = true;
                    _status = "Pausen-Gespräch aktiv · alle Standorte können sprechen";
                }
                else
                {
                    _mixer.SetBroadcastEnabled(false);
                    await _transport.StopMicrophonePublishingAsync(_lifetime.Token);
                    _conversationPublishing = false;
                    _status = "Song läuft · nur der Singer sendet Mikrofone";
                }
            }
        }
        catch (Exception exception)
        {
            _mixer.SetBroadcastEnabled(false);
            _conversationPublishing = false;
            _status = "Pausen-Gespräch fehlgeschlagen: " + exception.Message;
            Debug.LogWarning("[Online] conversation mode failed: " + exception.Message);
        }
        finally { _conversationChanging = false; }
    }

    private void HandleTimelineReceived(string queueEntryId, double positionSeconds, bool playing,
        double broadcastDelaySeconds)
    {
        SynchronizeConversationMode(playing);
        var now = Time.realtimeSinceStartupAsDouble;
        var duration = _audio.DurationSeconds > 0 ? _audio.DurationSeconds : 24 * 60 * 60;
        var incoming = new OnlineTimelineBeacon(queueEntryId, positionSeconds, playing, now,
            broadcastDelaySeconds);
        if (_remoteTimeline is { } previous && previous.QueueEntryId == queueEntryId)
        {
            var previousPosition = OnlineTimelineProjection.AudiblePosition(previous, now,
                RemoteLatencySeconds, duration);
            var incomingPosition = OnlineTimelineProjection.AudiblePosition(incoming, now,
                RemoteLatencySeconds, duration);
            var smoothed = OnlineTimelineProjection.SmoothPosition(previousPosition, incomingPosition);
            // Store an equivalent sender position. Projection will subtract the
            // current remote latency again when the render clock consumes it.
            positionSeconds = smoothed + RemoteLatencySeconds + broadcastDelaySeconds;
            incoming = new OnlineTimelineBeacon(queueEntryId, positionSeconds, playing, now,
                broadcastDelaySeconds);
        }
        _remoteTimeline = incoming;
    }

    private async Task<bool> EnsureMicrophonePermissionAsync()
    {
        if (Application.HasUserAuthorization(UserAuthorization.Microphone)) return true;
        var request = Application.RequestUserAuthorization(UserAuthorization.Microphone);
        while (!request.isDone) await Task.Yield();
        return Application.HasUserAuthorization(UserAuthorization.Microphone);
    }

    public async Task LeaveStageAsync()
    {
        // A return to the launcher may be requested while join/reconnect is
        // still finishing. Tear that session down immediately afterwards so
        // it cannot survive as a ghost participant.
        while (_busy) await Task.Yield();
        _busy = true;
        var room = _room;
        var participant = _participantId;
        Exception? disconnectError = null;
        try
        {
            _state.BeginLeave();
            Debug.Log($"[Online] leaving room={room}");
            if (_transport != null)
            {
                try { await _transport.DisconnectAsync(CancellationToken.None); }
                catch (Exception exception) { disconnectError = exception; }
            }
            if (!string.IsNullOrWhiteSpace(room) && !string.IsNullOrWhiteSpace(participant))
                await TryRemoveParticipantAsync(room, participant);
            Debug.Log("[Online] disconnected");
        }
        finally
        {
            ResetLocalConnection();
            _state.Left();
            _room = "";
            _launcherRoom = "";
            _password = "";
            _panelOpen = false;
            _status = disconnectError == null
                ? "Online-Room verlassen"
                : "Online-Room lokal verlassen; Netzwerk war bereits getrennt";
            _busy = false;
        }
    }

    private void ResetLocalConnection()
    {
        if (_transport != null)
        {
            _transport.Dispose();
            _transport = null;
        }
        _mixer.SetBroadcastEnabled(false);
        _mixer.StopMicrophone();
        _conversationPublishing = false;
        _conversationDesired = false;
        _conversationChanging = false;
        _allowConversation = false;
        _listenerMixLocked = false;
        _queueEntryId = "";
        _remoteTimeline = null;
        _audio.LocalOutputMuted = false;
    }

    private async Task TryRemoveParticipantAsync(string room, string participant)
    {
        try { await DeleteAsync($"/api/online/rooms/{room}/participants/{participant}"); }
        catch (Exception exception) { Debug.LogWarning("[Online] server leave failed: " + exception.Message); }
    }

    private void Fail(Exception exception)
    {
        if (_state.ConnectionState != OnlineConnectionState.Connected) _state.Failed(exception.Message);
        _status = exception.Message;
        Debug.LogError("[Online] " + exception);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = UnityWebRequest.Get(_server + path);
        return await SendAsync<T>(request, cancellationToken);
    }

    private async Task<T> PostAsync<T>(string path, string json, CancellationToken cancellationToken) =>
        await JsonRequestAsync<T>("POST", path, json, cancellationToken);
    private async Task<T> PutAsync<T>(string path, string json, CancellationToken cancellationToken) =>
        await JsonRequestAsync<T>("PUT", path, json, cancellationToken);

    private async Task<T> JsonRequestAsync<T>(string method, string path, string json, CancellationToken cancellationToken)
    {
        using var request = new UnityWebRequest(_server + path, method);
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        return await SendAsync<T>(request, cancellationToken);
    }

    private static async Task<T> SendAsync<T>(UnityWebRequest request, CancellationToken cancellationToken)
    {
        var operation = request.SendWebRequest();
        while (!operation.isDone) { cancellationToken.ThrowIfCancellationRequested(); await Task.Yield(); }
        if (request.result != UnityWebRequest.Result.Success)
            throw new InvalidOperationException($"HTTP {request.responseCode}: {request.downloadHandler?.text ?? request.error}");
        if (typeof(T) == typeof(object)) return default!;
        return JsonUtility.FromJson<T>(request.downloadHandler.text);
    }

    private async Task DeleteAsync(string path)
    {
        using var request = UnityWebRequest.Delete(_server + path);
        var operation = request.SendWebRequest();
        while (!operation.isDone) await Task.Yield();
    }

    private void OnDestroy()
    {
        _lifetime.Cancel();
        _transport?.Dispose();
        _lifetime.Dispose();
    }
}
}
