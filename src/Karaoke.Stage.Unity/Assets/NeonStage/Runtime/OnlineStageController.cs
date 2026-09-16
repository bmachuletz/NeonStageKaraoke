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
    private string _room = "PARTY";
    private string _displayName = "";
    private string _participantId = "";
    private string _status = "Online-Modus wird geprüft …";
    private bool _enabled;
    private bool _panelOpen;
    private bool _busy;
    private float _nextHeartbeat;
    private Texture2D? _onlineIcon;
    private Texture2D? _offlineIcon;

    public bool IsListener => _state.ConnectionState == OnlineConnectionState.Connected &&
                              _state.LocalRole == OnlineRole.Listener;
    public event Action<OnlineRole>? RoleChanged;

    public void Initialize(string server, StageAudioEngine audio)
    {
        _server = server.TrimEnd('/');
        _audio = audio;
        _mixer = gameObject.AddComponent<OnlineBroadcastMixer>();
        _mixer.Initialize(audio);
        _displayName = PlayerPrefs.GetString("NeonStage.OnlineName", SystemInfo.deviceName);
        _participantId = PlayerPrefs.GetString("NeonStage.OnlineParticipant", Guid.NewGuid().ToString("N"));
        PlayerPrefs.SetString("NeonStage.OnlineParticipant", _participantId);
        _onlineIcon = Resources.Load<Texture2D>("OnlineBroadcastIcon");
        _offlineIcon = Resources.Load<Texture2D>("OnlineBroadcastIconOffline");
        Debug.Log($"[Online] platform={Application.platform} processor={SystemInfo.processorType} outputRate={AudioSettings.outputSampleRate}");
        _ = LoadConfigurationAsync();
    }

    private async Task LoadConfigurationAsync()
    {
        try
        {
            var configuration = await GetAsync<OnlineConfigurationDto>("/api/online/config", _lifetime.Token);
            _enabled = configuration.enabled && configuration.provider.Equals("LiveKit", StringComparison.OrdinalIgnoreCase);
            _status = _enabled ? "Bereit zum Verbinden" : configuration.status;
        }
        catch (Exception exception) { _status = "Online-Status nicht erreichbar: " + exception.Message; }
    }

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8)) _panelOpen = !_panelOpen;
        if (_state.ConnectionState is not (OnlineConnectionState.Connected or OnlineConnectionState.Reconnecting)) return;
        if (Time.unscaledTime < _nextHeartbeat || _busy) return;
        _nextHeartbeat = Time.unscaledTime + 5f;
        _ = HeartbeatAsync();
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
            270 * scale);
        var boxStyle = ScaledStyle(GUI.skin.box, scale, 14, FontStyle.Bold);
        var labelStyle = ScaledStyle(GUI.skin.label, scale, 13);
        var fieldStyle = ScaledStyle(GUI.skin.textField, scale, 14);
        GUI.Box(box, "ONLINE KARAOKE", boxStyle);
        GUI.Label(ScaledRect(box, 18, 32, 80, 28, scale), "Room", labelStyle);
        _room = GUI.TextField(ScaledRect(box, 100, 32, 246, 28, scale), _room, fieldStyle).ToUpperInvariant();
        GUI.Label(ScaledRect(box, 18, 66, 80, 28, scale), "Standort", labelStyle);
        _displayName = GUI.TextField(ScaledRect(box, 100, 66, 246, 28, scale), _displayName, fieldStyle);
        GUI.Label(ScaledRect(box, 18, 102, 328, 46, scale), _status, labelStyle);

        GUI.enabled = _enabled && !_busy;
        var actionStyle = ScaledStyle(GUI.skin.button, scale, 12, FontStyle.Bold);
        var leftAction = ScaledRect(box, 18, 158, 156, 42, scale);
        var rightAction = ScaledRect(box, 190, 158, 156, 42, scale);
        if (_state.ConnectionState is OnlineConnectionState.Disconnected or OnlineConnectionState.Failed)
        {
            if (DrawButton(leftAction, "Als Listener beitreten", actionStyle, consumePointerClick)) _ = JoinAsync(OnlineRole.Listener);
            if (DrawButton(rightAction, "Als Singer beitreten", actionStyle, consumePointerClick)) _ = JoinAsync(OnlineRole.Singer);
        }
        else
        {
            if (_state.LocalRole == OnlineRole.Listener && DrawButton(leftAction, "Singer werden", actionStyle, consumePointerClick)) _ = BecomeSingerAsync();
            if (_state.LocalRole == OnlineRole.Singer && DrawButton(leftAction, "Broadcast stoppen", actionStyle, consumePointerClick)) _ = ChangeRoleAsync(OnlineRole.Listener);
            if (DrawButton(rightAction, "Verlassen", actionStyle, consumePointerClick)) _ = LeaveAsync();
        }
        GUI.enabled = true;
        GUI.Label(ScaledRect(box, 18, 210, 328, 44, scale), ParticipantSummary(), labelStyle);
    }

    private void DrawIcon(Rect rect)
    {
        var connected = _state.ConnectionState == OnlineConnectionState.Connected;
        var icon = connected ? _onlineIcon : _offlineIcon ?? _onlineIcon;
        if (icon != null)
        {
            var previous = GUI.color;
            if (connected)
            {
                var pulse = .9f + Mathf.Sin(Time.unscaledTime * 4f) * .1f;
                GUI.color = new Color(1f, 1f, 1f, pulse);
            }
            GUI.DrawTexture(rect, icon, ScaleMode.ScaleToFit, true);
            GUI.color = previous;
        }
        else
        {
            var fallbackStyle = ScaledStyle(GUI.skin.label,
                Mathf.Max(1f, rect.height / 80f), 28, FontStyle.Bold);
            fallbackStyle.alignment = TextAnchor.MiddleCenter;
            GUI.Label(rect, "◎", fallbackStyle);
        }
    }

    private static Rect IconRect(float screenWidth, float scale)
    {
        scale = Mathf.Max(1f, scale);
        var safeArea = Screen.safeArea;
        var rightInset = Mathf.Max(0f, screenWidth - safeArea.xMax);
        var topInset = Mathf.Max(0f, Screen.height - safeArea.yMax);
        return new Rect(
            screenWidth - rightInset - 94 * scale,
            topInset + 12 * scale,
            80 * scale,
            80 * scale);
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

    private async Task JoinAsync(OnlineRole role)
    {
        _busy = true;
        try
        {
            _state.BeginJoin();
            Debug.Log($"[Online] joining room={_room} requestedRole={role}");
            if (role == OnlineRole.Singer && !await EnsureMicrophonePermissionAsync())
                throw new InvalidOperationException("Mikrofonzugriff wurde nicht erlaubt.");
            PlayerPrefs.SetString("NeonStage.OnlineName", _displayName);
            var request = new OnlineJoinRequestDto { roomId = _room, participantId = _participantId, displayName = _displayName, role = (int)role };
            var session = await PostAsync<OnlineRoomSessionDto>("/api/online/rooms/join", JsonUtility.ToJson(request), _lifetime.Token);
            await ApplySessionAsync(session);
        }
        catch (Exception exception) { Fail(exception); }
        finally { _busy = false; }
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
        var role = (OnlineRole)session.role;
        if (_transport != null) await _transport.DisconnectAsync(_lifetime.Token);
        _transport = new LiveKitAudioTransport(_mixer);
        _transport.ConnectionLost += () => { _state.BeginReconnect(); _status = "Verbindung verloren"; };
        _transport.Reconnecting += () => { _state.BeginReconnect(); _status = "Verbinde neu …"; };
        _transport.Reconnected += () =>
        {
            if (_state.ConnectionState == OnlineConnectionState.Reconnecting)
                _state.Connected(_state.LocalRole, _state.Participants);
            _status = "Wieder verbunden";
        };
        await _transport.ConnectAsync(new OnlineTransportConnection(session.serverUrl, session.accessToken,
            session.roomId, session.participantId, role), _lifetime.Token);
        if (role == OnlineRole.Singer)
        {
            if (!_mixer.StartMicrophone())
                throw new InvalidOperationException("Kein Mikrofoneingang gefunden.");
            await _transport.StartPublishingAsync(_lifetime.Token);
        }
        var participants = session.participants.Select(item => new OnlineParticipantInfo(item.participantId,
            item.displayName, (OnlineRole)item.role)).ToArray();
        if (_state.ConnectionState is OnlineConnectionState.Joining or OnlineConnectionState.Reconnecting)
            _state.Connected(role, participants);
        else { _state.SetRole(role); _state.ReplaceParticipants(participants); }
        if (role == OnlineRole.Listener) _audio.Stop();
        RoleChanged?.Invoke(role);
        _status = role == OnlineRole.Singer
            ? $"Du sendest Musik + {_mixer.ActiveMicrophoneCount} " +
              (_mixer.ActiveMicrophoneCount == 1
                  ? "Mikrofon-Eingang (Hardware-Duomix möglich)"
                  : "Mikrofon-Eingänge")
            : "Du hörst den Remote-Mix";
        Debug.Log($"[Online] role={role} participants={participants.Length}");
    }

    private async Task<bool> EnsureMicrophonePermissionAsync()
    {
        if (Application.HasUserAuthorization(UserAuthorization.Microphone)) return true;
        var request = Application.RequestUserAuthorization(UserAuthorization.Microphone);
        while (!request.isDone) await Task.Yield();
        return Application.HasUserAuthorization(UserAuthorization.Microphone);
    }

    private async Task LeaveAsync()
    {
        _busy = true;
        try
        {
            _state.BeginLeave();
            Debug.Log($"[Online] leaving room={_room}");
            if (_transport != null) { await _transport.DisconnectAsync(CancellationToken.None); _transport.Dispose(); _transport = null; }
            await DeleteAsync($"/api/online/rooms/{_room}/participants/{_participantId}");
            _state.Left();
            _status = "Online-Room verlassen";
            Debug.Log("[Online] disconnected");
        }
        catch (Exception exception) { Fail(exception); }
        finally { _busy = false; }
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
