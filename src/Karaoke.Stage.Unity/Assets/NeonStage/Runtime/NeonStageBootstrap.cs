using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Threading.Tasks;
using NeonStage.Testing;
using NeonStage.Timing;
using UnityEngine;
using UnityEngine.Networking;

namespace NeonStage.Stage
{

public sealed class NeonStageBootstrap : MonoBehaviour
{
    private const string DefaultServer = "http://127.0.0.1:5274";
    // Der Server bevorzugt Unity-kompatible Ogg/Vorbis-Stems. Der Client fällt
    // bei älteren Bibliothekseinträgen sicher auf die MP3-Masterspur zurück.
    private const bool PreparedStemsAreUnityCompatible = true;
    private StageAudioEngine _audio = null!;
    private StageIdleMusic _idleMusic = null!;
    private OnlineStageController? _online;
    private IStageClock _clock = null!;
    private string _server = DefaultServer;
    private string _status = "Verbinde mit NeonStage …";
    private string? _loadedSongId;
    private string? _loadedQueueEntryId;
    private string? _loadedStartedAt;
    private string? _completedQueueEntryId;
    private string? _completedStartedAt;
    private string _title = "NEON STAGE – UNITY AUDIO LAB";
    private string _songTitle = "NEON STAGE";
    private string _songArtist = "UNITY AUDIO LAB";
    private bool _refreshing;
    private string _controllerId = "";
    private bool _ownsControl;
    private bool _commandRunning;
    private readonly Queue<string> _playbackCommands = new();
    private string? _optimisticPlaybackCommand;
    private bool _autoAdvanceRunning;
    private float _nextQrRefresh;
    private float _nextTimingReport;
    private bool _timingReportRunning;
    private bool _hasActiveSession;
    private bool _activeEventOnline;
    private string? _activeEventId;
    private StageLauncherDto[] _launcherStages = Array.Empty<StageLauncherDto>();
    private readonly Dictionary<string, Texture2D> _launcherImages = new();
    private bool _launcherLoading;
    private int _launcherPage;
    private float _nextLauncherRefresh;
    private bool _exiting;
    private bool _leavingSession;
    private int _sessionGeneration;
    private float _musicVolume = 0.85f;
    private float _vocalVolume = 0.35f;
    private float _microphoneVolume = 1f;
    private readonly StageLyricsEngine _lyrics = new();
    private StageVisualView _visuals = null!;
    private StageVideoView _video = null!;
    private StageLoadingView _loading = null!;
    private StageReactionView _reactions = null!;
    private float _nextReactionPoll;
    private bool _reactionPolling;
    private long _lastReactionId;
    private Texture2D? _controlButton;
    private Texture2D? _controlButtonHover;
    private Texture2D? _controlButtonActive;
    private Texture2D? _sliderTrack;
    private Texture2D? _sliderThumb;
    private Texture2D? _transparent;
    private Texture2D? _pill;
    private Texture2D? _softGlow;
    private Texture2D? _iconBadge;
    private Texture2D? _stageSelectionIcon;
    private Texture2D? _wordmark;
    private Texture2D? _settingsIcon;
    private bool _settingsOpen;
    private int _settingsTab;
    private bool _settingsAutomaticMicrophones;
    private readonly List<string> _settingsSelectedMicrophones = new();
    private string[] _settingsMicrophoneDevices = Array.Empty<string>();
    private float _nextSettingsDeviceRefresh;
    private string _settingsServerUrl = string.Empty;
    private string _settingsLiveKitUrl = string.Empty;
    private bool _settingsFullscreen;
    private int _settingsDisplayIndex;
    private bool _settingsDisplaySelectionChanged;
    private readonly List<DisplayInfo> _settingsDisplays = new();
    private string _settingsMessage = string.Empty;
    private readonly StagePointerInput _pointer = new();
    private bool _editorTestMode;
    private bool _editorExportMode;
    private bool _exportRunning;
    private bool _exportCancelled;
    private StageTestSongState? _editorSongState;
    private StageEditorTestClient? _editorTestClient;
    private readonly StageTestRevisionGate _editorRevisionGate = new();
    private float _nextEditorPlaybackState;
    private int _editorSongLoadGeneration;
    private string _editorLyricsJson = "";
    private IReadOnlyList<double> _editorBeatTimes = Array.Empty<double>();
    private StageAudioAnalysisTimeline _editorAudioAnalysis = StageAudioAnalysisTimeline.Empty;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void StartWithoutSceneSetup()
    {
        if (FindAnyObjectByType<NeonStageBootstrap>() != null) return;
        DontDestroyOnLoad(new GameObject("NeonStage Stage Runtime", typeof(NeonStageBootstrap)));
    }

    private void Awake()
    {
        Application.runInBackground = true;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        if (Application.platform == RuntimePlatform.Android)
        {
            // The karaoke UI and the 24-fps stage video do not benefit from a
            // 60-fps render loop on the low-power Shell hardware. The freed GPU
            // and thermal budget keeps hardware video decoding stable.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 30;
        }
        ConfigureStageCamera();
        var hasEditorTestSettings = TryGetEditorTestSettings(
            out var testHost, out var testPort, out var testSession, out var testToken);
        if (FindAnyObjectByType<AudioListener>() == null)
            gameObject.AddComponent<AudioListener>();
        _server = ResolveServer();
        LoadSettingsDraft();
        ApplySavedDisplaySettings();
        Debug.Log($"Neon Stage server: {_server}");
        _controllerId = PlayerPrefs.GetString("NeonStage.ControllerId", "");
        if (!Guid.TryParse(_controllerId, out _))
        {
            _controllerId = Guid.NewGuid().ToString();
            PlayerPrefs.SetString("NeonStage.ControllerId", _controllerId);
            PlayerPrefs.Save();
        }
        _audio = gameObject.AddComponent<StageAudioEngine>();
        _idleMusic = gameObject.AddComponent<StageIdleMusic>();
        _idleMusic.SetSuppressed(hasEditorTestSettings);
        _audio.ConfigureOutputLatencySeconds(ResolveOutputLatencySeconds());
        _clock = new AudioStageClock(_audio);
        _audio.PlaybackEnded += HandlePlaybackEnded;
        _loading = new StageLoadingView(gameObject);
        _lyrics.Initialize(gameObject);
        _visuals = new StageVisualView(gameObject);
        _video = new StageVideoView(gameObject);
        _reactions = new StageReactionView(gameObject);
        _visuals.SetSessionActive(false);
        _editorExportMode = HasArgument("--editor-export");
        if (hasEditorTestSettings)
        {
            _editorTestMode = true;
            Screen.fullScreenMode = FullScreenMode.Windowed;
            Screen.SetResolution(1280, 720, FullScreenMode.Windowed);
            _hasActiveSession = true;
            _visuals.SetSessionActive(true);
            _status = StageLocale.Text("Warte auf den Lyrics-Editor …", "Waiting for lyrics editor …");
            _editorTestClient = new StageEditorTestClient(testHost, testPort, testSession, testToken,
                HandleEditorTestMessage);
            return;
        }
        _online = gameObject.AddComponent<OnlineStageController>();
        _online.Initialize(_server, _audio, _controllerId);
        _online.RoleChanged += HandleOnlineRoleChanged;
        _online.StageLeaveRequested += HandleStageLeaveRequested;
        _clock = new OnlineSynchronizedStageClock(_audio, _online);
        StartCoroutine(PollQueue());
    }

    private static string ResolveServer()
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].Equals("--server", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Length && TryNormalizeServer(arguments[index + 1], out var argumentServer))
                return argumentServer;
            const string prefix = "--server=";
            if (arguments[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                TryNormalizeServer(arguments[index].Substring(prefix.Length), out argumentServer))
                return argumentServer;
        }

        foreach (var variable in new[] { "NEONSTAGE_SERVER_URL", "KARAOKE_SERVER" })
            if (TryNormalizeServer(Environment.GetEnvironmentVariable(variable), out var environmentServer))
                return environmentServer;

        return TryNormalizeServer(PlayerPrefs.GetString("NeonStage.Server", DefaultServer), out var storedServer)
            ? storedServer
            : DefaultServer;
    }

    private static bool TryNormalizeServer(string? value, out string server)
    {
        server = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        server = value.Trim().TrimEnd('/');
        return true;
    }

    private static bool TryGetEditorTestSettings(out string host, out int port, out string session, out string token)
    {
        host = "127.0.0.1";
        port = 0;
        session = token = "";
        var arguments = Environment.GetCommandLineArgs();
        if (!Array.Exists(arguments, value => value.Equals("--editor-test", StringComparison.OrdinalIgnoreCase) ||
                                               value.Equals("--editor-export", StringComparison.OrdinalIgnoreCase)))
            return false;
        for (var index = 0; index + 1 < arguments.Length; index++)
        {
            if (arguments[index].Equals("--editor-test-host", StringComparison.OrdinalIgnoreCase)) host = arguments[index + 1];
            else if (arguments[index].Equals("--editor-test-port", StringComparison.OrdinalIgnoreCase))
                int.TryParse(arguments[index + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out port);
            else if (arguments[index].Equals("--editor-test-session", StringComparison.OrdinalIgnoreCase)) session = arguments[index + 1];
            else if (arguments[index].Equals("--editor-test-token", StringComparison.OrdinalIgnoreCase)) token = arguments[index + 1];
        }
        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address) &&
               port is > 0 and <= 65535 && Guid.TryParseExact(session, "N", out _) && token.Length >= 32;
    }

    private static bool HasArgument(string name) => Array.Exists(Environment.GetCommandLineArgs(),
        value => value.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static double? ResolveOutputLatencySeconds()
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index].Equals("--stage-audio-latency-ms", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < arguments.Length && TryLatency(arguments[index + 1], out var argumentLatency))
                return argumentLatency;
            const string prefix = "--stage-audio-latency-ms=";
            if (arguments[index].StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                TryLatency(arguments[index].Substring(prefix.Length), out argumentLatency))
                return argumentLatency;
        }

        if (TryLatency(Environment.GetEnvironmentVariable("NEONSTAGE_AUDIO_LATENCY_MS"), out var environmentLatency))
            return environmentLatency;
        return TryLatency(PlayerPrefs.GetString("NeonStage.AudioLatencyMs", "auto"), out var storedLatency)
            ? storedLatency
            : null;
    }

    private static bool TryLatency(string? value, out double? seconds)
    {
        seconds = null;
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds) ||
            milliseconds < 0 || milliseconds > 500) return false;
        seconds = milliseconds / 1000d;
        return true;
    }

    private void OnDestroy()
    {
        StageOfflineExporter.CancelActive();
        _video?.Stop();
        if (_audio != null) _audio.PlaybackEnded -= HandlePlaybackEnded;
        _editorTestClient?.Dispose();
        _pointer.Dispose();
    }

    private static void ConfigureStageCamera()
    {
        var camera = Camera.main;
        if (camera == null)
        {
            var cameraObject = new GameObject("Neon Stage Camera", typeof(Camera));
            cameraObject.tag = "MainCamera";
            camera = cameraObject.GetComponent<Camera>();
        }
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.035f, 0.008f, 0.055f);
        camera.cullingMask = 0;
    }

    private IEnumerator PollQueue()
    {
        while (true)
        {
            // A transport command owns the playback state until the server has
            // acknowledged it. Otherwise the one-second poll can immediately
            // undo the local pause/resume feedback with an older server state.
            if (!_commandRunning) _ = RefreshQueueAsync();
            yield return new WaitForSecondsRealtime(1f);
        }
    }

    private void HandleOnlineRoleChanged(NeonStage.Online.OnlineRole role)
    {
        // Listener stages retain the local transport solely as a synchronized
        // clock for lyrics, video and visuals. Only the LiveKit track is audible.
        _audio.LocalOutputMuted = role == NeonStage.Online.OnlineRole.Listener;
    }

    private void Update()
    {
        _editorTestClient?.Update();
        _pointer.Update();
        if (_pointer.Released && _online != null)
        {
            var onlineScale = Mathf.Max(1f,
                Mathf.Min(Screen.width / 1280f, Screen.height / 720f));
            _online.HandlePointerRelease(_pointer.Position, Screen.width, onlineScale);
        }
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (_editorTestMode) { Application.Quit(); return; }
            if (_hasActiveSession) _ = ReturnToLauncherAsync();
            else _ = ExitStageAsync();
            return;
        }
        // The opaque audio-reactive backdrop uses its own shader render queue.
        // On Linux/OpenGL it can cover a lower-sorted video canvas despite the
        // intended Canvas order. A song video is the authoritative backdrop on
        // every platform, so disable the shader pass while video is active.
        var videoBackgroundMode = _video.Active;
        if (!videoBackgroundMode && Time.unscaledTime >= _nextQrRefresh)
        {
            _nextQrRefresh = Time.unscaledTime + 15f;
            _ = _visuals.LoadQrAsync(_server, _controllerId, _activeEventId, force: true);
        }
        _visuals.SetVideoPerformanceMode(videoBackgroundMode);
        _lyrics.SetVideoPerformanceMode(Application.platform == RuntimePlatform.Android && videoBackgroundMode);
        var stageTime = _clock.Capture();
        StageAudioAnalysisFrame? offlineAnalysis = _exportRunning && _editorAudioAnalysis.HasFrames
            ? _editorAudioAnalysis.Sample(stageTime.PositionSeconds)
            : null;
        _visuals.Update(_audio, stageTime, offlineAnalysis);
        _video.Update(stageTime);
        _loading.Update();
        _reactions.Update();
        if (!_editorTestMode && _hasActiveSession && Time.unscaledTime >= _nextReactionPoll)
        {
            _nextReactionPoll = Time.unscaledTime + .35f;
            _ = PollReactionsAsync();
        }
        _lyrics.Update(stageTime, _visuals.AudioImpact);
        if (!_editorTestMode && _audio.HasClip && Time.unscaledTime >= _nextTimingReport)
        {
            _nextTimingReport = Time.unscaledTime + 1f;
            _ = ReportTimingAsync();
        }
        if (_editorTestMode && Time.unscaledTime >= _nextEditorPlaybackState)
        {
            _nextEditorPlaybackState = Time.unscaledTime + .5f;
            _editorTestClient?.Send(StageTestProtocol.PlaybackState, positionSeconds: _audio.PositionSeconds,
                playing: _audio.IsPlaying);
        }
    }

    private void HandleEditorTestMessage(StageTestMessage message)
    {
        // Defensive invariant: Pause, Stop and replacement snapshots must
        // never turn a test/export process back into a lobby.
        _idleMusic.SetSuppressed(true);
        switch (message.type)
        {
            case StageTestProtocol.ReplaceSongState:
                _ = ApplyEditorSongStateAsync(message);
                break;
            case StageTestProtocol.UpdateLyrics:
                if (_editorExportMode) break; // Export uses the immutable initial snapshot.
                if (_editorRevisionGate.TryApply(message.revision))
                {
                    _editorLyricsJson = message.stateJson;
                    _lyrics.LoadJson(_editorLyricsJson, _editorBeatTimes, clearView: false);
                    _editorTestClient?.Send(StageTestProtocol.Applied, message.revision,
                        _audio.PositionSeconds, _audio.IsPlaying);
                }
                break;
            case StageTestProtocol.Play:
                if (_audio.HasClip)
                {
                    if (Math.Abs(_audio.PositionSeconds - message.positionSeconds) > .08)
                        _audio.Seek(message.positionSeconds);
                    _audio.Resume();
                }
                break;
            case StageTestProtocol.Pause:
                if (_audio.HasClip)
                {
                    _audio.Seek(message.positionSeconds);
                    _audio.Pause();
                }
                break;
            case StageTestProtocol.Stop:
                if (_audio.HasClip) { _audio.Pause(); _audio.Seek(0); }
                break;
            case StageTestProtocol.Seek:
                if (_audio.HasClip) _audio.Seek(message.positionSeconds);
                break;
            case StageTestProtocol.Clock:
                ApplyEditorClock(message);
                break;
            case StageTestProtocol.BeginExport:
                BeginEditorExport(message);
                break;
            case StageTestProtocol.CancelExport:
                _exportCancelled = true;
                break;
            case StageTestProtocol.Shutdown:
                Application.Quit();
                break;
        }
    }

    private async Task ApplyEditorSongStateAsync(StageTestMessage message)
    {
        if (!_editorRevisionGate.TryApply(message.revision)) return;
        var loadGeneration = ++_editorSongLoadGeneration;
        try
        {
            var state = JsonUtility.FromJson<StageTestSongState>(message.stateJson);
            if (state == null || !Guid.TryParse(state.songId, out _))
                throw new InvalidOperationException("Der Editor hat keinen gültigen Songzustand gesendet.");
            var oldTitle = _songTitle;
            var oldArtist = _songArtist;
            _server = state.serverUrl.TrimEnd('/');
            _editorSongState = state;
            _loadedSongId = state.songId;
            _songTitle = state.title;
            _songArtist = state.artist;
            _title = $"{state.title} · {state.artist}";
            _status = StageLocale.Text("Editor-Song wird geladen …", "Loading editor song …");
            var song = new SongDto { id = state.songId, title = state.title, artist = state.artist };
            _visuals.BeginSongTransition(song, oldTitle, oldArtist);
            _visuals.SetStageTheme(state.stageThemeId);
            _lyrics.SetStageTheme(state.stageThemeId);
            _editorLyricsJson = state.lyricsJson;
            _editorBeatTimes = Array.Empty<double>();
            _lyrics.LoadJson(_editorLyricsJson);

            var coverTask = _visuals.LoadCoverAsync(_server, state.songId);
            var videoTask = _video.LoadAsync(_server, state.songId);
            var stemsTask = GetStemsAsync(state.songId);
            var analysisTask = GetEditorAudioAnalysisAsync(state.songId);
            await Task.WhenAll(coverTask, videoTask, stemsTask, analysisTask);
            if (_editorSongLoadGeneration != loadGeneration) return;
            _editorAudioAnalysis = await analysisTask;
            _editorBeatTimes = _editorAudioAnalysis.BeatTimes;
            _lyrics.LoadJson(_editorLyricsJson, _editorBeatTimes, clearView: false);
            _lyrics.SetVideoBackground(_video.Active);
            var stems = await stemsTask;
            var useStems = PreparedStemsAreUnityCompatible && stems.hasInstrumental;
            var master = useStems
                ? $"{_server}/api/songs/{state.songId}/stems/instrumental?format=ogg"
                : $"{_server}/api/songs/{state.songId}/audio";
            var vocals = useStems && stems.hasVocals
                ? $"{_server}/api/songs/{state.songId}/stems/vocals?format=ogg"
                : null;
            try { await _audio.LoadPausedAsync(master, vocals); }
            catch when (stems.hasInstrumental)
            {
                await _audio.LoadPausedAsync($"{_server}/api/songs/{state.songId}/audio", null);
            }
            if (_editorSongLoadGeneration != loadGeneration) return;
            _audio.Seek(state.positionSeconds);
            if (state.playing) _audio.Resume();
            _status = StageLocale.Text("Editor-Test bereit", "Editor test ready");
            _editorTestClient?.Send(StageTestProtocol.Ready, message.revision,
                _audio.PositionSeconds, _audio.IsPlaying);
        }
        catch (Exception exception)
        {
            _status = StageLocale.Text("Editor-Test fehlgeschlagen", "Editor test failed") + ": " + exception.Message;
            _editorTestClient?.Send(StageTestProtocol.Error, message.revision, details: exception.Message);
        }
    }

    private void BeginEditorExport(StageTestMessage request)
    {
        if (!_editorExportMode || _exportRunning || _editorSongState == null) return;
        if (request.revision != _editorRevisionGate.AppliedRevision)
        {
            _editorTestClient?.Send(StageTestProtocol.Error, details:
                "Der angeforderte Exportstand stimmt nicht mit dem geladenen Snapshot überein.");
            return;
        }
        if (request.width is < 320 or > 7680 || request.height is < 180 or > 4320 ||
            request.framesPerSecond is < 1 or > 120 || string.IsNullOrWhiteSpace(request.outputPath))
        {
            _editorTestClient?.Send(StageTestProtocol.Error, details: "Ungültige MP4-Exportparameter.");
            return;
        }
        _exportRunning = true;
        _exportCancelled = false;
        _audio.Pause();
        var offlineVideoPath = _video.DetachForOfflineExport();
        _lyrics.SetVideoBackground(!string.IsNullOrWhiteSpace(offlineVideoPath));
        var exportClock = new FrameStageClock();
        _clock = exportClock;
        StartCoroutine(StageOfflineExporter.Run(request, _editorSongState, exportClock,
            offlineVideoPath, _video.OffsetSeconds,
            () => _exportCancelled,
            (frame, total) => _editorTestClient?.Send(StageTestProtocol.ExportProgress,
                positionSeconds: frame / (double)request.framesPerSecond,
                details: "", frame: frame, totalFrames: total),
            output =>
            {
                _exportRunning = false;
                _video.CompleteOfflineExport();
                _editorTestClient?.Send(StageTestProtocol.ExportComplete, details: "", outputPath: output);
            },
            error =>
            {
                _exportRunning = false;
                _video.CompleteOfflineExport();
                _editorTestClient?.Send(StageTestProtocol.Error, details: error);
            }));
    }

    private void ApplyEditorClock(StageTestMessage message)
    {
        if (!_audio.HasClip) return;
        if (Math.Abs(_audio.PositionSeconds - message.positionSeconds) > .15)
            _audio.Seek(message.positionSeconds);
        if (message.playing && !_audio.IsPlaying) _audio.Resume();
        else if (!message.playing && _audio.IsPlaying) _audio.Pause();
    }

    private async Task<StageAudioAnalysisTimeline> GetEditorAudioAnalysisAsync(string songId)
    {
        using var request = UnityWebRequest.Get($"{_server}/api/songs/{songId}/visualization");
        await request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success) return StageAudioAnalysisTimeline.Empty;
        var visualization = JsonUtility.FromJson<SongVisualizationDto>(request.downloadHandler.text);
        var points = new List<StageAudioAnalysisPoint>();
        foreach (var frame in visualization?.frames ?? Array.Empty<VisualizationFrameDto>())
            points.Add(new StageAudioAnalysisPoint(frame.timeSeconds, frame.energy, frame.bass, frame.mid,
                frame.high, frame.beat));
        return new StageAudioAnalysisTimeline(points);
    }

    private async Task ReportTimingAsync()
    {
        if (_timingReportRunning || string.IsNullOrWhiteSpace(_loadedSongId)) return;
        _timingReportRunning = true;
        try
        {
            var sample = _audio.CaptureTiming(SystemInfo.deviceUniqueIdentifier, _loadedSongId);
            using var request = CreateJsonPost(
                $"{_server}/api/diagnostics/stage-timing", JsonUtility.ToJson(sample), false);
            await request.SendWebRequest();
        }
        catch { /* Telemetrie darf die Bühnenwiedergabe niemals beeinflussen. */ }
        finally { _timingReportRunning = false; }
    }

    private async Task RefreshQueueAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        var sessionGeneration = _sessionGeneration;
        var eventId = _activeEventId;
        try
        {
            if (!_hasActiveSession || string.IsNullOrWhiteSpace(_activeEventId))
            {
                _idleMusic.SetIdle(true);
                await LoadLauncherStagesAsync();
                return;
            }
            if (_activeEventOnline && !(_online?.IsConnectedToEvent(_activeEventId) ?? false))
            {
                _idleMusic.SetIdle(true);
                if (_audio.IsPlaying) _audio.Pause();
                _video.Stop();
                _lyrics.SetVideoBackground(false);
                _loadedSongId = null;
                _loadedQueueEntryId = null;
                _loadedStartedAt = null;
                _status = StageLocale.Text(
                    "Online-Stage aktiv – über das Online-Symbol beitreten",
                    "Online stage active – join from the online panel");
                return;
            }
            using var request = UnityWebRequest.Get(StageUrl("/api/queue"));
            await request.SendWebRequest();
            if (sessionGeneration != _sessionGeneration || eventId != _activeEventId) return;
            if (request.result != UnityWebRequest.Result.Success)
            {
                _status = $"Serverfehler {request.responseCode}: {request.error}";
                return;
            }

            var state = JsonUtility.FromJson<PlaybackStateDto>(request.downloadHandler.text);
            var current = state?.current;
            var songIsTakingOver = current?.song != null &&
                ((state?.isRunning == true && state.isPaused == false) ||
                 !string.IsNullOrWhiteSpace(_loadedSongId));
            _idleMusic.SetIdle(!songIsTakingOver);
            _online?.SynchronizePlaybackOwner(current?.locationId,
                current?.song != null, current?.id);
            _online?.ConfigureListenerMixPolicy(current?.singerControlsMix == true,
                current?.singerMicrophoneVolume ?? 1);
            _online?.SynchronizeConversationMode(state?.isRunning == true &&
                state.isPaused == false && current?.song != null);

            // A poll that started just before a mouse click may still complete
            // while the command is in flight. It may update passive metadata,
            // but it must not apply its stale transport state or reload a song.
            if (_commandRunning && !string.IsNullOrWhiteSpace(_optimisticPlaybackCommand))
                return;

            var completedPoll = current?.song != null && current.id == _completedQueueEntryId &&
                string.Equals(current.startedAt, _completedStartedAt, StringComparison.Ordinal);
            if (completedPoll)
            {
                _idleMusic.SetIdle(true);
                _status = StageLocale.Text("Titel beendet – warte auf Warteliste …",
                    "Song finished – waiting for queue …");
                return;
            }
            if (current?.song == null || current.id != _completedQueueEntryId ||
                !string.Equals(current.startedAt, _completedStartedAt, StringComparison.Ordinal))
            {
                _completedQueueEntryId = null;
                _completedStartedAt = null;
            }

            var playbackChanged = current?.song != null &&
                (_loadedQueueEntryId != current.id ||
                 !string.Equals(_loadedStartedAt, current.startedAt, StringComparison.Ordinal));
            if (current?.song != null && playbackChanged)
                _visuals.BeginSongTransition(current.song, _songTitle, _songArtist);
            var nextEntry = state?.queue is { Length: > 0 } ? state.queue[0] : null;
            await _visuals.SetNextAsync(_server, nextEntry);
            if (sessionGeneration != _sessionGeneration || eventId != _activeEventId) return;
            if (nextEntry?.song != null) _ = _video.PrefetchAsync(_server, nextEntry.song.id);
            if (state == null || current?.song == null)
            {
                _idleMusic.SetIdle(true);
                _loadedSongId = null;
                _video.Stop();
                _lyrics.SetVideoBackground(false);
                _loadedQueueEntryId = null;
                _loadedStartedAt = null;
                if (_audio.HasClip) _audio.Stop();
                _status = "Warteliste bereit – noch kein aktiver Song";
                return;
            }

            _title = $"{current.song.title} · {current.song.artist}";
            _songTitle = current.song.title;
            _songArtist = current.song.artist;
            if (!state.isRunning || state.isPaused)
            {
                if (_audio.IsPlaying) _audio.Pause();
                _status = state.isPaused ? StageLocale.Text("Server pausiert", "Server paused") : StageLocale.Text("Bühne wartet", "Stage waiting");
                return;
            }

            if (!playbackChanged)
            {
                if (_audio.HasEnded && !(_online?.IsListener ?? false))
                {
                    _status = StageLocale.Text("Titel beendet – nächster Song wird gestartet …", "Song finished – starting next song …");
                    _ = CompletePlaybackAndAdvanceAsync();
                    return;
                }
                if (!_audio.IsPlaying) _audio.Resume();
                _status = _audio.Status;
                return;
            }

            var selectedSongId = current.song.id;
            _loadedSongId = selectedSongId;
            _loadedQueueEntryId = current.id;
            _loadedStartedAt = current.startedAt;
            _status = StageLocale.Text("Prüfe vorbereitete Karaoke-Spuren …", "Checking prepared karaoke stems …");
            var lyricsTask = _lyrics.LoadAsync(_server, selectedSongId);
            var coverTask = _visuals.LoadCoverAsync(_server, selectedSongId);
            var videoTask = _video.LoadAsync(_server, selectedSongId);
            var stemsTask = GetStemsAsync(selectedSongId);
            await Task.WhenAll(lyricsTask, coverTask, videoTask, stemsTask);
            if (sessionGeneration != _sessionGeneration || eventId != _activeEventId) return;
            _lyrics.SetVideoBackground(_video.Active);
            var stems = await stemsTask;
            var useStems = PreparedStemsAreUnityCompatible && stems.hasInstrumental;
            var master = useStems
                ? $"{_server}/api/songs/{selectedSongId}/stems/instrumental?format=ogg"
                : $"{_server}/api/songs/{selectedSongId}/audio";
            var vocals = useStems && stems.hasVocals
                ? $"{_server}/api/songs/{selectedSongId}/stems/vocals?format=ogg"
                : null;
            try
            {
                await _audio.PlayAsync(master, vocals);
            }
            catch when (stems.hasInstrumental)
            {
                // Unitys Decoder-Unterstützung für FLAC ist geräteabhängig. Der
                // ARM32-Prototyp muss trotzdem beweisen können, dass Unity-Audio läuft.
                _status = StageLocale.Text("Stem-Decoder nicht verfügbar – MP3-Fallback wird gestartet …", "Stem decoder unavailable – starting MP3 fallback …");
                await _audio.PlayAsync($"{_server}/api/songs/{selectedSongId}/audio", null);
            }
            if (sessionGeneration != _sessionGeneration || eventId != _activeEventId)
            {
                _audio.Stop();
                return;
            }
            _status = _audio.Status;
        }
        catch (Exception exception)
        {
            if (sessionGeneration != _sessionGeneration || eventId != _activeEventId) return;
            if (!_audio.IsPlaying) _idleMusic.SetIdle(true);
            _status = $"Audiofehler: {exception.Message}";
            _loadedSongId = null;
            _video.Stop();
            _lyrics.SetVideoBackground(false);
            _loadedQueueEntryId = null;
            _loadedStartedAt = null;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void HandlePlaybackEnded()
    {
        if (!(_online?.IsListener ?? false)) _ = CompletePlaybackAndAdvanceAsync();
    }

    private async Task CompletePlaybackAndAdvanceAsync()
    {
        if (_autoAdvanceRunning || string.IsNullOrWhiteSpace(_loadedQueueEntryId)) return;
        _autoAdvanceRunning = true;
        var completedEntryId = _loadedQueueEntryId;
        var completedStartedAt = _loadedStartedAt;
        try
        {
            _status = StageLocale.Text("Titel beendet – nächster Song wird gestartet …", "Song finished – starting next song …");
            await ClaimControlAsync(force: true);
            if (!_ownsControl)
            {
                _status = StageLocale.Text("Titel beendet – warte auf Bühnensteuerung …", "Song finished – waiting for stage control …");
                return;
            }

            var payload = $"{{\"queueEntryId\":\"{completedEntryId}\"}}";
            using var request = CreateJsonPost(StageUrl("/api/playback/complete"), payload, true);
            await request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                _status = $"{StageLocale.Text("Automatischer Wechsel fehlgeschlagen", "Automatic transition failed")} ({request.responseCode})";
                return;
            }

            // Complete is idempotent on the server. Clearing the local playback
            // identity makes consecutive requests for the same song reload too.
            if (_loadedQueueEntryId == completedEntryId)
            {
                _completedQueueEntryId = completedEntryId;
                _completedStartedAt = completedStartedAt;
                _loadedSongId = null;
                _loadedQueueEntryId = null;
                _loadedStartedAt = null;
            }
            await RefreshQueueAsync();
        }
        catch (Exception exception)
        {
            _status = $"{StageLocale.Text("Automatischer Wechsel fehlgeschlagen", "Automatic transition failed")}: {exception.Message}";
        }
        finally { _autoAdvanceRunning = false; }
    }

    private async Task PollReactionsAsync()
    {
        if (_reactionPolling || string.IsNullOrWhiteSpace(_activeEventId)) return;
        _reactionPolling = true;
        try
        {
            using var request = UnityWebRequest.Get($"{_server}/api/events/{_activeEventId}/reactions?after={_lastReactionId}");
            await request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success) return;
            var result = JsonUtility.FromJson<StageReactionListDto>(request.downloadHandler.text);
            foreach (var reaction in result?.items ?? Array.Empty<StageReactionDto>())
            {
                _lastReactionId = Math.Max(_lastReactionId, reaction.id);
                _reactions.Spawn(reaction.type);
            }
        }
        finally { _reactionPolling = false; }
    }

    private async Task<StemAvailabilityDto> GetStemsAsync(string songId)
    {
        using var request = UnityWebRequest.Get($"{_server}/api/songs/{songId}/stems");
        await request.SendWebRequest();
        return request.result == UnityWebRequest.Result.Success
            ? JsonUtility.FromJson<StemAvailabilityDto>(request.downloadHandler.text) ?? new StemAvailabilityDto()
            : new StemAvailabilityDto();
    }

    private string StageUrl(string path) => string.IsNullOrWhiteSpace(_activeEventId)
        ? $"{_server}{path}"
        : $"{_server}{path}?eventId={UnityWebRequest.EscapeURL(_activeEventId)}";

    private async Task LoadLauncherStagesAsync()
    {
        if (_launcherLoading || Time.unscaledTime < _nextLauncherRefresh) return;
        _launcherLoading = true;
        _nextLauncherRefresh = Time.unscaledTime + 5f;
        try
        {
            using var request = UnityWebRequest.Get($"{_server}/api/stages");
            request.timeout = 5;
            await request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                if (request.responseCode == 404 && await LoadLegacyDirectStageAsync())
                {
                    _status = StageLocale.Text("Direkt-Stage bereit · Server bitte aktualisieren",
                        "Direct Stage ready · please update the server");
                    return;
                }
                _status = StageLocale.Text("Stage-Liste konnte nicht geladen werden", "Could not load stages");
                return;
            }
            var result = JsonUtility.FromJson<StageLauncherListDto>(request.downloadHandler.text);
            _launcherStages = result?.items ?? Array.Empty<StageLauncherDto>();
            foreach (var stage in _launcherStages)
                if (_launcherImages.TryGetValue(stage.id, out var cached)) stage.image = cached;
                else if (stage.hasImage) _ = LoadLauncherImageAsync(stage);
            _status = StageLocale.Text("Stage auswählen", "Select a stage");
        }
        finally { _launcherLoading = false; }
    }

    private async Task<bool> LoadLegacyDirectStageAsync()
    {
        using var request = UnityWebRequest.Get($"{_server}/api/events/active");
        request.timeout = 5;
        await request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success) return false;
        var active = JsonUtility.FromJson<KaraokeEventDto>(request.downloadHandler.text);
        if (active == null || string.IsNullOrWhiteSpace(active.id)) return false;
        _launcherStages = new[]
        {
            new StageLauncherDto
            {
                id = active.id,
                name = string.IsNullOrWhiteSpace(active.name) ? "Direkt-Stage" : active.name,
                inviteToken = active.inviteToken,
                isDirect = true,
                isOnline = active.isOnline,
                hasOnlinePassword = active.hasOnlinePassword,
                stageThemeId = active.stageThemeId
            }
        };
        return true;
    }

    private async Task LoadLauncherImageAsync(StageLauncherDto stage)
    {
        using var request = UnityWebRequestTexture.GetTexture($"{_server}/api/events/{stage.id}/image", true);
        await request.SendWebRequest();
        if (request.result == UnityWebRequest.Result.Success)
        {
            stage.image = DownloadHandlerTexture.GetContent(request);
            if (stage.image != null) _launcherImages[stage.id] = stage.image;
        }
    }

    private void SelectLauncherStage(StageLauncherDto stage)
    {
        _sessionGeneration++;
        _idleMusic.SetIdle(true);
        _activeEventId = stage.id;
        _activeEventOnline = stage.isOnline;
        _hasActiveSession = true;
        _lastReactionId = 0;
        _visuals.SetSessionActive(true);
        _visuals.SetStageTheme(stage.stageThemeId);
        _lyrics.SetStageTheme(stage.stageThemeId);
        _songTitle = stage.name;
        _songArtist = stage.isDirect
            ? StageLocale.Text("DIREKTE BÜHNE", "DIRECT STAGE")
            : StageLocale.Text("SESSION BEREIT", "SESSION READY");
        _status = stage.isOnline
            ? StageLocale.Text("Online-Stage gewählt – jetzt online verbinden", "Online stage selected – connect now")
            : StageLocale.Text("Stage bereit", "Stage ready");
        _online?.ConfigureLauncherStage(stage.id, stage.isOnline);
        _ = _visuals.LoadQrAsync(_server, _controllerId, _activeEventId, force: true);
        _ = ClaimControlAsync();
        _ = RefreshQueueAsync();
    }

    private void HandleStageLeaveRequested() => _ = ReturnToLauncherAsync();

    private async Task ReturnToLauncherAsync()
    {
        if (_leavingSession || !_hasActiveSession) return;
        _leavingSession = true;
        _sessionGeneration++;
        var eventId = _activeEventId;
        try
        {
            if (_ownsControl && !string.IsNullOrWhiteSpace(eventId))
            {
                using var release = UnityWebRequest.Delete(
                    $"{_server}/api/playback/controller?eventId={UnityWebRequest.EscapeURL(eventId)}");
                release.SetRequestHeader("X-Karaoke-Controller", _controllerId);
                release.timeout = 3;
                await release.SendWebRequest();
            }
            if (_online != null) await _online.LeaveStageAsync();
        }
        catch (Exception exception)
        {
            Debug.LogWarning("[Stage] cleanup while leaving failed: " + exception.Message);
        }
        finally
        {
            _audio.Stop();
            _idleMusic.SetIdle(true);
            _audio.LocalOutputMuted = false;
            _video.Stop();
            _lyrics.Clear();
            _lyrics.SetVideoBackground(false);
            _reactions.Clear();
            _visuals.SetVideoPerformanceMode(false);
            _visuals.SetSessionActive(false);
            _playbackCommands.Clear();
            _optimisticPlaybackCommand = null;
            _ownsControl = false;
            _activeEventId = null;
            _activeEventOnline = false;
            _hasActiveSession = false;
            _loadedSongId = null;
            _loadedQueueEntryId = null;
            _loadedStartedAt = null;
            _completedQueueEntryId = null;
            _completedStartedAt = null;
            _lastReactionId = 0;
            _songTitle = "NEON STAGE";
            _songArtist = StageLocale.Text("STAGE-AUSWAHL", "STAGE SELECT");
            _status = StageLocale.Text("Stage auswählen", "Select a stage");
            _launcherPage = 0;
            _nextLauncherRefresh = 0;
            _leavingSession = false;
            _ = LoadLauncherStagesAsync();
        }
    }

    private async Task ClaimControlAsync(bool force = false)
    {
        var payload = JsonUtility.ToJson(new PlaybackControllerRequestDto
        {
            clientId = _controllerId,
            clientName = $"Neon Stage Unity ({SystemInfo.deviceName})",
            force = force
        });
        using var request = CreateJsonPost(StageUrl("/api/playback/controller/claim"), payload, false);
        request.timeout = 4;
        await request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success)
        {
            _ownsControl = false;
            return;
        }
        var result = JsonUtility.FromJson<PlaybackControllerDto>(request.downloadHandler.text);
        _ownsControl = result?.ownsControl == true;
    }

    private void RequestPlaybackCommand(string command)
    {
        if (_online?.IsListener == true) return;
        // Give mouse/touch input immediate audible feedback. Network ownership
        // and persistence are processed in order below; clicks are no longer
        // silently discarded while a previous request is running.
        ApplyOptimisticPlaybackCommand(command);
        _playbackCommands.Enqueue(command);
        if (!_commandRunning) _ = DrainPlaybackCommandsAsync();
    }

    private void ApplyOptimisticPlaybackCommand(string command)
    {
        _optimisticPlaybackCommand = command;
        if (command == "pause" && _audio.HasClip) _audio.Pause();
        else if (command == "resume" && _audio.HasClip) _audio.Resume();
        else if ((command == "next" || command == "previous") && _audio.HasClip) _audio.Pause();
    }

    private async Task DrainPlaybackCommandsAsync()
    {
        _commandRunning = true;
        try
        {
            // Ein Knopf direkt auf der Bühne ist eine bewusste Übernahme. Damit
            // blockiert eine im Hintergrund laufende Editor-Lease die Stage nicht.
            await ClaimControlAsync(force: true);
            if (!_ownsControl)
            {
                _status = StageLocale.Text("Bühnensteuerung konnte nicht übernommen werden", "Could not take control of the stage");
                return;
            }

            while (_playbackCommands.Count > 0)
            {
                var command = _playbackCommands.Dequeue();
                _optimisticPlaybackCommand = command;
                using var request = CreateJsonPost(StageUrl($"/api/playback/{command}"), "{}", true);
                request.timeout = 5;
                await request.SendWebRequest();
                if (request.result == UnityWebRequest.Result.Success) continue;

                _ownsControl = false;
                _playbackCommands.Clear();
                _status = request.responseCode == 409
                    ? StageLocale.Text("Eine andere App steuert gerade die Bühne", "Another app is controlling the stage")
                    : $"{StageLocale.Text("Steuerfehler", "Control error")} {request.responseCode}: {request.error}";
                return;
            }
        }
        catch (Exception exception)
        {
            _status = $"{StageLocale.Text("Steuerfehler", "Control error")}: {exception.Message}";
            _playbackCommands.Clear();
        }
        finally
        {
            _optimisticPlaybackCommand = null;
            _commandRunning = false;
        }

        // Reconcile once after all queued clicks. A poll that was already in
        // progress is allowed to finish first, then the authoritative state is
        // fetched without making the controls unresponsive meanwhile.
        for (var attempt = 0; _refreshing && attempt < 40; attempt++)
            await Task.Delay(25);
        await RefreshQueueAsync();
    }

    private async Task SeekAsync(double seconds)
    {
        if (_online?.IsListener == true || _commandRunning || string.IsNullOrWhiteSpace(_loadedSongId)) return;
        _audio.Seek(seconds);
        _commandRunning = true;
        try
        {
            await ClaimControlAsync(force: true);
            if (!_ownsControl)
            {
                _status = StageLocale.Text("Bühnensteuerung konnte nicht übernommen werden", "Could not take control of the stage");
                return;
            }
            var entryId = await GetCurrentEntryIdAsync();
            if (string.IsNullOrWhiteSpace(entryId)) return;
            var time = TimeSpan.FromSeconds(seconds);
            var payload = $"{{\"queueEntryId\":\"{entryId}\",\"position\":\"{time:c}\"}}";
            using var request = CreateJsonPost(StageUrl("/api/playback/position"), payload, true);
            await request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success) _status = $"Seek fehlgeschlagen ({request.responseCode})";
        }
        finally { _commandRunning = false; }
    }

    private async Task<string?> GetCurrentEntryIdAsync()
    {
        using var request = UnityWebRequest.Get(StageUrl("/api/queue"));
        await request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success) return null;
        return JsonUtility.FromJson<PlaybackStateDto>(request.downloadHandler.text)?.current?.id;
    }

    private UnityWebRequest CreateJsonPost(string url, string json, bool authenticated)
    {
        var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST);
        request.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        if (authenticated) request.SetRequestHeader("X-Karaoke-Controller", _controllerId);
        request.timeout = 8;
        return request;
    }

    private Task ExitStageAsync()
    {
        if (_exiting) return Task.CompletedTask;
        _exiting = true;
        _idleMusic.SetIdle(false, immediate: true);
        _audio.Stop();
        Application.Quit();
        return Task.CompletedTask;
    }

    private void OnGUI()
    {
        EnsureControlTextures();
        // Keep the visual reference layout, but never let a transformed
        // IMGUI matrix handle pointer hit-testing. On Linux a borderless
        // fullscreen window on a mixed-DPI multi-monitor desktop can receive
        // pointer coordinates in the player surface while GUI.matrix applies
        // another scale to its controls. The result looks correct but clicks
        // land beside (or on the wrong) button. Interactive controls below are
        // therefore drawn in identity screen space with explicitly scaled
        // rectangles.
        var scale = Mathf.Max(1f, Mathf.Min(Screen.width / 1280f, Screen.height / 720f));
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1));
        var width = Screen.width / scale;
        var height = Screen.height / scale;
        if (!_hasActiveSession)
        {
            DrawSessionLauncher(width, height);
            DrawSettingsLauncherButton(width, height, scale);
            if (_settingsOpen) DrawSettingsPanel(scale);
            return;
        }
        if (!_editorTestMode && DrawStageSelectionButton(scale)) _ = ReturnToLauncherAsync();
        GUI.color = Color.white;
        if (!_visuals.IsSongTransitioning)
        {
            DrawArcadeTitle(new Rect(118, 12, width - 386, 52), _songTitle);
            var artistStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                alignment = TextAnchor.MiddleCenter,
                fontStyle = FontStyle.BoldAndItalic
            };
            artistStyle.normal.textColor = new Color(.92f, .86f, .97f);
            GUI.Label(new Rect(118, 59, width - 386, 27), _songArtist, artistStyle);
        }
        var controlsY = height - 158;

        GUI.color = new Color(0.055f, 0.018f, 0.085f, 0.94f);
        GUI.Box(new Rect(0, controlsY, width, 158), GUIContent.none);
        DrawSolid(new Rect(0, controlsY, width, 2), new Color(1f, .2f, .72f, .55f));
        GUI.color = Color.white;
        if (_online?.IsListener == true)
        {
            var listenerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 19,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            listenerStyle.normal.textColor = new Color(.72f, .82f, .9f);
            GUI.Label(new Rect(40, controlsY + 5, width - 80, 32),
                _online.IsConversationActive
                    ? StageLocale.Text("PAUSEN-GESPRÄCH · DEIN MIKROFON IST FREIGEGEBEN",
                        "PAUSE CONVERSATION · YOUR MICROPHONE IS LIVE")
                    : StageLocale.Text("ONLINE-ZUHÖRER · Steuerung am Sänger-Standort",
                        "ONLINE LISTENER · Controls are available at the singer location"), listenerStyle);
            var listenerInfo = new GUIStyle(listenerStyle) { fontSize = 13, fontStyle = FontStyle.Normal };
            listenerInfo.normal.textColor = new Color(.64f, .55f, .7f);
            GUI.Label(new Rect(40, controlsY + 128, width - 80, 24),
                $"{_clock.Capture().PositionSeconds:0.0}s · LiveKit A/V-Sync {_online.RemoteLatencySeconds * 1000:0} ms · {_server}",
                listenerInfo);

            var mixLabel = new GUIStyle(listenerInfo)
            {
                fontSize = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            mixLabel.normal.textColor = new Color(.86f, .8f, .92f);
            var listenerMatrix = GUI.matrix;
            GUI.matrix = Matrix4x4.identity;
            var musicLabelRect = ToScreenRect(new Rect(50, controlsY + 49, 105, 24), scale);
            var musicSliderRect = ToScreenRect(new Rect(155, controlsY + 46, 225, 30), scale);
            var rightX = Mathf.Max(430, width - 455);
            var vocalLabelRect = ToScreenRect(new Rect(rightX, controlsY + 42, 130, 24), scale);
            var vocalSliderRect = ToScreenRect(new Rect(rightX + 135, controlsY + 39, 245, 30), scale);
            var microphoneLabelRect = ToScreenRect(new Rect(rightX, controlsY + 87, 130, 24), scale);
            var microphoneSliderRect = ToScreenRect(new Rect(rightX + 135, controlsY + 84, 245, 30), scale);
            var screenMixLabel = ScaleInteractiveStyle(mixLabel, scale);
            if (_online.ListenerMixLocked)
            {
                _musicVolume = .85f;
                _vocalVolume = .35f;
                _microphoneVolume = _online.LockedMicrophoneVolume;
                GUI.Label(ToScreenRect(new Rect(50, controlsY + 24, width - 100, 20), scale),
                    StageLocale.Text("Mix wird vom Singer vorgegeben", "Mix is controlled by the singer"),
                    screenMixLabel);
                GUI.enabled = false;
            }
            GUI.Label(musicLabelRect, StageLocale.Text("MUSIK", "MUSIC"), screenMixLabel);
            _musicVolume = DrawNeonSlider("listener-music-volume", musicSliderRect,
                _musicVolume, new Color(.87f, 1f, .05f), scale);
            GUI.Label(vocalLabelRect, StageLocale.Text("ORIGINALVOCALS", "ORIGINAL VOCALS"), screenMixLabel);
            _vocalVolume = DrawNeonSlider("listener-vocal-volume", vocalSliderRect,
                _vocalVolume, new Color(1f, .25f, .75f), scale);
            GUI.Label(microphoneLabelRect, StageLocale.Text("LIVE-MIKRO", "LIVE MICROPHONE"), screenMixLabel);
            _microphoneVolume = DrawNeonSlider("listener-microphone-volume", microphoneSliderRect,
                _microphoneVolume, new Color(.25f, .85f, 1f), scale);
            GUI.enabled = true;
            _online.SetListenerMix(_musicVolume, _vocalVolume, _microphoneVolume);
            GUI.matrix = listenerMatrix;
            DrawOnlineGui();
            return;
        }
        var buttonStyle = new GUIStyle(GUI.skin.button)
        {
            fontSize = 28,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
            border = new RectOffset(10, 10, 10, 10)
        };
        buttonStyle.normal.background = _controlButton;
        buttonStyle.hover.background = _controlButtonHover;
        buttonStyle.active.background = _controlButtonActive;
        buttonStyle.normal.textColor = new Color(.96f, .92f, 1f);
        buttonStyle.hover.textColor = new Color(.87f, 1f, .05f);
        buttonStyle.active.textColor = new Color(1f, .25f, .75f);
        const float buttonSize = 58;
        const float buttonGap = 10;
        var buttonsX = width * .5f - (buttonSize * 4 + buttonGap * 3) * .5f;
        var buttonsY = controlsY + 62;
        DrawIconBadge(new Rect(48, buttonsY + 5, 44, 44), new Color(.87f, 1f, .05f));
        DrawMusicIcon(new Rect(55, buttonsY + 10, 32, 32), new Color(0.87f, 1f, 0.05f));
        DrawIconBadge(new Rect(width - 94, buttonsY + 5, 44, 44), new Color(1f, .25f, .75f));
        DrawMicrophoneIcon(new Rect(width - 87, buttonsY + 9, 30, 34), new Color(1f, 0.25f, 0.75f));
        var layoutMatrix = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
        var screenButtonStyle = ScaleInteractiveStyle(buttonStyle, scale);
        if (DrawPointerButton(ToScreenRect(new Rect(buttonsX, buttonsY, buttonSize, buttonSize), scale), "|◀", screenButtonStyle, "previous")) RequestPlaybackCommand("previous");
        if (DrawPointerButton(ToScreenRect(new Rect(buttonsX + buttonSize + buttonGap, buttonsY, buttonSize, buttonSize), scale), "Ⅱ", screenButtonStyle, "pause")) RequestPlaybackCommand("pause");
        if (DrawPointerButton(ToScreenRect(new Rect(buttonsX + (buttonSize + buttonGap) * 2, buttonsY, buttonSize, buttonSize), scale), "▶", screenButtonStyle, "resume")) RequestPlaybackCommand("resume");
        if (DrawPointerButton(ToScreenRect(new Rect(buttonsX + (buttonSize + buttonGap) * 3, buttonsY, buttonSize, buttonSize), scale), "▶|", screenButtonStyle, "next")) RequestPlaybackCommand("next");

        _musicVolume = DrawNeonSlider("music-volume",
            ToScreenRect(new Rect(100, buttonsY + 17, 220, 30), scale),
            _musicVolume, new Color(.87f, 1f, .05f), scale);
        _audio.MusicVolume = _musicVolume;
        _vocalVolume = DrawNeonSlider("vocal-volume",
            ToScreenRect(new Rect(width - 320, buttonsY + 17, 220, 30), scale),
            _vocalVolume, new Color(1f, .25f, .75f), scale);
        _audio.VocalVolume = _vocalVolume;

        if (_audio.HasClip)
        {
            var oldPosition = (float)_audio.PositionSeconds;
            var normalized = _audio.DurationSeconds > 0 ? oldPosition / (float)_audio.DurationSeconds : 0;
            var newNormalized = DrawProgressTimeline(ToScreenRect(
                new Rect(64, controlsY + 14, width - 128, 34), scale), normalized,
                scale, out var seekReleased);
            var newPosition = newNormalized * (float)_audio.DurationSeconds;
            if (seekReleased && Math.Abs(newPosition - oldPosition) > 0.5f)
                _ = SeekAsync(newPosition);
        }
        GUI.matrix = layoutMatrix;
        var infoStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
        infoStyle.normal.textColor = new Color(0.64f, 0.55f, 0.7f);
        var syncMode = _audio.UsesAutomaticOutputLatency ? "Auto" : "Fix";
        GUI.Label(new Rect(40, height - 29, width - 80, 22),
            $"{_audio.PositionSeconds:0.0}s  ·  Lyrics-Sync {syncMode} {_audio.AppliedOutputLatencySeconds * 1000:0} ms  ·  {(_ownsControl ? "Steuerung aktiv" : "nur Anzeige")}  ·  {_server}", infoStyle);
        DrawOnlineGui();
    }

    private void DrawOnlineGui()
    {
        if (_online == null) return;
        var matrix = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
        var scale = Mathf.Max(1f, Mathf.Min(Screen.width / 1280f, Screen.height / 720f));
        _online.DrawGui(Screen.width, scale,
            rect => Event.current.type == EventType.Repaint && _pointer.ConsumeClick(rect));
        GUI.matrix = matrix;
    }

    private void DrawSessionLauncher(float width, float height)
    {
        var heading = new GUIStyle(GUI.skin.label) { fontSize = 42, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        heading.normal.textColor = new Color(.87f, 1f, .05f);
        if (_wordmark != null)
            GUI.DrawTexture(new Rect(width * .5f - 250, 3, 500, 110),
                _wordmark, ScaleMode.ScaleToFit, true);
        else
            GUI.Label(new Rect(40, 28, width - 80, 66), "NEON STAGE", heading);
        var sub = new GUIStyle(heading) { fontSize = 18, fontStyle = FontStyle.Normal };
        sub.normal.textColor = new Color(1f, .3f, .78f);
        DrawArcadeLabel(new Rect(40, 104, width - 80, 34),
            StageLocale.Text("WÄHLE DEINE BÜHNE", "SELECT YOUR STAGE"),
            14, 24, 1.35f, 3f);
        var button = new GUIStyle(GUI.skin.button) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.LowerCenter, wordWrap = true };
        button.normal.background = _controlButtonHover;
        button.hover.background = _controlButtonActive;
        button.active.background = _controlButtonActive;
        button.normal.textColor = new Color(.94f, 1f, .72f);
        var columns = Mathf.Clamp(Mathf.FloorToInt((width - 80) / 280), 1, 4);
        var cardWidth = Mathf.Min(250, (width - 80 - (columns - 1) * 20) / columns);
        const float cardHeight = 205;
        var rows = Mathf.Max(1, Mathf.FloorToInt((height - 190) / (cardHeight + 20)));
        var pageSize = columns * rows;
        var pageCount = Mathf.Max(1, Mathf.CeilToInt(_launcherStages.Length / (float)pageSize));
        _launcherPage = Mathf.Clamp(_launcherPage, 0, pageCount - 1);
        var firstStage = _launcherPage * pageSize;
        var lastStage = Mathf.Min(_launcherStages.Length, firstStage + pageSize);
        var totalWidth = columns * cardWidth + (columns - 1) * 20;
        var startX = (width - totalWidth) * .5f;
        for (var index = firstStage; index < lastStage; index++)
        {
            var stage = _launcherStages[index];
            var pageIndex = index - firstStage;
            var row = pageIndex / columns;
            var column = pageIndex % columns;
            var card = new Rect(startX + column * (cardWidth + 20), 148 + row * (cardHeight + 20), cardWidth, cardHeight);
            var screenCard = ToScreenRect(card, Mathf.Max(1f, Mathf.Min(Screen.width / 1280f, Screen.height / 720f)));
            var hovered = screenCard.Contains(_pointer.Position);
            DrawLauncherCardFrame(card, stage.isDirect, stage.isOnline, hovered);
            var imageRect = new Rect(card.x + 9, card.y + 9, card.width - 18, 133);
            if (stage.image != null) GUI.DrawTexture(imageRect, stage.image, ScaleMode.ScaleAndCrop, true);
            else
            {
                var old = GUI.color;
                GUI.color = stage.isDirect ? new Color(.87f, 1f, .05f, .22f) : new Color(1f, .24f, .74f, .2f);
                GUI.DrawTexture(imageRect, _pill!, ScaleMode.StretchToFill, true);
                GUI.color = old;
                var glyph = new GUIStyle(heading) { fontSize = 46 };
                GUI.Label(imageRect, stage.isDirect ? "▶" : "★", glyph);
            }
            var label = stage.isDirect
                ? StageLocale.Text("DIREKT-STAGE", "DIRECT STAGE")
                : stage.name;
            DrawArcadeLabel(new Rect(card.x + 10, card.y + 145, card.width - 20, 38),
                label, 10, 19, 1f, 2.25f, maxLines: 2);
            if (!stage.isDirect)
            {
                var status = stage.isOnline
                    ? StageLocale.Text("ONLINE-BÜHNE", "ONLINE STAGE")
                    : StageLocale.Text("LOKALE BÜHNE", "LOCAL STAGE");
                DrawArcadeLabel(new Rect(card.x + 14, card.y + 181, card.width - 28, 17),
                    status, 8, 11, .7f, 1.4f,
                    stage.isOnline ? new Color(.87f, 1f, .05f) : new Color(.75f, .7f, .82f));
            }
            if (!_settingsOpen && Event.current.type == EventType.Repaint && _pointer.ConsumeClick(screenCard))
                SelectLauncherStage(stage);
        }
        if (pageCount > 1)
        {
            var scale = Mathf.Max(1f, Mathf.Min(Screen.width / 1280f, Screen.height / 720f));
            var navigation = new GUIStyle(button) { fontSize = 16, alignment = TextAnchor.MiddleCenter };
            GUI.enabled = _launcherPage > 0;
            if (!_settingsOpen && DrawScreenSpaceButton(new Rect(32, height - 62, 82, 42), "◀", navigation, scale)) _launcherPage--;
            GUI.enabled = _launcherPage + 1 < pageCount;
            if (!_settingsOpen && DrawScreenSpaceButton(new Rect(width - 114, height - 62, 82, 42), "▶", navigation, scale)) _launcherPage++;
            GUI.enabled = true;
            GUI.Label(new Rect(width * .5f - 80, height - 58, 160, 32),
                $"{_launcherPage + 1} / {pageCount}", sub);
        }
        var hint = new GUIStyle(sub) { fontSize = 14 };
        hint.normal.textColor = new Color(.68f, .6f, .74f);
        if (_launcherLoading || _launcherStages.Length == 0)
            GUI.Label(new Rect(40, height - 70, width - 80, 32),
                _launcherLoading ? StageLocale.Text("Stages werden geladen …", "Loading stages …") : _status, hint);
    }

    private void LoadSettingsDraft()
    {
        _settingsAutomaticMicrophones = StageRuntimeSettings.AutomaticMicrophones;
        _settingsSelectedMicrophones.Clear();
        _settingsSelectedMicrophones.AddRange(StageRuntimeSettings.SelectedMicrophones);
        _settingsServerUrl = _server;
        _settingsLiveKitUrl = StageRuntimeSettings.LiveKitServerOverride;
        _settingsFullscreen = StageRuntimeSettings.Fullscreen;
        _settingsDisplayIndex = StageRuntimeSettings.DisplayIndex;
        _settingsDisplaySelectionChanged = false;
        RefreshSettingsDevices(force: true);
    }

    private void RefreshSettingsDevices(bool force = false)
    {
        if (!force && Time.unscaledTime < _nextSettingsDeviceRefresh) return;
        _nextSettingsDeviceRefresh = Time.unscaledTime + 1.5f;
        _settingsMicrophoneDevices = OnlineBroadcastMixer.SelectMicrophoneDevices(
            Microphone.devices,
            Application.platform is RuntimePlatform.LinuxPlayer or RuntimePlatform.LinuxEditor);
        _settingsDisplays.Clear();
        if (Application.platform != RuntimePlatform.Android) Screen.GetDisplayLayout(_settingsDisplays);
        if (!_settingsDisplaySelectionChanged)
        {
            _settingsDisplayIndex = 0;
            var savedName = StageRuntimeSettings.DisplayName;
            for (var index = 0; index < _settingsDisplays.Count; index++)
                if (!string.IsNullOrWhiteSpace(savedName) &&
                    string.Equals(_settingsDisplays[index].name, savedName, StringComparison.Ordinal))
                {
                    _settingsDisplayIndex = index;
                    break;
                }
        }
        _settingsDisplayIndex = Mathf.Clamp(_settingsDisplayIndex, 0, Mathf.Max(0, _settingsDisplays.Count - 1));
    }

    private void ApplySavedDisplaySettings()
    {
        if (Application.platform == RuntimePlatform.Android) return;
        _settingsDisplays.Clear();
        Screen.GetDisplayLayout(_settingsDisplays);
        if (_settingsDisplays.Count == 0) return;
        var savedName = StageRuntimeSettings.DisplayName;
        var selected = 0;
        if (!string.IsNullOrWhiteSpace(savedName))
            for (var index = 0; index < _settingsDisplays.Count; index++)
                if (string.Equals(_settingsDisplays[index].name, savedName, StringComparison.Ordinal))
                {
                    selected = index;
                    break;
                }

        // A disconnected saved display deliberately falls back to display 0.
        _settingsDisplayIndex = selected;
        var target = _settingsDisplays[selected];
        Screen.MoveMainWindowTo(target, Vector2Int.zero);
        Screen.fullScreenMode = StageRuntimeSettings.Fullscreen
            ? FullScreenMode.FullScreenWindow
            : FullScreenMode.Windowed;
    }

    private void DrawSettingsLauncherButton(float width, float height, float scale)
    {
        var matrix = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
        var rect = ToScreenRect(new Rect(width * .5f - 33, height - 70, 66, 60), scale);
        var hovered = rect.Contains(_pointer.Position);
        DrawIconBadge(rect, hovered ? new Color(.87f, 1f, .05f) : new Color(.12f, .88f, 1f));
        var old = GUI.color;
        GUI.color = hovered ? Color.white : new Color(1f, 1f, 1f, .86f);
        if (_settingsIcon != null)
            GUI.DrawTexture(new Rect(rect.x + 8 * scale, rect.y + 5 * scale,
                rect.width - 16 * scale, rect.height - 10 * scale), _settingsIcon, ScaleMode.ScaleToFit, true);
        GUI.color = old;
        if (Event.current.type == EventType.Repaint && _pointer.ConsumeClick(rect))
        {
            _settingsOpen = !_settingsOpen;
            if (_settingsOpen)
            {
                LoadSettingsDraft();
                _settingsMessage = string.Empty;
            }
        }
        GUI.matrix = matrix;
    }

    private void DrawSettingsPanel(float scale)
    {
        RefreshSettingsDevices();
        var matrix = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
        var panel = new Rect(Screen.width * .1f, Screen.height * .08f,
            Screen.width * .8f, Screen.height * .84f);
        DrawSolid(new Rect(0, 0, Screen.width, Screen.height), new Color(.015f, .005f, .035f, .78f));
        DrawSolid(new Rect(panel.x + 8 * scale, panel.y + 10 * scale, panel.width, panel.height),
            new Color(.35f, .01f, .25f, .76f));
        DrawSolid(panel, new Color(.045f, .018f, .09f, .99f));
        DrawRectOutline(panel, 3 * scale, new Color(.03f, .9f, 1f));
        DrawRectOutline(new Rect(panel.x + 6 * scale, panel.y + 6 * scale,
            panel.width - 12 * scale, panel.height - 12 * scale), scale, new Color(1f, .72f, .08f, .8f));

        var title = new Rect(panel.x + 24 * scale, panel.y + 13 * scale,
            panel.width - 48 * scale, 48 * scale);
        DrawArcadeLabel(title, StageLocale.Text("STAGE-EINSTELLUNGEN", "STAGE SETTINGS"),
            Mathf.RoundToInt(15 * scale), Mathf.RoundToInt(28 * scale), 1.5f * scale, 3f * scale);

        var tabStyle = MakeSettingsButtonStyle(scale, 15);
        var tabs = new[]
        {
            StageLocale.Text("AUDIO", "AUDIO"), StageLocale.Text("VIDEO", "VIDEO"),
            "LIVEKIT", StageLocale.Text("SERVER", "SERVER")
        };
        var tabsY = panel.y + 65 * scale;
        var tabWidth = (panel.width - 48 * scale) / tabs.Length;
        for (var index = 0; index < tabs.Length; index++)
        {
            var rect = new Rect(panel.x + 24 * scale + index * tabWidth, tabsY,
                tabWidth - 7 * scale, 40 * scale);
            if (index == _settingsTab) DrawRectOutline(rect, 2 * scale, new Color(.87f, 1f, .05f));
            if (DrawPointerButton(rect, tabs[index], tabStyle, "settings-tab-" + index)) _settingsTab = index;
        }

        var content = new Rect(panel.x + 34 * scale, panel.y + 119 * scale,
            panel.width - 68 * scale, panel.height - 196 * scale);
        switch (_settingsTab)
        {
            case 0: DrawAudioSettings(content, scale); break;
            case 1: DrawVideoSettings(content, scale); break;
            case 2: DrawLiveKitSettings(content, scale); break;
            default: DrawServerSettings(content, scale); break;
        }

        var footerY = panel.yMax - 62 * scale;
        var footerStyle = MakeSettingsButtonStyle(scale, 14);
        if (DrawPointerButton(new Rect(panel.x + 28 * scale, footerY, 150 * scale, 39 * scale),
                StageLocale.Text("SCHLIESSEN", "CLOSE"), footerStyle, "settings-close"))
            _settingsOpen = false;
        if (DrawPointerButton(new Rect(panel.xMax - 178 * scale, footerY, 150 * scale, 39 * scale),
                StageLocale.Text("SPEICHERN", "SAVE"), footerStyle, "settings-save"))
            SaveStageSettings();
        if (!string.IsNullOrWhiteSpace(_settingsMessage))
        {
            var messageStyle = MakeSettingsLabelStyle(scale, 12, TextAnchor.MiddleCenter);
            messageStyle.normal.textColor = new Color(.87f, 1f, .05f);
            GUI.Label(new Rect(panel.x + 190 * scale, footerY, panel.width - 380 * scale, 39 * scale),
                _settingsMessage, messageStyle);
        }
        GUI.matrix = matrix;
    }

    private void DrawAudioSettings(Rect content, float scale)
    {
        var heading = MakeSettingsLabelStyle(scale, 18, TextAnchor.MiddleLeft, true);
        var label = MakeSettingsLabelStyle(scale, 13, TextAnchor.MiddleLeft);
        GUI.Label(new Rect(content.x, content.y, content.width, 28 * scale),
            StageLocale.Text("MIKROFON-EINGÄNGE", "MICROPHONE INPUTS"), heading);
        var button = MakeSettingsButtonStyle(scale, 13);
        var automaticRect = new Rect(content.x, content.y + 34 * scale, content.width, 34 * scale);
        if (DrawPointerButton(automaticRect,
                (_settingsAutomaticMicrophones ? "●  " : "○  ") +
                StageLocale.Text("Automatisch – bis zu zwei echte Mikrofone", "Automatic – up to two real microphones"),
                button, "settings-microphone-auto"))
            _settingsAutomaticMicrophones = !_settingsAutomaticMicrophones;

        var y = content.y + 76 * scale;
        foreach (var device in _settingsMicrophoneDevices)
        {
            if (y + 33 * scale > content.yMax - 70 * scale) break;
            var selected = _settingsSelectedMicrophones.Contains(device);
            var deviceRect = new Rect(content.x, y, content.width, 30 * scale);
            GUI.enabled = !_settingsAutomaticMicrophones;
            if (DrawPointerButton(deviceRect, (selected ? "✓  " : "○  ") + device,
                    button, "settings-microphone-" + device)) ToggleSettingsMicrophone(device);
            GUI.enabled = true;
            y += 34 * scale;
        }
        if (_settingsMicrophoneDevices.Length == 0)
            GUI.Label(new Rect(content.x, y, content.width, 28 * scale),
                StageLocale.Text("Kein Mikrofon angeschlossen", "No microphone connected"), label);

        GUI.Label(new Rect(content.x, content.yMax - 62 * scale, content.width, 25 * scale),
            StageLocale.Text("AUDIO-AUSGANG", "AUDIO OUTPUT"), heading);
        GUI.Label(new Rect(content.x, content.yMax - 34 * scale, content.width, 28 * scale),
            StageLocale.Text("Systemstandard · Änderungen in PipeWire/Windows/macOS werden live übernommen",
                "System default · PipeWire/Windows/macOS changes are picked up live"), label);
    }

    private void DrawVideoSettings(Rect content, float scale)
    {
        var heading = MakeSettingsLabelStyle(scale, 18, TextAnchor.MiddleLeft, true);
        var label = MakeSettingsLabelStyle(scale, 13, TextAnchor.MiddleLeft);
        GUI.Label(new Rect(content.x, content.y, content.width, 28 * scale),
            StageLocale.Text("ZIELMONITOR", "TARGET DISPLAY"), heading);
        if (Application.platform == RuntimePlatform.Android)
        {
            GUI.Label(new Rect(content.x, content.y + 40 * scale, content.width, 60 * scale),
                StageLocale.Text("Android verwendet weiterhin den gespiegelten Systemausgang.",
                    "Android continues to use the mirrored system output."), label);
            return;
        }

        var button = MakeSettingsButtonStyle(scale, 13);
        var y = content.y + 38 * scale;
        for (var index = 0; index < _settingsDisplays.Count; index++)
        {
            var display = _settingsDisplays[index];
            var selected = index == _settingsDisplayIndex;
            var text = $"{(selected ? "●" : "○")}  {index + 1}: {display.name}  ·  {display.width}×{display.height}";
            if (DrawPointerButton(new Rect(content.x, y, content.width, 34 * scale),
                    text, button, "settings-display-" + index))
            {
                _settingsDisplayIndex = index;
                _settingsDisplaySelectionChanged = true;
            }
            y += 38 * scale;
        }
        if (DrawPointerButton(new Rect(content.x, content.yMax - 48 * scale, content.width, 36 * scale),
                (_settingsFullscreen ? "✓  " : "○  ") +
                StageLocale.Text("Fullscreen auf diesem Monitor", "Fullscreen on this display"),
                button, "settings-fullscreen")) _settingsFullscreen = !_settingsFullscreen;
        GUI.Label(new Rect(content.x, content.yMax - 82 * scale, content.width, 27 * scale),
            StageLocale.Text("Fehlt der gespeicherte Monitor, startet die Stage auf dem Hauptdisplay.",
                "If the saved display is missing, the Stage starts on the primary display."), label);
    }

    private void DrawLiveKitSettings(Rect content, float scale)
    {
        var heading = MakeSettingsLabelStyle(scale, 18, TextAnchor.MiddleLeft, true);
        var label = MakeSettingsLabelStyle(scale, 13, TextAnchor.UpperLeft);
        GUI.Label(new Rect(content.x, content.y, content.width, 28 * scale), "LIVEKIT SIGNALING", heading);
        GUI.Label(new Rect(content.x, content.y + 33 * scale, content.width, 44 * scale),
            StageLocale.Text("Leer lassen: Die vom NeonStage-Server gelieferte URL verwenden.",
                "Leave empty to use the URL supplied by the NeonStage server."), label);
        _settingsLiveKitUrl = GUI.TextField(new Rect(content.x, content.y + 82 * scale,
            content.width, 39 * scale), _settingsLiveKitUrl, MakeSettingsTextFieldStyle(scale));
        GUI.Label(new Rect(content.x, content.y + 133 * scale, content.width, 78 * scale),
            StageLocale.Text("Nur ws:// oder wss://. API-Key und Secret bleiben ausschließlich auf dem Server.",
                "ws:// or wss:// only. API key and secret remain exclusively on the server."), label);
    }

    private void DrawServerSettings(Rect content, float scale)
    {
        var heading = MakeSettingsLabelStyle(scale, 18, TextAnchor.MiddleLeft, true);
        var label = MakeSettingsLabelStyle(scale, 13, TextAnchor.UpperLeft);
        GUI.Label(new Rect(content.x, content.y, content.width, 28 * scale),
            StageLocale.Text("NEONSTAGE-SERVER", "NEONSTAGE SERVER"), heading);
        GUI.Label(new Rect(content.x, content.y + 34 * scale, content.width, 35 * scale),
            StageLocale.Text("HTTP-/HTTPS-Adresse der Bibliothek und Stage-Verwaltung",
                "HTTP/HTTPS address of the library and stage management"), label);
        _settingsServerUrl = GUI.TextField(new Rect(content.x, content.y + 78 * scale,
            content.width, 39 * scale), _settingsServerUrl, MakeSettingsTextFieldStyle(scale));
    }

    private void ToggleSettingsMicrophone(string device)
    {
        if (_settingsSelectedMicrophones.Remove(device)) return;
        if (_settingsSelectedMicrophones.Count >= 2) _settingsSelectedMicrophones.RemoveAt(0);
        _settingsSelectedMicrophones.Add(device);
    }

    private void SaveStageSettings()
    {
        if (!TryNormalizeServer(_settingsServerUrl, out var server))
        {
            _settingsMessage = StageLocale.Text("Ungültige Server-Adresse", "Invalid server address");
            return;
        }
        var liveKit = _settingsLiveKitUrl.Trim().TrimEnd('/');
        if (liveKit.Length > 0 && (!Uri.TryCreate(liveKit, UriKind.Absolute, out var liveKitUri) ||
                                   liveKitUri.Scheme is not ("ws" or "wss")))
        {
            _settingsMessage = StageLocale.Text("LiveKit benötigt ws:// oder wss://", "LiveKit requires ws:// or wss://");
            return;
        }

        StageRuntimeSettings.AutomaticMicrophones = _settingsAutomaticMicrophones;
        StageRuntimeSettings.SelectedMicrophones = _settingsSelectedMicrophones.ToArray();
        StageRuntimeSettings.LiveKitServerOverride = liveKit;
        StageRuntimeSettings.Fullscreen = _settingsFullscreen;
        StageRuntimeSettings.DisplayIndex = _settingsDisplayIndex;
        if (_settingsDisplays.Count > _settingsDisplayIndex &&
            (_settingsDisplaySelectionChanged || string.IsNullOrWhiteSpace(StageRuntimeSettings.DisplayName)))
            StageRuntimeSettings.DisplayName = _settingsDisplays[_settingsDisplayIndex].name;
        PlayerPrefs.SetString("NeonStage.Server", server);
        PlayerPrefs.Save();

        var serverChanged = !string.Equals(_server, server, StringComparison.OrdinalIgnoreCase);
        _server = server;
        _online?.ConfigureServer(_server);
        ApplySavedDisplaySettings();
        if (serverChanged) _ = LoadLauncherStagesAsync();
        _settingsMessage = StageLocale.Text("Gespeichert und angewendet", "Saved and applied");
    }

    private static GUIStyle MakeSettingsLabelStyle(float scale, int size,
        TextAnchor alignment, bool bold = false)
    {
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = Mathf.RoundToInt(size * scale), alignment = alignment,
            fontStyle = bold ? FontStyle.BoldAndItalic : FontStyle.Normal,
            wordWrap = true
        };
        style.normal.textColor = bold ? new Color(1f, .3f, .78f) : new Color(.9f, .86f, .96f);
        return style;
    }

    private GUIStyle MakeSettingsButtonStyle(float scale, int size)
    {
        var style = new GUIStyle(GUI.skin.button)
        {
            fontSize = Mathf.RoundToInt(size * scale), alignment = TextAnchor.MiddleLeft,
            fontStyle = FontStyle.Bold, padding = new RectOffset(
                Mathf.RoundToInt(12 * scale), Mathf.RoundToInt(8 * scale), 0, 0)
        };
        style.normal.background = _controlButton;
        style.hover.background = _controlButtonHover;
        style.active.background = _controlButtonActive;
        style.normal.textColor = new Color(.94f, .9f, 1f);
        style.hover.textColor = new Color(.87f, 1f, .05f);
        return style;
    }

    private static GUIStyle MakeSettingsTextFieldStyle(float scale)
    {
        var style = new GUIStyle(GUI.skin.textField)
        {
            fontSize = Mathf.RoundToInt(15 * scale), alignment = TextAnchor.MiddleLeft,
            padding = new RectOffset(Mathf.RoundToInt(12 * scale), Mathf.RoundToInt(12 * scale), 0, 0)
        };
        style.normal.textColor = new Color(.92f, .96f, 1f);
        style.focused.textColor = Color.white;
        return style;
    }

    private float DrawNeonSlider(string id, Rect rect, float value, Color accent,
        float scale)
    {
        if (Event.current.type == EventType.Repaint &&
            _pointer.UpdateDrag(id, rect, out var draggedValue, out _))
            value = draggedValue;
        value = Mathf.Clamp01(value);
        var track = new Rect(rect.x, rect.y + rect.height * .5f - 4 * scale,
            rect.width, 8 * scale);
        GUI.DrawTexture(track, _sliderTrack!, ScaleMode.StretchToFill, true);
        var fill = new Rect(track.x, track.y,
            Mathf.Max(8 * scale, track.width * value), track.height);
        var previous = GUI.color; GUI.color = accent; GUI.DrawTexture(fill, _pill!, ScaleMode.StretchToFill, true); GUI.color = previous;
        var thumbSize = 20 * scale;
        var thumb = new Rect(rect.x + rect.width * value - thumbSize * .5f,
            rect.center.y - thumbSize * .5f, thumbSize, thumbSize);
        GUI.DrawTexture(thumb, _sliderThumb!, ScaleMode.StretchToFill, true);
        return value;
    }

    private static Rect ToScreenRect(Rect layoutRect, float scale) => new(
        layoutRect.x * scale, layoutRect.y * scale,
        layoutRect.width * scale, layoutRect.height * scale);

    private static GUIStyle ScaleInteractiveStyle(GUIStyle source, float scale)
    {
        var style = new GUIStyle(source)
        {
            fontSize = Mathf.Max(1, Mathf.RoundToInt(source.fontSize * scale)),
            border = ScaleRectOffset(source.border, scale),
            padding = ScaleRectOffset(source.padding, scale),
            margin = ScaleRectOffset(source.margin, scale),
            overflow = ScaleRectOffset(source.overflow, scale)
        };
        return style;
    }

    private static RectOffset ScaleRectOffset(RectOffset source, float scale) => new(
        Mathf.RoundToInt(source.left * scale), Mathf.RoundToInt(source.right * scale),
        Mathf.RoundToInt(source.top * scale), Mathf.RoundToInt(source.bottom * scale));

    private bool DrawScreenSpaceButton(Rect layoutRect, string text,
        GUIStyle style, float scale)
    {
        var matrix = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
        var clicked = DrawPointerButton(ToScreenRect(layoutRect, scale), text,
            ScaleInteractiveStyle(style, scale), "instant-session");
        GUI.matrix = matrix;
        return clicked;
    }

    private bool DrawPointerButton(Rect rect, string text, GUIStyle style,
        string diagnosticName)
    {
        var hovered = rect.Contains(_pointer.Position);
        var pressed = hovered && _pointer.IsDown;
        var renderStyle = new GUIStyle(style);
        if (pressed)
        {
            renderStyle.normal.background = style.active.background;
            renderStyle.normal.textColor = style.active.textColor;
        }
        else if (hovered)
        {
            renderStyle.normal.background = style.hover.background;
            renderStyle.normal.textColor = style.hover.textColor;
        }
        GUI.Label(rect, text, renderStyle);
        var clicked = GUI.enabled && Event.current.type == EventType.Repaint &&
                      _pointer.ConsumeClick(rect);
        if (clicked) Debug.Log($"Neon Stage control clicked: {diagnosticName}");
        return clicked;
    }

    private float DrawProgressTimeline(Rect rect, float value, float scale,
        out bool released)
    {
        released = false;
        if (Event.current.type == EventType.Repaint &&
            _pointer.UpdateDrag("song-progress", rect, out var draggedValue,
                out released))
            value = draggedValue;
        value = Mathf.Clamp01(value);
        var line = new Rect(rect.x, rect.y + 13 * scale, rect.width, 8 * scale);
        GUI.DrawTexture(line, _sliderTrack!, ScaleMode.StretchToFill, true);
        var filledWidth = Mathf.Max(8 * scale, line.width * value);
        var cyan = new Color(.15f, .82f, 1f, 1f);
        var old = GUI.color;
        GUI.color = new Color(cyan.r, cyan.g, cyan.b, .16f);
        GUI.DrawTexture(new Rect(line.x - 8 * scale, line.y - 8 * scale,
            filledWidth + 16 * scale, 24 * scale), _softGlow!, ScaleMode.StretchToFill, true);
        GUI.color = new Color(cyan.r, cyan.g, cyan.b, .4f);
        GUI.DrawTexture(new Rect(line.x - 3 * scale, line.y - 3 * scale,
            filledWidth + 6 * scale, 14 * scale), _pill!, ScaleMode.StretchToFill, true);
        GUI.color = cyan;
        GUI.DrawTexture(new Rect(line.x, line.y, filledWidth, 8), _pill!, ScaleMode.StretchToFill, true);
        var pulse = (22 + Mathf.Sin(Time.unscaledTime * 6f) * 3f) * scale;
        GUI.color = new Color(.72f, .96f, 1f, .75f);
        GUI.DrawTexture(new Rect(line.x + filledWidth - pulse * .5f, line.center.y - pulse * .5f, pulse, pulse), _softGlow!, ScaleMode.StretchToFill, true);
        GUI.color = old;
        return value;
    }

    private void EnsureControlTextures()
    {
        if (_controlButton != null) return;
        _controlButton = MakeRoundedTexture(64, 13, new Color(.18f, .11f, .24f, 1f), new Color(.52f, .3f, .65f, 1f), 2);
        _controlButtonHover = MakeRoundedTexture(64, 13, new Color(.34f, .18f, .43f, 1f), new Color(.87f, 1f, .05f, 1f), 3);
        _controlButtonActive = MakeRoundedTexture(64, 13, new Color(.58f, .12f, .42f, 1f), new Color(1f, .35f, .8f, 1f), 3);
        _sliderTrack = MakeRoundedTexture(32, 16, new Color(.17f, .11f, .22f, 1f), new Color(.42f, .28f, .5f, 1f), 2);
        _sliderThumb = MakeRoundedTexture(32, 16, new Color(.92f, .88f, 1f, 1f), new Color(1f, 1f, 1f, 1f), 2);
        _transparent = MakeTexture(Color.clear);
        _pill = MakeRoundedTexture(32, 16, Color.white, Color.white, 0);
        _softGlow = MakeRadialTexture(64);
        _iconBadge = MakeRoundedTexture(64, 32, new Color(.09f, .035f, .13f, .96f), Color.white, 3);
        _stageSelectionIcon = Resources.Load<Texture2D>("StageSelectionBackIcon");
        _wordmark = Resources.Load<Texture2D>("NeonStageWordmark");
        _settingsIcon = MakeSettingsIcon(96);
    }

    private static void DrawArcadeTitle(Rect rect, string text)
    {
        DrawArcadeLabel(rect, text, 20, 34, 2f, 5f);
    }

    private static void DrawArcadeLabel(Rect rect, string text, int minimumSize,
        int maximumSize, float outline, float extrusion, Color? faceColor = null,
        int maxLines = 1)
    {
        text ??= "";
        var fitSize = text.Length == 0
            ? maximumSize
            : Mathf.FloorToInt(rect.width * Mathf.Max(1, maxLines) / (text.Length * .61f));
        var size = Mathf.Clamp(fitSize, minimumSize, maximumSize);
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = size,
            alignment = TextAnchor.MiddleCenter,
            fontStyle = FontStyle.BoldAndItalic,
            clipping = TextClipping.Clip,
            wordWrap = maxLines > 1
        };
        style.normal.textColor = new Color(.025f, .035f, .16f, .98f);
        GUI.Label(new Rect(rect.x + extrusion, rect.y + extrusion + 1, rect.width, rect.height), text, style);
        style.normal.textColor = new Color(.03f, .92f, 1f, 1f);
        foreach (var offset in new[]
                 {
                     new Vector2(-outline, 0), new Vector2(outline, 0),
                     new Vector2(0, -outline), new Vector2(0, outline),
                     new Vector2(-outline * .72f, -outline * .72f),
                     new Vector2(outline * .72f, -outline * .72f),
                     new Vector2(-outline * .72f, outline * .72f),
                     new Vector2(outline * .72f, outline * .72f)
                 })
            GUI.Label(new Rect(rect.x + offset.x, rect.y + offset.y, rect.width, rect.height), text, style);
        style.normal.textColor = faceColor ?? new Color(1f, .12f, .7f, 1f);
        GUI.Label(rect, text, style);
        style.normal.textColor = new Color(1f, .82f, .12f, .22f);
        GUI.Label(new Rect(rect.x, rect.y - Mathf.Max(.5f, outline * .5f), rect.width, rect.height), text, style);
    }

    private void DrawLauncherCardFrame(Rect card, bool isDirect, bool isOnline, bool hovered)
    {
        var cyan = new Color(.03f, .9f, 1f, hovered ? 1f : .76f);
        var magenta = new Color(1f, .1f, .68f, hovered ? 1f : .78f);
        var gold = new Color(1f, .72f, .08f, hovered ? .95f : .62f);
        var primary = isDirect ? new Color(.8f, 1f, .04f, hovered ? 1f : .78f) :
            isOnline ? cyan : magenta;

        if (hovered)
        {
            var pulse = .15f + (Mathf.Sin(Time.unscaledTime * 7f) + 1f) * .07f;
            DrawRectOutline(new Rect(card.x - 5, card.y - 5, card.width + 10, card.height + 10),
                3, new Color(primary.r, primary.g, primary.b, pulse));
        }

        DrawSolid(new Rect(card.x + 6, card.y + 7, card.width, card.height),
            new Color(.34f, .015f, .25f, .72f));
        DrawSolid(card, new Color(.045f, .018f, .09f, .98f));
        DrawRectOutline(card, hovered ? 4 : 3, primary);
        DrawRectOutline(new Rect(card.x + 5, card.y + 5, card.width - 10, card.height - 10),
            1, gold);

        // Asymmetrical arcade accents keep the cards lively without stealing
        // horizontal room from long, user-defined stage names.
        DrawSolid(new Rect(card.x + 3, card.y + 3, card.width * .42f, 3), magenta);
        DrawSolid(new Rect(card.x + card.width * .58f - 3, card.y + card.height - 6,
            card.width * .42f, 3), cyan);
        DrawSolid(new Rect(card.x - 2, card.y + 18, 6, 24), gold);
        DrawSolid(new Rect(card.x + card.width - 4, card.y + card.height - 42, 6, 24), magenta);
    }

    private static void DrawRectOutline(Rect rect, float thickness, Color color)
    {
        DrawSolid(new Rect(rect.x, rect.y, rect.width, thickness), color);
        DrawSolid(new Rect(rect.x, rect.yMax - thickness, rect.width, thickness), color);
        DrawSolid(new Rect(rect.x, rect.y, thickness, rect.height), color);
        DrawSolid(new Rect(rect.xMax - thickness, rect.y, thickness, rect.height), color);
    }

    private bool DrawStageSelectionButton(float scale)
    {
        var matrix = GUI.matrix;
        GUI.matrix = Matrix4x4.identity;
        var rect = ToScreenRect(new Rect(14, 10, 92, 92), scale);
        var style = new GUIStyle(GUI.skin.button)
        {
            alignment = TextAnchor.MiddleCenter,
            normal = { background = _controlButton },
            hover = { background = _controlButtonHover },
            active = { background = _controlButtonActive }
        };
        var enabled = GUI.enabled;
        GUI.enabled = !_leavingSession;
        var clicked = DrawPointerButton(rect, "", ScaleInteractiveStyle(style, scale), "stage-selection");

        var pulse = _leavingSession
            ? .45f + .2f * (Mathf.Sin(Time.unscaledTime * 5f) + 1f) * .5f
            : 1f;
        var previous = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, pulse);
        var padding = 5f * scale;
        if (_stageSelectionIcon != null)
            GUI.DrawTexture(new Rect(rect.x + padding, rect.y + padding,
                    rect.width - padding * 2, rect.height - padding * 2),
                _stageSelectionIcon, ScaleMode.ScaleToFit, true);
        else
        {
            var fallback = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt(38 * scale),
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter
            };
            fallback.normal.textColor = new Color(.1f, .95f, 1f);
            GUI.Label(rect, "←", fallback);
        }
        GUI.color = previous;
        GUI.enabled = enabled;
        GUI.matrix = matrix;
        return clicked;
    }

    private static Texture2D MakeRoundedTexture(int size, float radius, Color fill, Color border, float borderWidth)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
        {
            var dx = Mathf.Max(Mathf.Abs(x - (size - 1) * .5f) - (size * .5f - radius), 0);
            var dy = Mathf.Max(Mathf.Abs(y - (size - 1) * .5f) - (size * .5f - radius), 0);
            var distance = Mathf.Sqrt(dx * dx + dy * dy);
            var edge = Mathf.Clamp01(radius - distance);
            var inside = distance <= radius;
            var isBorder = inside && distance > radius - borderWidth;
            var color = isBorder ? border : fill; color.a *= edge;
            texture.SetPixel(x, y, inside ? color : Color.clear);
        }
        texture.Apply(); return texture;
    }

    private static Texture2D MakeRadialTexture(int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
        {
            var distance = Vector2.Distance(new Vector2(x, y), new Vector2((size - 1) * .5f, (size - 1) * .5f)) / (size * .5f);
            texture.SetPixel(x, y, new Color(1, 1, 1, Mathf.Pow(Mathf.Clamp01(1 - distance), 2)));
        }
        texture.Apply(); return texture;
    }

    private static Texture2D MakeSettingsIcon(int size)
    {
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var center = (size - 1) * .5f;
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
        {
            var dx = x - center;
            var dy = y - center;
            var radius = Mathf.Sqrt(dx * dx + dy * dy);
            var angle = Mathf.Atan2(dy, dx);
            var tooth = Mathf.Cos(angle * 8f) > .18f;
            var outer = size * (tooth ? .43f : .35f);
            var inner = size * .15f;
            if (radius < inner || radius > outer)
            {
                texture.SetPixel(x, y, Color.clear);
                continue;
            }
            var edge = radius < inner + 3 || radius > outer - 3;
            texture.SetPixel(x, y, edge
                ? new Color(.02f, .94f, 1f, 1f)
                : new Color(1f, .1f, .68f, 1f));
        }
        texture.Apply();
        return texture;
    }

    private void DrawIconBadge(Rect rect, Color accent)
    {
        var old = GUI.color;
        GUI.color = new Color(accent.r, accent.g, accent.b, .22f);
        GUI.DrawTexture(new Rect(rect.x - 5, rect.y - 5, rect.width + 10, rect.height + 10), _softGlow!);
        GUI.color = accent; GUI.DrawTexture(rect, _iconBadge!); GUI.color = old;
    }

    private static Texture2D MakeTexture(Color color)
    {
        var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        texture.SetPixel(0, 0, color);
        texture.Apply();
        return texture;
    }

    private static void DrawMusicIcon(Rect rect, Color color)
    {
        DrawSolid(new Rect(rect.x + 21, rect.y + 2, 4, 23), color);
        DrawSolid(new Rect(rect.x + 9, rect.y + 7, 15, 4), color);
        DrawSolid(new Rect(rect.x + 9, rect.y + 8, 4, 20), color);
        DrawSolid(new Rect(rect.x + 2, rect.y + 24, 11, 8), color);
        DrawSolid(new Rect(rect.x + 17, rect.y + 21, 11, 8), color);
    }

    private static void DrawMicrophoneIcon(Rect rect, Color color)
    {
        DrawSolid(new Rect(rect.x + 11, rect.y + 1, 13, 22), color);
        DrawSolid(new Rect(rect.x + 6, rect.y + 13, 4, 11), color);
        DrawSolid(new Rect(rect.x + 25, rect.y + 13, 4, 11), color);
        DrawSolid(new Rect(rect.x + 9, rect.y + 24, 17, 4), color);
        DrawSolid(new Rect(rect.x + 16, rect.y + 27, 4, 7), color);
        DrawSolid(new Rect(rect.x + 10, rect.y + 33, 16, 4), color);
    }

    private static void DrawSolid(Rect rect, Color color)
    {
        var previous = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(rect, Texture2D.whiteTexture);
        GUI.color = previous;
    }
}
}
