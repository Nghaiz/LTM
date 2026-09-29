using System;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// Whose head gets a name and health bar, when, and how large. Playtest 2026-09-28,
    /// feature 1; people only since 2026-09-29.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>People, not bots.</b> A plate is for a player on either side, never a bot: see
    /// <see cref="PlateNameOf"/>.
    /// </para>
    /// <para>
    /// <b>Only what the screen shows.</b> Owner's report of 2026-09-30: a player hiding behind a
    /// wall had their plate float over it, and the other side fired a rocket straight into the
    /// wall -- a map-hack the game was handing out. A plate is now drawn for a body the viewer
    /// could see on their own screen, on either side: some part of it inside the view and not
    /// behind terrain, a rock, a wall, a tree or a vehicle. Teammates no longer show through
    /// cover either.
    /// </para>
    /// <para>
    /// <b>A scope sees further.</b> The ranges below are for the naked eye; a narrower field of
    /// view multiplies them (<see cref="ZoomFactor"/>), so a sniper aiming down a scope still gets
    /// the plate of the body in their crosshair at 300 m, while nobody gets one through a wall at 5.
    /// </para>
    /// <para>
    /// Engine-free, so the rules are tested here; the client measures distance and sight and
    /// asks.
    /// </para>
    /// </remarks>
    public static class NameplateRules
    {
        /// <summary>How far a teammate's plate is shown, in metres.</summary>
        public const float TeammateRange = 150f;

        /// <summary>How far an enemy in sight gets a plate, in metres.</summary>
        public const float EnemyRange = 70f;

        /// <summary>The last share of either range, over which a plate fades out.</summary>
        public const float FadeShare = 0.15f;

        /// <summary>The field of view the ranges are set for, in degrees: the game's own hip fire.</summary>
        public const float DefaultFieldOfView = 60f;

        /// <summary>The most a scope multiplies the ranges by.</summary>
        public const float MaxZoom = 10f;

        /// <summary>Closer than this, a plate is full size.</summary>
        public const float FullSizeDistance = 12f;

        /// <summary>Metres past <see cref="FullSizeDistance"/> over which a plate shrinks to its floor.</summary>
        public const float ShrinkDistance = 150f;

        /// <summary>The smallest a distant plate gets, so a far name stays readable.</summary>
        public const float MinScale = 0.6f;

        /// <summary>Clear air between the top of a head and the bottom of its plate, in metres.</summary>
        public const float HeadClearance = 0.45f;

        /// <summary>A seated body's head over the seat pivot, which sits at the hips.</summary>
        public const float SeatedHeadHeight = 1.25f;

        /// <summary>
        /// The top of a kneeling body. Not <see cref="MovementCore.CrouchHeight"/>: that is the
        /// crouched COLLIDER, 0.5 m, well below the head the player actually sees.
        /// </summary>
        public const float CrouchedHeadHeight = 1.2f;

        /// <summary>The top of a body lying prone.</summary>
        public const float ProneHeadHeight = 0.5f;

        /// <summary>
        /// The name a plate shows over this actor, or null when the actor gets no plate at all:
        /// people only, on both sides (owner ruling 2026-09-29).
        /// </summary>
        /// <remarks>
        /// <c>S_PLAYER_LIST</c> names every connected person and never a bot, so it is the one
        /// answer to "is that somebody": a bot has no row, and neither has a slot whose player has
        /// left. Not <see cref="ActorNames.Display"/>, which names bots too ("VIPER") --
        /// a plate over every one of them was the clutter the owner asked to have removed.
        /// </remarks>
        public static string? PlateNameOf(ushort actorId, PlayerNameTable names)
            => names.NameOf(actorId);

        /// <summary>
        /// How visible a plate is, 0 (not drawn) to 1: nothing for a body the viewer cannot see,
        /// otherwise full inside the side's range -- scaled by the scope's <paramref name="zoom"/>
        /// -- and fading out over its last stretch.
        /// </summary>
        public static float Opacity(bool teammate, float distance, bool visible, float zoom = 1f)
        {
            if (!visible) return 0f;

            float range = (teammate ? TeammateRange : EnemyRange) * Math.Max(1f, Math.Min(zoom, MaxZoom));
            if (distance > range) return 0f;

            float fadeFrom = range * (1f - FadeShare);
            return distance <= fadeFrom ? 1f : 1f - (distance - fadeFrom) / (range - fadeFrom);
        }

        /// <summary>
        /// How much a camera's <paramref name="fieldOfViewDegrees"/> magnifies over
        /// <see cref="DefaultFieldOfView"/>: 1 at hip fire or wider, about 6.6 through a 10-degree
        /// sniper scope, never more than <see cref="MaxZoom"/>.
        /// </summary>
        public static float ZoomFactor(float fieldOfViewDegrees)
        {
            if (!(fieldOfViewDegrees > 0f) || fieldOfViewDegrees >= DefaultFieldOfView) return 1f;

            double wide = Math.Tan(DefaultFieldOfView * 0.5 * Math.PI / 180.0);
            double narrow = Math.Tan(fieldOfViewDegrees * 0.5 * Math.PI / 180.0);
            return (float)Math.Min(MaxZoom, wide / narrow);
        }

        /// <summary>How large a plate is drawn, <see cref="MinScale"/> to 1.</summary>
        /// <remarks>
        /// Through a scope the body looks <paramref name="zoom"/> times nearer, and its plate is
        /// sized for how near it looks.
        /// </remarks>
        public static float Scale(float distance, float zoom = 1f)
        {
            distance /= Math.Max(1f, zoom);
            if (distance <= FullSizeDistance) return 1f;

            float shrink = 1f - (distance - FullSizeDistance) / ShrinkDistance;
            return Math.Max(MinScale, shrink);
        }

        /// <summary>
        /// The heights above the feet at which the viewer looks for a body: the head, the chest and
        /// the hips, for the pose it is in. Writes three and returns 3.
        /// </summary>
        /// <remarks>
        /// Points ON the body, never the plate's anchor above it: the anchor floats half a metre over
        /// the head, and a line to it cleared the top of a wall the whole body was hiding behind --
        /// the plate over the wall in the owner's report.
        /// </remarks>
        public static int SightHeights(bool crouching, bool prone, bool seated, Span<float> heights)
        {
            if (heights.Length < 3) throw new ArgumentException("Room for three heights, please.", nameof(heights));

            if (seated)
            {
                heights[0] = SeatedHeadHeight - 0.1f;
                heights[1] = SeatedHeadHeight - 0.45f;
                heights[2] = SeatedHeadHeight - 0.75f;
            }
            else if (prone)
            {
                heights[0] = ProneHeadHeight - 0.1f;
                heights[1] = ProneHeadHeight - 0.2f;
                heights[2] = ProneHeadHeight - 0.3f;
            }
            else if (crouching)
            {
                heights[0] = CrouchedHeadHeight - 0.12f;
                heights[1] = CrouchedHeadHeight - 0.45f;
                heights[2] = CrouchedHeadHeight - 0.75f;
            }
            else
            {
                heights[0] = MovementCore.StandHeight - 0.15f;
                heights[1] = MovementCore.StandHeight - 0.55f;
                heights[2] = MovementCore.StandHeight - 0.9f;
            }

            return 3;
        }

        /// <summary>Metres from the body's feet (its snapshot position) up to where the plate sits.</summary>
        public static float AnchorHeight(bool crouching, bool prone, bool seated)
            => (seated ? SeatedHeadHeight
                : prone ? ProneHeadHeight
                : crouching ? CrouchedHeadHeight
                : MovementCore.StandHeight) + HeadClearance;

        /// <summary>The snapshot's health byte as a share of full health.</summary>
        public static float Health01(byte health) => Math.Min(health, (byte)100) / 100f;
    }

    /// <summary>
    /// When each body was last seen by the viewer, and how present its plate is because of it.
    /// Owner's report of 2026-09-30.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sight is tested a few times a second, not every frame, so a plate holds for
    /// <see cref="HoldSeconds"/> after the last sighting -- the gap between two tests -- and then
    /// fades over <see cref="FadeSeconds"/>. A body that ducks behind a wall loses its plate
    /// within about a quarter of a second; one that steps out gets it on the next test.
    /// </para>
    /// <para>Engine-free and allocation-free: one timestamp per actor id.</para>
    /// </remarks>
    public sealed class NameplateSight
    {
        /// <summary>Between two sight tests of one body.</summary>
        public const float RecheckSeconds = 0.05f;

        /// <summary>A plate stays at full strength this long after the last sighting.</summary>
        public const float HoldSeconds = 0.1f;

        /// <summary>Then it fades out over this long.</summary>
        public const float FadeSeconds = 0.15f;

        private readonly float[] _lastSeen = new float[Protocol.ProtocolConstants.MAX_ACTORS];
        private readonly float[] _nextTest = new float[Protocol.ProtocolConstants.MAX_ACTORS];

        public NameplateSight() => Reset();

        /// <summary>Whether this body is due another sight test at <paramref name="nowSeconds"/>.</summary>
        public bool IsDue(ushort actorId, float nowSeconds)
            => actorId < _nextTest.Length && nowSeconds >= _nextTest[actorId];

        /// <summary>Records one sight test. <paramref name="stagger"/> spreads bodies over frames.</summary>
        public void Report(ushort actorId, bool seen, float nowSeconds, float stagger = 0f)
        {
            if (actorId >= _lastSeen.Length) return;

            _nextTest[actorId] = nowSeconds + RecheckSeconds + stagger;
            if (seen) _lastSeen[actorId] = nowSeconds;
        }

        /// <summary>How present the body's plate is, 1 (just seen) to 0 (not seen lately).</summary>
        public float Presence(ushort actorId, float nowSeconds)
        {
            if (actorId >= _lastSeen.Length) return 0f;

            float age = nowSeconds - _lastSeen[actorId];
            if (age <= HoldSeconds) return 1f;

            float fade = 1f - (age - HoldSeconds) / FadeSeconds;
            return fade <= 0f ? 0f : fade;
        }

        /// <summary>Forgets every body: nobody is seen until the next test says so.</summary>
        public void Reset()
        {
            for (int i = 0; i < _lastSeen.Length; i++)
            {
                _lastSeen[i] = float.NegativeInfinity;
                _nextTest[i] = float.NegativeInfinity;
            }
        }
    }
}
