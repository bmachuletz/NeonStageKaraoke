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
    private const string DefaultServer = "http://cloud.hdvtec.de:5274";
    // Der Server bevorzugt Unity-kompatible Ogg/Vorbis-Stems. Der Client fällt
    // bei älteren Bibliothekseinträgen sicher auf die MP3-Masterspur zurück.
    private const bool PreparedStemsAreUnityCompatible = true;
    private StageAudioEngine _audio = null!;
    private IStageClock _clock = null!;
    private string _server = DefaultServer;
    private string _status = "Verbinde mit NeonStage …";
    private string? _loadedSongId;
    private string? _loadedQueueEntryId;
    private string? _loadedStartedAt;
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
    private bool _creatingQuickSession;
    private string? _activeEventId;
    private bool _initialSessionChecked;
    private bool _exiting;
    private float _musicVolume = 0.85f;
    private float _vocalVolume = 0.35f;
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
        if (FindAnyObjectByType<AudioListener>() == null)
            gameObject.AddComponent<AudioListener>();
        _server = ResolveServer();
        Debug.Log($"Neon Stage server: {_server}");
        _controllerId = PlayerPrefs.GetString("NeonStage.ControllerId", "");
        if (!Guid.TryParse(_controllerId, out _))
        {
            _controllerId = Guid.NewGuid().ToString();
            PlayerPrefs.SetString("NeonStage.ControllerId", _controllerId);
            PlayerPrefs.Save();
        }
        _audio = gameObject.AddComponent<StageAudioEngine>();
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
        if (TryGetEditorTestSettings(out var testHost, out var testPort, out var testSession, out var testToken))
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
        _ = _visuals.LoadQrAsync(_server);
        _ = ClaimControlAsync();
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

    private void Update()
    {
        _editorTestClient?.Update();
        _pointer.Update();
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (_editorTestMode) { Application.Quit(); return; }
            _ = ExitStageAsync();
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
            _ = _visuals.LoadQrAsync(_server, true);
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
        try
        {
            using (var eventRequest = UnityWebRequest.Get($"{_server}/api/events/active"))
            {
                await eventRequest.SendWebRequest();
                if (eventRequest.result != UnityWebRequest.Result.Success)
                {
                    // A sleeping Android Wi-Fi stack or a brief server restart
                    // is not evidence that the event was deactivated. Preserve
                    // the complete paused stage unless the server explicitly
                    // answers 404.
                    if (eventRequest.responseCode != 404)
                    {
                        _status = StageLocale.Text("Verbindung unterbrochen – Session bleibt erhalten",
                            "Connection interrupted – keeping session");
                        return;
                    }
                    _initialSessionChecked = true;
                    _hasActiveSession = false;
                    _visuals.SetSessionActive(false);
                    _visuals.SetStageTheme("standard");
                    _lyrics.SetStageTheme("standard");
                    _activeEventId = null;
                    _loadedSongId = null;
                    _video.Stop();
                    _lyrics.SetVideoBackground(false);
                    _loadedQueueEntryId = null;
                    _loadedStartedAt = null;
                    if (_audio.IsPlaying) _audio.Pause();
                    _songTitle = "NEON STAGE";
                    _songArtist = StageLocale.Text("BEREIT FÜR DEINE PARTY", "READY FOR YOUR PARTY");
                    _status = StageLocale.Text("Keine Session aktiv", "No active session");
                    return;
                }
                var activeEvent = JsonUtility.FromJson<KaraokeEventDto>(eventRequest.downloadHandler.text);
                if (!_initialSessionChecked)
                {
                    _initialSessionChecked = true;
                    var isStageQuickSession = activeEvent != null &&
                        (activeEvent.description == "Spontane Karaoke-Session" ||
                         activeEvent.description == "Spontane Karaoke-Session (Bühne)");
                    if (isStageQuickSession)
                    {
                        await DeactivateEventAsync(activeEvent!.id);
                        _hasActiveSession = false;
                        _visuals.SetSessionActive(false);
                        _activeEventId = null;
                        return;
                    }
                }
                _hasActiveSession = activeEvent != null;
                _visuals.SetSessionActive(_hasActiveSession);
                _visuals.SetStageTheme(activeEvent?.stageThemeId ?? "standard");
                _lyrics.SetStageTheme(activeEvent?.stageThemeId ?? "standard");
                if (activeEvent != null && _activeEventId != activeEvent.id)
                {
                    _activeEventId = activeEvent.id;
                    _lastReactionId = 0;
                    _loadedSongId = null;
                    _loadedQueueEntryId = null;
                    _loadedStartedAt = null;
                    _songTitle = activeEvent.name;
                    _songArtist = StageLocale.Text("SESSION BEREIT", "SESSION READY");
                    _ = _visuals.LoadQrAsync(_server, true);
                }
            }
            using var request = UnityWebRequest.Get($"{_server}/api/queue");
            await request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                _status = $"Serverfehler {request.responseCode}: {request.error}";
                return;
            }

            var state = JsonUtility.FromJson<PlaybackStateDto>(request.downloadHandler.text);
            var current = state?.current;

            // A poll that started just before a mouse click may still complete
            // while the command is in flight. It may update passive metadata,
            // but it must not apply its stale transport state or reload a song.
            if (_commandRunning && !string.IsNullOrWhiteSpace(_optimisticPlaybackCommand))
                return;

            var playbackChanged = current?.song != null &&
                (_loadedQueueEntryId != current.id ||
                 !string.Equals(_loadedStartedAt, current.startedAt, StringComparison.Ordinal));
            if (current?.song != null && playbackChanged)
                _visuals.BeginSongTransition(current.song, _songTitle, _songArtist);
            var nextEntry = state?.queue is { Length: > 0 } ? state.queue[0] : null;
            await _visuals.SetNextAsync(_server, nextEntry);
            if (nextEntry?.song != null) _ = _video.PrefetchAsync(_server, nextEntry.song.id);
            if (state == null || current?.song == null)
            {
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
                if (_audio.HasEnded)
                {
                    _status = StageLocale.Text("Titel beendet – nächster Song wird gestartet …", "Song finished – starting next song …");
                    _ = CompletePlaybackAndAdvanceAsync();
                    return;
                }
                if (!_audio.IsPlaying) _audio.Resume();
                _status = _audio.Status;
                return;
            }

            _loadedSongId = current.song.id;
            _loadedQueueEntryId = current.id;
            _loadedStartedAt = current.startedAt;
            _status = StageLocale.Text("Prüfe vorbereitete Karaoke-Spuren …", "Checking prepared karaoke stems …");
            var lyricsTask = _lyrics.LoadAsync(_server, _loadedSongId);
            var coverTask = _visuals.LoadCoverAsync(_server, _loadedSongId);
            var videoTask = _video.LoadAsync(_server, _loadedSongId);
            var stemsTask = GetStemsAsync(_loadedSongId);
            await Task.WhenAll(lyricsTask, coverTask, videoTask, stemsTask);
            _lyrics.SetVideoBackground(_video.Active);
            var stems = await stemsTask;
            var useStems = PreparedStemsAreUnityCompatible && stems.hasInstrumental;
            var master = useStems
                ? $"{_server}/api/songs/{_loadedSongId}/stems/instrumental?format=ogg"
                : $"{_server}/api/songs/{_loadedSongId}/audio";
            var vocals = useStems && stems.hasVocals
                ? $"{_server}/api/songs/{_loadedSongId}/stems/vocals?format=ogg"
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
                await _audio.PlayAsync($"{_server}/api/songs/{_loadedSongId}/audio", null);
            }
            _status = _audio.Status;
        }
        catch (Exception exception)
        {
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

    private void HandlePlaybackEnded() => _ = CompletePlaybackAndAdvanceAsync();

    private async Task CompletePlaybackAndAdvanceAsync()
    {
        if (_autoAdvanceRunning || string.IsNullOrWhiteSpace(_loadedQueueEntryId)) return;
        _autoAdvanceRunning = true;
        var completedEntryId = _loadedQueueEntryId;
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
            using var request = CreateJsonPost($"{_server}/api/playback/complete", payload, true);
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

    private async Task ClaimControlAsync(bool force = false)
    {
        var payload = JsonUtility.ToJson(new PlaybackControllerRequestDto
        {
            clientId = _controllerId,
            clientName = $"Neon Stage Unity ({SystemInfo.deviceName})",
            force = force
        });
        using var request = CreateJsonPost($"{_server}/api/playback/controller/claim", payload, false);
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
                using var request = CreateJsonPost($"{_server}/api/playback/{command}", "{}", true);
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
        if (_commandRunning || string.IsNullOrWhiteSpace(_loadedSongId)) return;
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
            using var request = CreateJsonPost($"{_server}/api/playback/position", payload, true);
            await request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success) _status = $"Seek fehlgeschlagen ({request.responseCode})";
        }
        finally { _commandRunning = false; }
    }

    private async Task<string?> GetCurrentEntryIdAsync()
    {
        using var request = UnityWebRequest.Get($"{_server}/api/queue");
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

    private async Task CreateQuickSessionAsync()
    {
        if (_creatingQuickSession) return;
        _creatingQuickSession = true;
        try
        {
            var now = DateTimeOffset.Now;
            var name = $"{StageLocale.Text("Sofort-Session", "Instant session")} · {now:g}";
            var payload = JsonUtility.ToJson(new CreateKaraokeEventDto
            {
                name = name,
                startsAt = now.ToString("O"),
                description = "Spontane Karaoke-Session (Bühne)",
                stageThemeId = "standard"
            });
            using var create = CreateJsonPost($"{_server}/api/events", payload, false);
            await create.SendWebRequest();
            if (create.result != UnityWebRequest.Result.Success)
            {
                _status = $"{StageLocale.Text("Session konnte nicht erstellt werden", "Could not create session")} ({create.responseCode})";
                return;
            }
            var created = JsonUtility.FromJson<KaraokeEventDto>(create.downloadHandler.text);
            if (created == null || string.IsNullOrWhiteSpace(created.id)) return;
            using var activate = CreateJsonPost($"{_server}/api/events/{created.id}/activate", "{}", false);
            await activate.SendWebRequest();
            if (activate.result != UnityWebRequest.Result.Success)
            {
                _status = $"{StageLocale.Text("Session konnte nicht aktiviert werden", "Could not activate session")} ({activate.responseCode})";
                return;
            }
            _activeEventId = created.id;
            _hasActiveSession = true;
            _visuals.SetSessionActive(true);
            _songTitle = created.name;
            _songArtist = StageLocale.Text("SESSION BEREIT", "SESSION READY");
            _status = StageLocale.Text("Sofort-Session gestartet", "Instant session started");
            _ = _visuals.LoadQrAsync(_server, true);
        }
        catch (Exception exception) { _status = $"{StageLocale.Text("Sessionfehler", "Session error")}: {exception.Message}"; }
        finally { _creatingQuickSession = false; }
    }

    private async Task DeactivateEventAsync(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId)) return;
        using var request = CreateJsonPost($"{_server}/api/events/{eventId}/deactivate", "{}", false);
        await request.SendWebRequest();
    }

    private async Task ExitStageAsync()
    {
        if (_exiting) return;
        _exiting = true;
        var eventId = _activeEventId;
        if (!string.IsNullOrWhiteSpace(eventId))
        {
            using var active = UnityWebRequest.Get($"{_server}/api/events/active");
            await active.SendWebRequest();
            if (active.result == UnityWebRequest.Result.Success)
            {
                var item = JsonUtility.FromJson<KaraokeEventDto>(active.downloadHandler.text);
                if (item != null && item.id == eventId && item.description == "Spontane Karaoke-Session (Bühne)")
                    await DeactivateEventAsync(eventId);
            }
        }
        _audio.Stop();
        Application.Quit();
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
            return;
        }
        GUI.color = Color.white;
        var titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        titleStyle.normal.textColor = new Color(0.87f, 1f, 0.05f);
        if (!_visuals.IsSongTransitioning)
        {
            GUI.Label(new Rect(40, 21, width - 80, 42), _songTitle, titleStyle);
            var artistStyle = new GUIStyle(titleStyle) { fontSize = 19 };
            artistStyle.normal.textColor = new Color(.92f, .86f, .97f);
            GUI.Label(new Rect(40, 57, width - 80, 28), _songArtist, artistStyle);
        }
        var controlsY = height - 158;

        GUI.color = new Color(0.055f, 0.018f, 0.085f, 0.94f);
        GUI.Box(new Rect(0, controlsY, width, 158), GUIContent.none);
        DrawSolid(new Rect(0, controlsY, width, 2), new Color(1f, .2f, .72f, .55f));
        GUI.color = Color.white;
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
    }

    private void DrawSessionLauncher(float width, float height)
    {
        var heading = new GUIStyle(GUI.skin.label) { fontSize = 42, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
        heading.normal.textColor = new Color(.87f, 1f, .05f);
        GUI.Label(new Rect(40, height * .18f, width - 80, 60), "NEON STAGE", heading);
        var sub = new GUIStyle(heading) { fontSize = 18, fontStyle = FontStyle.Normal };
        sub.normal.textColor = new Color(1f, .3f, .78f);
        GUI.Label(new Rect(40, height * .18f + 58, width - 80, 35), StageLocale.Text("Keine Karaoke-Session aktiv", "No active karaoke session"), sub);
        var buttonRect = new Rect(width * .5f - 220, height * .5f - 70, 440, 140);
        GUI.color = new Color(.87f, 1f, .05f, .18f);
        GUI.DrawTexture(new Rect(buttonRect.x - 24, buttonRect.y - 24, buttonRect.width + 48, buttonRect.height + 48), _softGlow!, ScaleMode.StretchToFill, true);
        GUI.color = Color.white;
        var button = new GUIStyle(GUI.skin.button) { fontSize = 28, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
        button.normal.background = _controlButtonHover;
        button.hover.background = _controlButtonActive;
        button.active.background = _controlButtonActive;
        button.normal.textColor = new Color(.94f, 1f, .72f);
        GUI.enabled = !_creatingQuickSession;
        if (DrawScreenSpaceButton(buttonRect,
                _creatingQuickSession
                    ? StageLocale.Text("SESSION WIRD GESTARTET …", "STARTING SESSION …")
                    : StageLocale.Text("SOFORT-SESSION\nSTARTEN", "START INSTANT\nSESSION"),
                button, Mathf.Max(1f, Mathf.Min(Screen.width / 1280f, Screen.height / 720f))))
            _ = CreateQuickSessionAsync();
        GUI.enabled = true;
        var hint = new GUIStyle(sub) { fontSize = 14 };
        hint.normal.textColor = new Color(.68f, .6f, .74f);
        GUI.Label(new Rect(40, buttonRect.yMax + 30, width - 80, 32), StageLocale.Text("Oder ein vorbereitetes Event im Admin-Portal auf die Bühne schalten", "Or activate a prepared event from the admin portal"), hint);
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
