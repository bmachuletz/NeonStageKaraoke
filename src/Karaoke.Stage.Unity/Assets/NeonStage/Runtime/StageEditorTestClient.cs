using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NeonStage.Testing;
using UnityEngine;

namespace NeonStage.Stage
{

public sealed class StageEditorTestClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _sessionId;
    private readonly string _token;
    private readonly Action<StageTestMessage> _received;
    private readonly string _stageVersion;
    private readonly ConcurrentQueue<Action> _mainThread = new();
    private readonly object _writeLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private TcpClient? _client;
    private StreamWriter? _writer;

    public StageEditorTestClient(string host, int port, string sessionId, string token,
        Action<StageTestMessage> received)
    {
        _host = host;
        _port = port;
        _sessionId = sessionId;
        _token = token;
        _received = received;
        _stageVersion = Application.version;
        _ = Task.Run(ConnectionLoopAsync);
    }

    public void Update()
    {
        while (_mainThread.TryDequeue(out var action)) action();
    }

    public void Send(string type, long revision = 0, double positionSeconds = 0,
        bool playing = false, string details = "", int frame = 0, int totalFrames = 0,
        string outputPath = "") => Send(new StageTestMessage
    {
        type = type,
        protocolVersion = StageTestProtocol.Version,
        sessionId = _sessionId,
        revision = revision,
        positionSeconds = positionSeconds,
        playing = playing,
        details = details,
        frame = frame,
        totalFrames = totalFrames,
        outputPath = outputPath
    });

    private async Task ConnectionLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(_host, _port);
                if (_lifetime.IsCancellationRequested) { client.Dispose(); break; }
                using (client)
                using (var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 16 * 1024, true))
                using (var writer = new StreamWriter(client.GetStream(), new UTF8Encoding(false), 16 * 1024, true)
                       { AutoFlush = true, NewLine = "\n" })
                {
                    lock (_writeLock)
                    {
                        _client = client;
                        _writer = writer;
                    }
                    Send(new StageTestMessage
                    {
                        type = StageTestProtocol.Hello,
                        protocolVersion = StageTestProtocol.Version,
                        sessionId = _sessionId,
                        token = _token,
                        stageVersion = _stageVersion
                    });
                    string? line;
                    while (!_lifetime.IsCancellationRequested && (line = await reader.ReadLineAsync()) != null)
                    {
                        var message = JsonUtility.FromJson<StageTestMessage>(line);
                        if (message == null || message.sessionId != _sessionId) continue;
                        _mainThread.Enqueue(() => _received(message));
                    }
                }
            }
            catch (Exception exception)
            {
                if (!_lifetime.IsCancellationRequested)
                    Debug.LogWarning("Editor test connection: " + exception.Message);
            }
            finally
            {
                lock (_writeLock)
                {
                    _writer = null;
                    _client = null;
                }
            }
            if (!_lifetime.IsCancellationRequested)
                try { await Task.Delay(300, _lifetime.Token); } catch (OperationCanceledException) { }
        }
    }

    private void Send(StageTestMessage message)
    {
        lock (_writeLock)
        {
            if (_writer == null) return;
            try { _writer.WriteLine(JsonUtility.ToJson(message)); }
            catch (Exception exception) { Debug.LogWarning("Editor test send: " + exception.Message); }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        lock (_writeLock)
        {
            try { _client?.Close(); } catch { }
            _writer = null;
            _client = null;
        }
        _lifetime.Dispose();
    }
}

}
