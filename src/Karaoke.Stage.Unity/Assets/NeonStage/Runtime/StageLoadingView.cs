using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace NeonStage.Stage
{
public sealed class StageLoadingView
{
    private readonly GameObject _canvasObject;
    private readonly CanvasGroup _group;
    private readonly float _startedAt;

    public bool Finished { get; private set; }

    public StageLoadingView(GameObject host)
    {
        _startedAt = Time.unscaledTime;
        _canvasObject = new GameObject("Neon Stage Loading", typeof(Canvas), typeof(CanvasScaler), typeof(CanvasGroup));
        _canvasObject.transform.SetParent(host.transform, false);
        var canvas = _canvasObject.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 1000;
        var scaler = _canvasObject.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1280, 720);
        scaler.matchWidthOrHeight = .5f;
        _group = _canvasObject.GetComponent<CanvasGroup>();

        var backdrop = CreateImage("Backdrop", _canvasObject.transform, new Color(.025f, .004f, .045f));
        backdrop.rectTransform.anchorMin = Vector2.zero;
        backdrop.rectTransform.anchorMax = Vector2.one;
        backdrop.rectTransform.offsetMin = backdrop.rectTransform.offsetMax = Vector2.zero;

        var icon = new GameObject("Neon Microphone", typeof(RectTransform), typeof(RawImage));
        icon.transform.SetParent(_canvasObject.transform, false);
        var iconRect = icon.GetComponent<RectTransform>();
        iconRect.anchorMin = iconRect.anchorMax = new Vector2(.5f, .56f);
        iconRect.sizeDelta = new Vector2(260, 260);
        var iconImage = icon.GetComponent<RawImage>();
        iconImage.texture = Resources.Load<Texture2D>("NeonStageIcon");
        iconImage.raycastTarget = false;

        var title = CreateText("Title", _canvasObject.transform, new Vector2(0, -105), 43, new Color(.87f, 1f, .05f));
        title.text = "NEON STAGE";
        var loading = CreateText("Loading", _canvasObject.transform, new Vector2(0, -157), 18, new Color(1f, .3f, .78f));
        loading.text = StageLocale.Text("BÜHNE WIRD GELADEN  ·  ·  ·", "LOADING STAGE  ·  ·  ·");
    }

    public void Update()
    {
        if (Finished) return;
        var elapsed = Time.unscaledTime - _startedAt;
        if (elapsed < 1.35f) return;
        _group.alpha = 1f - Mathf.Clamp01((elapsed - 1.35f) / .55f);
        if (elapsed < 1.9f) return;
        Finished = true;
        Object.Destroy(_canvasObject);
    }

    private static Image CreateImage(string name, Transform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        go.transform.SetParent(parent, false);
        var image = go.GetComponent<Image>(); image.color = color; image.raycastTarget = false; return image;
    }

    private static TextMeshProUGUI CreateText(string name, Transform parent, Vector2 position, float size, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(TextMeshProUGUI));
        go.transform.SetParent(parent, false);
        var rect = go.GetComponent<RectTransform>(); rect.anchorMin = rect.anchorMax = new Vector2(.5f, .5f); rect.sizeDelta = new Vector2(900, 60); rect.anchoredPosition = position;
        var text = go.GetComponent<TextMeshProUGUI>(); text.fontSize = size; text.fontStyle = FontStyles.Bold; text.alignment = TextAlignmentOptions.Center; text.color = color; text.raycastTarget = false;
        return text;
    }
}
}
