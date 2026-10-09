#nullable enable

using System;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// How a capture point's name is worded and where it sits on the map: under its flag on the M
    /// map and the deploy screen's map, and in the top-right corner while the player stands on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner request 2026-10-09:</b> the bases carry their names on the map, easy to read and
    /// covering as little as possible; and standing on one names it under the side chip and the
    /// flag indicator, top-right. Forest Lake first, then Dustbowl and Island the same day. A point
    /// is named by authoring <c>CapturePoint.mapLabel</c> in its scene, and a point left blank draws
    /// no label at all, so which maps are named is scene data, never a map test in code.
    /// </para>
    /// <para>
    /// <b>Covering as little as possible</b> is three rules here and one in the drawing code: the
    /// label is small (<see cref="FontPixels"/>), it sits clear of the flag rather than over it
    /// (<see cref="FitsBelow"/>), it stays inside the map instead of being cut by its edge
    /// (<see cref="ClampCentre"/>), and <c>MinimapPointLabels</c> draws it under every soldier and
    /// vehicle icon, so a fight at a base is never hidden behind the base's name.
    /// </para>
    /// <para>
    /// No UnityEngine here: <c>Ironfront.Client.Flow.Tests</c> links this file and pins it.
    /// </para>
    /// </remarks>
    public static class CapturePointLabelRules
    {
        /// <summary>A label's font size as a share of the map's width, between the clamps.</summary>
        public const float FontShareOfMap = 0.0135f;

        /// <summary>The smallest font a map label is drawn in; below this it stops being readable.</summary>
        public const int MinFontPixels = 11;

        /// <summary>The largest; past this a label on the big deploy-screen map covers the ground round it.</summary>
        public const int MaxFontPixels = 16;

        /// <summary>Canvas pixels between the flag's edge and its label.</summary>
        public const float GapPixels = 2f;

        /// <summary>
        /// What a point is called on screen, or null when it has no name to show.
        /// </summary>
        /// <param name="authored">The point's authored <c>mapLabel</c>, as typed.</param>
        public static string? Wording(string? authored)
        {
            if (authored == null)
            {
                return null;
            }
            string trimmed = authored.Trim();
            return trimmed.Length == 0 ? null : trimmed.ToUpperInvariant();
        }

        /// <summary>The label's font size for a map drawn <paramref name="mapWidthPixels"/> wide.</summary>
        public static int FontPixels(float mapWidthPixels)
        {
            if (float.IsNaN(mapWidthPixels) || mapWidthPixels <= 0f)
            {
                return MinFontPixels;
            }
            int size = (int)Math.Round(mapWidthPixels * FontShareOfMap);
            return Math.Max(MinFontPixels, Math.Min(MaxFontPixels, size));
        }

        /// <summary>
        /// Whether the label fits under its flag without leaving the bottom of the map; when it does
        /// not (a base on the map's bottom edge) it goes above the flag instead.
        /// </summary>
        /// <param name="flagCentreY">The flag's centre, in pixels up from the map's bottom edge.</param>
        /// <param name="flagPixels">The flag's size on the map.</param>
        /// <param name="labelPixels">The label's height.</param>
        public static bool FitsBelow(float flagCentreY, float flagPixels, float labelPixels)
        {
            return flagCentreY - flagPixels * 0.5f - GapPixels - labelPixels >= 0f;
        }

        /// <summary>
        /// Where a label centred on its flag at <paramref name="centre"/> is drawn so that all of it
        /// stays on a map <paramref name="extent"/> pixels across: moved in from an edge it would
        /// cross, and centred on a map too narrow for it.
        /// </summary>
        public static float ClampCentre(float centre, float halfWidth, float extent)
        {
            if (halfWidth * 2f >= extent)
            {
                return extent * 0.5f;
            }
            return Math.Max(halfWidth, Math.Min(extent - halfWidth, centre));
        }
    }
}
