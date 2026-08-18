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

        /// <summary>
        /// True when this answer changed nothing about the world — the actor was already exactly
        /// where it asked to be.
        /// </summary>
        /// <remarks>
        /// <b>An accept that changed nothing must not be broadcast.</b> The idempotent branch
        /// exists so a client whose <c>S_SEAT_CHANGE</c> was lost converges instead of being told
        /// it is somewhere it is not — but treating it as a normal accept made it a reliable
        /// broadcast to every player, and <c>C_SEAT_REQUEST</c> has no rate limit anywhere. One
        /// seated client repeating "enter the seat I am already in" then multiplies into
        /// N-players x request-rate reliable sends, each retransmitted until acked, from a
        /// message a client is free to send as fast as it likes.
        /// </remarks>
        public readonly bool ChangedNothing;

        public SeatDecision(
            SeatChangeResult result, ushort connectionId, ushort actorId,
            ushort vehicleId, byte seatIndex, bool changedNothing = false)
        {
            Result         = result;
            ConnectionId   = connectionId;
            ActorId        = actorId;
            VehicleId      = vehicleId;
            SeatIndex      = seatIndex;
            ChangedNothing = changedNothing;
        }

        /// <summary>True when the request was granted.</summary>
        public bool Accepted
            => Result == SeatChangeResult.Entered || Result == SeatChangeResult.Left;

        /// <summary>
        /// True when every client needs this answer; false when it belongs to the requester
        /// alone.
        /// </summary>
        /// <remarks>
        /// An accept that moved somebody is everyone's business. A refusal, and an accept that
        /// changed nothing, concern one client — see <see cref="ChangedNothing"/> for why the
        /// second case is not merely tidiness.
        /// </remarks>
        public bool Broadcast => Accepted && !ChangedNothing;

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
