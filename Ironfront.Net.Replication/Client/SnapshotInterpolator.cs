using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// Why <see cref="SnapshotInterpolator.TrySample"/> could not produce a pair.
    /// </summary>
    public enum InterpolationResult
    {
        /// <summary>Two snapshots bracket the render tick. <c>Alpha</c> is meaningful.</summary>
        Interpolated = 0,

        /// <summary>Fewer than two snapshots have arrived. Nothing to draw yet.</summary>
        Starved = 1,

        /// <summary>
        /// The render tick is older than everything buffered — the buffer has already moved
        /// past it. Snap to the oldest snapshot rather than extrapolating backwards.
        /// </summary>
        TooOld = 2,

        /// <summary>
        /// The render tick is newer than the newest snapshot: the next one has not arrived.
        /// The caller holds the newest pose. See the type remarks on why this does not
        /// extrapolate.
        /// </summary>
        Stalled = 3,
    }

    /// <summary>
    /// Holds the last N world snapshots and finds the two that bracket a render time, so remote
    /// actors move smoothly between 30 Hz updates instead of teleporting on each one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client half of phase-01; M1 criterion 7 is graded on what this
    /// produces at 100 ms RTT and 5% loss.
    /// </para>
    /// <para>
    /// <b>Rendering runs deliberately in the past.</b> The client draws at
    /// <c>newestServerTick - </c><see cref="DelayTicks"/> so that the snapshot after the one
    /// being drawn has usually already arrived. Without that lead-in there is nothing to
    /// interpolate *towards*, and every remote actor stutters between arrivals no matter how
    /// good the transport is. Two ticks is 66 ms at 30 Hz, which covers one lost packet plus
    /// ordinary jitter — the 5% loss in criterion 7 means roughly one snapshot in twenty is
    /// missing, and a one-tick delay would visibly hitch on each of them.
    /// </para>
    /// <para>
    /// <b>It never extrapolates.</b> When the buffer runs dry the caller holds the last known
    /// pose (<see cref="InterpolationResult.Stalled"/>) rather than projecting velocity forward.
    /// Extrapolation looks smoother for about 100 ms and then produces a visible snap backwards
    /// when the real snapshot disagrees, and it makes actors run through walls during a stall
    /// because nothing in this layer knows about collision. A brief freeze is honest and, at 5%
    /// loss, rare.
    /// </para>
    /// <para>
    /// <b>Snapshots are copied in, not referenced.</b> <see cref="DeltaDecoder"/> mutates and
    /// reuses one <see cref="WorldSnapshot"/> instance, so storing the reference would leave the
    /// whole buffer pointing at the newest state — every entry identical, interpolation a no-op,
    /// and nothing to see in a debugger that would explain why. The ring owns its copies.
    /// </para>
    /// <para>
    /// <b>Zero allocation after construction.</b> The ring is allocated once and reused, which
    /// is what M1 criterion 9 asks of the per-tick path.
    /// </para>
    /// </remarks>
    public sealed class SnapshotInterpolator
    {
        /// <summary>
        /// How far behind the newest snapshot to render, in simulation ticks.
        /// </summary>
        /// <remarks>
        /// 2 ticks = 66 ms at <see cref="ProtocolConstants.SIM_TICK_RATE"/>. See the type
        /// remarks for why one is not enough at criterion 7's 5% loss.
        /// </remarks>
        public const int DelayTicks = 2;

        /// <summary>
        /// Snapshots retained. Half a second at 30 Hz — enough to ride out a burst of loss,
        /// short enough that a client which stalls longer than that resynchronises from a fresh
        /// baseline rather than interpolating across a hole it cannot see the far side of.
        /// </summary>
        public const int Capacity = 16;

        private readonly WorldSnapshot[] _ring = new WorldSnapshot[Capacity];

        // Count of pushes, not a wrapped index: _count - 1 is always the newest and
        // _count - Capacity the oldest still held, which removes the empty-versus-full
        // ambiguity a head/tail pair has at exactly Capacity entries.
        private long _count;

        /// <summary>Creates a ring with every slot pre-allocated.</summary>
        public SnapshotInterpolator()
        {
            for (int i = 0; i < Capacity; i++) _ring[i] = new WorldSnapshot();
        }

        /// <summary>How many snapshots are currently held, up to <see cref="Capacity"/>.</summary>
        public int Count => (int)Math.Min(_count, Capacity);

        /// <summary>The server tick of the newest snapshot, or 0 when empty.</summary>
        public uint NewestTick => _count == 0 ? 0u : Newest().ServerTick;

        /// <summary>Snapshots rejected as older than one already held. A reorder indicator.</summary>
        public long OutOfOrderCount { get; private set; }

        /// <summary>Samples that found no bracketing pair. A starvation indicator.</summary>
        public long StalledCount { get; private set; }

        /// <summary>Drops everything. Call on disconnect, or when the baseline is reset.</summary>
        public void Reset()
        {
            _count = 0;
            OutOfOrderCount = 0;
            StalledCount = 0;
        }

        /// <summary>
        /// Copies a decoded snapshot into the ring.
        /// </summary>
        /// <returns>
        /// False when <paramref name="snapshot"/> is not newer than the newest held, in which
        /// case nothing is stored.
        /// </returns>
        /// <remarks>
        /// Ordering is decided with <see cref="SequenceMath.IsNewer32"/> rather than
        /// <c>&gt;</c>: the tick is a u32 that wraps, and a plain comparison would reject every
        /// snapshot for a while after the wrap and then accept them again, producing a freeze
        /// that only reproduces after 4.5 years of uptime.
        /// </remarks>
        public bool Push(WorldSnapshot snapshot)
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
        /// How far the local clock has advanced into the current tick, 0..1. Pass
        /// <c>NetPredictionClock.Alpha</c>. It is what makes motion smooth at frame rates above
        /// the 30 Hz tick — without it the render tick only ever advances in whole steps and the
        /// interpolation is quantised to the tick rate it exists to hide.
        /// </param>
        public double RenderTick(double tickFraction)
        {
            if (_count == 0) return 0.0;
            return Newest().ServerTick + tickFraction - DelayTicks;
        }

        /// <summary>
        /// Finds the two snapshots bracketing <paramref name="renderTick"/>.
        /// </summary>
        /// <param name="from">The snapshot at or before the render tick.</param>
        /// <param name="to">The snapshot after it. Same as <paramref name="from"/> unless the
        /// result is <see cref="InterpolationResult.Interpolated"/>.</param>
        /// <param name="alpha">
        /// Position between the two, 0..1. Zero for every non-interpolated result, so a caller
        /// that ignores the return value still lands exactly on <paramref name="from"/> rather
        /// than somewhere arbitrary.
        /// </param>
        public InterpolationResult TrySample(
            double renderTick, out WorldSnapshot? from, out WorldSnapshot? to, out double alpha)
        {
            alpha = 0.0;
            from = null;
            to = null;

            if (_count < 2)
            {
                if (_count == 1) { from = to = Newest(); }
                StalledCount++;
                return InterpolationResult.Starved;
            }

            int held = Count;
            long oldestIndex = _count - held;

            WorldSnapshot oldest = At(oldestIndex);
            if (renderTick <= oldest.ServerTick)
            {
                from = to = oldest;
                return InterpolationResult.TooOld;
            }

            WorldSnapshot newest = Newest();
            if (renderTick >= newest.ServerTick)
            {
                from = to = newest;
                StalledCount++;
                return InterpolationResult.Stalled;
            }

            // Linear scan from the newest backwards. `held` is 16, and the answer is almost
            // always the first or second entry because the render tick trails the newest by
            // DelayTicks -- a binary search would cost more in branches than it saves.
            for (long i = _count - 1; i > oldestIndex; i--)
            {
                WorldSnapshot later = At(i);
                WorldSnapshot earlier = At(i - 1);

                if (renderTick >= earlier.ServerTick && renderTick < later.ServerTick)
                {
                    from = earlier;
                    to = later;

                    // The gap is not always 1: a dropped snapshot leaves a two-tick span, and
                    // dividing by a hardcoded 1 would make the actor cover that span in half
                    // the time and then wait -- the exact stutter this class exists to remove.
                    double span = later.ServerTick - (double)earlier.ServerTick;
                    alpha = span <= 0.0 ? 0.0 : (renderTick - earlier.ServerTick) / span;
                    return InterpolationResult.Interpolated;
                }
            }

            from = to = newest;
            StalledCount++;
            return InterpolationResult.Stalled;
        }

        /// <summary>
        /// Interpolates one actor's position between two snapshots, in world units.
        /// </summary>
        /// <returns>False when the actor is absent from either snapshot — it spawned or
        /// despawned across the pair, and a position blended from one end is a slide in from
        /// wherever the other end happened to leave it.</returns>
        public static bool TryLerpPosition(
            WorldSnapshot? from, WorldSnapshot? to, double alpha, ushort actorId, out Vec3 position)
        {
            position = default;

            if (from == null || to == null) return false;
            if (!from.TryFind(actorId, out ActorSnapshotEntry a)) return false;
            if (!to.TryFind(actorId, out ActorSnapshotEntry b)) return false;

            float t = (float)alpha;
            position = new Vec3(
                Lerp(Quantize.UnpackPos(a.PosX), Quantize.UnpackPos(b.PosX), t),
                Lerp(Quantize.UnpackPos(a.PosY), Quantize.UnpackPos(b.PosY), t),
                Lerp(Quantize.UnpackPos(a.PosZ), Quantize.UnpackPos(b.PosZ), t));
            return true;
        }

        /// <summary>
        /// Interpolates one actor's yaw in degrees, taking the short way round.
        /// </summary>
        /// <remarks>
        /// A plain lerp from 350 to 10 spins the actor 340 degrees the wrong way over one tick.
        /// The wrap is not an edge case — it is any actor facing roughly north.
        /// </remarks>
        public static bool TryLerpYaw(
            WorldSnapshot? from, WorldSnapshot? to, double alpha, ushort actorId, out float yawDegrees)
        {
            yawDegrees = 0f;

            if (from == null || to == null) return false;
            if (!from.TryFind(actorId, out ActorSnapshotEntry a)) return false;
            if (!to.TryFind(actorId, out ActorSnapshotEntry b)) return false;

            float ya = Quantize.UnpackYaw(a.Yaw);
            float yb = Quantize.UnpackYaw(b.Yaw);

            float delta = yb - ya;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;

            float result = ya + delta * (float)alpha;
            result %= 360f;
            if (result < 0f) result += 360f;

            yawDegrees = result;
            return true;
        }

        private static float Lerp(float a, float b, float t) => a + (b - a) * t;

        private WorldSnapshot Newest() => At(_count - 1);

        private WorldSnapshot At(long absoluteIndex) => _ring[(int)(absoluteIndex % Capacity)];
    }
}
