using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// Which bots have reserved which vehicle seats, by identity rather than by count. V4-D10.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What this replaces was an <c>int</c> that could not be right.</b>
    /// <c>Vehicle.seatsClaimedByBots</c> is incremented by <c>ClaimSeat()</c>, decremented by
    /// <c>DropSeatClaim()</c> and drained by a 10-second whole-vehicle timer. Two bots claim and
    /// one dies: nothing decrements, so the count stays at 2 until a timer that knows nothing
    /// about either of them fires and takes one off — possibly the wrong one, since it names
    /// nobody. The vehicle then reports itself full to the AI while a seat sits empty, and no
    /// client could reconcile it because there is nothing to reconcile against.
    /// </para>
    /// <para>
    /// <b>Per-claim expiry, not per-vehicle.</b> That is the change that makes it reconcilable:
    /// a claim is a (bot, vehicle, seat, deadline) tuple, so releasing one leaves the others
    /// exactly as they were. A whole-vehicle drain can only ever be approximately right.
    /// </para>
    /// <para>
    /// <b>Not replicated</b> (V4-D10). This is server-side AI bookkeeping; no client consumes it
    /// and putting it on the wire would be bandwidth for a number nothing renders.
    /// </para>
    /// <para>
    /// <b>Arrays indexed by id, no allocation after construction.</b> One <c>ushort</c> claimant
    /// and one <c>float</c> deadline per (vehicle, seat) slot — 16 x 8 of each.
    /// </para>
    /// </remarks>
    public sealed class BotSeatClaims
    {
        /// <summary>
        /// Seconds a claim survives without being renewed.
        /// </summary>
        /// <remarks>
        /// 10 s, the shipped <c>drainClaimAction</c> period, kept so AI behaviour does not shift
        /// as a side effect of fixing the bookkeeping. What changed is what the timeout applies
        /// to: one claim rather than one arbitrary claim off an anonymous pile.
        /// </remarks>
        public const float DefaultExpirySeconds = 10f;

        private readonly ushort[] _claimant;      // (vehicle, seat) -> bot actor id, 0 = free
        private readonly float[] _expiresAt;      // (vehicle, seat) -> seconds
        private readonly int _capacity;
        private readonly float _expirySeconds;

        public BotSeatClaims(
            int capacity = ProtocolConstants.MAX_VEHICLES,
            float expirySeconds = DefaultExpirySeconds)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

            _capacity      = capacity;
            _expirySeconds = expirySeconds;
            _claimant      = new ushort[(capacity + 1) * VehicleState.MaxSeats];
            _expiresAt     = new float[(capacity + 1) * VehicleState.MaxSeats];
        }

        /// <summary>Claims live right now, across every vehicle.</summary>
        public int TotalClaimCount
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _claimant.Length; i++)
                    if (_claimant[i] != 0) total++;

                return total;
            }
        }

        /// <summary>
        /// Reserves a seat for a bot.
        /// </summary>
        /// <returns>
        /// False when the seat is already claimed by a different bot, or the ids are out of
        /// range. A bot re-claiming its own seat succeeds and renews the deadline — which is how
        /// a bot on a long walk to a vehicle keeps its reservation without a second mechanism.
        /// </returns>
        public bool TryClaim(ushort vehicleId, byte seatIndex, ushort botActorId, float nowSeconds)
        {
            if (botActorId == 0 || !TrySlot(vehicleId, seatIndex, out int slot)) return false;

            ushort held = _claimant[slot];
            if (held != 0 && held != botActorId && _expiresAt[slot] > nowSeconds) return false;

            _claimant[slot]  = botActorId;
            _expiresAt[slot] = nowSeconds + _expirySeconds;
            return true;
        }

        /// <summary>
        /// Releases every claim held by one bot.
        /// </summary>
        /// <remarks>
        /// Called from <c>ServerActorRegistry.ActorUnregistered</c>, which already fires — so a
        /// bot that dies gives its seat back on the tick it dies rather than up to ten seconds
        /// later, which is the entire bug this class exists to remove.
        /// </remarks>
        /// <returns>How many claims were dropped.</returns>
        public int Release(ushort botActorId)
        {
            if (botActorId == 0) return 0;

            int released = 0;
            for (int i = 0; i < _claimant.Length; i++)
            {
                if (_claimant[i] != botActorId) continue;
                _claimant[i]  = 0;
                _expiresAt[i] = 0f;
                released++;
            }

            return released;
        }

        /// <summary>Releases one specific claim, when a bot gives up on a seat.</summary>
        public bool Release(ushort vehicleId, byte seatIndex, ushort botActorId)
        {
            if (!TrySlot(vehicleId, seatIndex, out int slot)) return false;
            if (_claimant[slot] != botActorId) return false;

            _claimant[slot]  = 0;
            _expiresAt[slot] = 0f;
            return true;
        }

        /// <summary>Drops every claim whose deadline has passed.</summary>
        /// <returns>How many expired.</returns>
        public int ReleaseExpired(float nowSeconds)
        {
            int released = 0;
            for (int i = 0; i < _claimant.Length; i++)
            {
                if (_claimant[i] == 0 || _expiresAt[i] > nowSeconds) continue;
                _claimant[i]  = 0;
                _expiresAt[i] = 0f;
                released++;
            }

            return released;
        }

        /// <summary>Drops every claim on one vehicle. For a vehicle that died.</summary>
        public void ReleaseVehicle(ushort vehicleId)
        {
            if (vehicleId == 0 || vehicleId > _capacity) return;

            int start = Slot(vehicleId, 0);
            for (int s = 0; s < VehicleState.MaxSeats; s++)
            {
                _claimant[start + s]  = 0;
                _expiresAt[start + s] = 0f;
            }
        }

        /// <summary>Which bot holds a seat, or 0.</summary>
        public ushort ClaimantOf(ushort vehicleId, byte seatIndex)
            => TrySlot(vehicleId, seatIndex, out int slot) ? _claimant[slot] : (ushort)0;

        /// <summary>
        /// Live claims on one vehicle. <b>This is what <c>Vehicle.seatsClaimedByBots</c>
        /// becomes</b> — computed, never stored (<c>code-conventions.md</c> § "No Derived
        /// Fields").
        /// </summary>
        public int ClaimCount(ushort vehicleId)
        {
            if (vehicleId == 0 || vehicleId > _capacity) return 0;

            int start = Slot(vehicleId, 0);
            int count = 0;
            for (int s = 0; s < VehicleState.MaxSeats; s++)
                if (_claimant[start + s] != 0) count++;

            return count;
        }

        /// <summary>Whether any of <paramref name="seatCount"/> seats is unclaimed.</summary>
        public bool HasUnclaimedSeats(ushort vehicleId, int seatCount)
            => ClaimCount(vehicleId) < seatCount;

        /// <summary>Forgets every claim. For a round boundary.</summary>
        public void Clear()
        {
            Array.Clear(_claimant, 0, _claimant.Length);
            Array.Clear(_expiresAt, 0, _expiresAt.Length);
        }

        private bool TrySlot(ushort vehicleId, byte seatIndex, out int slot)
        {
            if (vehicleId == 0 || vehicleId > _capacity || seatIndex >= VehicleState.MaxSeats)
            {
                slot = 0;
                return false;
            }

            slot = Slot(vehicleId, seatIndex);
            return true;
        }

        private static int Slot(ushort vehicleId, byte seatIndex)
            => vehicleId * VehicleState.MaxSeats + seatIndex;
    }
}
