using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// How big each minimap icon is drawn, as a share of the map it sits on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a share and not a pixel size.</b> The HUD canvases are constant-pixel-size, so the
    /// original's fixed 16 px soldier was a different fraction of the map on every screen and on
    /// each of the two maps it is drawn on: 3% of the deploy screen's map at 1080p, less on
    /// anything larger (owner report 2026-09-29: the icons are too small to read). Sizing from
    /// the map's own drawn width keeps an icon the same share of the map everywhere; the clamp
    /// keeps it legible in a small window and stops it swallowing the map on a large one.
    /// </para>
    /// <para>
    /// Everything else is a multiple of the soldier, so the icons keep their proportions to each
    /// other at any size: a vehicle reads as bigger than the soldiers around it, and the player's
    /// own arrow as bigger than both.
    /// </para>
    /// <para>
    /// In this seam assembly rather than beside the HUD because both halves draw minimap icons:
    /// <c>ActorBlip</c> and <c>MinimapMarker</c> in <c>Assembly-CSharp</c>, which cannot see
    /// <c>Ironfront.Net.Unity.Client</c>, and the tests, which cannot see <c>Assembly-CSharp</c>.
    /// </para>
    /// </remarks>
    public static class MinimapIconLayout
    {
        /// <summary>A soldier icon's side as a share of the drawn map's width.</summary>
        public const float SoldierShareOfMap = 0.021f;

        /// <summary>Below this a soldier's heading is no longer readable.</summary>
        public const float MinSoldierPixels = 14f;

        /// <summary>Above this a soldier icon covers the ground it stands on.</summary>
        public const float MaxSoldierPixels = 26f;

        /// <summary>A player is drawn a little larger than a bot, as well as in a lighter shade.</summary>
        public const float HumanScale = 1.15f;

        /// <summary>The player's own arrow.</summary>
        public const float SelfScale = 1.55f;

        /// <summary>
        /// A flag, and the spawn button under it on the deploy screen: never smaller than a vehicle,
        /// because flags are drawn over the vehicles and soldiers and must stay the thing you see
        /// (owner report 2026-09-29: the icons covered the flags).
        /// </summary>
        public const float FlagScale = 1.6f;

        /// <summary>The team-coloured ring pulsing behind the player's own arrow.</summary>
        public const float HaloScale = 1.75f;

        /// <summary>A crewed vehicle. Empty vehicles are not drawn at all (owner ruling 2026-09-29).</summary>
        public const float VehicleScale = 1.6f;

        /// <summary>The newest dot of a movement trail; older dots shrink from here.</summary>
        public const float TrailDotScale = 0.36f;

        /// <summary>The player's view cone reaches this many soldier-widths from the arrow.</summary>
        public const float ViewConeReachScale = 4.6f;

        /// <summary>A vehicle's speed leader points to where it will be this many seconds ahead.</summary>
        public const float LeaderSeconds = 4f;

        /// <summary>Slower than this a vehicle is parked or crawling, and draws no leader.</summary>
        public const float LeaderMinSpeed = 2.5f;

        /// <summary>The longest leader, in lengths of the vehicle's own icon.</summary>
        public const float LeaderMaxIconLengths = 3f;

        /// <summary>A leader shorter than this, in canvas pixels, is hidden rather than drawn as a stub.</summary>
        public const float LeaderMinPixels = 4f;

        /// <summary>The leader line's width, in canvas pixels.</summary>
        public const float LeaderWidthPixels = 4f;

        /// <summary>The chevron at the leader's tip, in soldier widths.</summary>
        public const float LeaderTipScale = 0.6f;

        /// <summary>The side of a soldier icon, in canvas pixels, on a map drawn this wide.</summary>
        public static float SoldierPixels(float mapWidthPixels)
        {
            if (float.IsNaN(mapWidthPixels) || mapWidthPixels <= 0f)
            {
                return MinSoldierPixels;
            }
            return Mathf.Clamp(mapWidthPixels * SoldierShareOfMap, MinSoldierPixels, MaxSoldierPixels);
        }

        /// <summary>
        /// How long a vehicle's speed leader is drawn, in canvas pixels: the ground it covers in
        /// <see cref="LeaderSeconds"/> at <paramref name="speed"/> m/s, capped at
        /// <see cref="LeaderMaxIconLengths"/> icon lengths; 0 when it is too slow or too short to show.
        /// </summary>
        /// <remarks>
        /// <b>Why a leader as well as a trail</b> (owner report 2026-09-29: vehicles must show their
        /// movement clearly). Dustbowl's map draws about 1.8 m per pixel, so a jeep at 20 m/s
        /// leaves a trail of 20 px in two seconds, most of it under its own 50 px icon. A line
        /// to where the vehicle will be, as on a radar screen, reads at any map scale: the
        /// direction is where it is going, the length how fast.
        /// </remarks>
        public static float LeaderPixels(float speed, float pixelsPerMetre, float iconPixels)
        {
            if (float.IsNaN(speed) || speed < LeaderMinSpeed || pixelsPerMetre <= 0f)
            {
                return 0f;
            }
            float length = Mathf.Min(speed * LeaderSeconds * pixelsPerMetre, iconPixels * LeaderMaxIconLengths);
            return length < LeaderMinPixels ? 0f : length;
        }

        /// <summary>
        /// The horizontal field of view of <paramref name="camera"/> in degrees, or
        /// <paramref name="fallbackDegrees"/> when there is no usable camera.
        /// </summary>
        /// <remarks>
        /// <c>Camera.fieldOfView</c> is the VERTICAL angle; a cone drawn from it on a 16:9 screen
        /// shows barely two thirds of what the player can see.
        /// </remarks>
        public static float HorizontalFieldOfView(Camera camera, float fallbackDegrees = 90f)
        {
            if (camera == null || camera.orthographic || camera.aspect <= 0f)
            {
                return fallbackDegrees;
            }
            return Camera.VerticalToHorizontalFieldOfView(camera.fieldOfView, camera.aspect);
        }
    }
}
