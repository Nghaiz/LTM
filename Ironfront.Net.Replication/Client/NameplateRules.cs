using System;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// When a name and health bar float over somebody's head, and how large. Playtest
    /// 2026-09-28, feature 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Teammates through walls, enemies only in sight.</b> A teammate's plate shows out to
    /// <see cref="TeammateRange"/> and dims behind cover, because knowing where your side is is
    /// the point. An enemy's shows only in line of sight and within <see cref="EnemyRange"/>: an
    /// enemy plate through a wall is a wallhack the game would be handing out.
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

        /// <summary>A teammate behind cover: still placed, at this opacity.</summary>
        public const float CoveredTeammateOpacity = 0.5f;

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

        /// <summary>How visible a plate is, 0 (not drawn) to 1.</summary>
        public static float Opacity(bool teammate, float distance, bool covered)
        {
            float range = teammate ? TeammateRange : EnemyRange;

            if (distance > range) return 0f;
            if (!teammate && covered) return 0f;

            float fadeFrom = range * (1f - FadeShare);
            float opacity = distance <= fadeFrom ? 1f : 1f - (distance - fadeFrom) / (range - fadeFrom);

            return teammate && covered ? opacity * CoveredTeammateOpacity : opacity;
        }

        /// <summary>How large a plate is drawn, <see cref="MinScale"/> to 1.</summary>
        public static float Scale(float distance)
        {
            if (distance <= FullSizeDistance) return 1f;

            float shrink = 1f - (distance - FullSizeDistance) / ShrinkDistance;
            return Math.Max(MinScale, shrink);
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
}
