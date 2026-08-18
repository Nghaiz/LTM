using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// The arbiter's answer to one <see cref="SeatRequest"/>, ready to become an
    /// <c>S_SEAT_CHANGE</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every request produces one of these, including the refused ones.</b> That is the whole
    /// V4-D7 change: a dropped request and a refused one look identical to a client that only
    /// hears about success, and the client's own prediction has already seated the player by
    /// then. <c>Actor.EnterSeat</c>'s <c>bool</c> is discarded at all three shipped call sites
    /// (<c>FpsActorController</c>, <c>AiActorController</c>, <c>Actor.SwitchSeat</c>) — those are
    /// the offline and AI paths and stay as they are. The networked path never speculates.
    /// </para>
    /// <para>
    /// <b><see cref="Broadcast"/> is on the decision, not decided by the sender.</b> Who hears an
    /// answer follows from what the answer is: an accept changes the world and everyone needs
    /// it, a refusal changes nothing and concerns one client. Leaving that to the caller means
    /// it is re-derived at every send site, and the one that gets it wrong tells sixteen clients
    /// about a refusal they cannot act on — or, worse the other way, tells nobody that a tank
    /// now has a driver.
    /// </para>
    /// </remarks>
    public readonly struct SeatDecision
    {
        /// <summary>The wire's answer code.</summary>
        public readonly SeatChangeResult Result;

        /// <summary>The connection that asked, for an addressed refusal.</summary>
        public readonly ushort ConnectionId;

        /// <summary>The actor the answer is about.</summary>
        public readonly ushort ActorId;

        /// <summary>The vehicle, or 0 when the answer leaves the actor on foot.</summary>
        public readonly ushort VehicleId;

        /// <summary>The seat, meaningless when <see cref="VehicleId"/> is 0.</summary>
        public readonly byte SeatIndex;

        public SeatDecision(
            SeatChangeResult result, ushort connectionId, ushort actorId,
            ushort vehicleId, byte seatIndex)
        {
            Result       = result;
            ConnectionId = connectionId;
            ActorId      = actorId;
            VehicleId    = vehicleId;
            SeatIndex    = seatIndex;
        }

        /// <summary>True when the request was granted.</summary>
        public bool Accepted
            => Result == SeatChangeResult.Entered || Result == SeatChangeResult.Left;

        /// <summary>
        /// True when every client needs this answer; false when it belongs to the requester
        /// alone.
        /// </summary>
        public bool Broadcast => Accepted;

        /// <summary>Builds the message this decision becomes.</summary>
        public SeatChangeMessage ToMessage()
            => new SeatChangeMessage(ActorId, VehicleId, SeatIndex, Result);

        /// <summary>A refusal addressed to the requester.</summary>
        public static SeatDecision Refuse(in SeatRequest request, SeatChangeResult result)
            => new SeatDecision(
                result, request.ConnectionId, request.ActorId,
                request.VehicleId, request.SeatIndex);
    }
}
