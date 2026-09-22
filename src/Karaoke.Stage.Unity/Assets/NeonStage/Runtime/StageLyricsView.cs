using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NeonStage.Stage
{
public sealed class StageLyricsView
{
    private const int MaxLines = 8;
    private readonly RectTransform _root;
    private readonly Image _backdrop;
    private readonly TextMeshProUGUI[] _base = new TextMeshProUGUI[MaxLines];
    private readonly TextMeshProUGUI[] _fill = new TextMeshProUGUI[MaxLines];
    private readonly TextMeshProUGUI[][] _burn = new TextMeshProUGUI[MaxLines][];
    private readonly RectTransform[] _masks = new RectTransform[MaxLines];
    private readonly RectTransform[] _flames = new RectTransform[MaxLines];
    private readonly Image[] _flameImages = new Image[MaxLines];
    private readonly float[] _textWidths = new float[MaxLines];
    private readonly float[][] _glyphAdvances = new float[MaxLines][];
    private readonly float[] _textLeft = new float[MaxLines];
    private readonly float[] _rowWidths = new float[MaxLines];
    private readonly float[] _rowHeights = new float[MaxLines];
    private readonly float[] _rowCenterY = new float[MaxLines];
    private readonly float[] _lastProgress = new float[MaxLines];
    private readonly int[] _voiceLanes = new int[MaxLines];
    private readonly List<LyricParticle> _particles = new();
    private int _highlightCounter;
    private int _lineCount;
    private readonly GameObject _cueRoot;
    private readonly RectTransform _fuseTrack;
    private readonly RectTransform _fuseFill;
    private readonly RectTransform _fuseSpark;
    private string _presentationStyle = "neon";
    private bool _videoBackground;
    private bool _videoPerformanceMode;
    private float _songTime;
    private Color _unsungColor = Color.white;
    private Color _sungColor = new(0.875f, 1f, 0.157f, 1f);
    private Color _glowColor = new(1f, 0.314f, 0.031f, 1f);

    public StageLyricsView(GameObject host)
    {
        var canvasObject = new GameObject("Lyrics Canvas", typeof(Canvas), typeof(CanvasScaler));
        canvasObject.transform.SetParent(host.transform, false);
        var canvas = canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 20;
        var scaler = canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        scaler.matchWidthOrHeight = 0.5f;

        var rootObject = new GameObject("Lyrics", typeof(RectTransform));
        rootObject.transform.SetParent(canvasObject.transform, false);
        _root = rootObject.GetComponent<RectTransform>();
        // Keep the singing area clear of the persistent QR and transport bar.
        // Moving the complete block (instead of individual rows) preserves all
        // section spacing and word-progress geometry.
        // The complete lyrics page lives between the corner cards/header and
        // the transport bar. Section pagination keeps this deliberately
        // smaller safe area readable instead of allowing text to spill out.
        _root.anchorMin = new Vector2(0.12f, 0.50f);
        _root.anchorMax = new Vector2(0.94f, 0.73f);
        _root.offsetMin = Vector2.zero;
        _root.offsetMax = Vector2.zero;

        var backdrop = CreatePanel("Lyrics Glass Backdrop", _root, Color.clear);
        backdrop.anchorMin = new Vector2(-.035f, -.22f);
        backdrop.anchorMax = new Vector2(1.035f, 1.22f);
        backdrop.offsetMin = backdrop.offsetMax = Vector2.zero;
        _backdrop = backdrop.GetComponent<Image>();
        _backdrop.raycastTarget = false;
        _backdrop.sprite = CreateRoundedPanelSprite();
        _backdrop.type = Image.Type.Sliced;
        _backdrop.gameObject.SetActive(false);

        for (var index = 0; index < MaxLines; index++) CreateLine(index);

        _cueRoot = new GameObject("Entry Fuse", typeof(RectTransform));
        _cueRoot.transform.SetParent(_root, false);
        var cueRect = _cueRoot.GetComponent<RectTransform>();
        cueRect.anchorMin = cueRect.anchorMax = new Vector2(.5f, 1f);
        cueRect.pivot = new Vector2(.5f, .5f);
        cueRect.anchoredPosition = Vector2.zero;
        cueRect.sizeDelta = new Vector2(76, 30);
        _fuseTrack = CreatePanel("Fuse Track", cueRect, new Color(.23f, .12f, .28f, .9f));
        _fuseTrack.anchorMin = _fuseTrack.anchorMax = new Vector2(.5f, .5f);
        _fuseTrack.sizeDelta = new Vector2(66, 8);
        var fuseSprite = Sprite.Create(CreateFuseTexture(), new Rect(0, 0, 128, 32), new Vector2(.5f, .5f));
        _fuseTrack.GetComponent<Image>().sprite = fuseSprite;
        _fuseTrack.GetComponent<Image>().color = new Color(.55f, .19f, .08f, .72f);
        _fuseFill = CreatePanel("Burning Fuse", cueRect, new Color(1f, .38f, .02f, 1f));
        _fuseFill.anchorMin = _fuseFill.anchorMax = new Vector2(.5f, .5f);
        _fuseFill.pivot = new Vector2(0, .5f);
        _fuseFill.GetComponent<Image>().sprite = fuseSprite;
        _fuseSpark = CreatePanel("Fuse Spark", cueRect, new Color(.9f, 1f, .05f, 1f));
        _fuseSpark.anchorMin = _fuseSpark.anchorMax = new Vector2(.5f, .5f);
        _fuseSpark.sizeDelta = new Vector2(10, 48);
        _fuseSpark.GetComponent<Image>().sprite = Sprite.Create(CreateFlameTexture(), new Rect(0, 0, 64, 64), new Vector2(.5f, .5f));
        _cueRoot.SetActive(false);
        Show(Array.Empty<string>());
    }

    public void SetPresentationStyle(string? style)
    {
        var normalized = string.IsNullOrWhiteSpace(style) ? "neon" : style.Trim().ToLowerInvariant();
        if (string.Equals(_presentationStyle, normalized, StringComparison.Ordinal)) return;
        _presentationStyle = normalized;
        var milkGlass = normalized == "milk-glass";
        ApplyBackdrop(milkGlass);
        for (var index = 0; index < _lineCount; index++)
            ApplyVoicePalette(index, _voiceLanes[index]);
    }

    public void SetVideoBackground(bool active)
    {
        _videoBackground = active;
        ApplyBackdrop(_presentationStyle == "milk-glass");
        for (var index = 0; index < _lineCount; index++)
            ApplyVoicePalette(index, _voiceLanes[index]);
    }

    public void SetKaraokeColors(string? unsung, string? sung, string? glow)
    {
        _unsungColor = ParseColor(unsung, Color.white);
        _sungColor = ParseColor(sung, new Color(.875f, 1f, .157f, 1f));
        _glowColor = ParseColor(glow, new Color(1f, .314f, .031f, 1f));
        for (var index = 0; index < _lineCount; index++)
            ApplyVoicePalette(index, _voiceLanes[index]);
    }

    private static Color ParseColor(string? value, Color fallback) =>
        !string.IsNullOrWhiteSpace(value) && ColorUtility.TryParseHtmlString(value, out var parsed)
            ? new Color(parsed.r, parsed.g, parsed.b, 1f)
            : fallback;

    public void SetVideoPerformanceMode(bool active)
    {
        if (_videoPerformanceMode == active) return;
        _videoPerformanceMode = active;
        for (var line = 0; line < MaxLines; line++)
        {
            foreach (var burn in _burn[line]) burn.gameObject.SetActive(!active);
            if (active) _flames[line].gameObject.SetActive(false);
        }
        if (!active) return;
        foreach (var particle in _particles) UnityEngine.Object.Destroy(particle.Rect.gameObject);
        _particles.Clear();
    }

    public void SetSongTime(double seconds) => _songTime = (float)System.Math.Max(0, seconds);

    private void ApplyBackdrop(bool milkGlass)
    {
        _backdrop.gameObject.SetActive(milkGlass || _videoBackground);
        _backdrop.color = milkGlass
            ? new Color(.010f, .016f, .030f, .985f)
            : _videoBackground ? new Color(.010f, .016f, .030f, .82f) : Color.clear;
    }

    public void SetEntryCue(double remaining, bool hasPause, bool showCountdown)
    {
        var visible = hasPause && remaining > 0 && remaining <= 1.65;
        _cueRoot.SetActive(visible);
        if (!visible) return;
        var progress = Mathf.Clamp01(1f - (float)remaining / 1.65f);
        var width = 66f * progress;
        _fuseFill.anchoredPosition = new Vector2(-33, 0);
        _fuseFill.sizeDelta = new Vector2(Mathf.Max(2, width), 8);
        _fuseSpark.anchoredPosition = new Vector2(-33 + width, Mathf.Sin(_songTime * 27f) * 1.2f);
        var sparkScale = 1f + Mathf.Sin(_songTime * 31f) * .1f + Mathf.SmoothStep(0, .22f, progress);
        _fuseSpark.localScale = new Vector3(sparkScale, 1f + progress * .18f, 1);
        if (_lineCount > 0 && progress > .68f)
        {
            var handoff = Mathf.InverseLerp(.68f, 1f, progress);
            _flames[0].gameObject.SetActive(true);
            _flames[0].anchoredPosition = new Vector2(-_rowWidths[0] * .5f + _textLeft[0] + handoff * 7f, 0);
            _flames[0].sizeDelta = new Vector2(Mathf.Lerp(18, 34, handoff), _fuseTrack.sizeDelta.y * 1.15f);
            _flameImages[0].color = Color.Lerp(new Color(1f, .2f, .04f, .28f), new Color(.86f, 1f, .03f, .82f), handoff);
        }
    }

    public void Show(string[] lines) => Show(lines, null);

    public void Show(string[] lines, int[]? voiceLanes)
    {
        _lineCount = Math.Min(lines.Length, MaxLines);
        var laneOrder = new List<int>();
        var laneLineCounts = new Dictionary<int, int>();
        for (var index = 0; index < _lineCount; index++)
        {
            var lane = voiceLanes != null && index < voiceLanes.Length
                ? Math.Max(0, voiceLanes[index])
                : 0;
            _voiceLanes[index] = lane;
            if (!laneLineCounts.ContainsKey(lane))
            {
                laneOrder.Add(lane);
                laneLineCounts[lane] = 0;
            }
            laneLineCounts[lane]++;
        }
        laneOrder.Sort();

        // A duet needs two genuinely separate singing areas. Keep the upper
        // edge fixed so the lead layout does not jump, and grow only towards
        // the transport bar when another voice is present.
        // The single-voice block sits vertically between cover and QR card.
        // A duet needs more height; in that case reserve the left QR/cover
        // column and the right next-song card horizontally as well.
        _root.anchorMin = laneOrder.Count > 1
            ? new Vector2(.16f, .35f)
            : new Vector2(.12f, .50f);
        _root.anchorMax = laneOrder.Count > 1
            ? new Vector2(.74f, .73f)
            : new Vector2(.94f, .73f);
        Canvas.ForceUpdateCanvases();
        var height = _root.rect.height > 0 ? _root.rect.height : 410;
        var laneGap = laneOrder.Count > 1 ? Mathf.Clamp(height * .055f, 22f, 34f) : 0f;
        var laneHeight = (height - laneGap * Math.Max(0, laneOrder.Count - 1)) /
                         Math.Max(1, laneOrder.Count);
        var laneRowsUsed = new Dictionary<int, int>();
        for (var index = 0; index < MaxLines; index++)
        {
            var visible = index < _lineCount;
            _base[index].transform.parent.gameObject.SetActive(visible);
            if (!visible) continue;
            var row = (RectTransform)_base[index].transform.parent;
            var lane = _voiceLanes[index];
            var laneRank = laneOrder.IndexOf(lane);
            var rowInLane = laneRowsUsed.TryGetValue(lane, out var used) ? used : 0;
            laneRowsUsed[lane] = rowInLane + 1;
            var rowHeight = laneHeight / Math.Max(1, laneLineCounts[lane]);
            var laneTop = laneRank * (laneHeight + laneGap);
            row.anchorMin = new Vector2(0, 1);
            row.anchorMax = new Vector2(1, 1);
            row.pivot = new Vector2(.5f, 1);
            row.anchoredPosition = new Vector2(0, -(laneTop + rowInLane * rowHeight));
            row.sizeDelta = new Vector2(0, rowHeight);
            _rowHeights[index] = rowHeight;
            _rowCenterY[index] = -(laneTop + (rowInLane + .5f) * rowHeight);
            _base[index].text = lines[index];
            _fill[index].text = lines[index];
            ApplyVoicePalette(index, lane);
            foreach (var burn in _burn[index]) burn.text = lines[index];
            Canvas.ForceUpdateCanvases();
            _base[index].ForceMeshUpdate();
            var rowWidth = row.rect.width > 0 ? row.rect.width : 1126;
            _glyphAdvances[index] = MeasureGlyphAdvances(_base[index], rowWidth);
            _textWidths[index] = _glyphAdvances[index].Length > 0
                ? _glyphAdvances[index][^1]
                : Mathf.Min(rowWidth, _base[index].preferredWidth);
            _rowWidths[index] = rowWidth;
            _textLeft[index] = (rowWidth - _textWidths[index]) * .5f;
            _masks[index].anchoredPosition = new Vector2(_textLeft[index], 0);
            var fillRect = (RectTransform)_fill[index].transform;
            fillRect.anchoredPosition = new Vector2(-_textLeft[index], 0);
            fillRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, rowWidth);
            for (var burnIndex = 0; burnIndex < _burn[index].Length; burnIndex++)
            {
                var burn = _burn[index][burnIndex];
                var burnRect = (RectTransform)burn.transform;
                var x = burnIndex is 0 or 2 ? -2.2f : 2.2f;
                var y = burnIndex < 2 ? 2.2f : -2.2f;
                burnRect.anchoredPosition = new Vector2(-_textLeft[index] + x, y);
                burnRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, rowWidth);
            }
            _lastProgress[index] = 0;
            SetProgress(index, 0, .35f, 0, "Automatic");
        }
        if (_lineCount > 0)
        {
            var firstTextStart = -_rowWidths[0] * .5f + _textLeft[0];
            var cueRect = (RectTransform)_cueRoot.transform;
            var textHeight = Mathf.Clamp(_rowHeights[0] * .68f, 38f, 52f);
            cueRect.sizeDelta = new Vector2(76, textHeight + 8);
            _fuseTrack.sizeDelta = new Vector2(66, 8);
            _fuseFill.sizeDelta = new Vector2(_fuseFill.sizeDelta.x, 8);
            _fuseSpark.sizeDelta = new Vector2(10, textHeight * 1.12f);
            // The bar reaches slightly into the first glyph so its hot edge can
            // hand off directly to the lyric-progress flame.
            cueRect.anchoredPosition = new Vector2(firstTextStart - 28f, _rowCenterY[0]);
        }
    }

    public void SetProgress(int line, float progress, float pace, float audioImpact, string stageEffect)
    {
        if (line < 0 || line >= _lineCount) return;
        var p = Mathf.Clamp01(progress);
        var progressWidth = GlyphProgressWidth(line, p);
        _masks[line].SetSizeWithCurrentAnchors(
            RectTransform.Axis.Horizontal,
            progressWidth);
        if (_videoPerformanceMode)
        {
            _fill[line].transform.localScale = Vector3.one;
            _lastProgress[line] = p;
            return;
        }
        _flames[line].gameObject.SetActive(!_videoPerformanceMode && p > .002f && p < .998f);
        _flames[line].anchoredPosition = new Vector2(
            -_rowWidths[line] * .5f + _textLeft[line] + progressWidth,
            Mathf.Sin(_songTime * Mathf.Lerp(7f, 19f, pace) + line) * Mathf.Lerp(1.2f, 3.2f, pace));
        var pulse = 1f + Mathf.Sin(_songTime * Mathf.Lerp(8f, 24f, pace) + line * 1.7f) * Mathf.Lerp(.07f, .18f, pace);
        _flames[line].sizeDelta = new Vector2(Mathf.Lerp(82f, 25f, pace), Mathf.Lerp(98f, 45f, pace));
        _flames[line].localScale = new Vector3(pulse, 1f + (pulse - 1f) * Mathf.Lerp(1.1f, 2.2f, pace), 1);
        var slowColor = WithAlpha(_glowColor, .9f);
        var fastColor = WithAlpha(_sungColor, .88f);
        _flameImages[line].color = Color.Lerp(slowColor, fastColor, pace);
        var fillMaterial = _fill[line].fontMaterial;
        if (fillMaterial != null && fillMaterial.HasProperty(ShaderUtilities.ID_GlowPower))
        {
            fillMaterial.SetFloat(ShaderUtilities.ID_GlowOuter, Mathf.Lerp(.72f, .34f, pace));
            fillMaterial.SetFloat(ShaderUtilities.ID_GlowPower, Mathf.Lerp(.82f, .52f, pace));
            fillMaterial.SetColor(ShaderUtilities.ID_GlowColor, WithAlpha(_glowColor, Mathf.Lerp(.98f, .82f, pace)));
        }
        if (_lastProgress[line] < .985f && p >= .985f &&
            (audioImpact >= .22f || !string.Equals(stageEffect, "Automatic", StringComparison.OrdinalIgnoreCase)))
        {
            var explodes = string.Equals(stageEffect, "Shatter", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(stageEffect, "EmberBurst", StringComparison.OrdinalIgnoreCase) ||
                           (string.Equals(stageEffect, "Automatic", StringComparison.OrdinalIgnoreCase) && (_highlightCounter++ & 1) == 0);
            if (!string.Equals(stageEffect, "Pulse", StringComparison.OrdinalIgnoreCase)) SpawnHighlight(line, explodes);
            else _fill[line].transform.localScale = new Vector3(1.035f, 1.035f, 1);
        }
        else if (p < .985f) _fill[line].transform.localScale = Vector3.one;
        _lastProgress[line] = p;
    }

    private float GlyphProgressWidth(int line, float progress)
    {
        var advances = _glyphAdvances[line];
        if (advances == null || advances.Length < 2) return _textWidths[line] * progress;
        var characterPosition = progress * (advances.Length - 1);
        var left = Mathf.Clamp(Mathf.FloorToInt(characterPosition), 0, advances.Length - 1);
        var right = Math.Min(left + 1, advances.Length - 1);
        return Mathf.Lerp(advances[left], advances[right], characterPosition - left);
    }

    private static float[] MeasureGlyphAdvances(TextMeshProUGUI text, float availableWidth)
    {
        var info = text.textInfo;
        var count = info?.characterCount ?? 0;
        if (count <= 0) return Array.Empty<float>();
        var result = new float[count + 1];
        var origin = info!.characterInfo[0].origin;
        for (var index = 0; index < count; index++)
        {
            var character = info.characterInfo[index];
            result[index] = Math.Max(result[index], character.origin - origin);
            result[index + 1] = Math.Max(result[index], character.xAdvance - origin);
        }
        var measuredWidth = result[^1];
        if (measuredWidth <= 0) return Array.Empty<float>();
        var scale = Math.Min(1f, availableWidth / measuredWidth);
        if (scale < 1f)
            for (var index = 1; index < result.Length; index++) result[index] *= scale;
        return result;
    }

    public void TickEffects(float deltaTime)
    {
        for (var index = _particles.Count - 1; index >= 0; index--)
        {
            var particle = _particles[index];
            particle.Life -= deltaTime;
            if (particle.Life <= 0)
            {
                UnityEngine.Object.Destroy(particle.Rect.gameObject);
                _particles.RemoveAt(index);
                continue;
            }
            particle.Velocity += new Vector2(0, particle.Explodes ? -35f : 13f) * deltaTime;
            particle.Rect.anchoredPosition += particle.Velocity * deltaTime;
            particle.Rect.Rotate(0, 0, particle.Spin * deltaTime);
            var color = particle.Image.color;
            color.a = Mathf.Clamp01(particle.Life / particle.MaxLife);
            particle.Image.color = color;
        }
    }

    private void SpawnHighlight(int line, bool explodes)
    {
        var row = (RectTransform)_base[line].transform.parent;
        var count = explodes ? 28 : 20;
        for (var index = 0; index < count; index++)
        {
            var go = new GameObject(explodes ? "Lyric Spark" : "Lyric Dissolve", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(row, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f);
            rect.sizeDelta = Vector2.one * UnityEngine.Random.Range(3f, 8f);
            rect.anchoredPosition = new Vector2(
                UnityEngine.Random.Range(-_textWidths[line] * .48f, _textWidths[line] * .48f),
                UnityEngine.Random.Range(-18f, 18f));
            var direction = explodes
                ? new Vector2(rect.anchoredPosition.x * .45f, UnityEngine.Random.Range(25f, 95f))
                : new Vector2(UnityEngine.Random.Range(-12f, 12f), UnityEngine.Random.Range(10f, 42f));
            var image = go.GetComponent<Image>();
            image.raycastTarget = false;
            image.color = Color.Lerp(WithAlpha(_glowColor, .9f), WithAlpha(_sungColor, .95f),
                UnityEngine.Random.value);
            var life = UnityEngine.Random.Range(.45f, explodes ? .8f : 1.15f);
            _particles.Add(new LyricParticle(rect, image, direction, UnityEngine.Random.Range(-220f, 220f), life, explodes));
        }
    }

    private sealed class LyricParticle
    {
        public LyricParticle(RectTransform rect, Image image, Vector2 velocity, float spin, float life, bool explodes)
        { Rect = rect; Image = image; Velocity = velocity; Spin = spin; Life = MaxLife = life; Explodes = explodes; }
        public RectTransform Rect { get; }
        public Image Image { get; }
        public Vector2 Velocity;
        public float Spin { get; }
        public float Life;
        public float MaxLife { get; }
        public bool Explodes { get; }
    }

    public void SetAlpha(float alpha)
    {
        var group = _root.GetComponent<CanvasGroup>();
        if (group == null) group = _root.gameObject.AddComponent<CanvasGroup>();
        group.alpha = Mathf.Clamp01(alpha);
    }

    private void CreateLine(int index)
    {
        var rowObject = new GameObject($"Line {index + 1}", typeof(RectTransform));
        rowObject.transform.SetParent(_root, false);
        var row = rowObject.GetComponent<RectTransform>();

        _base[index] = CreateText("Base", row, new Color(0.92f, 0.87f, 0.96f));
        var flameObject = new GameObject("Singing Flame", typeof(RectTransform), typeof(Image));
        flameObject.transform.SetParent(row, false);
        _flames[index] = flameObject.GetComponent<RectTransform>();
        _flames[index].anchorMin = _flames[index].anchorMax = new Vector2(.5f, .5f);
        _flames[index].pivot = new Vector2(.5f, .5f);
        _flames[index].sizeDelta = new Vector2(34, 54);
        var flame = flameObject.GetComponent<Image>();
        _flameImages[index] = flame;
        flame.sprite = Sprite.Create(CreateFlameTexture(), new Rect(0, 0, 64, 64), new Vector2(.5f, .5f));
        flame.color = new Color(.82f, 1f, .05f, .7f);
        flame.raycastTarget = false;
        var maskObject = new GameObject("Neon Fill Mask", typeof(RectTransform), typeof(RectMask2D));
        maskObject.transform.SetParent(row, false);
        _masks[index] = maskObject.GetComponent<RectTransform>();
        _masks[index].anchorMin = new Vector2(0, 0);
        _masks[index].anchorMax = new Vector2(0, 1);
        _masks[index].pivot = new Vector2(0, .5f);
        _masks[index].anchoredPosition = Vector2.zero;
        _masks[index].sizeDelta = Vector2.zero;
        _burn[index] = new[]
        {
            CreateText("Burn Left Top", _masks[index], new Color(1f, .25f, .02f, .72f)),
            CreateText("Burn Right Top", _masks[index], new Color(1f, .48f, .01f, .72f)),
            CreateText("Burn Left Bottom", _masks[index], new Color(1f, .18f, .02f, .72f)),
            CreateText("Burn Right Bottom", _masks[index], new Color(1f, .62f, .01f, .72f))
        };
        var burnOffsets = new[] { new Vector2(-2, 2), new Vector2(2, 2), new Vector2(-2, -2), new Vector2(2, -2) };
        for (var burnIndex = 0; burnIndex < _burn[index].Length; burnIndex++)
        {
            var burnRect = (RectTransform)_burn[index][burnIndex].transform;
            burnRect.anchorMin = new Vector2(0, 0);
            burnRect.anchorMax = new Vector2(0, 1);
            burnRect.pivot = new Vector2(0, .5f);
            burnRect.anchoredPosition = burnOffsets[burnIndex];
            burnRect.sizeDelta = new Vector2(1126, 0);
        }
        _fill[index] = CreateText("Neon Fill", _masks[index], new Color(0.87f, 1f, 0.05f));
        var fillRect = (RectTransform)_fill[index].transform;
        fillRect.anchorMin = new Vector2(0, 0);
        fillRect.anchorMax = new Vector2(0, 1);
        fillRect.pivot = new Vector2(0, .5f);
        fillRect.anchoredPosition = Vector2.zero;
        fillRect.sizeDelta = new Vector2(1126, 0);
    }

    private void ApplyVoicePalette(int index, int lane)
    {
        var milkGlass = _presentationStyle == "milk-glass";
        var highContrast = milkGlass || _videoBackground;
        _base[index].color = _unsungColor;
        _fill[index].color = _sungColor;
        _base[index].outlineColor = highContrast ? new Color32(0, 0, 0, 255) : new Color32(30, 10, 40, 210);
        _base[index].outlineWidth = highContrast ? .24f : .11f;
        ConfigureTextUnderlay(_base[index], highContrast);

        var burnColors = new[]
        {
            WithAlpha(_glowColor, .72f),
            WithAlpha(Color.Lerp(_glowColor, _sungColor, .28f), .72f),
            WithAlpha(Color.Lerp(_glowColor, Color.black, .12f), .72f),
            WithAlpha(Color.Lerp(_glowColor, _sungColor, .5f), .72f)
        };
        for (var burnIndex = 0; burnIndex < _burn[index].Length; burnIndex++)
            _burn[index][burnIndex].color = burnColors[burnIndex];

        var material = _fill[index].fontMaterial;
        if (material == null) return;
        _fill[index].outlineColor = highContrast ? new Color32(0, 0, 0, 255) : ToColor32(_glowColor, 220);
        _fill[index].outlineWidth = highContrast ? .20f : .16f;
        ConfigureTextUnderlay(_fill[index], highContrast);
        if (material.HasProperty(ShaderUtilities.ID_GlowColor))
            material.SetColor(ShaderUtilities.ID_GlowColor, WithAlpha(_glowColor, .86f));
    }

    private static Color WithAlpha(Color color, float alpha) => new(color.r, color.g, color.b, alpha);

    private static Color32 ToColor32(Color color, byte alpha) => new(
        (byte)Mathf.RoundToInt(color.r * 255), (byte)Mathf.RoundToInt(color.g * 255),
        (byte)Mathf.RoundToInt(color.b * 255), alpha);

    private static void ConfigureTextUnderlay(TextMeshProUGUI text, bool enabled)
    {
        var material = text.fontMaterial;
        if (material == null || !material.HasProperty(ShaderUtilities.ID_UnderlayColor)) return;
        if (!enabled)
        {
            material.DisableKeyword(ShaderUtilities.Keyword_Underlay);
            return;
        }

        // A centred, soft black underlay behaves like a local contrast mask;
        // unlike a displaced shadow it does not create the disliked double-text
        // appearance and remains legible over animated cyan/magenta lines.
        material.EnableKeyword(ShaderUtilities.Keyword_Underlay);
        material.SetColor(ShaderUtilities.ID_UnderlayColor, new Color(0f, 0f, 0f, .92f));
        material.SetFloat(ShaderUtilities.ID_UnderlayOffsetX, 0f);
        material.SetFloat(ShaderUtilities.ID_UnderlayOffsetY, 0f);
        material.SetFloat(ShaderUtilities.ID_UnderlayDilate, .42f);
        material.SetFloat(ShaderUtilities.ID_UnderlaySoftness, .18f);
    }

    private static Sprite CreateRoundedPanelSprite()
    {
        const int size = 64;
        const float radius = 11f;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
        texture.wrapMode = TextureWrapMode.Clamp;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var nearestX = Mathf.Clamp(x, radius, size - 1 - radius);
            var nearestY = Mathf.Clamp(y, radius, size - 1 - radius);
            var distance = Vector2.Distance(new Vector2(x, y), new Vector2(nearestX, nearestY));
            var alpha = 1f - Mathf.SmoothStep(radius - 1.5f, radius + .5f, distance);
            texture.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
        }
        texture.Apply();
        return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), 100,
            0, SpriteMeshType.FullRect, new Vector4(14, 14, 14, 14));
    }

    private static Texture2D CreateFlameTexture()
    {
        var texture = new Texture2D(64, 64, TextureFormat.RGBA32, false);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 64; x++)
        {
            var dx = (x - 31.5f) / 25f;
            var dy = (y - 25f) / 34f;
            var radial = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
            var lick = Mathf.Clamp01(1f - Mathf.Abs(dx + Mathf.Sin(y * .22f) * .12f) * 2.8f) * Mathf.Clamp01((y - 24f) / 30f);
            var alpha = Mathf.Pow(Mathf.Max(radial * .65f, lick * .55f), 2f) * .68f;
            texture.SetPixel(x, y, new Color(1f, .9f, .05f, alpha));
        }
        texture.Apply();
        return texture;
    }

    private static Texture2D CreateFuseTexture()
    {
        var texture = new Texture2D(128, 32, TextureFormat.RGBA32, false);
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 128; x++)
        {
            var edge = Mathf.Min(Mathf.Min(x, 127 - x), Mathf.Min(y, 31 - y));
            var rounded = edge >= 3 || Vector2.Distance(new Vector2(x < 64 ? x : 127 - x, y < 16 ? y : 31 - y), new Vector2(3, 3)) <= 3;
            if (!rounded) { texture.SetPixel(x, y, Color.clear); continue; }
            var t = x / 127f;
            var shimmer = .82f + Mathf.Sin(x * .24f + y * .38f) * .18f;
            var color = Color.Lerp(new Color(1f, .08f, .36f), new Color(.72f, 1f, .02f), t);
            color = Color.Lerp(color, new Color(1f, .42f, .02f), Mathf.Sin(t * Mathf.PI) * .45f);
            color *= shimmer;
            color.a = edge < 2 ? .75f : 1f;
            texture.SetPixel(x, y, color);
        }
        texture.Apply();
        return texture;
    }

    private static RectTransform CreatePanel(string name, Transform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        go.GetComponent<Image>().color = color;
        go.GetComponent<Image>().raycastTarget = false;
        return rect;
    }

    private static TextMeshProUGUI CreateText(string name, Transform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = Vector2.zero;
        rect.offsetMax = Vector2.zero;
        var text = go.GetComponent<TextMeshProUGUI>();
        text.color = color;
        text.alignment = TextAlignmentOptions.Center;
        text.fontStyle = FontStyles.Bold;
        text.enableAutoSizing = true;
        text.fontSizeMin = 20;
        text.fontSizeMax = 43;
        text.enableWordWrapping = false;
        text.overflowMode = TextOverflowModes.Ellipsis;
        text.raycastTarget = false;
        // A restrained SDF outline keeps the words readable over the animated
        // backdrop. The currently sung neon layer gets the stronger bloom.
        var isBurn = name.StartsWith("Burn", StringComparison.Ordinal);
        text.outlineColor = isBurn ? Color.clear : name == "Neon Fill"
            ? new Color32(176, 255, 0, 210)
            : new Color32(75, 25, 100, 190);
        text.outlineWidth = isBurn ? 0 : name == "Neon Fill" ? 0.16f : 0.09f;
        var material = text.fontMaterial;
        if (!isBurn && material != null && material.HasProperty(ShaderUtilities.ID_GlowPower))
        {
            material.SetColor(ShaderUtilities.ID_GlowColor, name == "Neon Fill"
                ? new Color(0.72f, 1f, 0.02f, 0.82f)
                : new Color(0.55f, 0.18f, 0.72f, 0.32f));
            material.SetFloat(ShaderUtilities.ID_GlowOffset, 0.1f);
            material.SetFloat(ShaderUtilities.ID_GlowInner, 0.05f);
            material.SetFloat(ShaderUtilities.ID_GlowOuter, name == "Neon Fill" ? 0.38f : 0.18f);
            material.SetFloat(ShaderUtilities.ID_GlowPower, name == "Neon Fill" ? 0.55f : 0.35f);
            material.EnableKeyword("GLOW_ON");
        }
        return text;
    }
}
}
