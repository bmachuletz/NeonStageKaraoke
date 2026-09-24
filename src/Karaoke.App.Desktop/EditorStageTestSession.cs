using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NeonStage.Testing;

namespace Karaoke.App.Desktop;

internal sealed class EditorStageTestSession : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true
    };

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _connectionLock = new();
    private CancellationTokenSource? _lifetime;
    private TcpListener? _listener;
    private TcpClient? _client;
    private StreamWriter? _writer;
    private Process? _ownedProcess;
    private Task? _acceptTask;
    private StageTestSongState? _state;
    private string _sessionId = "";
    private string _token = "";
    private long _revision;
    private StageTestMessage? _pendingExport;
    private TaskCompletionSource<string>? _exportCompletion;
    private string? _exportEncoder;

    public event Action<string>? StatusChanged;
    public event Action<int, int>? ExportProgress;
    public bool IsRunning => _ownedProcess is { HasExited: false };

    public async Task StartAsync(StageTestSongState state, CancellationToken cancellationToken = default)
        => await StartCoreAsync(state, false, cancellationToken);

    public async Task<string> ExportAsync(StageTestSongState state, string outputPath, int width = 1920,
        int height = 1080, int framesPerSecond = 60, CancellationToken cancellationToken = default)
    {
        if (width < 320 || height < 180 || framesPerSecond is < 1 or > 120)
            throw new ArgumentOutOfRangeException(nameof(width), "Ungültige MP4-Exportparameter.");
        _exportCompletion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingExport = new StageTestMessage
        {
            type = StageTestProtocol.BeginExport,
            outputPath = Path.GetFullPath(outputPath),
            width = width,
            height = height,
            framesPerSecond = framesPerSecond
        };
        try
        {
            PublishStatus("Prüfe MP4-Encoder …");
            _exportEncoder = await EditorExportSettings.Load().ResolveEncoderAsync(cancellationToken);
            await StartCoreAsync(state, true, cancellationToken);
            using var registration = cancellationToken.Register(() =>
            {
                _ = SendAsync(new StageTestMessage { type = StageTestProtocol.CancelExport, sessionId = _sessionId });
                _exportCompletion?.TrySetCanceled(cancellationToken);
            });
            return await _exportCompletion.Task;
        }
        finally { _pendingExport = null; _exportCompletion = null; _exportEncoder = null; }
    }

    private async Task StartCoreAsync(StageTestSongState state, bool exportMode,
        CancellationToken cancellationToken)
    {
        await StopAsync();
        var executable = ResolveExecutable();
        if (executable is null)
            throw new FileNotFoundException(
                "Keine Unity-Stage gefunden. NEONSTAGE_STAGE_EXECUTABLE auf die Stage-Datei oder unter macOS auf NeonStage Karaoke.app setzen.");

        _state = state;
        _sessionId = Guid.NewGuid().ToString("N");
        _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        _revision = 0;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(2);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptTask = AcceptLoopAsync(_lifetime.Token);

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
        };
        if (OperatingSystem.IsMacOS())
        {
            // Die Editor-.app setzt diese Variablen für ihr gebündeltes LibVLC.
            // Der unabhängige Unity-Player darf die VLC-Dylibs nicht erben.
            start.Environment.Remove("DYLD_LIBRARY_PATH");
            start.Environment.Remove("VLC_PLUGIN_PATH");
            start.Environment.Remove("NEONSTAGE_LIBVLC_PATH");
        }
        start.ArgumentList.Add(exportMode ? "--editor-export" : "--editor-test");
        start.ArgumentList.Add("--editor-test-host");
        start.ArgumentList.Add(IPAddress.Loopback.ToString());
        start.ArgumentList.Add("--editor-test-port");
        start.ArgumentList.Add(port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--editor-test-session");
        start.ArgumentList.Add(_sessionId);
        start.ArgumentList.Add("--editor-test-token");
        start.ArgumentList.Add(_token);
        start.ArgumentList.Add("--server");
        start.ArgumentList.Add(state.serverUrl);
        // Testläufe bleiben bewusst in einem normalen Desktop-Fenster. Ein
        // späterer Fullscreen-Shortcut kann diesen Startzustand gezielt ändern.
        start.ArgumentList.Add("-screen-fullscreen");
        start.ArgumentList.Add("0");
        start.ArgumentList.Add("-screen-width");
        start.ArgumentList.Add("1280");
        start.ArgumentList.Add("-screen-height");
        start.ArgumentList.Add("720");
        // Offline exports do not need an interactive player window. Batch mode
        // retains the graphics device (unlike -nographics), so Camera.Render and
        // AsyncGPUReadback continue to work while Unity stays hidden.
        if (exportMode)
        {
            start.Environment["NEONSTAGE_EXPORT_VIDEO_ENCODER"] = _exportEncoder ?? "libx264";
            start.ArgumentList.Add("-batchmode");
        }

        _ownedProcess = Process.Start(start) ?? throw new InvalidOperationException("Unity-Stage konnte nicht gestartet werden.");
        _ownedProcess.EnableRaisingEvents = true;
        _ownedProcess.Exited += OwnedProcessExited;
        PublishStatus(exportMode
            ? $"Export-Stage gestartet · {_exportEncoder} · warte auf Handshake"
            : $"Stage gestartet · warte auf Handshake ({Path.GetFileName(executable)})");
    }

    public void QueueLyricsUpdate(string lyricsJson)
    {
        if (_lifetime is null) return;
        if (_state is not null) _state.lyricsJson = lyricsJson;
        var revision = Interlocked.Increment(ref _revision);
        _ = SendAsync(new StageTestMessage
        {
            type = StageTestProtocol.UpdateLyrics,
            sessionId = _sessionId,
            revision = revision,
            stateJson = lyricsJson
        });
    }

    public async Task ReplaceStateAsync(StageTestSongState state, CancellationToken cancellationToken = default)
    {
        _state = state;
        await SendFullStateAsync(cancellationToken);
    }

    public Task SendTransportAsync(string type, TimeSpan position, bool playing = false) =>
        SendTransportCoreAsync(type, position, playing);

    private Task SendTransportCoreAsync(string type, TimeSpan position, bool playing)
    {
        if (_state is not null)
        {
            _state.positionSeconds = Math.Max(0, position.TotalSeconds);
            if (type is StageTestProtocol.Play or StageTestProtocol.Clock) _state.playing = playing;
            else if (type is StageTestProtocol.Pause or StageTestProtocol.Stop) _state.playing = false;
        }
        return SendAsync(new StageTestMessage
        {
            type = type,
            sessionId = _sessionId,
            positionSeconds = Math.Max(0, position.TotalSeconds),
            playing = playing
        });
    }

    public Task SendClockAsync(TimeSpan position, bool playing) =>
        SendTransportAsync(StageTestProtocol.Clock, position, playing);

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is not null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                await HandleConnectionAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                PublishStatus("Stage-Verbindung unterbrochen · Wiederverbindung läuft: " + exception.Message);
                await Task.Delay(250, cancellationToken);
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 16 * 1024, leaveOpen: true))
        using (var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), 16 * 1024, leaveOpen: true)
               { AutoFlush = true, NewLine = "\n" })
        {
            var helloLine = await reader.ReadLineAsync(cancellationToken);
            var hello = Deserialize(helloLine);
            if (!StageTestProtocol.IsCompatibleHello(hello, _sessionId, _token))
                throw new InvalidDataException("Stage-Handshake wurde abgewiesen.");

            lock (_connectionLock)
            {
                _client?.Dispose();
                _client = client;
                _writer = writer;
            }
            PublishStatus($"Stage verbunden · Protokoll {hello!.protocolVersion}");
            await SendFullStateAsync(cancellationToken);

            while (!cancellationToken.IsCancellationRequested && client.Connected)
            {
                var line = await reader.ReadLineAsync(cancellationToken);
                if (line is null) break;
                var message = Deserialize(line);
                if (message?.sessionId != _sessionId) continue;
                if (message.type == StageTestProtocol.Ready)
                {
                    PublishStatus(_pendingExport is null
                        ? "Stage bereit · Live-Test aktiv"
                        : "Stage bereit · MP4-Export wird gestartet");
                    if (_pendingExport is not null)
                    {
                        _pendingExport.sessionId = _sessionId;
                        _pendingExport.revision = message.revision;
                        await SendAsync(_pendingExport, cancellationToken);
                        PublishStatus("MP4-Export läuft …");
                    }
                }
                else if (message.type == StageTestProtocol.ExportProgress)
                    ExportProgress?.Invoke(message.frame, message.totalFrames);
                else if (message.type == StageTestProtocol.ExportComplete)
                    _exportCompletion?.TrySetResult(message.outputPath);
                else if (message.type == StageTestProtocol.Error)
                {
                    PublishStatus("Stage-Fehler: " + message.details);
                    _exportCompletion?.TrySetException(new InvalidOperationException(message.details));
                }
            }
        }

        lock (_connectionLock)
        {
            if (ReferenceEquals(_client, client))
            {
                _client = null;
                _writer = null;
            }
        }
        if (!cancellationToken.IsCancellationRequested)
            PublishStatus("Stage-Verbindung getrennt · warte auf Wiederverbindung");
    }

    private async Task SendFullStateAsync(CancellationToken cancellationToken)
    {
        if (_state is null) return;
        var revision = Interlocked.Increment(ref _revision);
        await SendAsync(new StageTestMessage
        {
            type = StageTestProtocol.ReplaceSongState,
            sessionId = _sessionId,
            revision = revision,
            stateJson = JsonSerializer.Serialize(_state, JsonOptions),
            positionSeconds = _state.positionSeconds,
            playing = _state.playing
        }, cancellationToken);
    }

    private async Task SendAsync(StageTestMessage message, CancellationToken cancellationToken = default)
    {
        StreamWriter? writer;
        lock (_connectionLock) writer = _writer;
        if (writer is null) return;
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(message, JsonOptions).AsMemory(), cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or SocketException)
        {
            PublishStatus("Stage-Verbindung unterbrochen · Wiederverbindung läuft");
        }
        finally { _writeLock.Release(); }
    }

    private static StageTestMessage? Deserialize(string? json) => string.IsNullOrWhiteSpace(json)
        ? null
        : JsonSerializer.Deserialize<StageTestMessage>(json, JsonOptions);

    private void OwnedProcessExited(object? sender, EventArgs eventArgs)
    {
        if (_lifetime is { IsCancellationRequested: false })
            PublishStatus("Stage wurde beendet");
    }

    public async Task StopAsync()
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is null && _ownedProcess is null) return;
        try
        {
            if (lifetime is not null)
                await SendAsync(new StageTestMessage { type = StageTestProtocol.Shutdown, sessionId = _sessionId });
            await Task.Delay(150);
        }
        catch { }
        lifetime?.Cancel();
        _listener?.Stop();
        _listener = null;
        lock (_connectionLock)
        {
            _client?.Dispose();
            _client = null;
            _writer = null;
        }
        if (_acceptTask is not null)
        {
            try { await _acceptTask.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch { }
            _acceptTask = null;
        }
        var process = Interlocked.Exchange(ref _ownedProcess, null);
        if (process is not null)
        {
            process.Exited -= OwnedProcessExited;
            try
            {
                if (!process.HasExited)
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1200)) process.Kill(entireProcessTree: true);
                }
            }
            catch { }
            process.Dispose();
        }
        lifetime?.Dispose();
        PublishStatus("Stage-Test beendet");
    }

    private void PublishStatus(string value) => StatusChanged?.Invoke(value);

    private static string? ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("NEONSTAGE_STAGE_EXECUTABLE")?.Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var expanded = Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured));
            if (OperatingSystem.IsMacOS() && Directory.Exists(expanded))
                return ResolveMacAppExecutable(expanded);
            return File.Exists(expanded) ? expanded : null;
        }

        if (OperatingSystem.IsMacOS())
        {
            var macRoots = ParentDirectories(AppContext.BaseDirectory)
                .Concat(ParentDirectories(Environment.CurrentDirectory)).Distinct();
            foreach (var root in macRoots)
            foreach (var app in new[]
                     {
                         Path.Combine(root, "src", "Karaoke.Stage.Unity", "Builds", "macOS", "NeonStage Karaoke.app"),
                         Path.Combine(root, "artifacts", "NeonStage Karaoke.app"),
                         Path.Combine(root, "NeonStage Karaoke.app"),
                         "/Applications/NeonStage Karaoke.app",
                         "/Applications/Neon Stage Karaoke.app"
                     })
                if (ResolveMacAppExecutable(app) is { } executable) return executable;
            return null;
        }

        var fileNames = OperatingSystem.IsWindows()
            ? new[] { "NeonStage.exe", "NeonStage-Stage.exe" }
            : new[] { "NeonStage", "NeonStage-Stage-x86_64.AppImage" };
        var roots = ParentDirectories(AppContext.BaseDirectory)
            .Concat(ParentDirectories(Environment.CurrentDirectory)).Distinct();
        foreach (var root in roots)
        foreach (var candidate in new[]
                 {
                     Path.Combine(root, "src", "Karaoke.Stage.Unity", "Builds", OperatingSystem.IsWindows() ? "Windows" : "Linux", fileNames[0]),
                     Path.Combine(root, "artifacts", fileNames[^1]),
                     Path.Combine(root, fileNames[0])
                 })
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }

    private static string? ResolveMacAppExecutable(string appPath)
    {
        foreach (var name in new[] { "Neon Stage Karaoke", "NeonStage Karaoke", "NeonStage", "Karaoke.Stage.Unity" })
        {
            var executable = Path.Combine(appPath, "Contents", "MacOS", name);
            if (File.Exists(executable)) return Path.GetFullPath(executable);
        }
        return null;
    }

    private static IEnumerable<string> ParentDirectories(string path)
    {
        var current = new DirectoryInfo(Path.GetFullPath(path));
        for (var depth = 0; current is not null && depth < 9; depth++, current = current.Parent)
            yield return current.FullName;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _writeLock.Dispose();
    }
}
