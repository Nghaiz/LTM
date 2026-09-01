using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>GSP datagram type. protocol-spec.md section 3.</summary>
    public enum PacketType : byte
    {
        /// <summary>C-&gt;S. Requests a connection, carries the joinTicket. Retried.</summary>
        ConnectRequest   = 0x01,
        /// <summary>S-&gt;C. The server sends a nonce. Retried.</summary>
        ConnectChallenge = 0x02,
        /// <summary>C-&gt;S. The client answers the challenge. Retried.</summary>
        ConnectResponse  = 0x03,
        /// <summary>S-&gt;C. Assigns a connectionId. Retried.</summary>
        ConnectAccepted  = 0x04,
        /// <summary>S-&gt;C. Carries a reason code. Not retried.</summary>
        ConnectDenied    = 0x05,
        /// <summary>Both. Deliberate disconnect, sent 3 times.</summary>
        Disconnect       = 0x06,
        /// <summary>Both. Keeps the connection alive and measures RTT.</summary>
        Keepalive        = 0x07,
        /// <summary>Both. Carries batched messages. Reliability per flags.</summary>
        Payload          = 0x10,
        /// <summary>Both. One fragment of a large payload. Always reliable.</summary>
        Fragment         = 0x11,
    }

    /// <summary>GSP header flags bitfield. protocol-spec.md section 2.1.</summary>
    [Flags]
    public enum PacketFlags : byte
    {
        None       = 0,
        /// <summary>Bit 0. Must be acked, and retransmitted if lost.</summary>
        Reliable   = 1 << 0,
        /// <summary>Bit 1. The payload is one fragment (see section 6).</summary>
        Fragmented = 1 << 1,
        /// <summary>Bit 2. Must be delivered in order within its channel.</summary>
        Ordered    = 1 << 2,
        /// <summary>Bit 3. Reserved in v1 — never set.</summary>
        Compressed = 1 << 3,

        /// <summary>Bits 4-7 are reserved and must be zero on send.</summary>
        ReservedMask = 0xF0,
    }

    /// <summary>
    /// Reliability channel. protocol-spec.md section 5.
    /// Reliability is per channel, not per connection.
    /// </summary>
    public enum ChannelId : byte
    {
        /// <summary>Unreliable-unsequenced. Ping/pong.</summary>
        Unreliable         = 0,
        /// <summary>Unreliable-sequenced, server-&gt;client. S_SNAPSHOT, S_WEAPON_FIRE.</summary>
        SnapshotSequenced  = 1,
        /// <summary>Reliable-ordered. All gameplay events, chat, spawn/despawn.</summary>
        ReliableOrdered    = 2,
        /// <summary>Unreliable-sequenced, client-&gt;server. C_INPUT.</summary>
        InputSequenced     = 3,
    }

    /// <summary>CONNECT_DENIED reason code. protocol-spec.md section 3.2.</summary>
    public enum ConnectDenyReason : byte
    {
        None                    = 0,
        ServerFull              = 1,
        ProtocolVersionMismatch = 2,
        /// <summary>joinTicket invalid, tampered with, or expired.</summary>
        InvalidTicket           = 3,
        Banned                  = 4,
        ServerShuttingDown      = 5,
        AlreadyConnected        = 6,
        /// <summary>
        /// The side this ticket names is full; the other one may not be. Distinct from
        /// <see cref="ServerFull"/> on purpose — "the server is full" has no remedy, and
        /// "your side is full" has one the player can act on, so rendering them as the same
        /// sentence throws away the only actionable half. protocol-spec.md § 3.2.
        /// </summary>
        /// <remarks>
        /// Adding a value moves no byte — the reason field was already a <c>u8</c> — so this
        /// is not a wire change on its own and does not bump PROTOCOL_VERSION by itself. A
        /// client that predates it must degrade to a generic refusal rather than to silence
        /// or to a wrong reason; <c>Connection.MapDeniedReason</c> is where that is enforced.
        /// </remarks>
        TeamFull                = 7,
    }

    /// <summary>Connection state machine. protocol-spec.md section 9.</summary>
    public enum ConnectionState : byte
    {
        Disconnected = 0,
        Connecting   = 1,
        Challenged   = 2,
        Connected    = 3,
    }
}
