using Ironfront.Net.Replication.Combat;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// The local player's breath in water: a row of bubbles over the health readout that burst one
    /// by one while it swims, redden when little is left, and pulse once it is gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner ruling 2026-09-29</b>: water is swimming now, not an eight-second drowning, and the
    /// player sees how long they have -- "bubbles, or any good-looking design". The bar drains at the
    /// surface as much as under it (nobody sits in the water), refills on land, and is hidden
    /// whenever it is full and the player is dry.
    /// </para>
    /// <para>
    /// <b>The server's clock, run here.</b> The server keeps a <see cref="BreathClock"/> per actor
    /// and deals the damage; this runs the same class on the same test -- the body's own
    /// <c>inWater</c>, which for a networked player is its capsule's (<c>Actor.Update</c>) -- so
    /// the bar empties when the server's breath does, and the health loss that follows is the
    /// server's, drawn by the health readout.
    /// </para>
    /// <para>
    /// Added by <see cref="NetClientLocalCombatDriver"/>, which knows whether the player is alive
    /// and deployed; builds its own overlay canvas, so no scene or prefab carries it.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    internal sealed class BreathHud : MonoBehaviour
    {
        /// <summary>Bubbles in the full row; each is a tenth of the breath.</summary>
        public const int BubbleCount = 10;

        /// <summary>Below this much breath the bubbles turn from water to warning.</summary>
        public const float LowFraction = 0.3f;

        private const float BubbleSize = 24f;
        private const float BubbleStep = 27f;
        private const float FadePerSecond = 4f;

        private static readonly Color WaterTint = new Color(0.74f, 0.92f, 1f, 1f);
        private static readonly Color LowTint = new Color(1f, 0.62f, 0.24f, 1f);
        private static readonly Color GoneTint = new Color(1f, 0.26f, 0.22f, 1f);

        private static Sprite _bubbleSprite;

        private readonly BreathClock _breath = new BreathClock();
        private NetClientLocalCombatDriver _driver;
        private CanvasGroup _group;
        private Image[] _bubbles;

        /// <summary>The breath this HUD is drawing, from 1 (full) to 0.</summary>
        internal float Fraction => _breath.Fraction;

        private void Awake()
        {
            _driver = GetComponent<NetClientLocalCombatDriver>();
            if (!Application.isBatchMode) Build();
        }

        private void OnDestroy()
        {
            if (_group != null) Destroy(_group.gameObject);
        }

        private void Update()
        {
            bool alive = _driver != null && _driver.IsAuthoritativelyDeployed;
            ILocalPlayerRig rig = NetClientBindings.LocalPlayer;
            bool inWater = alive && rig != null && rig.Exists && rig.IsInWater;

            Advance(alive, inWater, Time.deltaTime);
            Draw(alive && (inWater || _breath.Fraction < 1f), Time.unscaledTime, Time.unscaledDeltaTime);
        }

        /// <summary>Advances the breath the way the server does; a dead or undeployed player breathes fully.</summary>
        internal void Advance(bool alive, bool inWater, float deltaSeconds)
        {
            if (!alive)
            {
                _breath.Reset();
                return;
            }
            _breath.Tick(inWater, deltaSeconds);
        }

        private void Draw(bool show, float now, float dt)
        {
            if (_group == null) return;

            _group.alpha = Mathf.MoveTowards(_group.alpha, show ? 1f : 0f, FadePerSecond * dt);
            if (_group.alpha <= 0f) return;

            float fraction = _breath.Fraction;
            bool gone = _breath.IsEmpty;
            Color tint = gone ? GoneTint
                : fraction < LowFraction ? Color.Lerp(GoneTint, LowTint, fraction / LowFraction)
                : WaterTint;

            for (int i = 0; i < _bubbles.Length; i++)
            {
                // How much of this bubble's tenth is left: a bubble shrinks as it drains, and an
                // empty socket stays faintly visible so the row keeps its length.
                float fill = Mathf.Clamp01(fraction * BubbleCount - i);
                Image bubble = _bubbles[i];
                float scale = fill > 0f ? Mathf.Lerp(0.55f, 1f, fill) : 0.5f;
                bubble.rectTransform.localScale = new Vector3(scale, scale, 1f);

                Color c = tint;
                if (gone) c.a = 0.35f + 0.3f * Mathf.Sin(now * 8f);
                else c.a = fill > 0f ? Mathf.Lerp(0.55f, 1f, fill) : 0.14f;
                bubble.color = c;
            }
        }

        private void Build()
        {
            // Built here rather than authored: it is added at runtime by the driver, so neither map
            // scene nor any prefab has to carry it, and the one sprite it needs is drawn below.
            var canvasObject = new GameObject("Breath HUD", typeof(RectTransform));
            DontDestroyOnLoad(canvasObject);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 12;
            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
            _group = canvasObject.AddComponent<CanvasGroup>();
            _group.alpha = 0f;
            _group.interactable = false;
            _group.blocksRaycasts = false;

            // Bottom left, over the health readout.
            var row = new GameObject("Bubbles", typeof(RectTransform)).GetComponent<RectTransform>();
            row.SetParent(canvasObject.transform, false);
            row.anchorMin = row.anchorMax = row.pivot = Vector2.zero;
            row.anchoredPosition = new Vector2(24f, 96f);
            row.sizeDelta = new Vector2(BubbleStep * BubbleCount, BubbleSize);

            Sprite sprite = BubbleSprite();
            _bubbles = new Image[BubbleCount];
            for (int i = 0; i < BubbleCount; i++)
            {
                var bubble = new GameObject("Bubble " + i, typeof(RectTransform)).AddComponent<Image>();
                bubble.sprite = sprite;
                bubble.raycastTarget = false;
                RectTransform rect = bubble.rectTransform;
                rect.SetParent(row, false);
                rect.anchorMin = rect.anchorMax = new Vector2(0f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);
                rect.sizeDelta = new Vector2(BubbleSize, BubbleSize);
                rect.anchoredPosition = new Vector2(BubbleStep * i + BubbleSize * 0.5f, 0f);
                _bubbles[i] = bubble;
            }
        }

        // A soap bubble: a bright rim, a faint body and a highlight up and to the left. Drawn once.
        private static Sprite BubbleSprite()
        {
            if (_bubbleSprite != null) return _bubbleSprite;

            const int size = 64;
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Breath Bubble",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            var pixels = new Color32[size * size];
            float centre = (size - 1) * 0.5f;
            float radius = size * 0.5f - 1.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - centre, dy = y - centre;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    float edge = Mathf.Clamp01(radius - r + 0.5f);
                    float rim = Mathf.Clamp01(1f - Mathf.Abs(r - (radius - 2.5f)) / 2.5f);
                    float hx = dx + radius * 0.35f, hy = dy - radius * 0.38f;
                    float highlight = Mathf.Clamp01(1f - Mathf.Sqrt(hx * hx * 1.6f + hy * hy) / (radius * 0.28f));
                    float alpha = edge * Mathf.Max(0.28f, Mathf.Max(rim, highlight));
                    byte v = (byte)(255f * Mathf.Lerp(0.82f, 1f, Mathf.Max(rim, highlight)));
                    pixels[y * size + x] = new Color32(v, v, v, (byte)(255f * alpha));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            _bubbleSprite = Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
            return _bubbleSprite;
        }
    }
}
