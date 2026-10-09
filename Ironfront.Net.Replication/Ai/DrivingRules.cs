using System;

namespace Ironfront.Net.Replication.Ai
{
    /// <summary>
    /// How a bot drives round what is in front of it, and how it gets a stuck vehicle free.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner, 2026-10-08:</b> bots drive into everything, do not know how to steer, back up or
    /// turn once they have, and then leave the vehicle and walk. The original driver followed its
    /// path and saw only teammates (<c>Vehicle.BlockTest</c> on the hitbox layer): a tree, a
    /// rock, a hedgehog or a wall beside the path was invisible to it until it was in it. Its
    /// recovery was one reverse of a second, straight back, and three of those inside 30 s
    /// abandoned the vehicle. A Forest Lake bot soak (as the server builds it, 600 game seconds, 50
    /// a side) logged 51 vehicles marked stuck and 93 left empty while still alive.
    /// </para>
    /// <para>
    /// <b>Feelers.</b> Three casts ahead of the vehicle, left, centre and right, report how far
    /// each runs before something solid. <see cref="Avoid"/> turns those three distances into a
    /// steering nudge away from the nearer side and a throttle that slows as the centre closes.
    /// </para>
    /// <para>
    /// <b>Recovery.</b> Each recovery backs up longer than the last (<see cref="ReverseSeconds"/>),
    /// turning the nose toward the clearer side as it goes (<see cref="NoseTurn"/>), and a
    /// vehicle is given up only after <see cref="RecoveriesBeforeGivingUp"/> recoveries inside
    /// <see cref="RecoveryMemorySeconds"/>.
    /// </para>
    /// <para>
    /// Engine-free so it is tested here; <c>AiActorController</c> casts the feelers and drives.
    /// </para>
    /// </remarks>
    public static class DrivingRules
    {
        /// <summary>Degrees each side feeler points off the vehicle's heading.</summary>
        public const float SideFeelerDegrees = 28f;

        /// <summary>Shortest look ahead, in metres: what a vehicle crawling needs to turn.</summary>
        public const float MinLookahead = 6f;

        /// <summary>Longest look ahead, in metres.</summary>
        public const float MaxLookahead = 20f;

        /// <summary>Seconds of travel the feelers look ahead at speed.</summary>
        public const float LookaheadSeconds = 1.3f;

        /// <summary>How hard an obstacle at a side feeler's tip-to-bumper pushes the steering away.</summary>
        public const float SideGain = 0.7f;

        /// <summary>How hard an obstacle dead ahead turns the steering toward the clearer side.</summary>
        public const float CentreGain = 1.1f;

        /// <summary>The slowest an obstacle ahead brings the throttle to, as a share of what the path wanted.</summary>
        public const float MinThrottleShare = 0.3f;

        /// <summary>Recoveries inside <see cref="RecoveryMemorySeconds"/> before the squad walks out.</summary>
        public const int RecoveriesBeforeGivingUp = 5;

        /// <summary>Seconds a recovery is remembered for.</summary>
        public const float RecoveryMemorySeconds = 45f;

        /// <summary>The look ahead at <paramref name="speed"/> metres a second.</summary>
        public static float Lookahead(float speed)
            => Math.Max(MinLookahead, Math.Min(MaxLookahead, Math.Abs(speed) * LookaheadSeconds));

        /// <summary>
        /// The steering nudge (+1 is full right) and the share of the path's throttle to keep, for
        /// obstacles <paramref name="left"/>, <paramref name="centre"/> and <paramref name="right"/>
        /// metres along the three feelers. A distance past <paramref name="lookahead"/>, or
        /// <see cref="float.PositiveInfinity"/>, is clear.
        /// </summary>
        public static void Avoid(float left, float centre, float right, float lookahead,
            out float steer, out float throttleShare)
        {
            float closeLeft = Closeness(left, lookahead);
            float closeCentre = Closeness(centre, lookahead);
            float closeRight = Closeness(right, lookahead);

            // Away from whichever side is nearer: an obstacle on the left pushes right.
            steer = (closeLeft - closeRight) * SideGain;

            // Dead ahead: toward the side with more room, and the harder the nearer it is.
            if (closeCentre > 0f)
            {
                float toward = left > right ? -1f : 1f;
                if (left == right) toward = closeLeft <= closeRight ? -1f : 1f;
                steer += toward * closeCentre * CentreGain;
            }

            steer = Math.Max(-1f, Math.Min(1f, steer));
            throttleShare = Math.Max(MinThrottleShare, 1f - closeCentre * (1f - MinThrottleShare));
        }

        /// <summary>
        /// The steering to drive with: the path's, nudged by the feelers', clamped. A feeler nudge
        /// against a hard turn the path is asking for fades out as the turn tightens, and is gone at
        /// full lock: the path knows the way round, and the first soak of this rule had nudges
        /// cancel such turns, so cars that could not make a corner drove straight on at full
        /// throttle until they left the map.
        /// </summary>
        public static float BlendSteer(float pathSteer, float avoidSteer)
        {
            float hardness = Math.Abs(pathSteer);
            if (hardness > HardTurn && Math.Sign(avoidSteer) != Math.Sign(pathSteer))
            {
                avoidSteer *= Math.Max(0f, (1f - hardness) / (1f - HardTurn));
            }
            return Math.Max(-1f, Math.Min(1f, pathSteer + avoidSteer));
        }

        /// <summary>The path steering past which a feeler nudge against it starts to fade.</summary>
        public const float HardTurn = 0.5f;

        /// <summary>Fastest a bot backs up, in metres a second: reversing is for manoeuvring.</summary>
        public const float MaxReverseSpeed = 6f;

        /// <summary>
        /// Metres a waypoint may lie behind before a driver turns round to it rather than backing up
        /// the whole way.
        /// </summary>
        public const float TurnAroundDistance = 15f;

        /// <summary>
        /// The throttle with a cap on backing up: past <see cref="MaxReverseSpeed"/> a reverse
        /// throttle becomes a forward one, which is the brake. The original driver steered less the
        /// faster it went, backwards too, so a car backing toward a waypoint behind it soon could not
        /// steer at all: bot soaks of 2026-10-08 caught quad bikes reversing straight at 23-38 m/s
        /// until they left the map.
        /// </summary>
        public static float LimitReverse(float throttle, float forwardSpeed)
            => forwardSpeed < -MaxReverseSpeed && throttle < 0f ? 0.6f : throttle;

        /// <summary>
        /// Whether a driver turns round, forward, toward a waypoint
        /// <paramref name="aheadComponent"/> metres ahead (negative: behind) and
        /// <paramref name="distance"/> metres away, rather than backing up to it.
        /// </summary>
        public static bool TurnAround(float aheadComponent, float distance)
            => aheadComponent < 0f && distance > TurnAroundDistance;

        /// <summary>
        /// Seconds to back up on the <paramref name="attempt"/>th recovery in a row (1-based):
        /// 1.2, 2.0, 2.8, then 3.6 for every one after.
        /// </summary>
        public static float ReverseSeconds(int attempt)
            => 1.2f + 0.8f * (Math.Max(1, Math.Min(attempt, 4)) - 1);

        /// <summary>
        /// Which way a recovery turns the nose (+1 right, -1 left): toward the side with more room
        /// along the feelers, <paramref name="left"/> and <paramref name="right"/> metres. With
        /// neither side clearer, it alternates between recoveries so a second try takes another line.
        /// </summary>
        public static float NoseTurn(float left, float right, int attempt)
        {
            if (left > right) return -1f;
            if (right > left) return 1f;
            return attempt % 2 == 0 ? 1f : -1f;
        }

        /// <summary>
        /// The steering a CAR backs up with to make <see cref="NoseTurn"/> happen: wheels to the
        /// right swing a reversing car's nose to the left, so it is the opposite sign. A tank's
        /// tracks turn the hull directly, so a tank steers by <see cref="NoseTurn"/> itself.
        /// </summary>
        public static float CarReverseSteer(float left, float right, int attempt)
            => -NoseTurn(left, right, attempt);

        private static float Closeness(float distance, float lookahead)
        {
            if (!(lookahead > 0f) || float.IsNaN(distance) || distance >= lookahead) return 0f;
            return 1f - Math.Max(0f, distance) / lookahead;
        }
    }
}
