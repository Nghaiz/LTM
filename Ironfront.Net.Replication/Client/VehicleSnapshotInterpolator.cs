using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// Why <see cref="VehicleSnapshotInterpolator.TrySample"/> produced what it produced.
    /// </summary>
    public enum VehicleSampleResult
    {
        /// <summary>Two snapshots bracket the render tick. The pose is interpolated.</summary>
        Interpolated = 0,

        /// <summary>Fewer than two snapshots have arrived. Nothing to draw yet.</summary>
        Starved = 1,

        /// <summary>
        /// The render tick is older than everything buffered. The pose is the oldest held,
        /// rather than an extrapolation backwards.
        /// </summary>
        TooOld = 2,

        /// <summary>
        /// The render tick is newer than the newest snapshot: the next one has not arrived. The
        /// pose is the newest held and the caller holds it. See the type remarks on why this
        /// does not extrapolate.
        /// </summary>
        Stalled = 3,

        /// <summary>
        /// The vehicle is not in the snapshots that would have been sampled. It spawned or
        /// despawned across the pair, or it is out of interest. No pose.
        /// </summary>
        NotPresent = 4,

        /// <summary>
        /// Held at a single snapshot's pose: the vehicle's first sample while the render tick is
        /// still before it, or the end of an extrapolation that ran to its limit.
        /// </summary>
        /// <remarks>
        /// <b>No longer the steady state past 60 m.</b> A rate-limited vehicle is in every 2nd
        /// (Mid) or 5th (Far) snapshot and never in two ADJACENT worlds; ledger X-64 made that
        /// drawable by holding the one real end, which stepped the body at 10 Hz or 4 Hz. It is
        /// now bracketed by its own samples, so Mid interpolates and Far interpolates or
        /// extrapolates, and a Held sample is the exception it looks like.
        /// </remarks>
        Held = 5,

        /// <summary>
        /// Newer snapshots exist but the vehicle is in none of them — the server rate-limited it
        /// out. Carried along its own velocities for at most
        /// <see cref="SnapshotInterpolator.MaxExtrapolationTicks"/>.
        /// </summary>
        Extrapolated = 6,
    }

    /// <summary>
    /// Holds the last N vehicle snapshots and samples one vehicle's pose at a render time, so
    /// replicated vehicles move smoothly between 20 Hz updates instead of teleporting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A separate class from <see cref="SnapshotInterpolator"/>, sharing its constants but
    /// not its code (V5-D1).</b> The actor interpolator lerps a position and a single yaw,
    /// because an infantryman does not roll. A vehicle needs a full quaternion slerp, rides its
    /// own stream at its own cadence, and needs its own ring. What the two must agree on is
    /// <i>when</i> to render, and both are sampled at the one <see cref="InterpolationClock"/>
    /// the router owns — two render times is how the vehicle and the man standing on it end up
    /// a tick apart.
    /// </para>
    /// <para>
    /// <b>A stall never extrapolates (V5-D2); a rate-limited gap does.</b> When the render tick
    /// reaches the newest snapshot the stream has stopped, the caller holds the last known pose
    /// and <see cref="StalledCount"/> moves: a vehicle at 30 m/s projected across a 200 ms stall
    /// is 6 metres wrong and then snaps back, and the freeze is what a bad network honestly looks
    /// like. A Far vehicle is different — its samples are 7.5 ticks apart by design while newer
    /// snapshots keep arriving without it — and carrying it on its own velocities for up to
    /// <see cref="SnapshotInterpolator.MaxExtrapolationTicks"/> is what keeps it moving between
    /// them instead of stopping and jumping at 4 Hz.
    /// </para>
    /// <para>
    /// <b>Snapshots are copied in, not referenced.</b> <see cref="VehicleDeltaDecoder"/> mutates
    /// and reuses one <see cref="VehicleWorldSnapshot"/>, so storing the reference would leave
    /// every ring slot pointing at the newest state — sixteen identical entries and an
    /// interpolation that is a no-op, with nothing in a debugger to say why.
    /// </para>
    /// <para>
    /// Zero allocation after construction.
    /// </para>
    /// </remarks>
    public sealed class VehicleSnapshotInterpolator
    {
        /// <summary>Snapshots retained. The actor value, by reference.</summary>
        public const int Capacity = SnapshotInterpolator.Capacity;

        private readonly VehicleWorldSnapshot[] _ring = new VehicleWorldSnapshot[Capacity];

        // Count of pushes, not a wrapped index: _count - 1 is always the newest and
        // _count - Capacity the oldest still held, which removes the empty-versus-full
        // ambiguity a head/tail pair has at exactly Capacity entries.
        private long _count;

        /// <summary>Creates a ring with every slot pre-allocated.</summary>
        public VehicleSnapshotInterpolator()
        {
            for (int i = 0; i < Capacity; i++) _ring[i] = new VehicleWorldSnapshot();
        }

        /// <summary>How many snapshots are currently held, up to <see cref="Capacity"/>.</summary>
        public int Count => (int)Math.Min(_count, Capacity);

        /// <summary>The server tick of the newest snapshot, or 0 when empty.</summary>
        public uint NewestTick => _count == 0 ? 0u : Newest().ServerTick;

        /// <summary>Snapshots rejected as not newer than one already held. A reorder indicator.</summary>
        public long OutOfOrderCount { get; private set; }

        /// <summary>Samples that ran off the newest end of the buffer. A starvation indicator.</summary>
        public long StalledCount { get; private set; }

        /// <summary>Samples held at a single pose. See <see cref="VehicleSampleResult.Held"/>.</summary>
        /// <remarks>
        /// <b>Counted because the silence is what made X-64 survive.</b> The old code returned
        /// <see cref="VehicleSampleResult.NotPresent"/> without touching
        /// <see cref="StalledCount"/>, so a permanently frozen vehicle read as a perfectly
        /// healthy stream on every counter there was -- the lane-B regrade measured 303 m of
        /// divergence at <c>vehicleInterpStalled 0</c>. Every way of not interpolating is counted.
        /// </remarks>
        public long HeldCount { get; private set; }

        /// <summary>Samples carried past a vehicle's newest sample across a rate-limited gap.</summary>
        public long ExtrapolatedCount { get; private set; }

        /// <summary>Drops everything. Call on disconnect, or when the baseline is reset.</summary>
        public void Reset()
        {
            _count = 0;
            OutOfOrderCount = 0;
            StalledCount = 0;
            HeldCount = 0;
            ExtrapolatedCount = 0;
        }

        /// <summary>
        /// Copies a decoded vehicle snapshot into the ring.
        /// </summary>
        /// <returns>
        /// False when <paramref name="snapshot"/> is not newer than the newest held, in which
        /// case nothing is stored.
        /// </returns>
        /// <remarks>
        /// Ordering is decided with <see cref="SequenceMath.IsNewer32"/> rather than <c>&gt;</c>:
        /// the tick is a u32 that wraps, and a plain comparison would reject every snapshot for
        /// a while after the wrap and then accept them again — a freeze that only reproduces
        /// after years of uptime.
        /// </remarks>
        public bool Push(VehicleWorldSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));

            if (_count > 0 && !SequenceMath.IsNewer32(snapshot.ServerTick, Newest().ServerTick))
            {
                OutOfOrderCount++;
                return false;
            }

            _ring[(int)(_count % Capacity)].CopyFrom(snapshot);
            _count++;
            return true;
        }

        /// <summary>
        /// Samples one vehicle's pose at <paramref name="renderTick"/>.
        /// </summary>
        /// <param name="vehicleId">The vehicle to sample.</param>
        /// <param name="renderTick">Normally <see cref="InterpolationClock.RenderTick"/>.</param>
        /// <param name="pose">Default for <see cref="VehicleSampleResult.NotPresent"/>.</param>
        /// <remarks>
        /// The vehicle is bracketed by its OWN samples, not by adjacent worlds — see
        /// <see cref="VehicleSampleResult.Held"/>. Position and velocities lerp, rotation slerps,
        /// turret yaw takes the short way round, flags and the subtype tail come from the earlier
        /// sample — see <see cref="VehiclePose"/> for why the tail is not blended.
        /// </remarks>
        public VehicleSampleResult TrySample(ushort vehicleId, double renderTick, out VehiclePose pose)
        {
            pose = default;

            if (_count == 0) return VehicleSampleResult.Starved;

            if (_count == 1)
            {
                StalledCount++;
                return Single(Newest(), vehicleId, out pose)
                    ? VehicleSampleResult.Starved
                    : VehicleSampleResult.NotPresent;
            }

            long oldestIndex = _count - Count;

            // Newest to oldest. `later` keeps being overwritten while the samples are still after
            // the render tick, so it ends as the EARLIEST of them -- the bracket's far end.
            bool hasLater = false, hasEarlier = false;
            VehicleSnapshotEntry later = default, earlier = default;
            uint laterTick = 0, earlierTick = 0;

            for (long i = _count - 1; i >= oldestIndex; i--)
            {
                VehicleWorldSnapshot world = At(i);
                if (!world.TryFind(vehicleId, out VehicleSnapshotEntry entry)) continue;

                if (world.ServerTick > renderTick)
                {
                    later = entry;
                    laterTick = world.ServerTick;
                    hasLater = true;
                    continue;
                }

                earlier = entry;
                earlierTick = world.ServerTick;
                hasEarlier = true;
                break;
            }

            // In no buffered snapshot: genuinely gone. The explicit S_VEHICLE_DESPAWN on channel 2
            // is what retires the proxy; this only declines to draw it.
            if (!hasEarlier && !hasLater) return VehicleSampleResult.NotPresent;

            if (hasEarlier && hasLater)
            {
                // The vehicle's own span, not a world's. A dropped snapshot or a rate limit leaves
                // more than one tick between its samples, and dividing by a hardcoded 1 would cover
                // the gap in a fraction of the time and then wait.
                double span = laterTick - (double)earlierTick;
                float alpha = span <= 0.0 ? 0f : (float)((renderTick - earlierTick) / span);

                pose = Blend(in earlier, in later, alpha);
                return VehicleSampleResult.Interpolated;
            }

            if (!hasEarlier)
            {
                // Only samples after the render tick: the vehicle has just appeared, or the render
                // tick is older than the whole buffer. Its first pose, never a projection backwards.
                pose = VehiclePose.FromEntry(in later);
                if (renderTick <= At(oldestIndex).ServerTick) return VehicleSampleResult.TooOld;

                HeldCount++;
                return VehicleSampleResult.Held;
            }

            pose = VehiclePose.FromEntry(in earlier);

            if (renderTick >= Newest().ServerTick)
            {
                StalledCount++;
                return VehicleSampleResult.Stalled;
            }

            double gap = renderTick - earlierTick;
            bool capped = gap > SnapshotInterpolator.MaxExtrapolationTicks;
            if (capped) gap = SnapshotInterpolator.MaxExtrapolationTicks;

            pose = Extrapolate(in pose, (float)(gap / ProtocolConstants.SIM_TICK_RATE));

            if (capped)
            {
                HeldCount++;
                return VehicleSampleResult.Held;
            }

            ExtrapolatedCount++;
            return VehicleSampleResult.Extrapolated;
        }

        /// <summary>
        /// Carries a pose along its own linear and angular velocity for <paramref name="seconds"/>.
        /// </summary>
        /// <remarks>
        /// The wire velocities, not a finite difference: unlike an actor's, a vehicle's are never
        /// culled by distance, and they are the server's own numbers for the instant the sample
        /// was taken. Everything else — health, flags, turret, tail — stays as sampled.
        /// </remarks>
        private static VehiclePose Extrapolate(in VehiclePose pose, float seconds)
            => new VehiclePose(
                new Vec3(
                    pose.Position.X + pose.LinearVelocity.X * seconds,
                    pose.Position.Y + pose.LinearVelocity.Y * seconds,
                    pose.Position.Z + pose.LinearVelocity.Z * seconds),
                QuatMath.IntegrateAngularVelocity(in pose.Rotation, in pose.AngularVelocity, seconds),
                pose.LinearVelocity,
                pose.AngularVelocity,
                pose.Health,
                pose.Flags,
                pose.TurretYaw,
                pose.TurretPitch,
                pose.SubtypeA,
                pose.SubtypeB);

        /// <summary>
        /// Blends two dequantized entries. Public so a test can pin the arithmetic without
        /// driving a whole ring through it.
        /// </summary>
        public static VehiclePose Blend(in VehicleSnapshotEntry from, in VehicleSnapshotEntry to, float alpha)
        {
            VehiclePose a = VehiclePose.FromEntry(in from);
            VehiclePose b = VehiclePose.FromEntry(in to);

            if (float.IsNaN(alpha) || alpha <= 0f) return a;
            if (alpha >= 1f) return b;

            return new VehiclePose(
                Lerp(in a.Position, in b.Position, alpha),
                QuatMath.Slerp(in a.Rotation, in b.Rotation, alpha),
                Lerp(in a.LinearVelocity, in b.LinearVelocity, alpha),
                Lerp(in a.AngularVelocity, in b.AngularVelocity, alpha),
                a.Health + (b.Health - a.Health) * alpha,

                // Flags are a bitfield, not a quantity. Blending Burning would produce a
                // vehicle that is 40% on fire, which no consumer can render.
                a.Flags,

                LerpAngleDegrees(a.TurretYaw, b.TurretYaw, alpha),
                a.TurretPitch + (b.TurretPitch - a.TurretPitch) * alpha,
                a.SubtypeA,
                a.SubtypeB);
        }

        private static bool Single(VehicleWorldSnapshot snapshot, ushort vehicleId, out VehiclePose pose)
        {
            if (!snapshot.TryFind(vehicleId, out VehicleSnapshotEntry entry))
            {
                pose = default;
                return false;
            }

            pose = VehiclePose.FromEntry(in entry);
            return true;
        }

        private static Vec3 Lerp(in Vec3 a, in Vec3 b, float t)
            => new Vec3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t);

        /// <summary>
        /// Lerps two 0..360 angles the short way round. A plain lerp from 350 to 10 spins a
        /// turret 340 degrees the wrong way in one tick, for any turret facing roughly north.
        /// </summary>
        private static float LerpAngleDegrees(float a, float b, float t)
        {
            float delta = b - a;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;

            float result = (a + delta * t) % 360f;
            return result < 0f ? result + 360f : result;
        }

        private VehicleWorldSnapshot Newest() => At(_count - 1);

        private VehicleWorldSnapshot At(long absoluteIndex) => _ring[(int)(absoluteIndex % Capacity)];
    }
}
