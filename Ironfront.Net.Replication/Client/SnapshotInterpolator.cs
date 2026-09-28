using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// What <see cref="SnapshotInterpolator.TrySampleActor"/> could make of one actor.
    /// </summary>
    public enum InterpolationResult
    {
        /// <summary>Two of the actor's own samples bracket the render tick. Blended.</summary>
        Interpolated = 0,

        /// <summary>Fewer than two snapshots have arrived. Nothing to draw yet.</summary>
        Starved = 1,

        /// <summary>
        /// The render tick is older than everything buffered. The actor's earliest sample is
        /// held rather than extrapolated backwards.
        /// </summary>
        TooOld = 2,

        /// <summary>
        /// The render tick has reached the newest snapshot: the stream has stopped. The actor's
        /// newest sample is held. See the type remarks on why a stall never extrapolates.
        /// </summary>
        Stalled = 3,

        /// <summary>
        /// Newer snapshots exist but the actor is in none of them — the server rate-limited it
        /// out. Projected along its last two samples, for at most
        /// <see cref="SnapshotInterpolator.MaxExtrapolationTicks"/>.
        /// </summary>
        Extrapolated = 4,

        /// <summary>
        /// Held at a single sample: the actor's only one, or the end of an extrapolation that
        /// ran to its limit, or its first sample while the render tick is still before it.
        /// </summary>
        Held = 5,

        /// <summary>In no buffered snapshot at all. Nothing to draw.</summary>
        NotPresent = 6,
    }

    /// <summary>
    /// One actor at one render tick: where to draw it, which way it faces, and the entry its
    /// discrete state (stance, weapon, health, ragdoll) is read from.
    /// </summary>
    public readonly struct ActorSample
    {
        public readonly Vec3 Position;
        public readonly float YawDegrees;

        /// <summary>
        /// The later of the two samples while interpolating, otherwise the one sample in use.
        /// Discrete state is stepped, never blended — lerping a crouch is meaningless.
        /// </summary>
        public readonly ActorSnapshotEntry State;

        public ActorSample(in Vec3 position, float yawDegrees, in ActorSnapshotEntry state)
        {
            Position = position;
            YawDegrees = yawDegrees;
            State = state;
        }
    }

    /// <summary>
    /// Holds the last N world snapshots and samples one actor at a render tick, so remote
    /// actors move smoothly between 20 Hz updates instead of teleporting on each one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The client half of phase-01; M1 criterion 7 is graded on what this produces at 100 ms
    /// RTT and 5% loss. The render tick itself comes from <see cref="InterpolationClock"/>.
    /// </para>
    /// <para>
    /// <b>Each actor is bracketed by its OWN samples, not by adjacent worlds.</b>
    /// <c>InterestManager.SendEveryN</c> sends a Mid actor (60–100 m) in every 2nd snapshot and
    /// a Far one in every 5th, and <c>DeltaDecoder</c> rebuilds each world from its message
    /// alone, so such an actor is never in two ADJACENT worlds. Bracketing by adjacent worlds
    /// could only ever hold its one real end, and the body stepped at 10 Hz or 4 Hz — the
    /// "teleports frame by frame" of the 2026-09-27 report for everything past 60 m. Searching
    /// for the actor's own previous and next samples interpolates a Mid actor across its three
    /// ticks exactly as it does a Near one across its one or two.
    /// </para>
    /// <para>
    /// <b>A rate-limited gap is extrapolated; a stall is not.</b> A Far actor's samples are 7.5
    /// ticks apart, more than the three-tick render delay can cover, so for part of every cycle
    /// the render tick has passed its newest sample while newer snapshots — without it — keep
    /// arriving. That gap is the band working as designed, and the actor is carried along its
    /// last two samples for up to <see cref="MaxExtrapolationTicks"/>, which hands over to plain
    /// interpolation without a jump whenever it kept a straight line. A STALL is different:
    /// when the render tick reaches the newest snapshot the whole stream has stopped, and every
    /// actor holds. Projecting through a stall looks smoother for 100 ms and then snaps back
    /// when the real snapshot disagrees, and it carries actors through walls because nothing in
    /// this layer knows about collision. A freeze is honest and, at 5% loss, rare.
    /// </para>
    /// <para>
    /// <b>Snapshots are copied in, not referenced.</b> <see cref="DeltaDecoder"/> mutates and
    /// reuses one <see cref="WorldSnapshot"/> instance, so storing the reference would leave the
    /// whole buffer pointing at the newest state — every entry identical, interpolation a no-op,
    /// and nothing to see in a debugger that would explain why. The ring owns its copies.
    /// </para>
    /// <para>
    /// <b>Zero allocation after construction.</b> The ring is allocated once and reused, and a
    /// sample is a struct, which is what M1 criterion 9 asks of the per-tick path.
    /// </para>
    /// </remarks>
    public sealed class SnapshotInterpolator
    {
        /// <summary>
        /// Snapshots retained. 24 ticks at 20 Hz — enough for a Far actor's two most recent
        /// samples (7.5 ticks apart) behind a three-tick render delay, and short enough that a
        /// client which stalls longer than that resynchronises from a fresh baseline rather than
        /// interpolating across a hole it cannot see the far side of.
        /// </summary>
        public const int Capacity = 16;

        /// <summary>
        /// The longest a rate-limited actor is carried past its newest sample, in ticks.
        /// </summary>
        /// <remarks>
        /// A Far actor is in every 5th snapshot, and five snapshot strides of the 2,1,2,1 pattern
        /// span 7 or 8 ticks. Its next sample lands about 4.5 ticks past the previous one's render
        /// time, so eight covers that with jitter to spare and no more: an actor that has genuinely
        /// left interest stops drifting a quarter of a second later.
        /// </remarks>
        public const int MaxExtrapolationTicks = 8;

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

        /// <summary>Samples taken with the stream starved or stalled. A starvation indicator.</summary>
        public long StalledCount { get; private set; }

        /// <summary>Samples carried past an actor's newest sample across a rate-limited gap.</summary>
        public long ExtrapolatedCount { get; private set; }

        /// <summary>Drops everything. Call on disconnect, or when the baseline is reset.</summary>
        public void Reset()
        {
            _count = 0;
            OutOfOrderCount = 0;
            StalledCount = 0;
            ExtrapolatedCount = 0;
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
        /// Samples one actor at <paramref name="renderTick"/>.
        /// </summary>
        /// <param name="actorId">The actor to sample.</param>
        /// <param name="renderTick">Normally <see cref="InterpolationClock.RenderTick"/>.</param>
        /// <param name="sample">
        /// Meaningful for every result except <see cref="InterpolationResult.Starved"/> and
        /// <see cref="InterpolationResult.NotPresent"/>, which leave it default.
        /// </param>
        public InterpolationResult TrySampleActor(
            ushort actorId, double renderTick, out ActorSample sample)
        {
            sample = default;

            if (_count < 2)
            {
                StalledCount++;
                return InterpolationResult.Starved;
            }

            long oldestIndex = _count - Count;

            // Newest to oldest. `later` keeps being overwritten while the samples are still
            // after the render tick, so it ends as the EARLIEST of them -- the bracket's far end.
            bool hasLater = false, hasEarlier = false, hasPrior = false;
            ActorSnapshotEntry later = default, earlier = default, prior = default;
            uint laterTick = 0, earlierTick = 0, priorTick = 0;

            for (long i = _count - 1; i >= oldestIndex; i--)
            {
                WorldSnapshot world = At(i);
                if (!world.TryFind(actorId, out ActorSnapshotEntry entry)) continue;

                if (world.ServerTick > renderTick)
                {
                    later = entry;
                    laterTick = world.ServerTick;
                    hasLater = true;
                    continue;
                }

                if (!hasEarlier)
                {
                    earlier = entry;
                    earlierTick = world.ServerTick;
                    hasEarlier = true;

                    // A bracket needs nothing older. Only an extrapolation reads `prior`.
                    if (hasLater) break;
                    continue;
                }

                prior = entry;
                priorTick = world.ServerTick;
                hasPrior = true;
                break;
            }

            if (!hasEarlier && !hasLater) return InterpolationResult.NotPresent;

            if (hasEarlier && hasLater)
            {
                // The span is the actor's own, not a world's: a Mid actor's two samples are three
                // ticks apart, and dividing by one would cover them in a third of the time and
                // then wait -- the exact stutter this class exists to remove.
                double span = laterTick - (double)earlierTick;
                float t = span <= 0.0 ? 0f : (float)((renderTick - earlierTick) / span);

                sample = new ActorSample(
                    Lerp(PositionOf(in earlier), PositionOf(in later), t),
                    LerpYaw(Quantize.UnpackYaw(earlier.Yaw), Quantize.UnpackYaw(later.Yaw), t),
                    in later);
                return InterpolationResult.Interpolated;
            }

            if (!hasEarlier)
            {
                // Only samples after the render tick: the actor has just appeared, or the render
                // tick is older than the whole buffer. Its first known pose, never a projection
                // backwards from it.
                sample = new ActorSample(PositionOf(in later), Quantize.UnpackYaw(later.Yaw), in later);
                return renderTick <= At(oldestIndex).ServerTick
                    ? InterpolationResult.TooOld
                    : InterpolationResult.Held;
            }

            // Past the actor's newest sample.
            float earlierYaw = Quantize.UnpackYaw(earlier.Yaw);
            if (renderTick >= Newest().ServerTick)
            {
                StalledCount++;
                sample = new ActorSample(PositionOf(in earlier), earlierYaw, in earlier);
                return InterpolationResult.Stalled;
            }

            if (!hasPrior)
            {
                sample = new ActorSample(PositionOf(in earlier), earlierYaw, in earlier);
                return InterpolationResult.Held;
            }

            // The actor's own spacing bounds the projection as well as the constant: a Mid actor
            // three ticks apart has no business being carried eight.
            double spacing = earlierTick - (double)priorTick;
            double limit = Math.Min(spacing, MaxExtrapolationTicks);
            double gap = renderTick - earlierTick;
            bool capped = gap > limit;
            if (capped) gap = limit;

            Vec3 from = PositionOf(in prior);
            Vec3 to = PositionOf(in earlier);
            float ahead = spacing <= 0.0 ? 0f : (float)(gap / spacing);

            sample = new ActorSample(
                new Vec3(
                    to.X + (to.X - from.X) * ahead,
                    to.Y + (to.Y - from.Y) * ahead,
                    to.Z + (to.Z - from.Z) * ahead),
                earlierYaw,
                in earlier);

            if (capped) return InterpolationResult.Held;

            ExtrapolatedCount++;
            return InterpolationResult.Extrapolated;
        }

        /// <summary>One entry's dequantized position.</summary>
        private static Vec3 PositionOf(in ActorSnapshotEntry entry)
            => new Vec3(
                Quantize.UnpackPos(entry.PosX),
                Quantize.UnpackPos(entry.PosY),
                Quantize.UnpackPos(entry.PosZ));

        private static Vec3 Lerp(in Vec3 a, in Vec3 b, float t)
            => new Vec3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t);

        /// <summary>
        /// Interpolates a yaw in degrees, taking the short way round.
        /// </summary>
        /// <remarks>
        /// A plain lerp from 350 to 10 spins the actor 340 degrees the wrong way over one tick.
        /// The wrap is not an edge case — it is any actor facing roughly north.
        /// </remarks>
        private static float LerpYaw(float from, float to, float t)
        {
            float delta = to - from;
            while (delta > 180f) delta -= 360f;
            while (delta < -180f) delta += 360f;

            float result = (from + delta * t) % 360f;
            return result < 0f ? result + 360f : result;
        }

        private WorldSnapshot Newest() => At(_count - 1);

        private WorldSnapshot At(long absoluteIndex) => _ring[(int)(absoluteIndex % Capacity)];
    }
}
