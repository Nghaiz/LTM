using UnityEngine;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// The readout's small graphics, drawn once in code: a crosshair, a star, a flag, a diamond, a
    /// fade, a vignette and a glowing rule. Features 1 and 2, 2026-09-29.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Drawn rather than imported.</b> The project has no icon set for any of these, a glyph
    /// cannot be trusted to exist in Roboto, and each is a white shape the view tints -- so the
    /// headshot mark takes <see cref="HudStyle.HeadshotInk"/> and a team band takes the palette's
    /// colour without either being baked into a texture.
    /// </para>
    /// <para>
    /// Built on first use and kept for the session. A headless server never asks: every view
    /// that uses one is disabled off the client.
    /// </para>
    /// </remarks>
    public static class HudSprites
    {
        private static Sprite _crosshair;
        private static Sprite _star;
        private static Sprite _flag;
        private static Sprite _fade;
        private static Sprite _vignette;
        private static Sprite _rule;
        private static Sprite _diamond;

        /// <summary>A ring, a centre dot and four ticks: the headshot mark.</summary>
        public static Sprite Crosshair()
        {
            if (_crosshair != null) return _crosshair;

            const int size = 48;
            float centre = (size - 1) * 0.5f;

            _crosshair = Draw("Hud Crosshair", size, size, (x, y) =>
            {
                float dx = x - centre;
                float dy = y - centre;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);

                float ring = Coverage(2.2f - Mathf.Abs(distance - 15f));
                float dot = Coverage(4.5f - distance);
                float tick = distance > 9f && distance < 22f
                    ? Mathf.Max(Coverage(1.6f - Mathf.Abs(dx)), Coverage(1.6f - Mathf.Abs(dy)))
                    : 0f;

                return Mathf.Max(ring, Mathf.Max(dot, tick));
            });

            return _crosshair;
        }

        /// <summary>A five-pointed star: the top of a team's column.</summary>
        public static Sprite Star()
        {
            if (_star != null) return _star;

            const int size = 48;
            float centre = (size - 1) * 0.5f;
            const float outer = 22f;
            const float inner = 9.5f;

            var points = new Vector2[10];
            for (int i = 0; i < 10; i++)
            {
                float angle = Mathf.PI * 0.5f + i * Mathf.PI / 5f;
                float radius = i % 2 == 0 ? outer : inner;
                points[i] = new Vector2(centre + Mathf.Cos(angle) * radius, centre + Mathf.Sin(angle) * radius);
            }

            _star = Draw("Hud Star", size, size, (x, y) => Supersample(x, y, (px, py) => Inside(points, px, py)));
            return _star;
        }

        /// <summary>A flag on a pole: capture points held.</summary>
        public static Sprite Flag()
        {
            if (_flag != null) return _flag;

            const int size = 48;

            _flag = Draw("Hud Flag", size, size, (x, y) => Supersample(x, y, (px, py) =>
            {
                bool pole = px >= 8f && px <= 12f && py >= 4f && py <= 44f;

                // A pennant with a notch cut into its fly end.
                bool cloth = px >= 12f && px <= 42f && py >= 22f && py <= 42f
                             && !(px > 32f && Mathf.Abs(py - 32f) < (px - 32f) * 0.9f);

                return pole || cloth;
            }));

            return _flag;
        }

        /// <summary>Opaque at the left edge, transparent at the right: a team band.</summary>
        public static Sprite FadeRight()
        {
            if (_fade != null) return _fade;

            const int width = 256;
            _fade = Draw("Hud Fade", width, 4, (x, y) =>
            {
                float t = x / (float)(width - 1);
                return (1f - t) * (1f - t);
            });

            return _fade;
        }

        /// <summary>Clear in the middle and dark at the edges, for the board's backdrop.</summary>
        public static Sprite Vignette()
        {
            if (_vignette != null) return _vignette;

            const int size = 128;
            float centre = (size - 1) * 0.5f;

            _vignette = Draw("Hud Vignette", size, size, (x, y) =>
            {
                float dx = (x - centre) / centre;
                float dy = (y - centre) / centre;
                float r = Mathf.Sqrt(dx * dx + dy * dy) / Mathf.Sqrt(2f);
                float edge = Mathf.SmoothStep(0.25f, 1f, r);
                return 0.62f + 0.33f * edge;
            });

            return _vignette;
        }

        /// <summary>A horizontal rule that glows in the middle and fades to nothing at both ends.</summary>
        public static Sprite Rule()
        {
            if (_rule != null) return _rule;

            const int width = 256;
            _rule = Draw("Hud Rule", width, 2, (x, y) => Mathf.Sin(Mathf.PI * x / (width - 1)));
            return _rule;
        }

        /// <summary>A diamond: the mark before a person's name, which a bot's plate does not carry.</summary>
        public static Sprite Diamond()
        {
            if (_diamond != null) return _diamond;

            const int size = 32;
            float centre = (size - 1) * 0.5f;

            // |dx| + |dy| <= r is a square turned 45 degrees; the soft band is its edge.
            _diamond = Draw("Hud Diamond", size, size,
                (x, y) => Coverage(13f - (Mathf.Abs(x - centre) + Mathf.Abs(y - centre))));

            return _diamond;
        }

        private static float Coverage(float signedDistance) => Mathf.Clamp01(signedDistance + 0.5f);

        /// <summary>Four samples per pixel, so a shape tested by containment has soft edges.</summary>
        private static float Supersample(int x, int y, System.Func<float, float, bool> inside)
        {
            int hits = 0;
            for (int sy = 0; sy < 2; sy++)
                for (int sx = 0; sx < 2; sx++)
                    if (inside(x + 0.25f + sx * 0.5f, y + 0.25f + sy * 0.5f)) hits++;

            return hits * 0.25f;
        }

        private static bool Inside(Vector2[] polygon, float x, float y)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[j];
                if ((a.y > y) != (b.y > y) && x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x)
                    inside = !inside;
            }

            return inside;
        }

        private static Sprite Draw(string name, int width, int height, System.Func<int, int, float> alphaAt)
        {
            var pixels = new Color32[width * height];

            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    pixels[y * width + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(alphaAt(x, y)) * 255f));

            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            {
                name = name,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            return Sprite.Create(texture, new Rect(0f, 0f, width, height), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
