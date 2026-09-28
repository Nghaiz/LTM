using System;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// The two animator floats and one bool that make a remote body's legs move, for one frame.
    /// phase-P2 task 3.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The names are the controller's, not ours.</b> <c>Assets/AnimatorController/Actor.controller</c>
    /// — the one controller <c>Player Fps Actor.prefab</c> and <c>Remote Actor Proxy.prefab</c>
    /// BOTH use — carries <c>movement x</c> and <c>movement y</c> as floats and <c>moving</c> as
    /// a bool. Read from the asset on 2026-08-29, not assumed: the proxy's controller GUID is
    /// <c>54b1bd752e9742e459d70a1045db1667</c> and it resolves to that file.
    /// </para>
    /// <para>
    /// <b>The blend tree's axis convention, read from the same asset.</b> Four 2D freeform
    /// trees drive locomotion, all keyed on the pair, and their motion nodes sit at
    /// x = ±1.18 / ±3.05..3.49 and y = +1.23..3.28 / −0.89..−2.87. Those are METRES PER SECOND,
    /// not a normalised −1..1: <c>MovementCore.WalkSpeed</c> is 3.5, which is where the run nodes
    /// are, and the walk nodes sit near a third of it. <b>y is forward-positive, x is
    /// right-positive</b> — <c>Actor.UpdateMovement</c> feeds them
    /// <c>new Vector2(localVelocity.x, localVelocity.z)</c>, and Unity's local z is forward. So
    /// the value this solver produces is a local-space velocity in m/s, fed in unscaled.
    /// </para>
    /// <para>
    /// <b><c>moving</c> is the gate, and leaving it out is why the floats alone would have
    /// changed nothing.</b> Every transition into a locomotion state is conditioned on it —
    /// <c>Standing Idle → Locomotion Forward</c> reads <c>moving == true AND movement y > -0.01</c>,
    /// and <c>Locomotion * → Standing Idle</c> reads <c>moving == false</c>. A body with correct
    /// <c>movement x</c>/<c>y</c> and no <c>moving</c> stays in <c>Standing Idle</c> forever and
    /// slides exactly as it did before.
    /// </para>
    /// </remarks>
    public readonly struct RemoteLocomotion : IEquatable<RemoteLocomotion>
    {
        /// <summary>Local-space rightward speed, m/s. The blend tree's x axis.</summary>
        public readonly float MovementX;

        /// <summary>Local-space forward speed, m/s. The blend tree's y axis.</summary>
        public readonly float MovementY;

        /// <summary>Whether the animator may leave its idle state at all.</summary>
        public readonly bool IsMoving;

        public RemoteLocomotion(float movementX, float movementY, bool isMoving)
        {
            MovementX = movementX;
            MovementY = movementY;
            IsMoving  = isMoving;
        }

        /// <summary>Standing still: both axes exactly zero, and the gate shut.</summary>
        public static RemoteLocomotion Idle => default;

        public bool Equals(RemoteLocomotion other)
            => MovementX.Equals(other.MovementX)
            && MovementY.Equals(other.MovementY)
            && IsMoving == other.IsMoving;

        public override bool Equals(object? obj) => obj is RemoteLocomotion other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = MovementX.GetHashCode();
                hash = (hash * 397) ^ MovementY.GetHashCode();
                return (hash * 397) ^ IsMoving.GetHashCode();
            }
        }

        public override string ToString()
            => $"({MovementX:0.###}, {MovementY:0.###}) moving={IsMoving}";
    }

    /// <summary>
    /// Turns a remote actor's replicated velocity into the locomotion parameters its animator
    /// controller expects. The testable half of the fix; <c>RemoteActorView</c> pushes the result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Decision P2-D1 — read the wire, do not derive. Neither option the phase plan tabled.</b>
    /// The plan weighed "derive from the interpolated transform" against "add the pair to the
    /// snapshot", chose the first, and noted that <c>SnapshotField</c> is 8/8 full so the second
    /// costs a protocol version bump. <b>The 8/8 claim is true and was re-verified</b> — bits 0..7
    /// are <c>Position, Rotation, Velocity, StateFlags, Health, Weapon, Team, SeatInfo</c> on a
    /// <c>byte</c> enum, with no room left. But bit 2 is <c>Velocity</c>: the field is <em>already
    /// there</em>, <c>NetServerActor.Capture</c> already feeds it <c>Movement.State.Velocity</c>,
    /// and <c>DeltaEncoder</c>/<c>DeltaDecoder</c> already carry it. The plan's decision rested on
    /// a premise its own rule 3 told it to re-read. There is no wire change to make and no
    /// derivation to justify: this is the same number the owner's simulation produced.
    /// </para>
    /// <para>
    /// <b>Why a derived fallback still exists.</b> <c>InterestManager</c> ZEROES the velocity
    /// fields for any actor past <c>NearRadius</c> = 60 m when
    /// <c>ReplicationConfig.UseVelocityCulling</c> is on, which it is by default. Wire-only would
    /// therefore have reinstated the exact defect this phase closes for every body beyond 60 m —
    /// sliding at <c>Standing Idle</c>. So the wire is the source whenever it carries anything,
    /// and a displacement-derived velocity covers the band where the server deliberately declined
    /// to send one. The fallback inherits interpolation jitter by construction; it only ever runs
    /// at 60 m and beyond, where a leg cycle is a few pixels tall.
    /// </para>
    /// <para>
    /// <b>Reopening condition for P2-D1.</b> If a later phase needs the owner's <em>intent</em>
    /// rather than its displacement — a body strafing while sliding on ice, an animation that must
    /// lead the movement — neither the wire velocity nor the derived one can express it, and the
    /// question of a dedicated field reopens. It would then require a protocol version bump,
    /// because <c>SnapshotField</c> has no ninth bit.
    /// </para>
    /// <para>
    /// <b>Allocation-free and Unity-free.</b> Every input is a struct passed by reference and the
    /// result is a struct. This assembly may not reference <c>UnityEngine</c>
    /// (architecture.md § 5.1), which is also what makes the whole derivation gradeable by CI.
    /// </para>
    /// </remarks>
    public static class RemoteLocomotionSolver
    {
        /// <summary>
        /// Horizontal m/s below which a body counts as standing still. The local actor's own
        /// threshold, copied deliberately: <c>Actor.UpdateMovement</c> gates its <c>moving</c>
        /// write on <c>velocity.magnitude &gt; 0.1f</c>, and a remote body that disagreed with the
        /// local one about when walking starts would read as a netcode fault rather than a
        /// tuning difference.
        /// </summary>
        public const float MovingSpeed = 0.1f;

        /// <summary>
        /// Per-second rate of the exponential smoothing, matching <c>Actor.UpdateMovement</c>'s
        /// <c>Vector2.Lerp(movement, b, 5f * dt)</c>. Same constant, so a remote body accelerates
        /// into its walk cycle at the same rate the owner's own body does.
        /// </summary>
        public const float SmoothingRate = 5f;

        /// <summary>
        /// Solves one frame of locomotion for one remote body.
        /// </summary>
        /// <param name="previous">Last frame's result, for the smoothing. <see cref="RemoteLocomotion.Idle"/> on the first.</param>
        /// <param name="state">The decoded snapshot, for the velocity and the halt conditions.</param>
        /// <param name="derivedVelocity">World-space m/s from the body's own displacement. Used only when the wire carries no velocity at all.</param>
        /// <param name="yawDegrees">The yaw the body is drawn at, which is the frame the blend tree's axes are expressed in.</param>
        /// <param name="deltaSeconds">Seconds since the previous solve. Zero or negative holds the previous value rather than jumping.</param>
        public static RemoteLocomotion Solve(
            in RemoteLocomotion previous,
            in RemoteActorVisualState state,
            in Vec3 derivedVelocity,
            float yawDegrees,
            float deltaSeconds)
        {
            // The three bodies that are moving and are NOT walking, each rejected by name rather
            // than left to the arithmetic:
            //
            //   dead / ragdolled -- a corpse slides, tumbles and is dragged by its own rig. Every
            //     metre of that is displacement, and none of it is a step. Feeding it into the
            //     blend tree would put a walk cycle on a body lying on the floor.
            //   seated -- a driven vehicle moves its passenger at the vehicle's speed. The
            //     passenger is stationary relative to the seat, and Actor.controller has a
            //     `seated` state for exactly this; sending it 30 m/s of "forward walk" would
            //     sprint a man sitting in a jeep.
            //
            // Returning Idle rather than smoothing toward it is deliberate: `moving` goes false in
            // the same frame, so the state machine leaves the blend tree and the float values stop
            // being read. Lerping them to zero would be invisible work with a non-zero result.
            if (!state.IsAlive || state.IsRagdoll || state.IsSeated) return RemoteLocomotion.Idle;

            // Wire first, displacement only where the wire is silent. `!= 0f` and not an epsilon:
            // PackVel truncates toward zero, so a culled actor and a genuinely still one both
            // produce exact zeros, and both are answered correctly by falling through -- a still
            // body's displacement is zero too.
            Vec3 wire = state.Velocity;
            bool wireCarriesMotion = wire.X != 0f || wire.Z != 0f;
            Vec3 source = wireCarriesMotion ? wire : derivedVelocity;

            // Flattened, because the blend tree has no vertical axis and a body falling down a
            // slope would otherwise read as running. `Actor.UpdateMovement` drops Y the same way,
            // through its `removeY` scale.
            float worldX = source.X;
            float worldZ = source.Z;

            // World -> body-local, yaw only. This is the inverse of the rotation the registry
            // writes to the transform, expanded rather than composed so it stays allocation-free
            // and reference-free: lx = wx*cos - wz*sin, lz = wx*sin + wz*cos.
            double radians = yawDegrees * (Math.PI / 180.0);
            float sin = (float)Math.Sin(radians);
            float cos = (float)Math.Cos(radians);

            float localX = worldX * cos - worldZ * sin;
            float localZ = worldX * sin + worldZ * cos;

            float speedSquared = localX * localX + localZ * localZ;
            if (speedSquared <= MovingSpeed * MovingSpeed) return RemoteLocomotion.Idle;

            // The owner's backpedal convention, reproduced rather than approximated.
            // `Actor.UpdateMovement` computes `flag3 = Dot(velocity, forward) < 0` -- which is
            // exactly `localZ < 0` -- and on that branch negates the x component before writing
            // it. Without this line a body strafing left while walking backwards would lean the
            // wrong way against the same blend tree the local player leans correctly in.
            if (localZ < 0f) localX = -localX;

            // Guarded rather than trusted: the first solve after a Bind has no elapsed time, and
            // a stalled frame can hand over a delta long enough to overshoot the target. Clamping
            // to [0, 1] makes the worst case "snap to target", never "oscillate past it".
            float t = SmoothingRate * deltaSeconds;
            if (!(t > 0f)) t = 0f;      // also catches NaN, which any comparison would pass
            else if (t > 1f) t = 1f;

            return new RemoteLocomotion(
                previous.MovementX + (localX - previous.MovementX) * t,
                previous.MovementY + (localZ - previous.MovementY) * t,
                isMoving: true);
        }
    }
}
