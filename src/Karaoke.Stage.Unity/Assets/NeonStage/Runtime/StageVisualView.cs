using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;
using System.Collections.Generic;

namespace NeonStage.Stage
{
public sealed class StageVisualView
{
    private readonly Material _material;
    private readonly RawImage _cover;
    private readonly float[] _spectrum = new float[128];
    private readonly GameObject _nextCard;
    private readonly RawImage _nextCover;
    private readonly TextMeshProUGUI _nextTitle;
    private readonly TextMeshProUGUI _nextSinger;
    private readonly GameObject _qrCard;
    private readonly RawImage _qrImage;
    private string _nextSongId = "";
    private float _energy, _bass, _pulse;
    private bool _promoting;
    private float _promotionStarted;
    private string _qrServer = "";
    private bool _qrLoaded, _qrLoading;
    private float _nextQrAttempt;
    private float _energyPeak = .001f, _bassPeak = .001f, _midPeak = .001f, _treblePeak = .001f;
    private float _mid, _treble;
    private readonly Canvas _transitionCanvas;
    private readonly TextMeshProUGUI _flyingTitle;
    private readonly TextMeshProUGUI _flyingArtist;
    private readonly List<TitleFragment> _titleFragments = new();
    public bool IsSongTransitioning => _promoting;
    public float AudioImpact => Mathf.Max(_bass, _pulse);

    public StageVisualView(GameObject host)
    {
        var backgroundCanvas = CreateCanvas("Stage Backdrop", host.transform, -20);
        var background = CreateImage("Audio Reactive Shader", backgroundCanvas.transform);
        background.rectTransform.anchorMin = Vector2.zero;
        background.rectTransform.anchorMax = Vector2.one;
        background.rectTransform.offsetMin = background.rectTransform.offsetMax = Vector2.zero;
        _material = StageBackgroundShaderCatalog.CreateMaterial();
        background.material = _material;

        var coverCanvas = CreateCanvas("Album Art Canvas", host.transform, 8);
        _cover = CreateImage("Album Art", coverCanvas.transform);
        _cover.rectTransform.anchorMin = _cover.rectTransform.anchorMax = new Vector2(.035f, .82f);
        _cover.rectTransform.pivot = new Vector2(0, .5f);
        _cover.rectTransform.sizeDelta = new Vector2(92, 92);
        _cover.color = new Color(1, 1, 1, .55f);
        _cover.gameObject.SetActive(false);

        var nextCanvas = CreateCanvas("Next Song Canvas", host.transform, 9);
        _nextCard = new GameObject("Als Nächstes", typeof(RectTransform), typeof(Image));
        _nextCard.transform.SetParent(nextCanvas.transform, false);
        var cardRect = _nextCard.GetComponent<RectTransform>();
        cardRect.anchorMin = cardRect.anchorMax = new Vector2(.965f, .245f);
        cardRect.pivot = new Vector2(1, 0);
        cardRect.sizeDelta = new Vector2(285, 94);
        _nextCard.GetComponent<Image>().color = new Color(.07f, .025f, .11f, .88f);
        _nextCover = CreateImage("Next Cover", _nextCard.transform);
        _nextCover.rectTransform.anchorMin = _nextCover.rectTransform.anchorMax = new Vector2(0, .5f);
        _nextCover.rectTransform.pivot = new Vector2(0, .5f);
        _nextCover.rectTransform.anchoredPosition = new Vector2(10, 0);
        _nextCover.rectTransform.sizeDelta = new Vector2(74, 74);
        _nextTitle = CreateText("Next Title", _nextCard.transform, new Vector2(94, 23), 18, Color.white);
        _nextSinger = CreateText("Next Singer", _nextCard.transform, new Vector2(94, -17), 13, new Color(1, .3f, .78f));
        _nextCard.SetActive(false);

        _transitionCanvas = CreateCanvas("Song Transition Canvas", host.transform, 30);
        _flyingTitle = CreateTransitionText("Flying Song Title", _transitionCanvas.transform, 31, new Color(.87f, 1f, .05f));
        _flyingArtist = CreateTransitionText("Flying Artist", _transitionCanvas.transform, 20, new Color(1f, .3f, .78f));
        _flyingTitle.gameObject.SetActive(false);
        _flyingArtist.gameObject.SetActive(false);

        var qrCanvas = CreateCanvas("Guest QR Canvas", host.transform, 11);
        _qrCard = new GameObject("Song wünschen", typeof(RectTransform), typeof(Image));
        _qrCard.transform.SetParent(qrCanvas.transform, false);
        var qrCardRect = _qrCard.GetComponent<RectTransform>();
        qrCardRect.anchorMin = qrCardRect.anchorMax = new Vector2(.025f, .235f);
        qrCardRect.pivot = new Vector2(0, 0);
        qrCardRect.sizeDelta = new Vector2(154, 180);
        _qrCard.GetComponent<Image>().color = new Color(.055f, .018f, .085f, .94f);
        _qrImage = CreateImage("QR Code", _qrCard.transform);
        _qrImage.rectTransform.anchorMin = _qrImage.rectTransform.anchorMax = new Vector2(.5f, 1);
        _qrImage.rectTransform.pivot = new Vector2(.5f, 1);
        _qrImage.rectTransform.anchoredPosition = new Vector2(0, -9);
        _qrImage.rectTransform.sizeDelta = new Vector2(136, 136);
        _qrImage.color = Color.white;
        var qrLabel = CreateText("QR Label", _qrCard.transform, new Vector2(9, -73), 14, new Color(.87f, 1f, .05f));
        var qrLabelRect = (RectTransform)qrLabel.transform;
        qrLabelRect.anchorMin = qrLabelRect.anchorMax = new Vector2(0, .5f);
        qrLabelRect.sizeDelta = new Vector2(136, 30);
        qrLabel.text = "S C A N  M E";
        qrLabel.fontStyle = FontStyles.Bold | FontStyles.Italic;
        qrLabel.alignment = TextAlignmentOptions.Center;
        _qrImage.color = new Color(.22f, .16f, .28f, 1f);
        _qrCard.SetActive(true);
    }

    public void SetSessionActive(bool active)
    {
        _qrCard.SetActive(active);
        if (!active)
        {
            _nextCard.SetActive(false);
            _cover.gameObject.SetActive(false);
        }
    }

    public async Task LoadQrAsync(string server, bool force = false)
    {
        if (_qrLoading || (_qrLoaded && !force)) return;
        _qrServer = server;
        _qrLoading = true;
        using var request = UnityWebRequestTexture.GetTexture($"{server}/api/stage/guest-qr", true);
        await request.SendWebRequest();
        if (request.result == UnityWebRequest.Result.Success)
        {
            var previousTexture = _qrImage.texture;
            _qrImage.texture = DownloadHandlerTexture.GetContent(request);
            _qrLoaded = _qrImage.texture != null;
            if (_qrLoaded) _qrImage.color = Color.white;
            if (previousTexture != null && previousTexture != _qrImage.texture)
                Object.Destroy(previousTexture);
        }
        _qrLoading = false;
        _nextQrAttempt = Time.unscaledTime + 5f;
    }

    public async Task SetNextAsync(string server, QueueEntryDto? entry)
    {
        if (entry?.song == null)
        {
            _nextSongId = "";
            _nextCard.SetActive(false);
            return;
        }
        _nextCard.SetActive(true);
        _nextTitle.text = $"{StageLocale.Text("ALS NÄCHSTES", "UP NEXT")}\n{entry.song.title}";
        _nextSinger.text = string.IsNullOrWhiteSpace(entry.requestedBy) ? entry.song.artist : $"{entry.song.artist} · {entry.requestedBy}";
        if (_nextSongId == entry.song.id) return;
        _nextSongId = entry.song.id;
        using var request = UnityWebRequestTexture.GetTexture($"{server}/api/songs/{entry.song.id}/cover", true);
        await request.SendWebRequest();
        if (_nextSongId != entry.song.id) return;
        _nextCover.texture = request.result == UnityWebRequest.Result.Success ? DownloadHandlerTexture.GetContent(request) : null;
        _nextCover.color = _nextCover.texture == null ? new Color(.25f, .16f, .32f, 1) : Color.white;
    }

    public void BeginSongTransition(SongDto song, string oldTitle, string oldArtist)
    {
        if (song.id != _nextSongId || _nextCover.texture == null) return;
        _cover.texture = _nextCover.texture;
        _cover.gameObject.SetActive(true);
        _promoting = true;
        _promotionStarted = Time.unscaledTime;
        _nextCard.SetActive(false);
        _flyingTitle.text = song.title;
        _flyingArtist.text = song.artist;
        _flyingTitle.gameObject.SetActive(true);
        _flyingArtist.gameObject.SetActive(true);
        ExplodeOldHeading(oldTitle, oldArtist);
    }

    public async Task LoadCoverAsync(string server, string songId)
    {
        var promoteFromNext = songId == _nextSongId && _nextCover.texture != null;
        if (promoteFromNext)
        {
            _cover.texture = _nextCover.texture;
            _cover.gameObject.SetActive(true);
            _promoting = true;
            _promotionStarted = Time.unscaledTime;
            _nextCard.SetActive(false);
        }
        using var request = UnityWebRequestTexture.GetTexture($"{server}/api/songs/{songId}/cover", true);
        await request.SendWebRequest();
        if (request.result != UnityWebRequest.Result.Success) { _cover.gameObject.SetActive(false); return; }
        _cover.texture = DownloadHandlerTexture.GetContent(request);
        _cover.gameObject.SetActive(_cover.texture != null);
    }

    public void Update(StageAudioEngine audio)
    {
        if (!_qrLoaded && !_qrLoading && !string.IsNullOrWhiteSpace(_qrServer) && Time.unscaledTime >= _nextQrAttempt)
            _ = LoadQrAsync(_qrServer);
        audio.GetSpectrum(_spectrum);
        var energy = 0f; var bass = 0f; var mid = 0f; var treble = 0f;
        for (var i = 0; i < _spectrum.Length; i++)
        {
            energy += _spectrum[i];
            if (i < 10) bass += _spectrum[i];
            else if (i < 48) mid += _spectrum[i];
            else treble += _spectrum[i];
        }
        _energyPeak = Mathf.Max(energy, _energyPeak * Mathf.Exp(-Time.unscaledDeltaTime * .55f));
        _bassPeak = Mathf.Max(bass, _bassPeak * Mathf.Exp(-Time.unscaledDeltaTime * .7f));
        _midPeak = Mathf.Max(mid, _midPeak * Mathf.Exp(-Time.unscaledDeltaTime * .75f));
        _treblePeak = Mathf.Max(treble, _treblePeak * Mathf.Exp(-Time.unscaledDeltaTime * .8f));
        var normalizedEnergy = Mathf.Clamp01(energy / Mathf.Max(.0001f, _energyPeak));
        var newBass = Mathf.Clamp01(bass / Mathf.Max(.0001f, _bassPeak));
        var newMid = Mathf.Clamp01(mid / Mathf.Max(.0001f, _midPeak));
        var newTreble = Mathf.Clamp01(treble / Mathf.Max(.0001f, _treblePeak));
        _energy = Mathf.Lerp(_energy, normalizedEnergy, Time.unscaledDeltaTime * 6);
        _pulse = Mathf.Max(_pulse * Mathf.Exp(-Time.unscaledDeltaTime * 5.5f), Mathf.Max(0, newBass - _bass) * 7);
        _bass = Mathf.Lerp(_bass, newBass, Time.unscaledDeltaTime * 7);
        _mid = Mathf.Lerp(_mid, newMid, Time.unscaledDeltaTime * 7);
        _treble = Mathf.Lerp(_treble, newTreble, Time.unscaledDeltaTime * 8);
        SetShaderFloat("_Energy", _energy);
        SetShaderFloat("_Bass", _bass);
        SetShaderFloat("_Mid", _mid);
        SetShaderFloat("_Treble", _treble);
        SetShaderFloat("_Pulse", _pulse);
        SetShaderFloat("_SongTime", (float)audio.PositionSeconds);
        SetShaderFloat("_IsPlaying", audio.IsPlaying ? 1f : 0f);
        if (_promoting)
        {
            var t = Mathf.Clamp01((Time.unscaledTime - _promotionStarted) / 1.35f);
            var eased = t < .5f ? 2f * t * t : 1f - Mathf.Pow(-2f * t + 2f, 2f) * .5f;
            _cover.rectTransform.anchorMin = _cover.rectTransform.anchorMax = Vector2.Lerp(new Vector2(.91f, .31f), new Vector2(.035f, .82f), eased);
            _cover.rectTransform.pivot = Vector2.Lerp(new Vector2(.5f, .5f), new Vector2(0, .5f), eased);
            var dramaticSize = Vector2.Lerp(new Vector2(74, 74), new Vector2(92, 92), eased) + Vector2.one * Mathf.Sin(t * Mathf.PI) * 125f;
            _cover.rectTransform.sizeDelta = dramaticSize;
            _cover.color = new Color(1, 1, Mathf.Lerp(.72f, 1f, eased), Mathf.Lerp(1f, .55f, eased));
            var launch = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(.2f, .88f, t));
            _flyingTitle.rectTransform.anchoredPosition = Vector2.Lerp(new Vector2(470, -125), new Vector2(0, 318), launch);
            _flyingArtist.rectTransform.anchoredPosition = Vector2.Lerp(new Vector2(485, -145), new Vector2(0, 282), launch);
            var textScale = Mathf.Lerp(.35f, 1f, launch);
            _flyingTitle.rectTransform.localScale = Vector3.one * textScale;
            _flyingArtist.rectTransform.localScale = Vector3.one * textScale;
            if (t >= 1)
            {
                _promoting = false;
                _flyingTitle.gameObject.SetActive(false);
                _flyingArtist.gameObject.SetActive(false);
            }
        }
        UpdateTitleFragments();
    }

    private void SetShaderFloat(string property, float value)
    {
        if (_material.HasProperty(property)) _material.SetFloat(property, value);
    }

    private void ExplodeOldHeading(string title, string artist)
    {
        SpawnHeadingFragments(title, 300, new Color(.87f, 1f, .05f));
        SpawnHeadingFragments(artist, 265, new Color(1f, .3f, .78f));
    }

    private void SpawnHeadingFragments(string text, float y, Color color)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var visible = text.Length > 28 ? text.Substring(0, 28) : text;
        var spacing = Mathf.Min(24f, 620f / Mathf.Max(1, visible.Length));
        var start = -spacing * (visible.Length - 1) * .5f;
        for (var index = 0; index < visible.Length; index++)
        {
            if (char.IsWhiteSpace(visible[index])) continue;
            var fragment = CreateTransitionText("Title Fragment", _transitionCanvas.transform, 21, color);
            fragment.text = visible[index].ToString();
            fragment.rectTransform.sizeDelta = new Vector2(30, 36);
            fragment.rectTransform.anchoredPosition = new Vector2(start + index * spacing, y);
            _titleFragments.Add(new TitleFragment(fragment, new Vector2((index - visible.Length * .5f) * 3.5f, Random.Range(45f, 125f)), Random.Range(-260f, 260f)));
        }
    }

    private void UpdateTitleFragments()
    {
        var dt = Time.unscaledDeltaTime;
        for (var index = _titleFragments.Count - 1; index >= 0; index--)
        {
            var fragment = _titleFragments[index];
            fragment.Life -= dt;
            fragment.Velocity += Vector2.down * 145f * dt;
            fragment.Text.rectTransform.anchoredPosition += fragment.Velocity * dt;
            fragment.Text.rectTransform.Rotate(0, 0, fragment.Spin * dt);
            var color = fragment.Text.color; color.a = Mathf.Clamp01(fragment.Life / .8f); fragment.Text.color = color;
            if (fragment.Life > 0) continue;
            Object.Destroy(fragment.Text.gameObject);
            _titleFragments.RemoveAt(index);
        }
    }

    private static TextMeshProUGUI CreateTransitionText(string name, Transform parent, float size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f); rect.pivot = new Vector2(.5f, .5f); rect.sizeDelta = new Vector2(900, 52);
        var text = go.GetComponent<TextMeshProUGUI>(); text.fontSize = size; text.fontStyle = FontStyles.Bold;
        text.alignment = TextAlignmentOptions.Center; text.color = color; text.raycastTarget = false;
        return text;
    }

    private sealed class TitleFragment
    {
        public TitleFragment(TextMeshProUGUI text, Vector2 velocity, float spin) { Text = text; Velocity = velocity; Spin = spin; Life = 1.05f; }
        public TextMeshProUGUI Text { get; }
        public Vector2 Velocity;
        public float Spin { get; }
        public float Life;
    }

    private static Canvas CreateCanvas(string name, Transform parent, int order)
    {
        var go = new GameObject(name, typeof(Canvas), typeof(CanvasScaler)); go.transform.SetParent(parent, false);
        var canvas = go.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = order;
        var scaler = go.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1280,720);
        return canvas;
    }
    private static RawImage CreateImage(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(RawImage)); go.transform.SetParent(parent,false);
        var image = go.GetComponent<RawImage>(); image.raycastTarget=false; return image;
    }
    private static TextMeshProUGUI CreateText(string name, Transform parent, Vector2 position, float size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI)); go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = new Vector2(0, .5f); rect.pivot = new Vector2(0, .5f);
        rect.anchoredPosition = position; rect.sizeDelta = new Vector2(180, 48);
        var text = go.GetComponent<TextMeshProUGUI>(); text.fontSize = size; text.fontStyle = FontStyles.Bold;
        text.color = color; text.alignment = TextAlignmentOptions.Left; text.overflowMode = TextOverflowModes.Ellipsis; text.raycastTarget = false;
        return text;
    }
}
}
