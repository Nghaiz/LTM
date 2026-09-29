using UnityEngine;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// The readout's small graphics, drawn once in code: a crosshair, a star, a flag, a fade, a
    /// vignette, a glowing rule, and the name plate's frame and icons. Features 1 and 2,
    /// 2026-09-29.
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
    public static partial class HudSprites
    {
        private static Sprite _crosshair;
        private static Sprite _star;
        private static Sprite _flag;
        private static Sprite _fade;
        private static Sprite _vignette;
        private static Sprite _rule;
        private static Sprite _caret;
        private static Sprite _bracket;
        private static Sprite _scanlines;
        private static Sprite _shield;
        private static Sprite _hostile;
        private static Sprite _medic;
        private static Sprite _pin;
        private static Sprite _wheel;
        private static Sprite _wave;

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

        /// <summary>A small triangle pointing down: the tip under a name plate, over the head it names.</summary>
        public static Sprite Caret()
        {
            if (_caret != null) return _caret;

            const int width = 32;
            const int height = 18;

            // Row 0 is the bottom of the texture: wide at the top, the point at the bottom.
            var points = new[]
            {
                new Vector2(1f, height - 1f),
                new Vector2(width - 1f, height - 1f),
                new Vector2(width * 0.5f, 1f),
            };

            _caret = Draw("Hud Caret", width, height, (x, y) => Supersample(x, y, (px, py) => Inside(points, px, py)));
            return _caret;
        }

        /// <summary>Where a bracket's corner sits in <see cref="Bracket"/>, from the bottom-left, in pixels.</summary>
        public const float BracketCornerX = 8f;

        /// <inheritdoc cref="BracketCornerX"/>
        public const float BracketCornerY = 24f;

        /// <summary>The side of <see cref="Bracket"/>'s square texture, in pixels.</summary>
        public const int BracketSize = 32;

        /// <summary>
        /// One glowing corner of a frame, an L opening right and down, its corner at
        /// (<see cref="BracketCornerX"/>, <see cref="BracketCornerY"/>) so a plate can pin that
        /// point to its own corner and mirror the one sprite into all four.
        /// </summary>
        /// <remarks>The glow is baked in around the stroke, so one tinted image is the whole effect.</remarks>
        public static Sprite Bracket()
        {
            if (_bracket != null) return _bracket;

            const float arm = 12f;
            const float half = 1.2f;
            const float glowSigma = 2.6f;

            _bracket = Draw("Hud Bracket", BracketSize, BracketSize, (x, y) =>
            {
                float px = x + 0.5f;
                float py = y + 0.5f;
                float distance = Mathf.Min(
                    SegmentDistance(px, py, BracketCornerX, BracketCornerY, BracketCornerX + arm, BracketCornerY),
                    SegmentDistance(px, py, BracketCornerX, BracketCornerY, BracketCornerX, BracketCornerY - arm));

                float stroke = Coverage(half - distance);
                float glow = 0.5f * Mathf.Exp(-(distance * distance) / (2f * glowSigma * glowSigma));
                return Mathf.Max(stroke, glow);
            });

            return _bracket;
        }

        /// <summary>
        /// Fine horizontal lines, one row in three: a name plate's glass. Drawn to be stretched
        /// over a plate of about its own height, so the lines stay about three pixels apart.
        /// </summary>
        public static Sprite Scanlines()
        {
            if (_scanlines != null) return _scanlines;

            _scanlines = Draw("Hud Scanlines", 4, 48, (x, y) => y % 3 == 0 ? 1f : 0f);
            return _scanlines;
        }

        /// <summary>A shield with a chevron cut through it: the mark on a teammate's plate.</summary>
        public static Sprite Shield()
        {
            if (_shield != null) return _shield;

            const int size = 48;

            _shield = Draw("Hud Shield", size, size, (x, y) => Supersample(x, y, (px, py) =>
            {
                if (py < 4f || py > 44f) return false;

                // Straight sides above y = 26, then an elliptic curve down to the point.
                float halfWidth = py >= 26f ? 17f : 17f * Mathf.Sqrt(Mathf.Max(0f, 1f - Mathf.Pow((26f - py) / 22f, 2f)));
                if (Mathf.Abs(px - 24f) > halfWidth) return false;

                // The chevron, cut out: a rank mark, and the shield's one detail.
                float chevron = Mathf.Min(
                    SegmentDistance(px, py, 13f, 21f, 24f, 32f),
                    SegmentDistance(px, py, 24f, 32f, 35f, 21f));
                return chevron > 3f;
            }));

            return _shield;
        }

        /// <summary>
        /// A diamond frame round a dot: the mark on an enemy's plate, after the hostile frame of
        /// military map symbols. Not the killfeed's crosshair, which means a headshot.
        /// </summary>
        public static Sprite Hostile()
        {
            if (_hostile != null) return _hostile;

            const int size = 48;
            float centre = (size - 1) * 0.5f;

            _hostile = Draw("Hud Hostile", size, size, (x, y) =>
            {
                float manhattan = Mathf.Abs(x - centre) + Mathf.Abs(y - centre);
                float frame = Coverage(3.2f - Mathf.Abs(manhattan - 19f));
                float dx = x - centre;
                float dy = y - centre;
                float dot = Coverage(5.5f - Mathf.Sqrt(dx * dx + dy * dy));
                return Mathf.Max(frame, dot);
            });

            return _hostile;
        }

        /// <summary>A plus with rounded arms: health, beside a plate's bar.</summary>
        public static Sprite Medic()
        {
            if (_medic != null) return _medic;

            const int size = 32;
            float centre = (size - 1) * 0.5f;

            _medic = Draw("Hud Medic", size, size, (x, y) =>
            {
                float arm = Mathf.Min(
                    SegmentDistance(x, y, centre - 10f, centre, centre + 10f, centre),
                    SegmentDistance(x, y, centre, centre - 10f, centre, centre + 10f));
                return Coverage(4.2f - arm);
            });

            return _medic;
        }

        /// <summary>A map pin with a hole through its head: how far away, beside the metres.</summary>
        public static Sprite Pin()
        {
            if (_pin != null) return _pin;

            const int width = 24;
            const int height = 32;
            var head = new Vector2(12f, 20f);
            var tail = new[] { new Vector2(5.2f, 17f), new Vector2(18.8f, 17f), new Vector2(12f, 2f) };

            _pin = Draw("Hud Pin", width, height, (x, y) => Supersample(x, y, (px, py) =>
            {
                float fromHead = Vector2.Distance(new Vector2(px, py), head);
                if (fromHead < 3.4f) return false;
                return fromHead <= 8f || Inside(tail, px, py);
            }));

            return _pin;
        }

        /// <summary>A steering wheel: this player is at the wheel or in a seat of a vehicle.</summary>
        public static Sprite Wheel()
        {
            if (_wheel != null) return _wheel;

            const int size = 32;
            float centre = (size - 1) * 0.5f;

            _wheel = Draw("Hud Wheel", size, size, (x, y) =>
            {
                float dx = x - centre;
                float dy = y - centre;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);

                float rim = Coverage(2.2f - Mathf.Abs(distance - 12f));
                float hub = Coverage(4f - distance);
                float spokes = Mathf.Min(
                    SegmentDistance(x, y, centre - 12f, centre + 1f, centre + 12f, centre + 1f),
                    SegmentDistance(x, y, centre, centre, centre, centre - 12f));
                return Mathf.Max(rim, Mathf.Max(hub, Coverage(1.6f - spokes)));
            });

            return _wheel;
        }

        /// <summary>Two waves: this player is in the water.</summary>
        public static Sprite Wave()
        {
            if (_wave != null) return _wave;

            const int size = 32;

            _wave = Draw("Hud Wave", size, size, (x, y) =>
            {
                float best = 0f;
                for (int row = 0; row < 2; row++)
                {
                    float baseline = row == 0 ? 20f : 11f;
                    float curve = baseline + 3f * Mathf.Sin((x + row * 3f) * 0.42f);

                    // Vertical distance, divided by the slope, is close enough to the true
                    // distance for a curve this shallow.
                    float slope = 3f * 0.42f * Mathf.Cos((x + row * 3f) * 0.42f);
                    float distance = Mathf.Abs(y - curve) / Mathf.Sqrt(1f + slope * slope);
                    best = Mathf.Max(best, Coverage(1.8f - distance) * Mathf.Clamp01(Mathf.Min(x - 2f, 29f - x)));
                }

                return best;
            });

            return _wave;
        }

        private static float Coverage(float signedDistance) => Mathf.Clamp01(signedDistance + 0.5f);

        /// <summary>How far a point lies from the segment a-b.</summary>
        private static float SegmentDistance(float px, float py, float ax, float ay, float bx, float by)
        {
            float abx = bx - ax;
            float aby = by - ay;
            float t = Mathf.Clamp01(((px - ax) * abx + (py - ay) * aby) / (abx * abx + aby * aby));
            float cx = ax + t * abx - px;
            float cy = ay + t * aby - py;
            return Mathf.Sqrt(cx * cx + cy * cy);
        }

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
