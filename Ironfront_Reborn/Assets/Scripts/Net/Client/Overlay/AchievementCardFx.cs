#nullable enable

using Ironfront.Net.Protocol.Achievements;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// The living background of an achievement card, one per kind (owner's run of 2026-10-10, task 6:
    /// "only Mythic has a background effect; Bronze, Silver, Gold, Platinum and Hidden need their
    /// own"). Bronze: copper dust drifting. Silver: cold glints twinkling, a sheen now and then.
    /// Gold: sparks rising, a slow sweep of light. Platinum: a prismatic sheen and diamond glints.
    /// Mythic: embers rising through a red glow. Hidden (not yet revealed): violet fog and a flicker.
    /// </summary>
    /// <remarks>
    /// Everything is drawn from three small textures made once in code, so the effect costs no
    /// asset; a card not earned shows its effect at a third of the strength, as a promise.
    /// </remarks>
    public sealed class AchievementCardFx
    {
        public enum Style
        {
            Bronze,
            Silver,
            Gold,
            Platinum,
            Mythic,
            Hidden,
        }

        private const int Motes = 8;

        private static Sprite? _dot;
        private static Sprite? _glint;
        private static Sprite? _band;

        private readonly RectTransform _root;
        private readonly Image _glow;
        private readonly Image _sheen;
        private readonly Image[] _motes = new Image[Motes];
        private Style _style;
        private float _strength = 1f;
        private float _seed;

        private AchievementCardFx(RectTransform root, Image glow, Image sheen)
        {
            _root = root;
            _glow = glow;
            _sheen = sheen;
        }

        /// <summary>The kind an achievement's card shows: a hidden one not yet revealed is Hidden.</summary>
        public static Style For(Achievement achievement, bool revealed)
        {
            if (achievement.Hidden && !revealed) return Style.Hidden;
            return achievement.Tier switch
            {
                AchievementTier.Silver => Style.Silver,
                AchievementTier.Gold => Style.Gold,
                AchievementTier.Platinum => Style.Platinum,
                AchievementTier.Mythic => Style.Mythic,
                _ => Style.Bronze,
            };
        }

        /// <summary>The card's face for this kind: dark, tinted towards its metal.</summary>
        public static Color Face(Style style, bool earned)
        {
            Color face = style switch
            {
                Style.Silver => new Color(0.055f, 0.08f, 0.11f),
                Style.Gold => new Color(0.11f, 0.085f, 0.03f),
                Style.Platinum => new Color(0.03f, 0.1f, 0.12f),
                Style.Mythic => new Color(0.13f, 0.03f, 0.04f),
                Style.Hidden => new Color(0.075f, 0.04f, 0.11f),
                _ => new Color(0.11f, 0.065f, 0.035f),
            };
            return earned ? new Color(face.r, face.g, face.b, 0.95f) : new Color(face.r * 0.62f, face.g * 0.62f, face.b * 0.62f, 0.9f);
        }

        /// <summary>The effect's layer on <paramref name="card"/>, behind everything already on it.</summary>
        public static AchievementCardFx Build(RectTransform card, int index)
        {
            var rootObject = new GameObject("Fx", typeof(RectTransform), typeof(RectMask2D));
            var root = (RectTransform)rootObject.transform;
            root.SetParent(card, false);
            root.SetAsFirstSibling();
            root.anchorMin = Vector2.zero;
            root.anchorMax = Vector2.one;
            root.offsetMin = new Vector2(2f, 2f);
            root.offsetMax = new Vector2(-2f, -2f);

            Image glow = Make(root, "Glow", Dot());
            glow.rectTransform.anchorMin = glow.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            glow.rectTransform.sizeDelta = new Vector2(260f, 260f);
            glow.rectTransform.anchoredPosition = new Vector2(62f, 0f);

            Image sheen = Make(root, "Sheen", Band());
            sheen.rectTransform.anchorMin = sheen.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            sheen.rectTransform.sizeDelta = new Vector2(90f, 320f);
            sheen.rectTransform.localRotation = Quaternion.Euler(0f, 0f, -24f);

            var fx = new AchievementCardFx(root, glow, sheen) { _seed = index * 1.618f };
            for (int i = 0; i < Motes; i++)
            {
                Image mote = Make(root, "Mote", Dot());
                mote.rectTransform.anchorMin = mote.rectTransform.anchorMax = new Vector2(0f, 0f);
                fx._motes[i] = mote;
            }
            return fx;
        }

        /// <summary>Sets the kind and how strongly it shows (earned or not).</summary>
        public void Configure(Style style, bool earned)
        {
            _style = style;
            _strength = earned ? 1f : 0.35f;
            Sprite? moteSprite = style == Style.Silver || style == Style.Platinum ? Glint() : Dot();
            foreach (Image mote in _motes) mote.sprite = moteSprite;
        }

        /// <summary>One frame of the effect at <paramref name="time"/> seconds; the card is <paramref name="size"/>.</summary>
        public void Animate(float time, Vector2 size)
        {
            float t = time + _seed * 7f;
            float w = Mathf.Max(1f, size.x - 4f);
            float h = Mathf.Max(1f, size.y - 4f);
            Color metal = Metal(_style);

            // The glow behind the badge, breathing.
            float breathe = 0.5f + 0.5f * Mathf.Sin(t * (_style == Style.Mythic ? 2.2f : 1.3f));
            _glow.color = WithAlpha(metal, (0.12f + 0.1f * breathe) * _strength * (_style == Style.Hidden ? 1.4f : 1f));

            // The sheen: a band of light crossing the card now and then (not on Bronze or Mythic).
            bool sweeps = _style == Style.Silver || _style == Style.Gold || _style == Style.Platinum || _style == Style.Hidden;
            float period = _style == Style.Hidden ? 2.6f : _style == Style.Gold ? 4.2f : 5.5f;
            float cycle = (t / period) % 1f;
            _sheen.enabled = sweeps;
            if (sweeps)
            {
                float x = Mathf.Lerp(-120f, w + 120f, Mathf.Clamp01(cycle * 2.2f));
                _sheen.rectTransform.anchoredPosition = new Vector2(x, 0f);
                Color band = _style == Style.Platinum ? Prism(t * 0.6f) : _style == Style.Hidden ? new Color(0.62f, 0.38f, 1f) : metal;
                float flicker = _style == Style.Hidden ? (Mathf.PerlinNoise(t * 9f, _seed) > 0.62f ? 1f : 0.15f) : 1f;
                _sheen.color = WithAlpha(band, (cycle < 0.46f ? 0.16f : 0f) * _strength * flicker);
            }

            for (int i = 0; i < _motes.Length; i++)
            {
                RectTransform rect = _motes[i].rectTransform;
                float k = i + _seed;
                float phase = (t * Speed(_style, i) + k * 0.37f) % 1f;
                Vector2 at;
                float size2;
                float alpha;
                switch (_style)
                {
                    case Style.Bronze:
                        // Copper dust: drifting sideways and a little up.
                        at = new Vector2(((k * 97f) % w + phase * 60f) % w, 10f + ((k * 53f) % (h - 20f)) + Mathf.Sin(t + k) * 4f);
                        size2 = 3f + (i % 3);
                        alpha = Mathf.Sin(phase * Mathf.PI) * 0.6f;
                        break;
                    case Style.Silver:
                    case Style.Platinum:
                        // Glints: fixed points that flash and fade.
                        at = new Vector2(16f + (k * 131f) % (w - 32f), 10f + (k * 71f) % (h - 20f));
                        float flash = Mathf.Max(0f, Mathf.Sin(phase * Mathf.PI * 2f));
                        size2 = 6f + 10f * flash;
                        alpha = flash * flash * 0.9f;
                        break;
                    case Style.Gold:
                        // Sparks rising, quicker and finer than embers.
                        at = new Vector2(12f + (k * 89f) % (w - 24f) + Mathf.Sin(t * 2f + k) * 5f, 4f + phase * (h - 8f));
                        size2 = 2.5f + (i % 2);
                        alpha = Mathf.Sin(phase * Mathf.PI) * 0.85f;
                        break;
                    case Style.Mythic:
                        // Embers rising slowly through the frame.
                        at = new Vector2(20f + (k * 97f) % (w - 40f) + Mathf.Sin(t * 1.3f + k) * 6f, 6f + phase * (h - 12f));
                        size2 = 3f + (i % 2);
                        alpha = Mathf.Sin(phase * Mathf.PI) * 0.8f;
                        break;
                    default:
                        // Hidden: wide soft fog drifting across.
                        at = new Vector2(((k * 151f) % w + phase * w * 0.5f) % (w + 80f) - 40f, ((k * 37f) % h));
                        size2 = 70f + 30f * (i % 3);
                        alpha = Mathf.Sin(phase * Mathf.PI) * 0.12f;
                        break;
                }
                rect.anchoredPosition = at;
                rect.sizeDelta = new Vector2(size2, size2);
                Color colour = _style == Style.Platinum ? Prism(t * 0.4f + i * 0.13f) : _style == Style.Hidden ? new Color(0.55f, 0.32f, 0.95f) : MoteColour(_style, metal);
                _motes[i].color = WithAlpha(colour, alpha * _strength);
            }
        }

        /// <summary>Whether the layer is shown at all (cards filtered out hide it with themselves).</summary>
        public bool Visible => _root.gameObject.activeInHierarchy;

        private static float Speed(Style style, int i) => style switch
        {
            Style.Bronze => 0.05f + 0.01f * i,
            Style.Silver => 0.22f + 0.05f * (i % 3),
            Style.Platinum => 0.3f + 0.06f * (i % 3),
            Style.Gold => 0.32f + 0.05f * i,
            Style.Mythic => 0.18f + 0.04f * i,
            _ => 0.03f + 0.008f * i,
        };

        private static Color Metal(Style style) => style switch
        {
            Style.Silver => new Color(0.8f, 0.86f, 0.95f),
            Style.Gold => new Color(1f, 0.78f, 0.25f),
            Style.Platinum => new Color(0.6f, 0.92f, 1f),
            Style.Mythic => new Color(1f, 0.3f, 0.18f),
            Style.Hidden => new Color(0.55f, 0.32f, 0.95f),
            _ => new Color(0.86f, 0.52f, 0.24f),
        };

        private static Color MoteColour(Style style, Color metal) => style switch
        {
            Style.Mythic => new Color(1f, 0.42f, 0.2f),
            Style.Gold => new Color(1f, 0.88f, 0.45f),
            _ => metal,
        };

        /// <summary>A soft rainbow for platinum's prism, cycling with <paramref name="x"/>.</summary>
        private static Color Prism(float x) => Color.HSVToRGB(Mathf.Repeat(x, 1f), 0.35f, 1f);

        private static Color WithAlpha(Color c, float a) => new Color(c.r, c.g, c.b, Mathf.Clamp01(a));

        private static Image Make(RectTransform parent, string name, Sprite sprite)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.raycastTarget = false;
            image.color = new Color(1f, 1f, 1f, 0f);
            return image;
        }

        private static Sprite Dot() => _dot ??= MakeSprite(32, (x, y) => Mathf.Pow(Mathf.Clamp01(1f - Mathf.Sqrt(x * x + y * y)), 2f));

        private static Sprite Glint() => _glint ??= MakeSprite(32, (x, y) =>
        {
            float r = Mathf.Sqrt(x * x + y * y);
            float cross = Mathf.Max(Mathf.Clamp01(1f - Mathf.Abs(x) * 9f), Mathf.Clamp01(1f - Mathf.Abs(y) * 9f)) * Mathf.Clamp01(1f - r);
            return Mathf.Clamp01(cross + Mathf.Pow(Mathf.Clamp01(1f - r * 2.2f), 2f));
        });

        private static Sprite Band() => _band ??= MakeSprite(32, (x, y) => Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(x)), 2.5f) * Mathf.Clamp01(1f - Mathf.Abs(y) * 0.2f));

        /// <summary>A white sprite whose alpha is <paramref name="alpha"/> over -1..1 in x and y.</summary>
        private static Sprite MakeSprite(int size, System.Func<float, float, float> alpha)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = ((x + 0.5f) / size) * 2f - 1f;
                    float v = ((y + 0.5f) / size) * 2f - 1f;
                    pixels[(y * size) + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alpha(u, v)) * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            return Sprite.Create(texture, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
