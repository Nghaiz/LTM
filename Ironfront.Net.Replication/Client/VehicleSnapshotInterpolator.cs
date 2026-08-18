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
    /// <i>when</i> to render, so <see cref="DelayTicks"/> and <see cref="Capacity"/> are read
    /// from <see cref="SnapshotInterpolator"/> rather than redeclared — two definitions of the
    /// render delay is how the vehicle and the man standing on it end up 33 ms apart.
    /// </para>
    /// <para>
    /// <b>It never extrapolates (V5-D2).</b> When the buffer runs dry the caller holds the last
    /// known pose and <see cref="StalledCount"/> moves. A vehicle at 30 m/s extrapolated across
    /// a 200 ms gap is 6 metres wrong and then snaps back; the freeze is both less wrong and
    /// more informative, because a freeze is what a bad network looks like and a snap is not.
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
        /// <summary>
        /// How far behind the newest snapshot to render, in simulation ticks. The actor value,
        /// by reference — see the type remarks.
        /// </summary>
        public const int DelayTicks = SnapshotInterpolator.DelayTicks;

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

        /// <summary>Drops everything. Call on disconnect, or when the baseline is reset.</summary>
        public void Reset()
        {
            _count = 0;
            OutOfOrderCount = 0;
            StalledCount = 0;
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
        /// The tick to render right now: <see cref="DelayTicks"/> behind the newest snapshot.
        /// </summary>
        /// <param name="tickFraction">
        /// How far the local clock has advanced into the current tick, 0..1. Without it the
        /// render tick only advances in whole steps and the interpolation is quantised to the
        /// very rate it exists to hide.
        /// </param>
        public double RenderTick(double tickFraction)
        {
            if (_count == 0) return 0.0;
            return Newest().ServerTick + tickFraction - DelayTicks;
        }

        /// <summary>
        /// Samples one vehicle's pose at <paramref name="renderTick"/>.
        /// </summary>
        /// <remarks>
        /// Position and velocities lerp, rotation slerps, turret yaw takes the short way round,
        /// flags and the subtype tail come from the earlier snapshot — see
        /// <see cref="VehiclePose"/> for why the tail is not blended.
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

            int held = Count;
            long oldestIndex = _count - held;

            VehicleWorldSnapshot oldest = At(oldestIndex);
            if (renderTick <= oldest.ServerTick)
            {
                return Single(oldest, vehicleId, out pose)
                    ? VehicleSampleResult.TooOld
                    : VehicleSampleResult.NotPresent;
            }

            VehicleWorldSnapshot newest = Newest();
            if (renderTick >= newest.ServerTick)
            {
                StalledCount++;
                return Single(newest, vehicleId, out pose)
                    ? VehicleSampleResult.Stalled
                    : VehicleSampleResult.NotPresent;
            }

            // Linear scan from the newest backwards. `held` is 16 and the answer is almost
            // always the first or second entry, because the render tick trails the newest by
            // DelayTicks -- a binary search costs more in branches than it saves.
            for (long i = _count - 1; i > oldestIndex; i--)
            {
                VehicleWorldSnapshot later = At(i);
                VehicleWorldSnapshot earlier = At(i - 1);

                if (renderTick < earlier.ServerTick || renderTick >= later.ServerTick) continue;

                if (!earlier.TryFind(vehicleId, out VehicleSnapshotEntry a)) return VehicleSampleResult.NotPresent;
                if (!later.TryFind(vehicleId, out VehicleSnapshotEntry b)) return VehicleSampleResult.NotPresent;

                // The gap is not always 1: a dropped snapshot leaves a two-tick span, and
                // dividing by a hardcoded 1 would cover it in half the time and then wait --
                // the exact stutter this class exists to remove.
                double span = later.ServerTick - (double)earlier.ServerTick;
                float alpha = span <= 0.0 ? 0f : (float)((renderTick - earlier.ServerTick) / span);

                pose = Blend(in a, in b, alpha);
                return VehicleSampleResult.Interpolated;
            }

            StalledCount++;
            return Single(newest, vehicleId, out pose)
                ? VehicleSampleResult.Stalled
                : VehicleSampleResult.NotPresent;
        }

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
