using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Turns an arbitrated seat decision into an actual <c>Actor.EnterSeat</c> /
    /// <c>LeaveSeat</c>, and puts the answer on the wire. V4 task 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It applies; it does not decide.</b> <see cref="SeatArbiter"/> answers the request —
    /// including the races, the lockout and the capacity check — and does so engine-free, in
    /// CI. What is left here is moving a <c>MonoBehaviour</c>, which is the one part that cannot
    /// be tested without Unity.
    /// </para>
    /// <para>
    /// <b>It checks <c>EnterSeat</c>'s <c>bool</c>, and it is the only call site that does</b>
    /// (V4-D7). The three shipped ones discard it — those are the offline and AI paths. A
    /// <c>false</c> here means the live scene refused a seat the arbiter had already booked, so
    /// the booking is rolled back and the client is told <c>RejectedOccupied</c> rather than
    /// being left believing it is in a vehicle it never entered.
    /// </para>
    /// <para>
    /// <b>Inside the tick, never from a coroutine</b> (V4-D9). The router runs from
    /// <c>ServerTickLoop.RunInputStage</c>, so a request that arrives mid-frame is answered in
    /// arrival order against a world nothing else is mutating.
    /// </para>
    /// </remarks>
    internal sealed class ServerSeatBridge : ISeatRequestHandler
    {
        private readonly SeatArbiter _arbiter;
        private readonly ServerVehicleRegistry _vehicles;
        private readonly ServerActorRegistry _actors;
        private readonly Func<uint> _currentTick;
        private readonly Action<SeatDecision> _send;

        internal ServerSeatBridge(
            SeatArbiter arbiter,
            ServerVehicleRegistry vehicles,
            ServerActorRegistry actors,
            Func<uint> currentTick,
            Action<SeatDecision> send)
        {
            _arbiter     = arbiter ?? throw new ArgumentNullException(nameof(arbiter));
            _vehicles    = vehicles ?? throw new ArgumentNullException(nameof(vehicles));
            _actors      = actors ?? throw new ArgumentNullException(nameof(actors));
            _currentTick = currentTick ?? throw new ArgumentNullException(nameof(currentTick));
            _send        = send ?? throw new ArgumentNullException(nameof(send));
        }

        /// <summary>Decisions the live scene refused after the arbiter had accepted them.</summary>
        /// <remarks>
        /// Expected to be zero. Non-zero means the arbiter's record and the scene disagree about
        /// occupancy — an offline or AI path seating somebody between ticks — which is worth
        /// finding rather than absorbing silently.
        /// </remarks>
        internal long EngineRefusals { get; private set; }

        /// <inheritdoc />
        public void OnSeatRequested(ClientSession session, in SeatRequestMessage message)
        {
            uint now = _currentTick();

            // Measured before the request is built, so the three ways a measurement can be
            // IMPOSSIBLE are answered as themselves rather than as a distance. X-67.
            SeatChangeResult? unmeasurable = TryMeasureSeatReach(
                session.ActorId, message.VehicleId, message.SeatIndex, out float distanceSquared);

            var request = new SeatRequest(
                session.ConnectionId,
                session.ActorId,
                message.VehicleId,
                message.SeatIndex,
                message.Action,
                distanceSquared,
                clientTick: 0);

            // Only Enter is refused on it: DecideLeave answers from where the actor actually is
            // and never reads the distance, so a leave request for a vehicle the registry has
            // already dropped must still be allowed to put the player back on foot.
            if (unmeasurable.HasValue && message.Action == SeatAction.Enter)
            {
                _send(SeatDecision.Refuse(in request, unmeasurable.Value));
                return;
            }

            SeatDecision decision = _arbiter.Decide(in request, now);

            if (decision.Accepted && !Apply(in decision))
            {
                _arbiter.Rollback(in decision);
                decision = SeatDecision.Refuse(in request, SeatChangeResult.RejectedOccupied);
                EngineRefusals++;
            }

            _send(decision);
        }

        /// <summary>
        /// Moves the actor. Returns false when the live scene refused.
        /// </summary>
        private bool Apply(in SeatDecision decision)
        {
            if (!_actors.TryFind(decision.ActorId, out NetServerActor actor) || actor == null)
                return false;

            if (!_vehicles.TryFind(decision.VehicleId, out IGameplayVehicleSource vehicle))
                return false;

            return decision.Result == SeatChangeResult.Entered
                ? vehicle.TryEnterSeat(actor.gameObject, decision.SeatIndex)
                : vehicle.TryLeaveSeat(actor.gameObject);
        }

        /// <summary>
        /// How far the actor is from the seat, squared, or the reason no such distance exists.
        /// </summary>
        /// <returns>
        /// <c>null</c> when the distance is real. Otherwise the refusal that describes what was
        /// actually wrong.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>Measured on the server, from the server's own transforms.</b> Taking it from the
        /// request would let a client claim it is standing next to any vehicle on the map, which
        /// is the whole reason the arbiter has a reach check.
        /// </para>
        /// <para>
        /// <b>Three failures used to render as one, and X-67 is what that cost.</b> An
        /// unresolvable actor and an unresolvable vehicle each returned
        /// <see cref="float.MaxValue"/>, and a seat index with no <c>Seat</c> behind it returned
        /// <c>Vector3.positiveInfinity</c> from <c>GetSeatPosition</c> -- so all three arrived at
        /// the arbiter as an enormous distance and came back to the player as
        /// <see cref="SeatChangeResult.RejectedTooFar"/>, reading <i>"Too far from the seat."</i>
        /// P5 measured four such refusals with the client standing 3.70-4.27 m from the hull
        /// against a 6 m limit, and no artifact could say which of the four possible causes it
        /// was -- an unknown actor, an unknown vehicle, a missing seat, or a genuine reach
        /// failure from a seat that really is more than 6 m away.
        /// </para>
        /// <para>
        /// <see cref="SeatChangeResult.RejectedNoSuchSeat"/> already existed and its own
        /// documentation says <i>"No such vehicle, or no such seat index on it"</i>. It was never
        /// sent from here. Now it is, and a <c>RejectedTooFar</c> means what it says.
        /// </para>
        /// <para>
        /// <b>X-67's filed cause is REFUTED, and the fourth case above is the real one.</b> The
        /// row was filed as the client and the server measuring from different origins -- the
        /// client from <c>vehicle.Body.Transform.position</c>, the server from
        /// <c>vehicle.GetSeatPosition(seatIndex)</c>. Two things falsify it. That difference was
        /// removed in <c>c0923f4</c> (2026-08-31), one day AFTER the run the row cites, so it
        /// has not been testable against HEAD since before the row was last re-read; both ends
        /// now call the one <c>Vehicle.GetSeatPosition</c>. And the run's own artifacts carry
        /// the answer: <c>p5-e11-03</c>'s <c>server.log</c> logs <i>"actor 43 at
        /// (2093.31,-1024.67,1150.45) -- outside +/-3072 m"</i>, and
        /// <c>observer-b-checkpoints.jsonl</c> records <c>localActor.authoritativeY</c> at
        /// -1008.62 BEFORE the first of the four requests. The server measured ~1037 m to a
        /// seat at y = 12.9 and refused correctly. An origin mismatch cannot produce that: 4.08 m
        /// plus any hull-to-seat offset is still under 6 m, and the refusal was three orders of
        /// magnitude out.
        /// </para>
        /// <para>
        /// So the residual defect is not a mismatch but a body that is nowhere rendering as a
        /// body that is far -- fixed below by
        /// <see cref="SeatChangeResult.RejectedActorUnplaced"/>. The fall itself belongs to
        /// <b>X-75</b>, of which this run is the second recorded occurrence.
        /// </para>
        /// </remarks>
        private SeatChangeResult? TryMeasureSeatReach(
            ushort actorId, ushort vehicleId, byte seatIndex, out float distanceSquared)
        {
            distanceSquared = float.MaxValue;

            if (!_actors.TryFind(actorId, out NetServerActor actor) || actor == null)
                return SeatChangeResult.RejectedNoSuchSeat;

            if (!_vehicles.TryFind(vehicleId, out IGameplayVehicleSource vehicle))
                return SeatChangeResult.RejectedNoSuchSeat;

            Vector3 seat = vehicle.GetSeatPosition(seatIndex);

            // GetSeatPosition's own miss value. Checked rather than allowed to propagate: an
            // infinite coordinate subtracts to an infinite distance, which is a comparison the
            // arbiter answers "too far" without anything being far away.
            if (float.IsInfinity(seat.x) || float.IsInfinity(seat.y) || float.IsInfinity(seat.z))
                return SeatChangeResult.RejectedNoSuchSeat;

            Vector3 actorAt = actor.transform.position;

            // The fourth unmeasurable case, and the one that cost a day. X-67 was filed as a
            // client/server origin mismatch on the strength of four RejectedTooFar in
            // p5-e11-03; the client stood 4.08 m from the hull and the server measured
            // ~1037 m, because ITS copy of that body was at y = -1024.67 -- on POS_MIN, having
            // fallen out of the world (X-75). "Too far" was arithmetically true and told the
            // player to walk closer to a vehicle they were already touching.
            //
            // A body outside the wire's range is not far away, it is nowhere: its position has
            // saturated, so every distance computed from it is a distance to the clamp rather
            // than to the body. Refuse with a code that says that.
            if (Quantize.PositionSaturates(actorAt.x)
                || Quantize.PositionSaturates(actorAt.y)
                || Quantize.PositionSaturates(actorAt.z))
                return SeatChangeResult.RejectedActorUnplaced;

            distanceSquared = (seat - actorAt).sqrMagnitude;
            return null;
        }
    }
}
