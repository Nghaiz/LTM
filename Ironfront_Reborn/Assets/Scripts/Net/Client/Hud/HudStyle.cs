using UnityEngine;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// The in-match readout's one palette of neutrals: the killfeed, the Tab scoreboard and the
    /// deploy screen share it, so the three read as one design. Feature 2, 2026-09-29.
    /// </summary>
    /// <remarks>
    /// <b>No team colour here.</b> A side's colour comes from <c>ITeamPalette</c> through
    /// <see cref="MatchHud"/> (contracts § 6.3) and is handed to the views; these are only the
    /// inks and panes that are the same whatever side you are on.
    /// </remarks>
    public static class HudStyle
    {
        /// <summary>Primary text. The builder's own ink, so the gate's uniform-ink check holds.</summary>
        public static readonly Color Ink = new Color(0.93f, 0.94f, 0.96f);

        /// <summary>Secondary text: labels, units, the second line of anything.</summary>
        public static readonly Color Muted = new Color(0.64f, 0.68f, 0.74f);

        /// <summary>Tertiary text: column heads, the rules line, hints.</summary>
        public static readonly Color Faint = new Color(0.47f, 0.51f, 0.58f);

        /// <summary>A killfeed row at rest: dark enough to read over snow and sky alike.</summary>
        public static readonly Color RowBacking = new Color(0.05f, 0.06f, 0.08f, 0.78f);

        /// <summary>The scoreboard's team panels.</summary>
        public static readonly Color Pane = new Color(0.06f, 0.07f, 0.1f, 0.9f);

        /// <summary>A chip on a row: the killfeed's "how", the scoreboard's BOT tag.</summary>
        public static readonly Color ChipBacking = new Color(1f, 1f, 1f, 0.12f);

        public static readonly Color ChipInk = new Color(0.9f, 0.92f, 0.95f);

        /// <summary>What happened, in a killfeed sentence.</summary>
        public static readonly Color SentenceInk = new Color(0.8f, 0.82f, 0.86f);

        /// <summary>The headshot mark.</summary>
        public static readonly Color HeadshotInk = new Color(1f, 0.36f, 0.28f);

        /// <summary>You: a gold edge and name wherever the readout shows you.</summary>
        public static readonly Color Gold = new Color(1f, 0.78f, 0.25f, 0.95f);

        public static readonly Color GoldInk = new Color(1f, 0.85f, 0.4f);

        /// <summary>Your kill, warm; your death, dark red.</summary>
        public static readonly Color LocalKillBacking = new Color(0.24f, 0.19f, 0.05f, 0.9f);

        public static readonly Color LocalDeathBacking = new Color(0.28f, 0.06f, 0.06f, 0.9f);

        public static readonly Color Blood = new Color(0.96f, 0.32f, 0.26f, 0.95f);

        /// <summary>
        /// A side's colour for a name or number drawn on a dark pane: the palette's, lifted a
        /// little toward white so a dark blue stays readable on near-black.
        /// </summary>
        public static Color TeamInk(Color team) => Color.Lerp(team, Color.white, 0.2f);

        /// <summary>Cubic ease-out, for everything that arrives.</summary>
        public static float EaseOut(float t)
        {
            t = Mathf.Clamp01(t);
            float remaining = 1f - t;
            return 1f - remaining * remaining * remaining;
        }
    }
}
