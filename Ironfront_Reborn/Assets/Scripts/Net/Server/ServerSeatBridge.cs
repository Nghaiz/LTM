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

            var request = new SeatRequest(
                session.ConnectionId,
                session.ActorId,
                message.VehicleId,
                message.SeatIndex,
                message.Action,
                DistanceSquaredToSeat(session.ActorId, message.VehicleId, message.SeatIndex),
                clientTick: 0);

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
        /// How far the actor is from the seat, squared.
        /// </summary>
        /// <remarks>
        /// <b>Measured on the server, from the server's own transforms.</b> Taking it from the
        /// request would let a client claim it is standing next to any vehicle on the map, which
        /// is the whole reason the arbiter has a reach check. An unresolvable actor or vehicle
        /// reports <see cref="float.MaxValue"/> so the request is refused rather than admitted
        /// on a missing measurement.
        /// </remarks>
        private float DistanceSquaredToSeat(ushort actorId, ushort vehicleId, byte seatIndex)
        {
            if (!_actors.TryFind(actorId, out NetServerActor actor) || actor == null)
                return float.MaxValue;

            if (!_vehicles.TryFind(vehicleId, out IGameplayVehicleSource vehicle))
                return float.MaxValue;

            Vector3 seat = vehicle.GetSeatPosition(seatIndex);
            return (seat - actor.transform.position).sqrMagnitude;
        }
    }
}
