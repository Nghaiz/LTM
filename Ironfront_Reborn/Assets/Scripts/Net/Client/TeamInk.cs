using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// A team's palette colour, made legible as type on the UI's dark glass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The palette stays the one mapping.</b> <c>ITeamPalette</c> answers with the world's
    /// colours — <c>ColorScheme.TeamColor</c> is pure blue and pure red — and every caller still
    /// asks it through <c>NetClientBindings.TeamColourRgb</c>. This only lifts that answer toward
    /// white where it is drawn as text: pure blue on the menu's navy is about 1.3:1 and could not
    /// be read, where lifted 45% it is about 4.8:1, and within a few percent of the tints
    /// Ravenfield's own score bar already draws the sides in (#6F70FF, #FF4545).
    /// </para>
    /// <para>
    /// The neutral grey a missing palette answers with lifts to a lighter grey, so the degraded
    /// case stays the visible, uniform one it was meant to be.
    /// </para>
    /// </remarks>
    public static class TeamInk
    {
        /// <summary>How far toward white a team colour is lifted for type.</summary>
        public const float Lift = 0.45f;

        /// <summary>The type colour for a palette answer in <c>0xRRGGBB</c>.</summary>
        public static Color FromRgb(int rgb)
        {
            var colour = new Color(
                ((rgb >> 16) & 0xFF) / 255f,
                ((rgb >> 8) & 0xFF) / 255f,
                (rgb & 0xFF) / 255f);

            return Lifted(colour, Lift);
        }

        /// <summary>
        /// <paramref name="team"/> lifted <paramref name="lift"/> of the way to white. The one
        /// place the lift is done; the in-match HUD asks for less of it (<c>HudStyle.TeamInk</c>)
        /// because its panes are near-black rather than the menu's navy.
        /// </summary>
        public static Color Lifted(Color team, float lift) => Color.Lerp(team, Color.white, lift);
    }
}
