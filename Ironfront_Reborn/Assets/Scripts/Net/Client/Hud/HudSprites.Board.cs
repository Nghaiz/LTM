using UnityEngine;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// The Tab board's pictures: a person and a bot, the status dot, the rank medal and the ping
    /// bars. Owner's report of 2026-09-30: more colour, more icons, players apart from bots.
    /// </summary>
    /// <remarks>Drawn in code, white, for the view to tint -- the rest of <see cref="HudSprites"/>'s rule.</remarks>
    public static partial class HudSprites
    {
        private static Sprite _person;
        private static Sprite _bot;
        private static Sprite _dot;
        private static Sprite _medal;
        private static readonly Sprite[] _pingBars = new Sprite[4];

        /// <summary>A head and shoulders: a human player.</summary>
        public static Sprite Person()
        {
            if (_person != null) return _person;
            _person = DrawShape("Hud Person", (x, y) => PersonShape(x, y, 32f));
            return _person;
        }

        /// <summary>A robot's head, antenna and eyes: a bot.</summary>
        public static Sprite Bot()
        {
            if (_bot != null) return _bot;

            _bot = DrawShape("Hud Bot", (x, y) =>
            {
                bool head = InRoundedRect(x, y, 10f, 12f, 54f, 46f, 9f);
                bool eyes = InCircle(x, y, 23f, 31f, 5f) || InCircle(x, y, 41f, 31f, 5f);
                bool mouth = x >= 22f && x <= 42f && y >= 18f && y <= 21f;
                bool antenna = Mathf.Abs(x - 32f) <= 1.8f && y >= 46f && y <= 55f;
                bool bulb = InCircle(x, y, 32f, 57f, 4f);
                bool ears = (x >= 4f && x <= 10f || x >= 54f && x <= 60f) && y >= 24f && y <= 36f;
                return (head && !eyes && !mouth) || antenna || bulb || ears;
            });

            return _bot;
        }

        /// <summary>A plain disc: the status dot beside a live player.</summary>
        public static Sprite Dot()
        {
            if (_dot != null) return _dot;
            _dot = DrawShape("Hud Dot", (x, y) => InCircle(x, y, 32f, 32f, 16f));
            return _dot;
        }

        /// <summary>A disc with a bright rim: the rank medal the top three wear.</summary>
        public static Sprite Medal()
        {
            if (_medal != null) return _medal;

            const float centre = (GlyphSize - 1) * 0.5f;
            _medal = Draw("Hud Medal", GlyphSize, GlyphSize, (x, y) =>
            {
                float dx = x - centre;
                float dy = y - centre;
                float distance = Mathf.Sqrt(dx * dx + dy * dy);

                float disc = Coverage(29f - distance);
                float rim = Coverage(2.2f - Mathf.Abs(distance - 25.5f));

                // The disc a shade clear of the rim, so a tint reads as a coin with an edge.
                return Mathf.Max(disc * 0.8f, rim);
            });

            return _medal;
        }

        /// <summary>
        /// Three rising bars with <paramref name="level"/> of them lit (1 to 3): a connection's
        /// quality. The unlit bars stay faint, so the picture keeps its shape at one bar.
        /// </summary>
        public static Sprite PingBars(int level)
        {
            level = Mathf.Clamp(level, 1, 3);
            if (_pingBars[level] != null) return _pingBars[level];

            _pingBars[level] = Draw("Hud Ping " + level, GlyphSize, GlyphSize, (x, y) =>
            {
                for (int bar = 0; bar < 3; bar++)
                {
                    float left = 8f + bar * 18f;
                    float top = 22f + bar * 14f;
                    if (!InRoundedRect(x, y, left, 8f, left + 12f, top, 2.5f)) continue;
                    return bar < level ? 1f : 0.28f;
                }

                return 0f;
            });

            return _pingBars[level];
        }
    }
}
