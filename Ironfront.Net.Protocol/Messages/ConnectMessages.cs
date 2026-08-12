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
    public readonly struct ConnectResponsePayload
    {
        public const int Size = 8;

        public readonly ulong ChallengeResponse;

        public ConnectResponsePayload(ulong challengeResponse)
            => ChallengeResponse = challengeResponse;

        /// <summary>The expected answer: clientSalt XOR serverSalt.</summary>
        public static ulong ComputeResponse(ulong clientSalt, ulong serverSalt)
            => clientSalt ^ serverSalt;

        public int Write(Span<byte> dst)
        {
            var w = new SpanWriter(dst);
            w.WriteU64(ChallengeResponse);
            return w.Ok ? w.Position : -1;
        }

        public static bool TryParse(ReadOnlySpan<byte> src, out ConnectResponsePayload payload)
        {
            payload = default;
            var r = new SpanReader(src);
            ulong response = r.ReadU64();
            if (!r.Ok) return false;
            payload = new ConnectResponsePayload(response);
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
