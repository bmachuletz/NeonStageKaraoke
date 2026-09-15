using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Unity.Collections;
using NeonStage.Testing;
using NeonStage.Timing;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace NeonStage.Stage
{

public static class StageOfflineExporter
{
    private const int PipelineDepth = 4;
    private static Process? _activeProcess;
    private static Process? _activeVideoProcess;
    private static string _activePartialPath = "";

    public static void CancelActive()
    {
        try { if (_activeProcess is { HasExited: false }) _activeProcess.Kill(); }
        catch { }
        try { if (_activeVideoProcess is { HasExited: false }) _activeVideoProcess.Kill(); }
        catch { }
        try { if (!string.IsNullOrWhiteSpace(_activePartialPath) && File.Exists(_activePartialPath))
            File.Delete(_activePartialPath); }
        catch { }
        _activeProcess = null;
        _activeVideoProcess = null;
        _activePartialPath = "";
    }

    public static IEnumerator Run(StageTestMessage request, StageTestSongState song, FrameStageClock clock,
        string videoPath, double videoOffsetSeconds,
        Func<bool> cancelled, Action<int, int> progress, Action<string> completed, Action<string> failed)
    {
        var output = Path.GetFullPath(request.outputPath);
        var partial = output + ".partial.mp4";
        RenderTexture? target = null;
        Texture2D? pixels = null;
        Process? ffmpeg = null;
        BlockingCollection<FrameBuffer>? encoderFrames = null;
        Task? encoderWriter = null;
        Exception? encoderFailure = null;
        Camera? camera = null;
        var canvasStates = new List<CanvasState>();
        var previousMask = 0;
        var errors = new StringBuilder();
        VideoFrameReader? video = null;
        var totalFrames = 0;
        var useAsyncReadback = false;
        Exception? failure = null;
        try
        {
            if (song.durationSeconds <= 0) throw new InvalidOperationException("Die Songdauer fehlt.");
            var directory = Path.GetDirectoryName(output);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                throw new DirectoryNotFoundException("Der MP4-Zielordner existiert nicht.");
            if (File.Exists(partial)) File.Delete(partial);
            totalFrames = (int)Math.Ceiling(song.durationSeconds * request.framesPerSecond);
            camera = Camera.main ?? throw new InvalidOperationException("Keine Stage-Kamera gefunden.");
            target = new RenderTexture(request.width, request.height, 24, RenderTextureFormat.ARGB32)
            {
                name = "Neon Stage Offline Export"
            };
            target.Create();
            if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
            {
                video = new VideoFrameReader(videoPath, videoOffsetSeconds,
                    Math.Min(request.width, 1280), Math.Min(request.height, 720),
                    request.framesPerSecond);
                _activeVideoProcess = video.Process;
            }
            foreach (var canvas in UnityEngine.Object.FindObjectsByType<Canvas>())
            {
                canvasStates.Add(new CanvasState(canvas));
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = camera;
                canvas.planeDistance = 1;
            }
            previousMask = camera.cullingMask;
            camera.cullingMask = ~0;
            camera.targetTexture = target;
            var allowAsyncReadback = ShouldUseAsyncGpuReadback();
            if (allowAsyncReadback && SystemInfo.supportsAsyncGPUReadback)
            {
                camera.Render();
                var probe = AsyncGPUReadback.Request(target, 0);
                probe.WaitForCompletion();
                useAsyncReadback = !probe.hasError;
            }
            if (!useAsyncReadback)
                pixels = new Texture2D(request.width, request.height, TextureFormat.RGBA32, false, false);
            UnityEngine.Debug.Log($"Stage export GPU readback: {(useAsyncReadback ? "asynchronous" : "synchronous fallback")}");

            var audioUrl = $"{song.serverUrl.TrimEnd('/')}/api/songs/{song.songId}/audio";
            var arguments = $"-hide_banner -loglevel error -y -f rawvideo -pixel_format rgba " +
                            $"-video_size {request.width}x{request.height} -framerate {request.framesPerSecond} " +
                            $"-i pipe:0 -i {Quote(audioUrl)} -map 0:v:0 -map 1:a:0? -vf vflip " +
                            $"{EncoderArguments()} -pix_fmt yuv420p -c:a aac -b:a 192k " +
                            $"-shortest {Quote(partial)}";
            UnityEngine.Debug.Log("Stage export FFmpeg: ffmpeg " + arguments);
            ffmpeg = new Process
            {
                StartInfo = new ProcessStartInfo("ffmpeg", arguments)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };
            ffmpeg.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrWhiteSpace(eventArgs.Data)) errors.AppendLine(eventArgs.Data);
            };
            if (!ffmpeg.Start()) throw new InvalidOperationException("FFmpeg konnte nicht gestartet werden.");
            _activeProcess = ffmpeg;
            _activePartialPath = partial;
            ffmpeg.BeginErrorReadLine();
            encoderFrames = new BlockingCollection<FrameBuffer>(PipelineDepth);
            var encoderInput = ffmpeg.StandardInput.BaseStream;
            encoderWriter = Task.Run(() =>
            {
                try
                {
                    foreach (var frame in encoderFrames.GetConsumingEnumerable())
                    {
                        try { encoderInput.Write(frame.Buffer, 0, frame.Length); }
                        finally { ArrayPool<byte>.Shared.Return(frame.Buffer); }
                    }
                }
                catch (Exception exception) { encoderFailure = exception; }
            });
        }
        catch (Exception exception) { failure = exception; }

        var pendingReadbacks = new Queue<PendingReadback>();
        var renderedFrames = 0;
        var deliveredFrames = 0;
        var frameByteCount = 0;
        try { frameByteCount = checked(request.width * request.height * 4); }
        catch (Exception exception) { failure = exception; }
        while (failure == null && deliveredFrames < totalFrames)
        {
            if (cancelled())
            {
                failure = new OperationCanceledException("MP4-Export abgebrochen.");
                break;
            }
            if (encoderFailure != null) { failure = encoderFailure; break; }

            if (pendingReadbacks.Count > 0 && pendingReadbacks.Peek().IsDone)
            {
                if (encoderFrames!.Count >= PipelineDepth)
                {
                    yield return null;
                    continue;
                }
                var pending = pendingReadbacks.Peek();
                try
                {
                    if (!pending.TryDeliver(encoderFrames!, frameByteCount))
                        throw new InvalidOperationException("Der Encoder-Framepuffer ist unerwartet voll.");
                    pendingReadbacks.Dequeue();
                    deliveredFrames++;
                    if (deliveredFrames == 1 || deliveredFrames == totalFrames || deliveredFrames % 15 == 0)
                        progress(deliveredFrames, totalFrames);
                }
                catch (Exception exception) { failure = exception; }
                continue;
            }

            if (renderedFrames >= totalFrames || pendingReadbacks.Count >= PipelineDepth ||
                encoderFrames!.Count >= PipelineDepth)
            {
                yield return null;
                continue;
            }

            clock.SetFrame(renderedFrames, request.framesPerSecond);
            try { video?.ShowFrame(renderedFrames); }
            catch (Exception exception) { failure = exception; break; }
            yield return null;
            try
            {
                Canvas.ForceUpdateCanvases();
                camera!.Render();
                if (useAsyncReadback)
                {
                    pendingReadbacks.Enqueue(new PendingReadback(
                        AsyncGPUReadback.Request(target!, 0)));
                }
                else
                {
                    var previous = RenderTexture.active;
                    RenderTexture.active = target;
                    pixels!.ReadPixels(new Rect(0, 0, request.width, request.height), 0, 0, false);
                    RenderTexture.active = previous;
                    var source = pixels.GetRawTextureData();
                    var buffer = ArrayPool<byte>.Shared.Rent(frameByteCount);
                    Buffer.BlockCopy(source, 0, buffer, 0, frameByteCount);
                    if (!encoderFrames.TryAdd(new FrameBuffer(buffer, frameByteCount)))
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        throw new InvalidOperationException("Der Encoder-Framepuffer ist unerwartet voll.");
                    }
                    deliveredFrames++;
                    if (deliveredFrames == 1 || deliveredFrames == totalFrames || deliveredFrames % 15 == 0)
                        progress(deliveredFrames, totalFrames);
                }
                renderedFrames++;
            }
            catch (Exception exception) { failure = exception; }
        }

        encoderFrames?.CompleteAdding();
        if (failure != null)
        {
            try { if (ffmpeg is { HasExited: false }) ffmpeg.Kill(); } catch { }
        }
        while (encoderWriter is { IsCompleted: false }) yield return null;
        if (failure == null && encoderFailure != null) failure = encoderFailure;
        foreach (var pending in pendingReadbacks) pending.Dispose();

        if (failure == null)
        {
            try
            {
                ffmpeg!.StandardInput.Close();
                if (!ffmpeg.WaitForExit(120_000))
                {
                    ffmpeg.Kill();
                    throw new TimeoutException("FFmpeg hat den Export nicht abgeschlossen.");
                }
                if (ffmpeg.ExitCode != 0)
                    throw new InvalidOperationException("FFmpeg-Fehler: " + errors.ToString().Trim());
                if (File.Exists(output)) File.Delete(output);
                File.Move(partial, output);
            }
            catch (Exception exception) { failure = exception; }
        }

        if (failure != null)
        {
            try
            {
                if (ffmpeg is { HasExited: false }) ffmpeg.Kill();
                if (File.Exists(partial)) File.Delete(partial);
            }
            catch { }
            failed(failure.Message);
        }
        else completed(output);

        if (camera != null)
        {
            camera.targetTexture = null;
            camera.cullingMask = previousMask;
        }
        foreach (var state in canvasStates) state.Restore();
        if (target != null) { target.Release(); UnityEngine.Object.Destroy(target); }
        if (pixels != null) UnityEngine.Object.Destroy(pixels);
        ffmpeg?.Dispose();
        video?.Dispose();
        _activeProcess = null;
        _activeVideoProcess = null;
        _activePartialPath = "";
    }

    private static string Quote(string value) => '"' + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + '"';

    private static string EncoderArguments()
    {
        var selected = SelectedEncoder();
        return selected switch
        {
            "libx264" => "-c:v libx264 -preset fast -crf 18",
            "h264_nvenc" => "-c:v h264_nvenc -preset p4 -tune hq -rc vbr -cq 19 -b:v 0",
            _ => throw new InvalidOperationException(
                $"Unbekannter Export-Encoder '{selected}'. Erlaubt sind libx264 und h264_nvenc.")
        };
    }

    private static string SelectedEncoder()
    {
        var selected = Environment.GetEnvironmentVariable("NEONSTAGE_EXPORT_VIDEO_ENCODER")?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(selected) ? "libx264" : selected;
    }

    private static bool ShouldUseAsyncGpuReadback()
    {
        var configured = Environment.GetEnvironmentVariable("NEONSTAGE_EXPORT_GPU_READBACK")?.Trim().ToLowerInvariant();
        return configured switch
        {
            null or "" or "auto" => SelectedEncoder() != "h264_nvenc",
            "async" => true,
            "sync" => false,
            _ => throw new InvalidOperationException(
                $"Unbekannter GPU-Readback-Modus '{configured}'. Erlaubt sind auto, async und sync.")
        };
    }

    private readonly struct FrameBuffer
    {
        public FrameBuffer(byte[] buffer, int length) { Buffer = buffer; Length = length; }
        public byte[] Buffer { get; }
        public int Length { get; }
    }

    private sealed class PendingReadback : IDisposable
    {
        private readonly AsyncGPUReadbackRequest _request;
        private byte[]? _buffer;
        public PendingReadback(AsyncGPUReadbackRequest request) => _request = request;
        public bool IsDone => _request.done;

        public bool TryDeliver(BlockingCollection<FrameBuffer> destination, int length)
        {
            if (!_request.done) return false;
            if (_request.hasError) throw new InvalidOperationException("Asynchrones GPU-Readback ist fehlgeschlagen.");
            if (_buffer == null)
            {
                _buffer = ArrayPool<byte>.Shared.Rent(length);
                NativeArray<byte>.Copy(_request.GetData<byte>(), 0, _buffer, 0, length);
            }
            if (!destination.TryAdd(new FrameBuffer(_buffer, length))) return false;
            _buffer = null;
            return true;
        }

        public void Dispose()
        {
            if (_buffer != null) ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = null;
        }
    }

    private sealed class VideoFrameReader : IDisposable
    {
        private readonly Process _process;
        private readonly int _frameBytes;
        private readonly BlockingCollection<FrameBuffer> _frames = new(PipelineDepth);
        private readonly Task _decoderTask;
        private readonly Texture2D _texture;
        private readonly GameObject _canvasObject;
        private readonly int _leadFrames;
        private Exception? _decoderFailure;
        private bool _ended;
        public Process Process => _process;

        public VideoFrameReader(string path, double offsetSeconds, int width, int height, int fps)
        {
            _leadFrames = StageMediaTiming.VideoLeadFrames(offsetSeconds, fps);
            var sourceStart = StageMediaTiming.VideoSourceStartSeconds(offsetSeconds);
            var filter = $"fps={fps},scale={width}:{height}:force_original_aspect_ratio=decrease," +
                         $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black,vflip";
            var arguments = $"-hide_banner -loglevel error -ss {sourceStart.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                            $"-i {Quote(path)} -an -vf {Quote(filter)} -f rawvideo -pix_fmt rgba pipe:1";
            _process = new Process
            {
                StartInfo = new ProcessStartInfo("ffmpeg", arguments)
                {
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = false, CreateNoWindow = true
                }
            };
            if (!_process.Start()) throw new InvalidOperationException("FFmpeg-Videodecoder konnte nicht gestartet werden.");
            _frameBytes = checked(width * height * 4);
            _texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false)
            {
                name = "Neon Stage Offline Song Video"
            };
            Clear();
            _decoderTask = Task.Run(DecodeFrames);
            _canvasObject = new GameObject("Offline Song Video Canvas", typeof(Canvas), typeof(CanvasScaler));
            var canvas = _canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = -10;
            var scaler = _canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720);
            var imageObject = new GameObject("Offline Song Video", typeof(RectTransform), typeof(RawImage));
            imageObject.transform.SetParent(_canvasObject.transform, false);
            var image = imageObject.GetComponent<RawImage>();
            image.texture = _texture; image.raycastTarget = false;
            image.rectTransform.anchorMin = new Vector2(.055f, .075f);
            image.rectTransform.anchorMax = new Vector2(.945f, .91f);
            image.rectTransform.offsetMin = image.rectTransform.offsetMax = Vector2.zero;
        }

        public void ShowFrame(int songFrame)
        {
            if (songFrame < _leadFrames || _ended) return;
            if (!_frames.TryTake(out var frame, TimeSpan.FromSeconds(30)))
            {
                if (_decoderFailure != null) throw new InvalidOperationException(
                    "Hintergrundvideo konnte nicht dekodiert werden.", _decoderFailure);
                if (_frames.IsCompleted) { _ended = true; Clear(); return; }
                throw new TimeoutException("Der Hintergrundvideo-Decoder liefert keine Frames.");
            }
            try
            {
                _texture.LoadRawTextureData(frame.Buffer);
                _texture.Apply(false, false);
            }
            finally { ArrayPool<byte>.Shared.Return(frame.Buffer); }
        }

        private void Clear()
        {
            var empty = new byte[_frameBytes];
            _texture.LoadRawTextureData(empty);
            _texture.Apply(false, false);
        }

        private void DecodeFrames()
        {
            try
            {
                var stream = _process.StandardOutput.BaseStream;
                while (true)
                {
                    var buffer = ArrayPool<byte>.Shared.Rent(_frameBytes);
                    var offset = 0;
                    var delivered = false;
                    try
                    {
                        while (offset < _frameBytes)
                        {
                            var read = stream.Read(buffer, offset, _frameBytes - offset);
                            if (read <= 0) break;
                            offset += read;
                        }
                        if (offset < _frameBytes)
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                            break;
                        }
                        _frames.Add(new FrameBuffer(buffer, _frameBytes));
                        delivered = true;
                    }
                    catch
                    {
                        if (!delivered) ArrayPool<byte>.Shared.Return(buffer);
                        throw;
                    }
                }
            }
            catch (Exception exception) { _decoderFailure = exception; }
            finally { _frames.CompleteAdding(); }
        }

        public void Dispose()
        {
            try { _frames.CompleteAdding(); } catch { }
            try { if (!_process.HasExited) _process.Kill(); } catch { }
            try { _decoderTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
            while (_frames.TryTake(out var frame)) ArrayPool<byte>.Shared.Return(frame.Buffer);
            _process.Dispose();
            UnityEngine.Object.Destroy(_canvasObject);
            UnityEngine.Object.Destroy(_texture);
        }
    }

    private sealed class CanvasState
    {
        private readonly Canvas _canvas;
        private readonly RenderMode _mode;
        private readonly Camera? _camera;
        private readonly float _distance;
        public CanvasState(Canvas canvas)
        {
            _canvas = canvas; _mode = canvas.renderMode; _camera = canvas.worldCamera; _distance = canvas.planeDistance;
        }
        public void Restore()
        {
            if (_canvas == null) return;
            _canvas.renderMode = _mode; _canvas.worldCamera = _camera; _canvas.planeDistance = _distance;
        }
    }
}

}
