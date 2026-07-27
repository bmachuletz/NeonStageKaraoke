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
    private readonly TextMeshProUGUI[] _base = new TextMeshProUGUI[MaxLines];
    private readonly TextMeshProUGUI[] _fill = new TextMeshProUGUI[MaxLines];
    private readonly TextMeshProUGUI[][] _burn = new TextMeshProUGUI[MaxLines][];
    private readonly RectTransform[] _masks = new RectTransform[MaxLines];
    private readonly RectTransform[] _flames = new RectTransform[MaxLines];
    private readonly Image[] _flameImages = new Image[MaxLines];
    private readonly float[] _textWidths = new float[MaxLines];
    private readonly float[] _textLeft = new float[MaxLines];
    private readonly float[] _rowWidths = new float[MaxLines];
    private readonly float[] _lastProgress = new float[MaxLines];
    private readonly List<LyricParticle> _particles = new();
    private int _highlightCounter;
    private int _lineCount;
    private readonly GameObject _cueRoot;
    private readonly RectTransform _fuseTrack;
    private readonly RectTransform _fuseFill;
    private readonly RectTransform _fuseSpark;

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
        _root.anchorMin = new Vector2(0.06f, 0.49f);
        _root.anchorMax = new Vector2(0.94f, 0.75f);
        _root.offsetMin = Vector2.zero;
        _root.offsetMax = Vector2.zero;

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

    public void SetEntryCue(double remaining, bool hasPause, bool showCountdown)
    {
        var visible = hasPause && remaining > 0 && remaining <= 1.65;
        _cueRoot.SetActive(visible);
        if (!visible) return;
        var progress = Mathf.Clamp01(1f - (float)remaining / 1.65f);
        var width = 66f * progress;
        _fuseFill.anchoredPosition = new Vector2(-33, 0);
        _fuseFill.sizeDelta = new Vector2(Mathf.Max(2, width), 8);
        _fuseSpark.anchoredPosition = new Vector2(-33 + width, Mathf.Sin(Time.unscaledTime * 27f) * 1.2f);
        var sparkScale = 1f + Mathf.Sin(Time.unscaledTime * 31f) * .1f + Mathf.SmoothStep(0, .22f, progress);
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

    public void Show(string[] lines)
    {
        _lineCount = Math.Min(lines.Length, MaxLines);
        var height = _root.rect.height > 0 ? _root.rect.height : 410;
        var rowHeight = height / Math.Max(1, _lineCount);
        for (var index = 0; index < MaxLines; index++)
        {
            var visible = index < _lineCount;
            _base[index].transform.parent.gameObject.SetActive(visible);
            if (!visible) continue;
            var row = (RectTransform)_base[index].transform.parent;
            row.anchorMin = new Vector2(0, 1);
            row.anchorMax = new Vector2(1, 1);
            row.pivot = new Vector2(.5f, 1);
            row.anchoredPosition = new Vector2(0, -index * rowHeight);
            row.sizeDelta = new Vector2(0, rowHeight);
            _base[index].text = lines[index];
            _fill[index].text = lines[index];
            foreach (var burn in _burn[index]) burn.text = lines[index];
            Canvas.ForceUpdateCanvases();
            _base[index].ForceMeshUpdate();
            var rowWidth = row.rect.width > 0 ? row.rect.width : 1126;
            _textWidths[index] = Mathf.Min(rowWidth, _base[index].preferredWidth);
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
            var textHeight = Mathf.Clamp(rowHeight * .68f, 38f, 52f);
            cueRect.sizeDelta = new Vector2(76, textHeight + 8);
            _fuseTrack.sizeDelta = new Vector2(66, 8);
            _fuseFill.sizeDelta = new Vector2(_fuseFill.sizeDelta.x, 8);
            _fuseSpark.sizeDelta = new Vector2(10, textHeight * 1.12f);
            // The bar reaches slightly into the first glyph so its hot edge can
            // hand off directly to the lyric-progress flame.
            cueRect.anchoredPosition = new Vector2(firstTextStart - 28f, -rowHeight * .5f);
        }
    }

    public void SetProgress(int line, float progress, float pace, float audioImpact, string stageEffect)
    {
        if (line < 0 || line >= _lineCount) return;
        _masks[line].SetSizeWithCurrentAnchors(
            RectTransform.Axis.Horizontal,
            _textWidths[line] * Mathf.Clamp01(progress));
        var p = Mathf.Clamp01(progress);
        _flames[line].gameObject.SetActive(p > .002f && p < .998f);
        _flames[line].anchoredPosition = new Vector2(
            -_rowWidths[line] * .5f + _textLeft[line] + _textWidths[line] * p,
            Mathf.Sin(Time.unscaledTime * Mathf.Lerp(7f, 19f, pace) + line) * Mathf.Lerp(1.2f, 3.2f, pace));
        var pulse = 1f + Mathf.Sin(Time.unscaledTime * Mathf.Lerp(8f, 24f, pace) + line * 1.7f) * Mathf.Lerp(.07f, .18f, pace);
        _flames[line].sizeDelta = new Vector2(Mathf.Lerp(82f, 25f, pace), Mathf.Lerp(98f, 45f, pace));
        _flames[line].localScale = new Vector3(pulse, 1f + (pulse - 1f) * Mathf.Lerp(1.1f, 2.2f, pace), 1);
        _flameImages[line].color = Color.Lerp(
            new Color(1f, .3f, .015f, .9f),
            new Color(.86f, 1f, .03f, .88f), pace);
        var fillMaterial = _fill[line].fontMaterial;
        if (fillMaterial != null && fillMaterial.HasProperty(ShaderUtilities.ID_GlowPower))
        {
            fillMaterial.SetFloat(ShaderUtilities.ID_GlowOuter, Mathf.Lerp(.72f, .34f, pace));
            fillMaterial.SetFloat(ShaderUtilities.ID_GlowPower, Mathf.Lerp(.82f, .52f, pace));
            fillMaterial.SetColor(ShaderUtilities.ID_GlowColor,
                Color.Lerp(new Color(1f, .22f, .01f, .98f), new Color(.72f, 1f, .02f, .82f), pace));
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
            image.color = Color.Lerp(new Color(1f, .2f, .02f, .9f), new Color(.8f, 1f, .04f, .95f), UnityEngine.Random.value);
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
