using Ironfront.Net.Replication.Client;
using UnityEngine;
using UnityEngine.UI;

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

        /// <summary>
        /// The Tab board's secondary numbers (deaths, head counts): brighter than <see cref="Muted"/>,
        /// because the board is read in a hurry over a dark pane (owner report 2026-09-29: "màu sắc
        /// không tương phản").
        /// </summary>
        public static readonly Color BoardMuted = new Color(0.84f, 0.87f, 0.92f);

        /// <summary>The Tab board's quietest text (ranks, column heads, K/D): still readable at a glance.</summary>
        public static readonly Color BoardFaint = new Color(0.68f, 0.72f, 0.79f);

        /// <summary>A killfeed row at rest: dark enough to read over snow and sky alike.</summary>
        public static readonly Color RowBacking = new Color(0.05f, 0.06f, 0.08f, 0.78f);

        /// <summary>The scoreboard's team panels.</summary>
        public static readonly Color Pane = new Color(0.06f, 0.07f, 0.1f, 0.9f);

        /// <summary>
        /// A name plate's glass, before the side's tint: see-through enough to leave the scene
        /// the player is aiming into, dark enough for white text over snow or sky.
        /// </summary>
        public static readonly Color PlateGlass = new Color(0.03f, 0.047f, 0.07f, 0.64f);

        /// <summary>A chip on a row: the killfeed's "how" (MELEE, TANK).</summary>
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

        /// <summary>A match event's line at rest: a cooler slate than a death's, so the two part at a glance.</summary>
        public static readonly Color EventBacking = new Color(0.07f, 0.1f, 0.15f, 0.8f);

        /// <summary>The words on a killfeed badge, over its tone's colour.</summary>
        public static readonly Color BadgeInk = new Color(1f, 1f, 1f);

        /// <summary>The long-shot mark and its metres.</summary>
        public static readonly Color LongShotInk = new Color(0.45f, 1f, 0.62f);

        /// <summary>Water, for a drowning.</summary>
        public static readonly Color WaterInk = new Color(0.36f, 0.72f, 1f);

        /// <summary>Fire, for a blast.</summary>
        public static readonly Color BlastInk = new Color(1f, 0.62f, 0.2f);

        /// <summary>
        /// A killfeed badge's colour, by what it announces: orange for a multi-kill, violet for a
        /// streak, crimson for first blood, cyan for a revenge, gold for a shutdown, red for a team
        /// kill and green for a long shot. Saturated on purpose: a badge is the loudest thing on a line.
        /// </summary>
        public static Color ToneColour(KillfeedTone tone)
        {
            switch (tone)
            {
                case KillfeedTone.MultiKill:  return new Color(1f, 0.47f, 0.08f);
                case KillfeedTone.Streak:     return new Color(0.66f, 0.3f, 1f);
                case KillfeedTone.FirstBlood: return new Color(0.86f, 0.08f, 0.16f);
                case KillfeedTone.Revenge:    return new Color(0.05f, 0.72f, 0.86f);
                case KillfeedTone.Shutdown:   return new Color(0.93f, 0.68f, 0.05f);
                case KillfeedTone.TeamKill:   return new Color(0.9f, 0.16f, 0.12f);
                case KillfeedTone.LongShot:   return new Color(0.12f, 0.7f, 0.36f);
                default:                      return new Color(0.4f, 0.44f, 0.5f);
            }
        }

        /// <summary>How far toward white the HUD lifts a side's colour for type.</summary>
        public const float TeamInkLift = 0.2f;

        /// <summary>
        /// A side's colour for a name or number drawn on a dark pane: the palette's, lifted a
        /// little toward white so a dark blue stays readable on near-black. Less than the menu's
        /// lift (<see cref="Client.TeamInk.Lift"/>), whose navy glass needs more.
        /// </summary>
        public static Color TeamInk(Color team) => Client.TeamInk.Lifted(team, TeamInkLift);

        /// <summary>
        /// Shows <paramref name="picture"/> at <paramref name="height"/>, as wide as its own shape
        /// allows up to <paramref name="maxWidth"/>: the weapon art in the killfeed and on a name
        /// plate, which runs from a squat pistol to a long rifle.
        /// </summary>
        public static void FitPicture(Image image, LayoutElement size, Sprite picture, float height, float maxWidth)
        {
            image.sprite = picture;

            Rect shape = picture.rect;
            float aspect = shape.height > 0f ? shape.width / shape.height : 1f;
            float width = Mathf.Clamp(height * aspect, height, maxWidth);

            size.minWidth = width;
            size.preferredWidth = width;
        }

        // ---- the Tab board (owner's report of 2026-09-30) ----

        /// <summary>A bot's callsign: quieter than a player's name, still easy to read.</summary>
        public static readonly Color BotInk = new Color(0.78f, 0.81f, 0.86f);

        /// <summary>The robot beside a bot's name.</summary>
        public static readonly Color BotMark = new Color(0.58f, 0.63f, 0.7f);

        /// <summary>A live streak's flame and count.</summary>
        public static readonly Color StreakInk = new Color(1f, 0.55f, 0.12f);

        /// <summary>The kills column's mark and heading: a clear green.</summary>
        public static readonly Color KillsInk = new Color(0.36f, 0.9f, 0.5f);

        /// <summary>The K/D column's heading: the palette's cyan.</summary>
        public static readonly Color RatioHeadInk = new Color(0.48f, 0.81f, 1f);

        /// <summary>The score column's heading: a warm yellow, apart from the gold of BEST.</summary>
        public static readonly Color ScoreHeadInk = new Color(1f, 0.83f, 0.3f);

        /// <summary>The dot beside a live player.</summary>
        public static readonly Color AliveInk = new Color(0.3f, 0.95f, 0.45f);

        /// <summary>The skull beside a dead player.</summary>
        public static readonly Color DeadInk = new Color(1f, 0.3f, 0.26f);

        /// <summary>The rank number on a medal: dark, to read on gold, silver and bronze alike.</summary>
        public static readonly Color MedalInk = new Color(0.08f, 0.08f, 0.1f);

        /// <summary>Gold, silver and bronze for ranks 1 to 3.</summary>
        public static Color MedalColour(int rank)
            => rank == 1 ? new Color(1f, 0.8f, 0.22f)
             : rank == 2 ? new Color(0.8f, 0.84f, 0.9f)
             : new Color(0.86f, 0.55f, 0.3f);

        /// <summary>K/D from red to green: under 0.5, under 1, under 2, and 2 or better.</summary>
        public static Color RatioInk(float ratio, bool hasAny)
        {
            if (!hasAny) return BoardFaint;
            if (ratio >= 2f) return new Color(0.4f, 1f, 0.5f);
            if (ratio >= 1f) return Ink;
            if (ratio >= 0.5f) return new Color(1f, 0.78f, 0.35f);
            return new Color(1f, 0.45f, 0.4f);
        }

        /// <summary>Ping green under 80 ms, amber under 150, red beyond.</summary>
        public static Color PingInk(int pingMs)
            => pingMs < 80 ? new Color(0.4f, 1f, 0.5f)
             : pingMs < 150 ? new Color(1f, 0.8f, 0.3f)
             : new Color(1f, 0.4f, 0.35f);

        /// <summary>
        /// Ease-out that overshoots a little and settles back: for a killfeed row or badge that
        /// should land with some weight.
        /// </summary>
        public static float EaseOutBack(float t)
        {
            t = Mathf.Clamp01(t);
            const float c1 = 1.70158f;
            const float c3 = c1 + 1f;
            float u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        /// <summary>Cubic ease-out, for everything that arrives.</summary>
        public static float EaseOut(float t)
        {
            t = Mathf.Clamp01(t);
            float remaining = 1f - t;
            return 1f - remaining * remaining * remaining;
        }
    }
}
