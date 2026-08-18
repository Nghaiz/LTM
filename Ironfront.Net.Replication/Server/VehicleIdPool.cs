using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Hands out vehicle ids and holds released ones in quarantine before reissuing them.
    /// Phase-V8 task 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is <see cref="ActorIdPool"/>'s argument applied to vehicles</b>, and protocol v3
    /// already conceded it: <see cref="ProtocolConstants.VEHICLE_ID_QUARANTINE_TICKS"/> was
    /// declared at the freeze with nothing consuming it. Vehicle 7 burns out and its spawner
    /// replaces it five seconds later; snapshot entries and a despawn naming the wreck are still
    /// in flight, and unreliable-sequenced delivery means they can land after the replacement's
    /// spawn. The client then applies a wreck's health, flags and turret angle to a vehicle that
    /// is fine — silently, and only on the id that happened to be recycled fastest.
    /// </para>
    /// <para>
    /// <b>Ticks, not seconds.</b> The protocol states the quarantine in ticks, and the server
    /// already has a tick counter that a test can advance by hand. Converting to seconds here
    /// would introduce a second unit for one value and a rounding question with no right answer.
    /// </para>
    /// <para>
    /// <b>Why <see cref="ProtocolConstants.MAX_VEHICLES"/> is the capacity and not the u16
    /// space.</b> Sixteen is the number the vehicle snapshot body is sized against — a
    /// seventeenth live vehicle has nowhere on the wire to go, so handing out a seventeenth id
    /// would only move the failure somewhere harder to see. The shipped maps author fourteen
    /// spawners each, so the ceiling has two spare and the quarantine (150 ticks = 5 s) clears
    /// long before a spawner's 16 s respawn needs an id back.
    /// </para>
    /// <para>
    /// <b>Exhaustion returns false rather than throwing or wrapping.</b> A monotonic counter was
    /// the phase plan's fallback and it is wrong for the process this phase exists to keep
    /// alive: at fourteen spawners replacing a vehicle every 16 s, a <c>ushort</c> wraps in
    /// about ten hours, and a dedicated server runs for days. Wrapping reissues a live id with
    /// no quarantine at all, which is the bug above with the safety removed.
    /// </para>
    /// </remarks>
    public sealed class VehicleIdPool
    {
        private readonly ushort _capacity;
        private readonly int _quarantineTicks;

        // A queue rather than a stack, for ActorIdPool's reason: ids rotate instead of the same
        // few being reused over and over, so any residual stale-packet window stays spread thin.
        private readonly Queue<ushort> _free;
        private readonly Queue<QuarantinedId> _quarantine;
        private readonly HashSet<ushort> _inUse;

        /// <summary>Ids are 1-based; 0 means "no vehicle" everywhere in the protocol.</summary>
        public const ushort FirstId = 1;

        public VehicleIdPool(
            ushort capacity = ProtocolConstants.MAX_VEHICLES,
            int quarantineTicks = ProtocolConstants.VEHICLE_ID_QUARANTINE_TICKS)
        {
            if (capacity == 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            if (quarantineTicks < 0) throw new ArgumentOutOfRangeException(nameof(quarantineTicks));

            _capacity        = capacity;
            _quarantineTicks = quarantineTicks;
            _free            = new Queue<ushort>(capacity);
            _quarantine      = new Queue<QuarantinedId>(capacity);
            _inUse           = new HashSet<ushort>();

            for (ushort id = FirstId; id < FirstId + capacity; id++) _free.Enqueue(id);
        }

        /// <summary>Ids currently held by a live vehicle.</summary>
        public int InUseCount => _inUse.Count;

        /// <summary>Ids cooling down and not yet reissuable.</summary>
        public int QuarantinedCount => _quarantine.Count;

        /// <summary>Ids available right now.</summary>
        public int FreeCount => _free.Count;

        /// <summary>
        /// Takes the next free id, releasing any whose quarantine has expired first.
        /// </summary>
        /// <param name="nowTick">The server tick, monotonically non-decreasing.</param>
        /// <returns>
        /// <c>false</c> when every id is live or cooling. The caller keeps its vehicle and does
        /// not replicate it — see <c>ServerVehicleLifecycleSink</c>, which logs that case rather
        /// than dropping it silently.
        /// </returns>
        public bool TryAcquire(uint nowTick, out ushort id)
        {
            DrainExpiredQuarantine(nowTick);

            if (_free.Count == 0)
            {
                id = 0;
                return false;
            }

            id = _free.Dequeue();
            _inUse.Add(id);
            return true;
        }

        /// <summary>
        /// Retires an id into quarantine. Releasing one that is not in use is ignored — a
        /// double despawn is a duplicate report, not a second vehicle, and quarantining the id
        /// twice would take it out of circulation for two windows.
        /// </summary>
        public void Release(ushort id, uint nowTick)
        {
            if (!_inUse.Remove(id)) return;

            _quarantine.Enqueue(new QuarantinedId(id, nowTick + (uint)_quarantineTicks));
        }

        /// <summary>
        /// Returns an id to the free list immediately, with no quarantine, for a vehicle that
        /// was never announced.
        /// </summary>
        /// <remarks>
        /// The quarantine exists to outlast packets naming the id. When the spawn message never
        /// framed there are no such packets, so cooling the id for five seconds would take one
        /// of sixteen out of circulation to protect against nothing. Distinct from
        /// <see cref="Release"/> precisely so the difference is a decision at the call site
        /// rather than a flag.
        /// </remarks>
        public void ReturnUnused(ushort id)
        {
            if (!_inUse.Remove(id)) return;

            _free.Enqueue(id);
        }

        /// <summary>
        /// Returns every id — live and cooling — to the free list. For a round boundary, where
        /// every vehicle is destroyed at once.
        /// </summary>
        /// <remarks>
        /// The quarantine is dropped rather than honoured here on purpose. A world reset
        /// despawns every vehicle and the client tears down its whole vehicle table with the
        /// match phase, so there is nothing left for a stale packet to be applied to. Holding
        /// sixteen ids for five seconds into the next round would instead leave the opening
        /// spawns unreplicated, which is the visible failure.
        /// </remarks>
        public void ReleaseAll()
        {
            _inUse.Clear();
            _quarantine.Clear();
            _free.Clear();

            for (ushort id = FirstId; id < FirstId + _capacity; id++) _free.Enqueue(id);
        }

        /// <summary>True when this id is currently held by a live vehicle.</summary>
        public bool IsInUse(ushort id) => _inUse.Contains(id);

        private void DrainExpiredQuarantine(uint nowTick)
        {
            while (_quarantine.Count > 0 && _quarantine.Peek().ReleaseTick <= nowTick)
                _free.Enqueue(_quarantine.Dequeue().Id);
        }

        private readonly struct QuarantinedId
        {
            public readonly ushort Id;
            public readonly uint ReleaseTick;

            public QuarantinedId(ushort id, uint releaseTick)
            {
                Id          = id;
                ReleaseTick = releaseTick;
            }
        }
    }
}
