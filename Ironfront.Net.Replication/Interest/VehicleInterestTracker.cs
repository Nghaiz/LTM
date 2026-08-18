using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;

namespace Ironfront.Net.Replication.Interest
{
    /// <summary>
    /// Decides which vehicles go into which client's vehicle snapshot, and how often. V4-D3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A separate pair table from <see cref="InterestManager"/>, and that is the entire
    /// reason this class exists.</b> The actor tracker keys its rate table on
    /// <c>(viewer &lt;&lt; 16) | target</c>, a <c>uint</c>. Vehicle ids and actor ids are
    /// separate <c>u16</c> spaces, so vehicle 7 and actor 7 produce the <b>same key</b>: sharing
    /// one dictionary would have a Far vehicle consume the rate slot of whichever Far actor
    /// happened to share its number, and each would starve the other to half its band rate.
    /// Nothing would error. The pair test in <c>VehicleInterestTests</c> is what keeps a later
    /// "simplification" that reunifies the two from landing green.
    /// </para>
    /// <para>
    /// <b>The bands and the rate policy are NOT duplicated.</b> Radii come from
    /// <see cref="InterestManager"/>'s constants and the send periods from its
    /// <c>SnapshotsBetweenSends</c>, so "Near is 60 m at 20 Hz" is written once. What is
    /// duplicated is the <i>table</i>, not the <i>policy</i>.
    /// </para>
    /// <para>
    /// <b><see cref="IsDue"/> is split out of <see cref="ShouldSend"/> for the same
    /// anti-starvation reason the actor path learned</b> (phase-05 D6). A vehicle that loses the
    /// byte race must not also lose its rate slot: recording a send that never happened makes a
    /// Far vehicle wait a further five snapshots — a quarter of a second — every time, and the
    /// vehicles most likely to lose the race are the same ones every snapshot.
    /// </para>
    /// <para>
    /// <b><see cref="Forget"/> on despawn, or this leaks.</b> It is the identical trap-2 leak
    /// <see cref="InterestManager"/> documents, one dictionary over: 16 viewers x every vehicle
    /// id ever issued, growing for the life of the process.
    /// </para>
    /// </remarks>
    public sealed class VehicleInterestTracker
    {
        /// <summary>
        /// The widest one vehicle can encode to: every field of a 30-byte entry.
        /// </summary>
        /// <remarks>
        /// Pessimistic by design, exactly as <see cref="InterestManager.MaxEntrySize"/> is. The
        /// real width depends on a change mask computed later against a baseline this class has
        /// never seen; projecting optimistically and being wrong means the encode overruns and
        /// the whole snapshot is discarded, which is the failure shedding exists to remove.
        /// </remarks>
        public static readonly int MaxEntrySize = VehicleSnapshotMessage.FullEntrySize;

        private readonly Dictionary<uint, uint> _lastSentSnapshot;

        // Per-level candidate buckets, rebuilt per BuildView call and reused.
        private readonly int[] _nearBucket = new int[ProtocolConstants.MAX_VEHICLES];
        private readonly int[] _midBucket  = new int[ProtocolConstants.MAX_VEHICLES];
        private readonly int[] _farBucket  = new int[ProtocolConstants.MAX_VEHICLES];
        private int _nearCount;
        private int _midCount;
        private int _farCount;

        public VehicleInterestTracker()
        {
            _lastSentSnapshot = new Dictionary<uint, uint>(
                ProtocolConstants.MAX_PLAYERS * ProtocolConstants.MAX_VEHICLES);
        }

        /// <summary>(viewer, vehicle) pairs currently remembered. Zero after every despawn.</summary>
        public int TrackedPairCount => _lastSentSnapshot.Count;

        /// <summary>Vehicle slots classified, across every viewer.</summary>
        public long EntriesConsidered { get; private set; }

        /// <summary>Vehicle entries actually written into a view.</summary>
        public long EntriesRefreshed { get; private set; }

        /// <summary>Entries omitted because the pair was not due yet.</summary>
        public long EntriesHeld { get; private set; }

        /// <summary>Entries dropped past the cull radius, outside the view cone.</summary>
        public long EntriesCulled { get; private set; }

        /// <summary>
        /// Entries dropped for lack of byte budget.
        /// </summary>
        /// <remarks>
        /// <b>Non-zero at the shipped load is a FAILURE, not a statistic</b> (design § 8
        /// criterion 9): 16 players, 32 bots and 12 vehicles must shed nothing. The counter
        /// exists so that is gradeable rather than assumed, the same way
        /// <see cref="InterestManager.EntriesShed"/> is.
        /// </remarks>
        public long EntriesShed { get; private set; }

        /// <summary>What the most recent <see cref="BuildView"/> shed. Resets per call.</summary>
        public int LastViewShedCount { get; private set; }

        /// <summary>
        /// Whether this (viewer, vehicle) pair is due on <paramref name="snapshotIndex"/>,
        /// <b>without</b> recording the send.
        /// </summary>
        public bool IsDue(
            ushort viewerActorId, ushort vehicleId, InterestLevel level, uint snapshotIndex)
        {
            if (level == InterestLevel.Culled) return false;

            int everyN = InterestManager.SnapshotsBetweenSends(level);
            uint key = PackPair(viewerActorId, vehicleId);

            if (!_lastSentSnapshot.TryGetValue(key, out uint last)) return true;

            // Signed distance, for InterestManager.IsDue's reason: an unsigned subtraction turns
            // "the recorded index is somehow ahead of us" into a two-billion gap that passes
            // every threshold, pinning the pair to every snapshot.
            return SequenceMath.Distance32(snapshotIndex, last) >= everyN;
        }

        /// <summary>Marks this pair as sent on <paramref name="snapshotIndex"/>.</summary>
        public void RecordSend(ushort viewerActorId, ushort vehicleId, uint snapshotIndex)
            => _lastSentSnapshot[PackPair(viewerActorId, vehicleId)] = snapshotIndex;

        /// <summary>
        /// <see cref="IsDue"/> and <see cref="RecordSend"/> in one call, for callers that are
        /// not budget-limited.
        /// </summary>
        public bool ShouldSend(
            ushort viewerActorId, ushort vehicleId, InterestLevel level, uint snapshotIndex)
        {
            if (!IsDue(viewerActorId, vehicleId, level, snapshotIndex)) return false;

            RecordSend(viewerActorId, vehicleId, snapshotIndex);
            return true;
        }

        /// <summary>
        /// Fills <paramref name="destination"/> with the vehicles this viewer should receive,
        /// shedding rather than overflowing <paramref name="byteBudget"/>.
        /// </summary>
        /// <param name="viewer">
        /// The viewing actor, as an <see cref="InterestSubject"/> built from its own snapshot
        /// entry. Always an actor: a vehicle is never a viewer (V4-D5), which is what makes the
        /// view cone reachable with a real facing.
        /// </param>
        /// <param name="shedCursor">
        /// Where in each bucket admission starts, so the vehicles that lost the last byte race
        /// are at the front of the next one. Per client, and <b>separate from the actor
        /// cursor</b> — one shared cursor would rotate the vehicle view because the actor view
        /// shed, which couples two admission orders that have nothing to do with each other.
        /// </param>
        /// <returns>The cursor value to store back on the session.</returns>
        public int BuildView(
            in InterestSubject viewer,
            VehicleWorldSnapshot world,
            uint snapshotIndex,
            VehicleWorldSnapshot destination,
            int byteBudget,
            int shedCursor = 0)
        {
            if (world == null) throw new ArgumentNullException(nameof(world));
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (viewer.Space != InterestSpace.Actor)
                throw new ArgumentException(
                    "A vehicle is never a viewer (V4-D5).", nameof(viewer));

            destination.Clear();
            destination.ServerTick = world.ServerTick;
            LastViewShedCount = 0;

            // ---- Pass 1: classify and bucket by level.
            //
            // Split from the emit pass so shedding can drop the lowest band first. A single pass
            // in world order would shed whichever vehicles happened to be last in the registry,
            // regardless of how close they are — and the registry's live list is unordered
            // because removal swaps with the last entry.
            _nearCount = 0;
            _midCount  = 0;
            _farCount  = 0;

            for (int i = 0; i < world.VehicleCount; i++)
            {
                ref VehicleSnapshotEntry target = ref world.Vehicles[i];
                EntriesConsidered++;

                InterestLevel level = Classify(in viewer, in target);

                if (level == InterestLevel.Culled)
                {
                    EntriesCulled++;
                    continue;
                }

                switch (level)
                {
                    case InterestLevel.Near: _nearBucket[_nearCount++] = i; break;
                    case InterestLevel.Mid:  _midBucket[_midCount++]   = i; break;
                    default:                 _farBucket[_farCount++]   = i; break;
                }
            }

            // ---- Pass 2: emit highest interest first, until the budget runs out.
            int remaining = byteBudget > 0
                ? byteBudget - VehicleSnapshotHeader.Size
                : int.MaxValue;

            int admitted = EmitBucket(
                viewer.Id, world, _nearBucket, _nearCount, InterestLevel.Near,
                snapshotIndex, destination, shedCursor, ref remaining);

            admitted += EmitBucket(
                viewer.Id, world, _midBucket, _midCount, InterestLevel.Mid,
                snapshotIndex, destination, shedCursor, ref remaining);

            admitted += EmitBucket(
                viewer.Id, world, _farBucket, _farCount, InterestLevel.Far,
                snapshotIndex, destination, shedCursor, ref remaining);

            // Advances only when something was actually shed, and by however many got through —
            // which slides the admission window forward so the losers lead the next snapshot.
            // Advancing unconditionally would re-order a view that fits comfortably, for no gain
            // and at the cost of a change mask on every entry.
            return LastViewShedCount > 0
                ? shedCursor + (admitted > 0 ? admitted : 1)
                : shedCursor;
        }

        /// <summary>
        /// Classifies one vehicle for one viewer. Public so a caller that is not building a full
        /// view — a diagnostic, or a test — asks the same question the same way.
        /// </summary>
        public InterestLevel Classify(in InterestSubject viewer, in VehicleSnapshotEntry target)
        {
            InterestSubject subject = InterestSubject.From(in target);

            // The classifier itself, radii and all, is InterestManager's. This class owns WHEN a
            // vehicle is sent, never WHERE the band edges are — a second copy of 60 / 150 / 500
            // is how the two silently stop agreeing about what "Mid" means.
            Vec3 viewerPos = InterestManager.UnpackPosition(in viewer);
            Vec3 targetPos = InterestManager.UnpackPosition(in subject);
            float d2 = (targetPos - viewerPos).SqrMagnitude;

            if (d2 < InterestManager.NearRadius * InterestManager.NearRadius)
                return InterestLevel.Near;

            if (d2 < InterestManager.MidRadius * InterestManager.MidRadius)
                return InterestLevel.Mid;

            if (d2 < InterestManager.CullRadius * InterestManager.CullRadius)
                return InterestLevel.Far;

            // Past the cull radius the view cone is the only thing that rescues a target, and a
            // tank at 600 m down a scope is exactly the case it was sized for. There is no
            // teammate floor here: a vehicle has no team as far as interest is concerned
            // (V4-D5).
            return InterestManager.IsInViewCone(in viewer, in viewerPos, in targetPos)
                ? InterestLevel.Far
                : InterestLevel.Culled;
        }

        /// <summary>
        /// Drops every pair naming this vehicle. <b>Call this on despawn</b> — see the type
        /// remarks for what happens otherwise.
        /// </summary>
        public void Forget(ushort vehicleId)
        {
            // Materialised into a list first: removing from a Dictionary while enumerating it is
            // undefined. Runs once per despawn, not per tick.
            List<uint>? doomed = null;

            foreach (KeyValuePair<uint, uint> pair in _lastSentSnapshot)
            {
                if ((ushort)(pair.Key & 0xFFFF) != vehicleId) continue;
                doomed ??= new List<uint>();
                doomed.Add(pair.Key);
            }

            if (doomed == null) return;
            for (int i = 0; i < doomed.Count; i++) _lastSentSnapshot.Remove(doomed[i]);
        }

        /// <summary>
        /// Drops every pair naming this viewer. For a disconnect, where the vehicles survive but
        /// the client does not.
        /// </summary>
        public void ForgetViewer(ushort viewerActorId)
        {
            List<uint>? doomed = null;

            foreach (KeyValuePair<uint, uint> pair in _lastSentSnapshot)
            {
                if ((ushort)(pair.Key >> 16) != viewerActorId) continue;
                doomed ??= new List<uint>();
                doomed.Add(pair.Key);
            }

            if (doomed == null) return;
            for (int i = 0; i < doomed.Count; i++) _lastSentSnapshot.Remove(doomed[i]);
        }

        /// <summary>Forgets every pair and every accumulated statistic.</summary>
        public void Reset()
        {
            _lastSentSnapshot.Clear();
            EntriesConsidered = 0;
            EntriesRefreshed  = 0;
            EntriesHeld       = 0;
            EntriesCulled     = 0;
            EntriesShed       = 0;
            LastViewShedCount = 0;
        }

        private int EmitBucket(
            ushort viewerActorId, VehicleWorldSnapshot world, int[] bucket, int count,
            InterestLevel level, uint snapshotIndex, VehicleWorldSnapshot destination,
            int cursor, ref int remaining)
        {
            if (count == 0) return 0;

            int start = ((cursor % count) + count) % count;   // negative-safe
            int admitted = 0;

            for (int k = 0; k < count; k++)
            {
                int index = bucket[(start + k) % count];

                // Budget BEFORE due-ness, and the ordering is the anti-starvation property.
                // Asking IsDue first and then shedding would consume the rate slot of a vehicle
                // that was never sent. Every entry costs the same worst case, so once one does
                // not fit none will — the rest of this bucket and every lower one is shed.
                if (remaining < MaxEntrySize)
                {
                    LastViewShedCount += count - k;
                    EntriesShed += count - k;
                    return admitted;
                }

                ref VehicleSnapshotEntry target = ref world.Vehicles[index];

                // A not-due entry is SKIPPED, not stopped at, and it consumes no budget. That is
                // load-bearing and is easy to "optimise" away.
                //
                // A review flagged the shared cursor as starving a bucket: the cursor advances by
                // the TOTAL admitted across all three buckets but is applied modulo each bucket's
                // own length, so with 6 Near and 4 Mid it returns 8 every round, 8 % 4 == 0, and
                // Mid restarts at the same two entries forever. The arithmetic is right and the
                // conclusion is wrong, because of this line: those two entries are not due on the
                // next snapshot (Mid is every 2nd), so the scan walks past them and reaches the
                // ones behind. Rate limiting rotates the window for free.
                //
                // Near is the one band with period 1, where everything is always due — and Near
                // cannot starve either, because it is admitted first, so if it sheds then no
                // lower bucket gets any budget and the total advance IS Near's own admitted
                // count. Break here instead of continuing and both arguments collapse.
                if (!IsDue(viewerActorId, target.VehicleId, level, snapshotIndex))
                {
                    EntriesHeld++;
                    continue;
                }

                // A copy, because one viewer's decisions must not be written back into the
                // shared world buffer. The actor path learned this when a client standing next
                // to an actor lost its velocity because somebody else was far from it.
                if (!destination.Add(in target))
                {
                    // Unreachable while the destination is sized to MAX_VEHICLES like the world
                    // is — but counted rather than returned silently, because a shed that does
                    // not reach EntriesShed makes criterion 9 pass by not looking. A "0 shed"
                    // that is really "0 shed we bothered to count" is the exact shape of green
                    // that proves nothing.
                    LastViewShedCount += count - k;
                    EntriesShed += count - k;
                    return admitted;
                }

                RecordSend(viewerActorId, target.VehicleId, snapshotIndex);
                remaining -= MaxEntrySize;
                EntriesRefreshed++;
                admitted++;
            }

            return admitted;
        }

        private static uint PackPair(ushort viewerActorId, ushort vehicleId)
            => ((uint)viewerActorId << 16) | vehicleId;
    }
}
