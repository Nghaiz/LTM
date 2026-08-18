using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// The one place seat occupancy is decided. Pure state machine, no engine types. V4-D6
    /// through V4-D9.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two clients reaching for the same driver seat is the highest-scored risk in this
    /// phase</b> (15 = likelihood 3 x impact 5), and the mitigation is structural rather than
    /// careful: every seat mutation goes through <see cref="Decide"/>, <see cref="Decide"/> runs
    /// inside the tick, and the grant is booked into the registry BEFORE the method returns. Two
    /// clients cannot both be granted a seat because the first grant is visible to the second
    /// decision — there is no window between them to lose.
    /// </para>
    /// <para>
    /// <b>The ordering is the CALLER's, and V4-D9's ascending-connection-id tie-break is not
    /// implemented here.</b> Saying otherwise would be a guarantee this class cannot make: it
    /// answers one request per call and never sees a batch, so "arrival order" is simply the
    /// order <c>ServerSeatBridge</c> invokes it in — which today is the order
    /// <c>ServerMessageRouter</c> walks one payload's messages, and is therefore always defined.
    /// The tie-break exists for the case where several requests are drained from a queue with no
    /// inherent order, and nothing builds such a queue. If one is ever added, the sort belongs at
    /// that queue and this remark should stop being true.
    /// </para>
    /// <para>
    /// <b>Never from a coroutine</b> (V4-D9). The shipped
    /// <c>Actor.ReactivateCollisionsWith</c> held hitbox layer state across a half-second
    /// wall-clock wait and then re-sampled whether the actor happened to be seated — which any
    /// seat change arriving inside that window silently decided. V0 replaced it with a
    /// tick-counted timer; V4 consumes that and mutates seat state only inside
    /// <c>RunInputStage</c>.
    /// </para>
    /// <para>
    /// <b>Switching seats is two requests, not one</b> (V4-D8). <c>Actor.SwitchSeat</c> is a
    /// <c>LeaveSeat()</c> + <c>EnterSeat()</c> pair inside one frame that <b>bypasses
    /// <c>CanEnterSeat()</c></b>, so the 1-second re-entry lockout is enforced on the use-ray
    /// path and not on that one. Routing the network path through two independently arbitrated
    /// requests buys the lockout and the capacity check back, at the cost of one extra round
    /// trip on a rare action.
    /// </para>
    /// <para>
    /// <b>No allocation and no LINQ on the hot path</b> — what conventions § 3.2 actually says.
    /// The lockout table is an array indexed by actor id.
    /// </para>
    /// <para>
    /// <b>§ 3.2 does NOT ban <c>foreach</c>, and it bans LINQ IN THE HOT PATH rather than the
    /// <c>System.Linq</c> namespace.</b> Said here because this file's own comment claimed
    /// otherwise, and so does the phase plan's header — a citation that overstates its source is
    /// worse than none, because the next reader audits against a rule that does not exist and
    /// either wastes the pass or "fixes" conforming code. A <c>foreach</c> over a concrete
    /// <c>Dictionary</c> or array binds a struct enumerator by pattern and boxes nothing; the
    /// thing genuinely worth avoiding is iterating through an <c>IEnumerable&lt;T&gt;</c>
    /// interface, which does box.
    /// </para>
    /// </remarks>
    public sealed class SeatArbiter
    {
        /// <summary>
        /// Ticks an actor must wait after leaving a seat before it may enter one again.
        /// </summary>
        /// <remarks>
        /// 30 ticks = 1 s at <see cref="ProtocolConstants.SIM_TICK_RATE"/>, matching the shipped
        /// <c>Actor.cannotEnterVehicleAction = new Action(1f)</c>. Expressed in ticks rather than
        /// seconds so it fires on the same tick for every peer and so a test advances it by hand
        /// rather than by sleeping — the same reason <see cref="TickTimer"/> exists.
        /// </remarks>
        public const int ReentryLockoutTicks = ProtocolConstants.SIM_TICK_RATE;

        /// <summary>
        /// How far an actor may be from a seat and still take it, in metres.
        /// </summary>
        /// <remarks>
        /// The shipped game gates entry on a use-ray, which has no single distance constant to
        /// borrow — so this is the protocol's own limit, deliberately generous. It exists to
        /// refuse a client that asks to board a vehicle across the map, not to reproduce the
        /// ray. A generous limit that is enforced beats an exact one that is not.
        /// </remarks>
        public const float MaxSeatReachMetres = 6f;

        private readonly uint[] _lockedUntilTick;
        private readonly VehicleRegistry _registry;

        public SeatArbiter(VehicleRegistry registry, int maxActors = ProtocolConstants.MAX_ACTORS)
        {
            _registry        = registry ?? throw new ArgumentNullException(nameof(registry));
            _lockedUntilTick = new uint[maxActors + 1];   // 1-based actor ids
        }

        /// <summary>Requests answered, accepted or not.</summary>
        public long RequestsDecided { get; private set; }

        /// <summary>Requests granted.</summary>
        public long RequestsAccepted { get; private set; }

        /// <summary>
        /// Requests refused because the lockout had not elapsed. Non-zero is normal — it is a
        /// player double-tapping the use key on the way out of a vehicle.
        /// </summary>
        public long RequestsLockedOut { get; private set; }

        /// <summary>
        /// Answers one request against the world as it stands right now.
        /// </summary>
        /// <remarks>
        /// <b>It decides and records; it does not apply.</b> Occupancy is written into the
        /// registry here, because the next request in the same tick must see it — that is what
        /// makes the race deterministic. Moving the actual <c>Actor</c> is
        /// <c>ServerSeatBridge</c>'s, and if Unity then refuses (a condition this class could
        /// not see), the bridge calls <see cref="Rollback"/> rather than leaving the arbiter's
        /// record and the scene disagreeing.
        /// </remarks>
        public SeatDecision Decide(in SeatRequest request, uint nowTick)
        {
            RequestsDecided++;

            // An actor id past the pool's ceiling cannot be one this server issued, so it names
            // nothing and is refused rather than indexed with. A hostile client picking 60000
            // must not reach past the lockout array.
            if (request.ActorId == 0 || request.ActorId >= _lockedUntilTick.Length)
                return SeatDecision.Refuse(in request, SeatChangeResult.RejectedNoSuchSeat);

            SeatDecision decision = request.Action == SeatAction.Leave
                ? DecideLeave(in request, nowTick)
                : DecideEnter(in request, nowTick);

            if (decision.Accepted) RequestsAccepted++;
            if (decision.Result == SeatChangeResult.RejectedLockedOut) RequestsLockedOut++;

            return decision;
        }

        /// <summary>
        /// Undoes an accepted decision the engine then refused.
        /// </summary>
        /// <remarks>
        /// <c>Actor.EnterSeat</c> returns <c>false</c> for conditions the arbiter cannot check —
        /// it re-reads <c>seat.vehicle.dead</c> and <c>seat.IsOccupied()</c> against the live
        /// scene, and an offline or AI path may have seated somebody there between ticks. V4-D7
        /// says that <c>false</c> becomes a refusal rather than a silent divergence; this is
        /// what un-books the seat so the next request sees the truth.
        /// </remarks>
        public void Rollback(in SeatDecision decision)
        {
            if (!decision.Accepted) return;

            if (decision.Result == SeatChangeResult.Entered)
            {
                _registry.TrySetOccupant(decision.VehicleId, decision.SeatIndex, 0);
                return;
            }

            // A Left that the engine refused: put the actor back where it was. The lockout it
            // started is deliberately NOT rewound — it is cheap, it is self-clearing, and
            // rewinding it would need a second stored value whose only job is to be restored.
            _registry.TrySetOccupant(decision.VehicleId, decision.SeatIndex, decision.ActorId);
        }

        /// <summary>Forgets every lockout. For a round boundary.</summary>
        public void Reset()
        {
            Array.Clear(_lockedUntilTick, 0, _lockedUntilTick.Length);
            RequestsDecided   = 0;
            RequestsAccepted  = 0;
            RequestsLockedOut = 0;
        }

        /// <summary>
        /// Clears one actor's lockout, for an actor that has been despawned and whose id may be
        /// reissued.
        /// </summary>
        public void Forget(ushort actorId)
        {
            if (actorId < _lockedUntilTick.Length) _lockedUntilTick[actorId] = 0;
        }

        /// <summary>The tick this actor may next enter a seat. 0 when it is not locked out.</summary>
        public uint LockedUntilTick(ushort actorId)
            => actorId < _lockedUntilTick.Length ? _lockedUntilTick[actorId] : 0;

        private SeatDecision DecideEnter(in SeatRequest request, uint nowTick)
        {
            if (!_registry.TryGetState(request.VehicleId, out VehicleState vehicle))
                return SeatDecision.Refuse(in request, SeatChangeResult.RejectedNoSuchSeat);

            if (request.SeatIndex >= vehicle.SeatCount)
                return SeatDecision.Refuse(in request, SeatChangeResult.RejectedNoSuchSeat);

            // Dead before occupied: a wreck with somebody still nominally in it should report
            // the wreck, which is the thing the client has to react to.
            if (vehicle.Dead)
                return SeatDecision.Refuse(in request, SeatChangeResult.RejectedVehicleDead);

            // Checked before the lockout, because "somebody else is in it" is terminal for this
            // request and "wait a moment" is not — telling a player to retry a seat that is
            // taken sends them back for a second refusal.
            ushort occupant = _registry.OccupantOf(request.VehicleId, request.SeatIndex);
            if (occupant != 0 && occupant != request.ActorId)
                return SeatDecision.Refuse(in request, SeatChangeResult.RejectedOccupied);

            if (_registry.TryFindSeatOf(request.ActorId, out ushort seatedIn, out byte seatedAt))
            {
                // Already exactly where it asked to be: idempotent, and answered as a success so
                // a client whose S_SEAT_CHANGE was lost converges instead of being told it is
                // somewhere it is not.
                if (seatedIn == request.VehicleId && seatedAt == request.SeatIndex)
                    return Accept(in request, SeatChangeResult.Entered, changedNothing: true);

                // Seated elsewhere. V4-D8: the network path has no atomic switch, so this is
                // refused and the client sends Leave then Enter — two independently arbitrated
                // requests, which is what restores the lockout and the capacity check that
                // Actor.SwitchSeat bypasses.
                return SeatDecision.Refuse(in request, SeatChangeResult.RejectedAlreadySeated);
            }

            if (_lockedUntilTick[request.ActorId] != 0
                && SequenceMath.Distance32(nowTick, _lockedUntilTick[request.ActorId]) < 0)
                return SeatDecision.Refuse(in request, SeatChangeResult.RejectedLockedOut);

            if (request.DistanceSquaredToSeat > MaxSeatReachMetres * MaxSeatReachMetres)
                return SeatDecision.Refuse(in request, SeatChangeResult.RejectedTooFar);

            // Booked here, before the bridge has moved anything, so the next request in this
            // same tick sees the seat taken. That is the whole race mitigation: without it,
            // two requests drained in one tick both read an empty seat.
            _registry.TrySetOccupant(request.VehicleId, request.SeatIndex, request.ActorId);

            return Accept(in request, SeatChangeResult.Entered);
        }

        private SeatDecision DecideLeave(in SeatRequest request, uint nowTick)
        {
            // Answered from where the actor actually is, not from what the request named. A
            // client that asks to leave a vehicle it is not in is describing a state it has
            // already diverged from, and honouring its id would empty somebody else's seat.
            if (!_registry.TryFindSeatOf(request.ActorId, out ushort vehicleId, out byte seatIndex))
                return SeatDecision.Refuse(in request, SeatChangeResult.RejectedNoSuchSeat);

            _registry.TrySetOccupant(vehicleId, seatIndex, 0);

            // The lockout starts on the LEAVE, matching Actor.LeaveSeat's
            // cannotEnterVehicleAction.Start(). Starting it on the next enter attempt instead
            // would let an instant re-entry through and only lock the one after it.
            _lockedUntilTick[request.ActorId] = nowTick + ReentryLockoutTicks;

            return new SeatDecision(
                SeatChangeResult.Left, request.ConnectionId, request.ActorId,
                vehicleId, seatIndex);
        }

        private static SeatDecision Accept(
            in SeatRequest request, SeatChangeResult result, bool changedNothing = false)
            => new SeatDecision(
                result, request.ConnectionId, request.ActorId,
                request.VehicleId, request.SeatIndex, changedNothing);
    }
}
