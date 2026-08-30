using System;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// Where a parsed <c>C_CHAT</c> goes. Phase P6 task 3.3, ledger X-8.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An interface rather than an event, for the reason every seam on
    /// <see cref="ServerMessageRouter"/> is one: the router runs inside the tick loop and must
    /// not allocate, and a multicast delegate invocation list is state that class has no
    /// business owning.
    /// </para>
    /// <para>
    /// <b>The text arrives as bytes, not as a string.</b> Decoding is the handler's call because
    /// the handler is the thing that has somewhere to put a string; decoding in the router would
    /// allocate one per message inside a class documented as allocation-free after construction.
    /// </para>
    /// <para>
    /// <b>The span points into the transport's pooled receive buffer</b> and is recycled the
    /// moment <see cref="ServerMessageRouter.Route"/> returns. A handler that keeps the text
    /// past the call copies it — <c>ChatTextMessage.TextOf</c> is the allocating decode for exactly
    /// that moment.
    /// </para>
    /// <para>
    /// <b>Nothing here says who spoke.</b> The session is the attribution: the datagram arrived
    /// on that connection, and a client that stated its own id would be stating somebody else's.
    /// </para>
    /// </remarks>
    public interface IChatHandler
    {
        void OnChat(ClientSession session, ReadOnlySpan<byte> textUtf8);
    }
}
