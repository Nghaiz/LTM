using System;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// CONNECT_REQUEST (0x01) payload. protocol-spec.md section 3.1.
    /// </summary>
    public readonly struct ConnectRequestPayload
    {
        /// <summary>u8 + u8[64] + u64 = 73 bytes.</summary>
        public const int Size = 1 + ProtocolConstants.JOIN_TICKET_SIZE + 8;

        public readonly byte ProtocolVersion;
        /// <summary>The 64-byte HMAC-signed ticket issued by the master server.</summary>
        public readonly byte[] JoinTicket;
        /// <summary>Client half of the anti-spoofing challenge.</summary>
        public readonly ulong ClientSalt;

        public ConnectRequestPayload(byte protocolVersion, byte[] joinTicket, ulong clientSalt)
        {
            ProtocolVersion = protocolVersion;
            JoinTicket      = joinTicket;
            ClientSalt      = clientSalt;
        }

        public int Write(Span<byte> dst)
        {
            if (JoinTicket == null || JoinTicket.Length != ProtocolConstants.JOIN_TICKET_SIZE)
                return -1;

            var w = new SpanWriter(dst);
            w.WriteU8(ProtocolVersion);
            w.WriteBytes(JoinTicket);
            w.WriteU64(ClientSalt);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out ConnectRequestPayload payload)
        {
            payload = default;
            var r = new SpanReader(src);
            byte version = r.ReadU8();
            ReadOnlySpan<byte> ticket = r.ReadBytes(ProtocolConstants.JOIN_TICKET_SIZE);
            ulong salt = r.ReadU64();
            if (!r.Ok) return false;

            payload = new ConnectRequestPayload(version, ticket.ToArray(), salt);
            return true;
        }
    }

    /// <summary>
    /// CONNECT_CHALLENGE (0x02) payload. protocol-spec.md section 3.1.
    /// </summary>
    /// <remarks>
    /// The challenge exists to stop IP-spoofing amplification: an attacker who forges a
    /// victim's source address in a CONNECT_REQUEST never receives the serverSalt, so
    /// they cannot complete the handshake and the server allocates nothing on their behalf.
    /// </remarks>
    public readonly struct ConnectChallengePayload
    {
        public const int Size = 8;

        public readonly ulong ServerSalt;

        public ConnectChallengePayload(ulong serverSalt) => ServerSalt = serverSalt;

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU64(ServerSalt);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out ConnectChallengePayload payload)
        {
            payload = default;
            var r = new SpanReader(src);
            ulong salt = r.ReadU64();
            if (!r.Ok) return false;
            payload = new ConnectChallengePayload(salt);
            return true;
        }
    }

    /// <summary>
    /// CONNECT_RESPONSE (0x03) payload. protocol-spec.md section 3.1.
    /// </summary>
    /// <remarks>
    /// <b>The client echoes its own salt back.</b> That is what lets the server keep NO state
    /// between CHALLENGE and RESPONSE: it recomputes the server salt from the client's address
    /// and this salt with a secret key it never shares, so a challenge costs it nothing to
    /// issue and nothing to remember. Storing a pending-challenge record per request instead
    /// means a spoofed source address can make the server allocate — the resource-exhaustion
    /// hole protocol-spec.md section 3.1 exists to forbid ("the server allocates no
    /// resources"). See section 3.1's cookie description.
    /// </remarks>
    public readonly struct ConnectResponsePayload
    {
        /// <summary>u64 + u64 + joinTicket = 80 bytes.</summary>
        public const int Size = 16 + ProtocolConstants.JOIN_TICKET_SIZE;

        public readonly ulong ChallengeResponse;

        /// <summary>The salt the client sent in CONNECT_REQUEST, echoed unchanged.</summary>
        public readonly ulong ClientSalt;

        /// <summary>
        /// The same joinTicket the request carried, repeated here.
        /// </summary>
        /// <remarks>
        /// Repeated because the server keeps no memory of the request. It is also the only
        /// place the ticket is AUTHORITATIVELY checked: at CONNECT_REQUEST the source address
        /// is still unproven, so anything decided there is decided for an address that may not
        /// exist. By the time this arrives the challenge round trip has proved the client holds
        /// the address, so this is where the ticket is verified and its playerId is bound to
        /// the connection.
        /// </remarks>
        public readonly byte[] JoinTicket;

        public ConnectResponsePayload(ulong challengeResponse, ulong clientSalt, byte[] joinTicket)
        {
            ChallengeResponse = challengeResponse;
            ClientSalt = clientSalt;
            JoinTicket = joinTicket ?? throw new ArgumentNullException(nameof(joinTicket));
        }

        /// <summary>The expected answer: clientSalt XOR serverSalt.</summary>
        public static ulong ComputeResponse(ulong clientSalt, ulong serverSalt)
            => clientSalt ^ serverSalt;

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU64(ChallengeResponse);
            w.WriteU64(ClientSalt);
            w.WriteBytes(JoinTicket);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out ConnectResponsePayload payload)
        {
            payload = default;
            var r = new SpanReader(src);
            ulong response = r.ReadU64();
            ulong clientSalt = r.ReadU64();
            ReadOnlySpan<byte> ticket = r.ReadBytes(ProtocolConstants.JOIN_TICKET_SIZE);
            if (!r.Ok) return false;
            payload = new ConnectResponsePayload(response, clientSalt, ticket.ToArray());
            return true;
        }
    }

    /// <summary>
    /// CONNECT_ACCEPTED (0x04) payload. protocol-spec.md section 3.1.
    /// </summary>
    public readonly struct ConnectAcceptedPayload
    {
        /// <summary>u16 + u32 + u16 + u32 = 12 bytes.</summary>
        public const int Size = 12;

        public readonly ushort ConnectionId;
        public readonly uint ServerTick;
        public readonly ushort MapId;
        public readonly uint MyPlayerId;

        public ConnectAcceptedPayload(
            ushort connectionId, uint serverTick, ushort mapId, uint myPlayerId)
        {
            ConnectionId = connectionId;
            ServerTick   = serverTick;
            MapId        = mapId;
            MyPlayerId   = myPlayerId;
        }

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU16(ConnectionId);
            w.WriteU32(ServerTick);
            w.WriteU16(MapId);
            w.WriteU32(MyPlayerId);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out ConnectAcceptedPayload payload)
        {
            payload = default;
            var r = new SpanReader(src);
            ushort connectionId = r.ReadU16();
            uint serverTick     = r.ReadU32();
            ushort mapId        = r.ReadU16();
            uint myPlayerId     = r.ReadU32();
            if (!r.Ok) return false;

            payload = new ConnectAcceptedPayload(connectionId, serverTick, mapId, myPlayerId);
            return true;
        }
    }

    /// <summary>
    /// CONNECT_DENIED (0x05) payload. protocol-spec.md section 3.2.
    /// </summary>
    public readonly struct ConnectDeniedPayload
    {
        public const int Size = 1;

        public readonly ConnectDenyReason Reason;

        public ConnectDeniedPayload(ConnectDenyReason reason) => Reason = reason;

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU8((byte)Reason);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out ConnectDeniedPayload payload)
        {
            payload = default;
            var r = new SpanReader(src);
            byte reason = r.ReadU8();
            if (!r.Ok) return false;
            payload = new ConnectDeniedPayload((ConnectDenyReason)reason);
            return true;
        }
    }
}
