using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// When a moving minimap icon leaves a dot behind it, and how fast the dots fade.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a trail.</b> A rotated icon shows where something FACES, which for a soldier
    /// strafing or a helicopter sliding sideways is not where it is going, and a parked jeep and
    /// one doing 80 km/h looked the same (owner report 2026-09-29: vehicles need icons that show
    /// their movement clearly). The dots behind an icon show the direction, and their spacing
    /// the speed.
    /// </para>
    /// <para>
    /// A position is kept only after enough time AND enough distance, so a body standing still
    /// leaves no dots and its old trail fades out behind it. The rules are here, pure, so they are
    /// pinned by tests; <c>MinimapTrail</c> in <c>Assembly-CSharp</c> draws them.
    /// </para>
    /// </remarks>
    public static class MinimapTrailRules
    {
        /// <summary>The shortest time between two recorded positions.</summary>
        public const float SampleSeconds = 0.3f;

        /// <summary>A position closer than this to the last one is not a step, just jitter.</summary>
        public const float MinStepMetres = 1.5f;

        /// <summary>How long a dot stays on the map.</summary>
        public const float LifetimeSeconds = 3f;

        /// <summary>
        /// A jump this long between two samples is a respawn or a teleport, not movement: the old
        /// path is forgotten rather than joined to the new one by a dotted line across the map.
        /// </summary>
        public const float JumpMetres = 60f;

        /// <summary>Whether a position is worth a new dot: enough time AND enough distance.</summary>
        public static bool ShouldSample(Vector3 previous, float previousStamp, Vector3 current, float now)
        {
            return now - previousStamp >= SampleSeconds
                && (current - previous).sqrMagnitude >= MinStepMetres * MinStepMetres;
        }

        /// <summary>Whether the step from <paramref name="previous"/> is a jump, not movement.</summary>
        public static bool IsJump(Vector3 previous, Vector3 current)
        {
            return (current - previous).sqrMagnitude >= JumpMetres * JumpMetres;
        }

        /// <summary>
        /// A dot's opacity at this age: 1 when new, 0 at <see cref="LifetimeSeconds"/>, falling
        /// quadratically so the tail thins out instead of ending on a hard dot.
        /// </summary>
        public static float FadeOf(float ageSeconds)
        {
            if (ageSeconds <= 0f)
            {
                return 1f;
            }
            if (ageSeconds >= LifetimeSeconds)
            {
                return 0f;
            }
            float remaining = 1f - ageSeconds / LifetimeSeconds;
            return remaining * remaining;
        }
    }
}
