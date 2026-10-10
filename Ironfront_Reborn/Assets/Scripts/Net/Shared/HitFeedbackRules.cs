using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// How the hitmarker looks and sounds for each kind of hit (owner's run of 2026-10-10, phase
    /// P38): a body hit, a headshot, a kill, and a headshot that kills. The numbers the HUD draws
    /// with, kept out of the HUD so they can be read and tested.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the big shooters teach, and this follows.</b> A white cross for a hit, a gold one and
    /// a metal "dink" for the head, a red one with a heavier sound for the kill -- so a player knows
    /// without looking anywhere else whether that burst landed, where, and whether it finished the
    /// man. The game used to draw one white cross for all three, pitched its one tick a little
    /// higher for the rarer ones, and drew nothing at all for a hit that came while the last cross
    /// was still up, so a burst read as one hit.
    /// </para>
    /// <para>
    /// Severities follow <c>HitmarkerSeverity</c>'s order (the HUD is handed an <c>int</c> so the
    /// game takes no dependency on the replication library for a cosmetic): 0 body, 1 headshot,
    /// 2 kill, 3 headshot kill.
    /// </para>
    /// </remarks>
    public static class HitFeedbackRules
    {
        public const int Body = 0;
        public const int Headshot = 1;
        public const int Kill = 2;
        public const int HeadshotKill = 3;

        /// <summary>Two body ticks closer than this play as one: a shotgun's pellets land together.</summary>
        public const float BodyTickMinGapSeconds = 0.05f;

        /// <summary>A louder sound already played this recently covers a quieter or equal one.</summary>
        public const float LoudSoundMinGapSeconds = 0.06f;

        /// <summary>Of a shown cross's life, the share a quieter hit may not cut short.</summary>
        public const float HoldShare = 0.6f;

        public static int Clamp(int severity) => Mathf.Clamp(severity, Body, HeadshotKill);

        /// <summary>How long the cross stays up, seconds.</summary>
        public static float Seconds(int severity)
        {
            switch (Clamp(severity))
            {
                case Headshot: return 0.26f;
                case Kill: return 0.45f;
                case HeadshotKill: return 0.55f;
                default: return 0.16f;
            }
        }

        /// <summary>How much larger the cross starts than it ends.</summary>
        public static float Pop(int severity)
        {
            switch (Clamp(severity))
            {
                case Headshot: return 0.28f;
                case Kill: return 0.4f;
                case HeadshotKill: return 0.5f;
                default: return 0.12f;
            }
        }

        /// <summary>The cross's colour: white, gold, red, and a deeper red for a headshot kill.</summary>
        public static Color Colour(int severity)
        {
            switch (Clamp(severity))
            {
                case Headshot: return new Color(1f, 0.78f, 0.22f, 1f);
                case Kill: return new Color(1f, 0.24f, 0.18f, 1f);
                case HeadshotKill: return new Color(0.92f, 0.08f, 0.12f, 1f);
                default: return Color.white;
            }
        }

        /// <summary>The cross's scale at <paramref name="ratio"/> (0..1) of its life: a pop that eases out.</summary>
        public static float Scale(int severity, float ratio)
        {
            float t = Mathf.Clamp01(ratio / 0.35f);
            float ease = 1f - (1f - t) * (1f - t);
            return 1f + Pop(severity) * (1f - ease);
        }

        /// <summary>The cross's opacity at <paramref name="ratio"/> of its life: solid, then fading over the last third.</summary>
        public static float Alpha(float ratio) => Mathf.Clamp01((1f - Mathf.Clamp01(ratio)) * 3f);

        /// <summary>
        /// Whether a hit of <paramref name="incoming"/> severity takes over a cross of
        /// <paramref name="shown"/> severity that is <paramref name="shownRatio"/> through its life.
        /// A louder or equal hit always does; a quieter one waits out most of a kill's cross, so the
        /// red is not cut short by the next pellet.
        /// </summary>
        public static bool Replaces(int shown, float shownRatio, int incoming)
            => Clamp(incoming) >= Clamp(shown) || shownRatio >= HoldShare;

        /// <summary>
        /// The Resources path of the sound for a severity, under <c>IronfrontUi/</c>; null for a body
        /// hit, which keeps the HUD's own authored tick.
        /// </summary>
        public static string SoundPath(int severity)
        {
            switch (Clamp(severity))
            {
                case Headshot: return "IronfrontUi/hit-headshot";
                case Kill: return "IronfrontUi/hit-kill";
                case HeadshotKill: return "IronfrontUi/hit-headshot-kill";
                default: return null;
            }
        }
    }
}
