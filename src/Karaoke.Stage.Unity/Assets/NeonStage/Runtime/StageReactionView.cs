using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NeonStage.Stage
{
/// <summary>Font-independent audience reactions. Icons are generated as textures at runtime so Android needs no emoji font.</summary>
public sealed class StageReactionView
{
    private readonly RectTransform _root;
    private readonly List<FlyingReaction> _active = new();
    private readonly Dictionary<string, Sprite> _sprites = new();

    public StageReactionView(GameObject host)
    {
        var canvasObject = new GameObject("Live Reactions Canvas", typeof(Canvas), typeof(CanvasScaler));
        canvasObject.transform.SetParent(host.transform, false);
        var canvas = canvasObject.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 45;
        var scaler = canvasObject.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1280, 720);
        _root = canvasObject.GetComponent<RectTransform>();
    }

    public void Spawn(string type)
    {
        var go = new GameObject("Audience Reaction", typeof(RectTransform), typeof(Image));
        go.transform.SetParent(_root, false);
        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = rect.anchorMax = new Vector2(Random.Range(.18f, .82f), 0);
        rect.pivot = new Vector2(.5f, .5f); rect.anchoredPosition = new Vector2(Random.Range(-35f, 35f), -45); rect.sizeDelta = new Vector2(76, 76);
        var image = go.GetComponent<Image>(); image.sprite = GetSprite(type); image.preserveAspect = true; image.raycastTarget = false;
        image.color = type switch { "heart" => new Color(1f,.2f,.62f), "smile" => new Color(.9f,1f,.05f),
            "like" => new Color(.25f,.75f,1f), "clap" => new Color(1f,.68f,.08f), _ => new Color(1f,.25f,.04f) };
        _active.Add(new FlyingReaction(rect, image, Random.Range(-45f,45f), Random.Range(145f,220f), Random.Range(2.6f,4.1f)));
    }

    private Sprite GetSprite(string type)
    {
        if (_sprites.TryGetValue(type, out var existing)) return existing;
        const int size = 64;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Reaction-" + type, filterMode = FilterMode.Bilinear };
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++) for (var x = 0; x < size; x++)
            if (Inside(type, x / 63f, y / 63f)) pixels[y * size + x] = new Color32(255, 255, 255, 255);
        texture.SetPixels32(pixels); texture.Apply(false, true);
        var sprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(.5f, .5f), size);
        _sprites[type] = sprite; return sprite;
    }

    private static bool Inside(string type, float x, float y) => type switch
    {
        "heart" => Heart(x, y),
        "smile" => Circle(x,y,.5f,.5f,.42f) && (!Circle(x,y,.36f,.58f,.055f) && !Circle(x,y,.64f,.58f,.055f) && !(y<.46f && y>.31f && Mathf.Abs(x-.5f)<(.46f-y)*1.25f)),
        "like" => (x>.17f&&x<.36f&&y>.18f&&y<.58f) || (x>.34f&&x<.78f&&y>.19f&&y<.62f) || (x>.36f&&x<.58f&&y>.55f&&y<.86f&&x-y*.45f<.25f),
        "clap" => Palm(x,y,.39f,.38f) || Palm(x,y,.61f,.50f),
        "fire" => Flame(x,y),
        _ => Heart(x,y)
    };
    private static bool Circle(float x,float y,float cx,float cy,float radius) => (x-cx)*(x-cx)+(y-cy)*(y-cy)<radius*radius;
    private static bool Heart(float x,float y) { x=(x-.5f)*2; y=(y-.48f)*2; return Mathf.Pow(x*x+y*y-.32f,3)-x*x*y*y*y<0; }
    private static bool Palm(float x,float y,float cx,float cy) =>
        ((x-cx)*(x-cx)/.035f+(y-cy)*(y-cy)/.065f<1) || (x>cx-.18f&&x<cx+.17f&&y>cy+.10f&&y<cy+.42f&&Mathf.Abs((x-cx)*5)%0.22f<.15f);
    private static bool Flame(float x,float y)
    {
        var outer = (x-.5f)*(x-.5f)/(.28f*.28f)+(y-.38f)*(y-.38f)/(.38f*.38f)<1 && y < .18f + 1.35f*Mathf.Abs(x-.5f);
        var tip = y>.42f && Mathf.Abs(x-.5f)<(.98f-y)*.36f;
        var hollow = (x-.5f)*(x-.5f)/.010f+(y-.31f)*(y-.31f)/.025f<1;
        return (outer || tip) && !hollow;
    }

    public void Update()
    {
        for (var index = _active.Count - 1; index >= 0; index--)
        {
            var item = _active[index]; item.Age += Time.unscaledDeltaTime; var t = item.Age / item.Lifetime;
            if (t >= 1) { Object.Destroy(item.Rect.gameObject); _active.RemoveAt(index); continue; }
            item.Rect.anchoredPosition += new Vector2(item.Drift + Mathf.Sin(item.Age * 3.7f) * 22f, item.Speed) * Time.unscaledDeltaTime;
            item.Rect.localScale = Vector3.one * (1f + Mathf.Sin(item.Age * 6f) * .08f + Mathf.Min(t * 2f, .22f));
            var color = item.Image.color; color.a = t < .68f ? Mathf.Min(1, t * 5f) : 1f - Mathf.InverseLerp(.68f, 1f, t); item.Image.color = color;
        }
    }

    private sealed class FlyingReaction
    {
        public FlyingReaction(RectTransform rect, Image image, float drift, float speed, float lifetime)
        { Rect=rect; Image=image; Drift=drift; Speed=speed; Lifetime=lifetime; }
        public RectTransform Rect; public Image Image; public float Drift, Speed, Lifetime, Age;
    }
}
}
