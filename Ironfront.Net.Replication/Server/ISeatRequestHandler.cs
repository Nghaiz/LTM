using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Where an accepted <c>C_SEAT_REQUEST</c> goes once the router has decoded it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One method, so the Unity bridge can be held as a field rather than a capturing lambda —
    /// the same reasoning as <see cref="IAcceptedFrameObserver"/> and
    /// <see cref="ISpawnRequestHandler"/>. The router runs inside the tick loop and must not
    /// allocate, and a multicast delegate's invocation list is state it has no business owning.
    /// </para>
    /// <para>
    /// <b>The router does not arbitrate.</b> It parses, clamps, and hands over. Deciding needs
    /// the vehicle registry and the seat table, which the router has never held and should not
    /// start holding — <c>SeatArbiter</c> is where the decision lives and is what the tests
    /// point at.
    /// </para>
    /// </remarks>
    public interface ISeatRequestHandler
    {
        /// <summary>
        /// A well-formed seat request arrived from <paramref name="session"/>.
        /// </summary>
        /// <remarks>
        /// The session rather than a bare connection id, because the handler needs the actor id
        /// too and reading it from the message would let a client ask on somebody else's behalf.
        /// </remarks>
        void OnSeatRequested(ClientSession session, in SeatRequestMessage message);
    }
}
