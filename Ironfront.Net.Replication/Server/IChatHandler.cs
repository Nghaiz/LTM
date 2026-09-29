using System;
using Ironfront.Net.Protocol;

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
    /// <para>
    /// <b>The channel is a request, not an address.</b> It names who the speaker wants to hear
    /// the line; the handler works out who that is from the sides the server already knows
    /// (<see cref="ChatAudience"/>), so a client can reach its own team and nobody else's.
    /// </para>
    /// </remarks>
    public interface IChatHandler
    {
        void OnChat(ClientSession session, ChatChannel channel, ReadOnlySpan<byte> textUtf8);
    }
}
