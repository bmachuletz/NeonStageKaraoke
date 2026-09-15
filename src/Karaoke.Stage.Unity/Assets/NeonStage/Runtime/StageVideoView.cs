using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using UnityEngine.Video;
using NeonStage.Timing;

namespace NeonStage.Stage
{
public sealed class StageVideoView
{
    private readonly GameObject _canvasObject;
    private readonly RawImage _image;
    private readonly AspectRatioFitter _aspect;
    private readonly VideoPlayer _player;
    private readonly RenderTexture? _desktopTexture;
    private int _offsetMilliseconds;
    private string _songId = "";
    private string _cachedPath = "";
    private bool _firstFrameLogged;
    private bool _primingFirstFrame;
    private float _primingStartedAt;
    private bool _followsAudio;
    private float _lastSeekAt = float.NegativeInfinity;
    private bool _deleteCachedOnStop;
    private string _prefetchedSongId = "";
    private readonly Dictionary<string, Task<string?>> _androidDownloads = new();
    public bool Active => _canvasObject.activeSelf;
    public double OffsetSeconds => _offsetMilliseconds / 1000d;

    public string DetachForOfflineExport()
    {
        _player.Stop();
        _player.sendFrameReadyEvents = false;
        _canvasObject.SetActive(false);
        return File.Exists(_cachedPath) ? _cachedPath : "";
    }

    public void CompleteOfflineExport() => DeleteCachedVideo();

    public StageVideoView(GameObject host)
    {
        _canvasObject = new GameObject("Song Video Canvas", typeof(Canvas), typeof(CanvasScaler));
        _canvasObject.transform.SetParent(host.transform, false);
        var canvas = _canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Shader: -20, Video: -10, sämtliche Stage-Karten: 8+, Lyrics: 20.
        // Damit kann das Video niemals Lyrics oder Bedienobjekte überdecken.
        canvas.sortingOrder = -10;
        var scaler = _canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        scaler.matchWidthOrHeight = .5f;
        var imageObject = new GameObject("Muted Song Video", typeof(RectTransform), typeof(RawImage),
            typeof(AspectRatioFitter));
        imageObject.transform.SetParent(_canvasObject.transform, false);
        _image = imageObject.GetComponent<RawImage>();
        _image.rectTransform.anchorMin = new Vector2(.055f, .075f);
        _image.rectTransform.anchorMax = new Vector2(.945f, .91f);
        _image.rectTransform.offsetMin = _image.rectTransform.offsetMax = Vector2.zero;
        _image.color = Color.white;
        _image.raycastTarget = false;
        _image.enabled = true;
        _aspect = imageObject.GetComponent<AspectRatioFitter>();
        _aspect.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
        _aspect.aspectRatio = 16f / 9f;

        _player = host.AddComponent<VideoPlayer>();
        _player.source = VideoSource.Url;
        _player.playOnAwake = false;
        _player.isLooping = false;
        _player.skipOnDrop = true;
        _player.waitForFirstFrame = true;
        _player.audioOutputMode = VideoAudioOutputMode.None;
        if (Application.platform == RuntimePlatform.Android)
        {
            _player.renderMode = VideoRenderMode.APIOnly;
        }
        else
        {
            // The Linux decoder-owned APIOnly texture can remain black even
            // though frameReady fires. Copying into an ordinary render texture
            // gives uGUI a stable, renderable texture across seeks.
            _desktopTexture = new RenderTexture(1280, 720, 0, RenderTextureFormat.ARGB32)
            {
                name = "Neon Stage Song Video"
            };
            _desktopTexture.Create();
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.targetTexture = _desktopTexture;
            _image.texture = _desktopTexture;
        }
        _player.sendFrameReadyEvents = false;
        _player.prepareCompleted += player =>
        {
            if (_desktopTexture == null) _image.texture = player.texture;
            if (player.width > 0 && player.height > 0)
                _aspect.aspectRatio = (float)player.width / player.height;
            Debug.Log($"Stage video prepared: {player.width}x{player.height}, {player.frameRate:0.##} fps");
            // Linux exposes seeking and the first decoded texture only after
            // playback has been kicked once. Update() immediately takes over
            // pause/seek state from the audio engine on the following frame.
            _primingFirstFrame = true;
            _primingStartedAt = Time.unscaledTime;
            player.Play();
        };
        _player.started += _ => Debug.Log("Stage video playback started");
        _player.frameReady += (player, frame) =>
        {
            if (_desktopTexture == null) _image.texture = player.texture;
            // This callback only primes the initial decoder output. Leaving it
            // enabled used to reset _followsAudio after every frame, causing a
            // new seek on every Update and permanently starving the decoder.
            player.sendFrameReadyEvents = false;
            if (_primingFirstFrame)
            {
                _primingFirstFrame = false;
                _followsAudio = false;
            }
            if (_firstFrameLogged) return;
            _firstFrameLogged = true;
            Debug.Log($"Stage video first frame: {frame}");
        };
        _player.errorReceived += (_, message) => Debug.LogError("Stage video error: " + message);
        _canvasObject.SetActive(false);
    }

    public async Task LoadAsync(string server, string songId)
    {
        _songId = songId;
        using var infoRequest = UnityWebRequest.Get($"{server}/api/songs/{songId}/video/info");
        await infoRequest.SendWebRequest();
        if (_songId != songId) return;
        if (infoRequest.result != UnityWebRequest.Result.Success)
        {
            Stop();
            return;
        }
        var info = JsonUtility.FromJson<SongVideoInfoDto>(infoRequest.downloadHandler.text);
        _offsetMilliseconds = info?.offsetMilliseconds ?? 0;
        _player.Stop();
        _firstFrameLogged = false;
        _primingFirstFrame = false;
        _followsAudio = false;
        _lastSeekAt = float.NegativeInfinity;
        var android = Application.platform == RuntimePlatform.Android;
        var extension = android ? "mp4" : "webm";
        var endpoint = android ? "video.android.mp4" : "video.webm";
        string? downloadPath;
        if (android)
            downloadPath = await GetAndroidCacheAsync(server, songId, info?.updatedAt ?? "");
        else
        {
            DeleteCachedVideo();
            downloadPath = Path.Combine(Application.temporaryCachePath,
                $"neon-stage-{songId}.video.{extension}");
            if (File.Exists(downloadPath)) File.Delete(downloadPath);
            using var videoRequest = UnityWebRequest.Get($"{server}/api/songs/{songId}/{endpoint}");
            videoRequest.downloadHandler = new DownloadHandlerFile(downloadPath) { removeFileOnAbort = true };
            await videoRequest.SendWebRequest();
            if (videoRequest.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Stage video download failed ({videoRequest.responseCode}): {videoRequest.error}");
                downloadPath = null;
            }
        }
        if (_songId != songId || string.IsNullOrWhiteSpace(downloadPath) || !File.Exists(downloadPath))
        {
            if (_songId == songId) Stop();
            return;
        }
        _cachedPath = downloadPath;
        _deleteCachedOnStop = !android;
        // A local seekable file avoids Unitys incomplete HTTP streaming path.
        _player.url = new Uri(downloadPath).AbsoluteUri;
        Debug.Log("Stage video loading: " + _player.url);
        _canvasObject.SetActive(true);
        _player.sendFrameReadyEvents = true;
        _player.Prepare();
    }

    public void Update(StageClockFrame stageTime)
    {
        if (!_canvasObject.activeSelf || !_player.isPrepared) return;
        // Some Linux VideoPlayer backends replace the decoder-owned texture on
        // seek without preserving the object exposed during frame zero.
        if (_desktopTexture == null && _player.texture != null && _image.texture != _player.texture)
            _image.texture = _player.texture;
        // Linux liefert die APIOnly-Textur erst nach dem ersten wirklich
        // dekodierten Frame. Vorheriges Pause/Seek lässt den nativen VP8-Pfad
        // in einem vorbereiteten, aber bildlosen Zustand stehen. Ein kurzer
        // ungestörter Prime-Lauf erzeugt die Textur; danach übernimmt wieder
        // ausschließlich die Audiozeit die Synchronisierung.
        if (_primingFirstFrame)
        {
            if (Time.unscaledTime - _primingStartedAt < 1.5f) return;
            Debug.LogWarning("Stage video first-frame priming timed out; continuing with normal synchronization");
            _primingFirstFrame = false;
        }
        var desired = stageTime.PositionSeconds - _offsetMilliseconds / 1000d;
        if (desired < 0)
        {
            if (_player.isPlaying) _player.Pause();
            if (_player.canSetTime && _player.time > .02) _player.time = 0;
            _followsAudio = false;
            return;
        }
        if (stageTime.IsPlaying)
        {
            // Anchor once when playback starts. Repeated sub-second seeking
            // flushes Androids MediaCodec pipeline and was the main source of
            // visible stutter. Large drift is still corrected with one seek.
            if (!_followsAudio)
            {
                if (_player.canSetTime)
                {
                    _player.time = desired;
                    _lastSeekAt = Time.unscaledTime;
                }
                if (!_player.isPlaying) _player.Play();
                _followsAudio = true;
            }
            else if (_player.canSetTime && _player.isPlaying &&
                     Time.unscaledTime - _lastSeekAt > 1.25f &&
                     System.Math.Abs(_player.time - desired) > .8)
            {
                _player.time = desired;
                _lastSeekAt = Time.unscaledTime;
            }
            return;
        }
        if (_player.isPlaying) _player.Pause();
        _followsAudio = false;
        if (_player.canSetTime && Time.unscaledTime - _lastSeekAt > .2f &&
            System.Math.Abs(_player.time - desired) > .06)
        {
            _player.time = desired;
            _lastSeekAt = Time.unscaledTime;
        }
    }

    public void Stop()
    {
        _songId = "";
        _player.Stop();
        _player.sendFrameReadyEvents = false;
        _primingFirstFrame = false;
        _followsAudio = false;
        _lastSeekAt = float.NegativeInfinity;
        _canvasObject.SetActive(false);
        DeleteCachedVideo();
    }

    public async Task PrefetchAsync(string server, string songId)
    {
        if (Application.platform != RuntimePlatform.Android || string.IsNullOrWhiteSpace(songId) ||
            _prefetchedSongId == songId) return;
        _prefetchedSongId = songId;
        try
        {
            using var infoRequest = UnityWebRequest.Get($"{server}/api/songs/{songId}/video/info");
            await infoRequest.SendWebRequest();
            if (infoRequest.result != UnityWebRequest.Result.Success) return;
            var info = JsonUtility.FromJson<SongVideoInfoDto>(infoRequest.downloadHandler.text);
            await GetAndroidCacheAsync(server, songId, info?.updatedAt ?? "");
        }
        catch (Exception exception) { Debug.LogWarning("Stage video prefetch failed: " + exception.Message); }
    }

    private Task<string?> GetAndroidCacheAsync(string server, string songId, string updatedAt)
    {
        var version = server.TrimEnd('/') + "|" + updatedAt;
        var directory = Path.Combine(Application.persistentDataPath, "VideoCache");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"{songId}.android.mp4");
        var marker = target + ".version";
        if (File.Exists(target) && File.Exists(marker) && File.ReadAllText(marker) == version)
        {
            File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
            return Task.FromResult<string?>(target);
        }
        var key = songId + "|" + version;
        if (_androidDownloads.TryGetValue(key, out var running)) return running;
        var task = DownloadAndroidCacheAsync(server, songId, version, target, marker, key);
        _androidDownloads[key] = task;
        return task;
    }

    private async Task<string?> DownloadAndroidCacheAsync(string server, string songId, string version,
        string target, string marker, string key)
    {
        var partial = target + ".download";
        try
        {
            if (File.Exists(partial)) File.Delete(partial);
            using var request = UnityWebRequest.Get($"{server}/api/songs/{songId}/video.android.mp4");
            request.downloadHandler = new DownloadHandlerFile(partial) { removeFileOnAbort = true };
            await request.SendWebRequest();
            if (request.result != UnityWebRequest.Result.Success)
            {
                Debug.LogError($"Stage video download failed ({request.responseCode}): {request.error}");
                return null;
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(partial, target);
            File.WriteAllText(marker, version);
            TrimAndroidCache(Path.GetDirectoryName(target)!, target);
            return target;
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
            _androidDownloads.Remove(key);
        }
    }

    private static void TrimAndroidCache(string directory, string current)
    {
        const long maximumBytes = 1024L * 1024 * 1024;
        const int maximumFiles = 12;
        var files = Directory.EnumerateFiles(directory, "*.android.mp4")
            .Select(path => new FileInfo(path)).OrderByDescending(file => file.LastWriteTimeUtc).ToList();
        var bytes = files.Sum(file => file.Length);
        while (files.Count > maximumFiles || bytes > maximumBytes)
        {
            var victim = files
                .Where(file => !string.Equals(file.FullName, current, StringComparison.OrdinalIgnoreCase))
                .LastOrDefault();
            if (victim == null) break;
            bytes -= victim.Length;
            files.Remove(victim);
            try
            {
                victim.Delete();
                var marker = victim.FullName + ".version";
                if (File.Exists(marker)) File.Delete(marker);
            }
            catch (IOException) { }
        }
    }

    private void DeleteCachedVideo()
    {
        if (string.IsNullOrWhiteSpace(_cachedPath)) return;
        try { if (_deleteCachedOnStop && File.Exists(_cachedPath)) File.Delete(_cachedPath); }
        catch (IOException) { }
        _cachedPath = "";
        _deleteCachedOnStop = false;
    }
}
}
