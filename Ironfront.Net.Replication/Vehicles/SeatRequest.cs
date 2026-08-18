using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// One decoded <c>C_SEAT_REQUEST</c>, with everything the arbiter needs to answer it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><see cref="ConnectionId"/> is here so a refusal has an address.</b> An accept is
    /// broadcast — everyone must see who is in the vehicle — but a refusal goes to the requester
    /// alone (V4-D7), and the actor id is not enough to find the connection once the actor is
    /// a bot or has been recycled.
    /// </para>
    /// <para>
    /// <b><see cref="DistanceSquaredToSeat"/> is supplied by the caller, not computed here.</b>
    /// The arbiter is a pure decision function over engine-free values; the two positions it
    /// would need live in a Unity scene and a snapshot buffer respectively, and reaching into
    /// either would make the whole class untestable to save one subtraction. Squared, because
    /// the comparison is against a squared limit and the square root would be thrown away.
    /// </para>
    /// <para>
    /// <b><see cref="ClientTick"/> is recorded and deliberately not trusted.</b> Ordering is by
    /// arrival at the server, ties broken by connection id (V4-D9) — a client-supplied tick as
    /// the tie-break would let a client win every race by claiming an early one.
    /// </para>
    /// </remarks>
    public readonly struct SeatRequest
    {
        /// <summary>Who asked. The address a refusal is sent to.</summary>
        public readonly ushort ConnectionId;

        /// <summary>The actor that would move.</summary>
        public readonly ushort ActorId;

        /// <summary>
        /// The vehicle named by the request. Ignored for a <see cref="SeatAction.Leave"/>, which
        /// is answered from where the actor actually is.
        /// </summary>
        public readonly ushort VehicleId;

        /// <summary>Index into <c>Vehicle.seats</c>. <c>seats[0]</c> is the driver (V4-D6).</summary>
        public readonly byte SeatIndex;

        /// <summary>Enter or leave.</summary>
        public readonly SeatAction Action;

        /// <summary>Squared metres between the actor and the seat. See the type remarks.</summary>
        public readonly float DistanceSquaredToSeat;

        /// <summary>The client's tick, for diagnostics. Never a tie-break.</summary>
        public readonly uint ClientTick;

        public SeatRequest(
            ushort connectionId, ushort actorId, ushort vehicleId, byte seatIndex,
            SeatAction action, float distanceSquaredToSeat = 0f, uint clientTick = 0)
        {
            ConnectionId          = connectionId;
            ActorId               = actorId;
            VehicleId             = vehicleId;
            SeatIndex             = seatIndex;
            Action                = action;
            DistanceSquaredToSeat = distanceSquaredToSeat;
            ClientTick            = clientTick;
        }
    }
}
