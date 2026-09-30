using System;
using Ironfront.Net.Replication.Ai;
using Ironfront.Net.Replication.Movement;
using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>What a spot offers a bot against one enemy.</summary>
    public enum CoverFit
    {
        /// <summary>Nothing close stands between the spot and the enemy.</summary>
        None = 0,

        /// <summary>Covered crouched and standing, with no side to shoot round: somewhere to hide.</summary>
        Hidden = 1,

        /// <summary>Covered crouched; standing, the bot shoots over the top.</summary>
        OverTop = 2,

        /// <summary>Covered; leaning left, the bot shoots round the side.</summary>
        LeanLeft = 3,

        /// <summary>Covered; leaning right, the bot shoots round the side.</summary>
        LeanRight = 4,
    }

    /// <summary>
    /// Judges cover against the enemy a bot is actually facing, with real rays against the level.
    /// Phase P28, part 2: the owner asked for bots that dodge and hide behind props.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the maps already carry.</b> Both maps hold about 1,700 authored cover points, placed
    /// by the original's <c>CoverPlacer</c> along every edge where the walkable ground meets a rock,
    /// wall or tree. The original chose among them by facing alone -- the nearest point turned
    /// within 30 degrees of the incoming fire, up to 50 m away -- and never asked whether the point
    /// hid the bot from where the shots came from. This asks, with the same heights the points were
    /// generated with (<see cref="LowHeight"/>, <see cref="HighHeight"/>, <see cref="LeanDistance"/>),
    /// so a point that passed its generation test passes here whenever it faces the enemy.
    /// </para>
    /// <para>
    /// <b>Here, not in Assembly-CSharp,</b> so edit-mode tests call it directly against real
    /// colliders; the bot side hands in positions and maps the answer to its own cover types.
    /// </para>
    /// </remarks>
    public static class CoverProbe
    {
        /// <summary>What stops a bullet for cover's sake: the level (layer 0) and vehicles (12).</summary>
        public const int ObstacleMask = (1 << 0) | (1 << 12);

        /// <summary>The level alone: what a bot stands on.</summary>
        public const int GroundMask = 1 << 0;

        /// <summary>A crouched body's chest over the feet, in metres: CoverPlacer's low test.</summary>
        public const float LowHeight = 0.7f;

        /// <summary>A standing shooter's eye over the feet, in metres: CoverPlacer's high test.</summary>
        public const float HighHeight = 1.5f;

        /// <summary>How far a lean moves the eye sideways, in metres: CoverPlacer's lean test.</summary>
        public const float LeanDistance = 0.3f;

        /// <summary>The low line must meet the obstacle this close to the spot for it to hide the bot.</summary>
        /// <remarks>
        /// Authored points stand half a metre off their obstacle; anything that stops the line
        /// further out is a distant hill or wall, which hides the bot but gives it nothing to hug.
        /// </remarks>
        public const float NearBlock = 1.6f;

        /// <summary>How far a firing line must run clear from the eye, in metres: past the obstacle.</summary>
        public const float FireClearance = 3f;

        /// <summary>The radius of a firing line: a gun barrel's clearance, CoverPlacer's 0.1 m.</summary>
        public const float FireLineRadius = 0.1f;

        /// <summary>An obstacle moving faster than this, in metres a second, is not cover.</summary>
        public const float MovingObstacleSpeed = 1f;

        /// <summary>How far past an obstacle's edge a spot behind it stands, in metres.</summary>
        public const float StandOff = 0.6f;

        /// <summary>A body's room to stand: radius and height, in metres.</summary>
        public const float BodyRadius = 0.3f;

        public const float BodyHeight = 1.8f;

        /// <summary>The steepest ground a spot may sit on: the normal's up component.</summary>
        public const float MinGroundNormalY = 0.7f;

        /// <summary>
        /// How far a spot's ground may lie above or below the height it was sought at, in metres:
        /// further, and it is the top of a rock or the foot of a drop, not ground beside it.
        /// </summary>
        public const float MaxGroundStep = 1.2f;

        private const float GroundProbeAbove = 2.5f;
        private const float GroundProbeBelow = 4f;

        /// <summary>The most candidates <see cref="Choose"/> weighs.</summary>
        public const int MaxCandidates = 16;

        /// <summary>What <paramref name="spot"/> (on the ground) offers against an enemy whose eye is at <paramref name="threatEye"/>.</summary>
        public static CoverFit Judge(Vector3 spot, Vector3 threatEye)
        {
            Vector3 low = spot + Vector3.up * LowHeight;
            if (!BlockedNear(low, threatEye))
            {
                return CoverFit.None;
            }

            Vector3 high = spot + Vector3.up * HighHeight;
            if (ClearToFire(high, threatEye))
            {
                return CoverFit.OverTop;
            }

            Vector3 flat = threatEye - high;
            flat.y = 0f;
            if (flat.sqrMagnitude < 1e-4f)
            {
                return CoverFit.Hidden;
            }

            // Facing the enemy, Cross(up, forward) is the right hand -- CoverPlacer's own test.
            Vector3 right = Vector3.Cross(Vector3.up, flat.normalized);
            if (ClearToFire(high + right * LeanDistance, threatEye))
            {
                return CoverFit.LeanRight;
            }
            if (ClearToFire(high - right * LeanDistance, threatEye))
            {
                return CoverFit.LeanLeft;
            }
            return CoverFit.Hidden;
        }

        /// <summary>
        /// The cheapest of <paramref name="count"/> candidate spots against the enemy, or -1 when
        /// none hides a bot from it. Distances and <see cref="CombatRules.CoverCost"/> are measured
        /// from <paramref name="origin"/>: the bot, or the flag a defence digs in round.
        /// </summary>
        /// <remarks>
        /// Nearest first, and it stops at the first candidate further away than its best cost so
        /// far -- a cost is never below its distance -- so the rays are spent where they can win.
        /// </remarks>
        public static int Choose(
            Vector3[] spots, int count, Vector3 origin, Vector3 threatEye, bool fallingBack, out CoverFit fit)
        {
            fit = CoverFit.None;
            count = Math.Min(count, Math.Min(spots.Length, MaxCandidates));
            if (count <= 0)
            {
                return -1;
            }

            Span<int> order = stackalloc int[count];
            Span<float> distance = stackalloc float[count];
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
                distance[i] = Vector3.Distance(spots[i], origin);
            }
            // Insertion sort: sixteen at the most.
            for (int i = 1; i < count; i++)
            {
                int item = order[i];
                int j = i - 1;
                while (j >= 0 && distance[order[j]] > distance[item])
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = item;
            }

            Vector3 axis = threatEye - origin;
            axis.y = 0f;
            axis = axis.sqrMagnitude > 1e-4f ? axis.normalized : Vector3.zero;

            int best = -1;
            float bestCost = float.PositiveInfinity;
            for (int k = 0; k < count; k++)
            {
                int i = order[k];
                if (distance[i] >= bestCost)
                {
                    break;
                }
                CoverFit judged = Judge(spots[i], threatEye);
                if (judged == CoverFit.None)
                {
                    continue;
                }
                float toward = Vector3.Dot(spots[i] - origin, axis);
                float cost = CombatRules.CoverCost(distance[i], toward, judged != CoverFit.Hidden, fallingBack);
                if (cost < bestCost)
                {
                    bestCost = cost;
                    best = i;
                    fit = judged;
                }
            }
            return best;
        }

        /// <summary>
        /// A spot on the ground just past <paramref name="obstacle"/>'s far side from the enemy, with
        /// room to stand -- where a bot hides behind a vehicle, which no authored point can know.
        /// </summary>
        public static bool SpotBehind(Bounds obstacle, Vector3 threat, out Vector3 spot)
        {
            spot = default;
            Vector3 away = obstacle.center - threat;
            away.y = 0f;
            if (away.sqrMagnitude < 1e-4f)
            {
                return false;
            }
            away.Normalize();

            // The box's half-width along the line: how far its far side lies from the centre.
            float half = Mathf.Abs(away.x) * obstacle.extents.x + Mathf.Abs(away.z) * obstacle.extents.z;
            Vector3 behind = obstacle.center + away * (half + StandOff + BodyRadius);
            return Ground(behind, obstacle.min.y, out spot);
        }

        /// <summary>
        /// The ground under <paramref name="at"/>, if it is walkable, dry, within
        /// <see cref="MaxGroundStep"/> of <paramref name="baseHeight"/>, and has room for a body.
        /// </summary>
        public static bool Ground(Vector3 at, float baseHeight, out Vector3 spot)
        {
            spot = at;
            var top = new Vector3(at.x, Mathf.Max(at.y, baseHeight) + GroundProbeAbove, at.z);
            if (!Physics.Raycast(top, Vector3.down, out RaycastHit hit, GroundProbeAbove + GroundProbeBelow,
                    GroundMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            if (hit.normal.y < MinGroundNormalY || Mathf.Abs(hit.point.y - baseHeight) > MaxGroundStep)
            {
                return false;
            }
            if (hit.point.y <= MovementCore.SurfaceAt(hit.point.x, hit.point.z))
            {
                return false;
            }

            spot = hit.point;
            Vector3 foot = spot + Vector3.up * (BodyRadius + 0.1f);
            Vector3 head = spot + Vector3.up * (BodyHeight - BodyRadius);
            return !Physics.CheckCapsule(foot, head, BodyRadius, ObstacleMask, QueryTriggerInteraction.Ignore);
        }

        private static bool BlockedNear(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            float length = delta.magnitude;
            if (length < 1e-3f)
            {
                return false;
            }
            if (!Physics.Raycast(from, delta / length, out RaycastHit hit, Mathf.Min(NearBlock, length),
                    ObstacleMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            return IsStill(hit.rigidbody);
        }

        private static bool ClearToFire(Vector3 from, Vector3 to)
        {
            Vector3 delta = to - from;
            float length = delta.magnitude;
            if (length < 1e-3f)
            {
                return false;
            }
            // A sweep cannot see a collider it starts inside: an eye in the rock is not clear.
            if (Physics.CheckSphere(from, FireLineRadius, ObstacleMask, QueryTriggerInteraction.Ignore))
            {
                return false;
            }
            return !Physics.SphereCast(from, FireLineRadius, delta / length, out _, Mathf.Min(FireClearance, length),
                ObstacleMask, QueryTriggerInteraction.Ignore);
        }

        private static bool IsStill(Rigidbody body)
            => body == null || body.isKinematic
               || body.linearVelocity.sqrMagnitude < MovingObstacleSpeed * MovingObstacleSpeed;
    }
}
