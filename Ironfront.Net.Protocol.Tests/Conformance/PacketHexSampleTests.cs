using System;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// protocol-spec.md section 14, checklist items 6 and 7:
    /// <list type="bullet">
    /// <item>Parsing a hard-coded hex sample packet yields the correct struct
    /// (one test per packetType)</item>
    /// <item>Serializing a struct yields the correct hard-coded hex byte array
    /// (the reverse test)</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Every expected string here was written out from the spec's byte tables by hand, NOT
    /// captured from this implementation's own output. That is the whole point: a test
    /// that records what the code currently does proves only that the code agrees with
    /// itself. These strings are what makes the suite a referee.
    /// </remarks>
    public class PacketHexSampleTests
    {
        private const ulong ClientSalt = 0x0123456789ABCDEFul;
        private const ulong ServerSalt = 0x1122334455667788ul;

        // ------------------------------------------------------- CONNECT_REQUEST 0x01

        // Leading byte is PROTOCOL_VERSION, so it is built from the constant rather than
        // written as a literal — a hardcoded "01" here would keep passing after a version bump
        // while asserting the previous protocol.
        private static readonly string ConnectRequestHex =
            Hex.ToHex(new[] { ProtocolConstants.PROTOCOL_VERSION }) + " "
            + Hex.Repeat(0xAA, ProtocolConstants.JOIN_TICKET_SIZE)
            + " EF CD AB 89 67 45 23 01";

        [Fact]
        public void ConnectRequest_Serializes_ToTheExpectedBytes()
        {
            var ticket = new byte[ProtocolConstants.JOIN_TICKET_SIZE];
            ticket.AsSpan().Fill(0xAA);

            var payload = new ConnectRequestPayload(
                ProtocolConstants.PROTOCOL_VERSION, ticket, ClientSalt);

            Span<byte> buffer = stackalloc byte[ConnectRequestPayload.Size];
            Assert.Equal(ConnectRequestPayload.Size, payload.Write(buffer));
            Assert.Equal(ConnectRequestHex, Hex.ToHex(buffer));
        }

        [Fact]
        public void ConnectRequest_Parses_FromTheExpectedBytes()
        {
            byte[] bytes = Hex.FromHex(ConnectRequestHex);

            Assert.True(ConnectRequestPayload.TryParse(bytes, out ConnectRequestPayload payload));
            Assert.Equal(ProtocolConstants.PROTOCOL_VERSION, payload.ProtocolVersion);
            Assert.Equal(ProtocolConstants.JOIN_TICKET_SIZE, payload.JoinTicket.Length);
            Assert.All(payload.JoinTicket, b => Assert.Equal(0xAA, b));
            Assert.Equal(ClientSalt, payload.ClientSalt);
        }

        [Fact]
        public void ConnectRequest_IsSeventyThreeBytes()
            => Assert.Equal(73, ConnectRequestPayload.Size);

        // ----------------------------------------------------- CONNECT_CHALLENGE 0x02

        private const string ConnectChallengeHex = "88 77 66 55 44 33 22 11";

        [Fact]
        public void ConnectChallenge_RoundTripsThroughTheExpectedBytes()
        {
            Span<byte> buffer = stackalloc byte[ConnectChallengePayload.Size];
            Assert.Equal(8, new ConnectChallengePayload(ServerSalt).Write(buffer));
            Assert.Equal(ConnectChallengeHex, Hex.ToHex(buffer));

            Assert.True(ConnectChallengePayload.TryParse(
                Hex.FromHex(ConnectChallengeHex), out ConnectChallengePayload parsed));
            Assert.Equal(ServerSalt, parsed.ServerSalt);
        }

        // ------------------------------------------------------ CONNECT_RESPONSE 0x03

        // v2: challengeResponse (clientSalt XOR serverSalt) + the echoed clientSalt + the
        // 64-byte joinTicket. Both u64s are little-endian on the wire.
        private const string ConnectResponseHeadHex =
            "67 BA CD DC 23 76 01 10 EF CD AB 89 67 45 23 01";

        [Fact]
        public void ConnectResponse_CarriesTheXorTheEchoedSaltAndTheTicket()
        {
            ulong response = ConnectResponsePayload.ComputeResponse(ClientSalt, ServerSalt);
            Assert.Equal(0x10017623DCCDBA67ul, response);

            // The echoed salt and the repeated ticket are what let the server keep no state
            // between CHALLENGE and RESPONSE — see HandshakeCookie. Before v2 the server had to
            // remember a pending challenge per source address, which a spoofed-source flood
            // filled until it began evicting legitimate clients mid-handshake.
            var ticket = new byte[ProtocolConstants.JOIN_TICKET_SIZE];
            for (int i = 0; i < ticket.Length; i++) ticket[i] = (byte)i;

            Span<byte> buffer = stackalloc byte[ConnectResponsePayload.Size];
            Assert.Equal(80, ConnectResponsePayload.Size);
            Assert.Equal(80, new ConnectResponsePayload(response, ClientSalt, ticket).Write(buffer));

            Assert.Equal(ConnectResponseHeadHex, Hex.ToHex(buffer.Slice(0, 16)));

            Assert.True(ConnectResponsePayload.TryParse(buffer, out ConnectResponsePayload parsed));
            Assert.Equal(response, parsed.ChallengeResponse);
            Assert.Equal(ClientSalt, parsed.ClientSalt);
            Assert.Equal(ticket, parsed.JoinTicket);
        }

        [Fact]
        public void ConnectResponse_RejectsATruncatedTicket()
        {
            Span<byte> short_ = stackalloc byte[ConnectResponsePayload.Size - 1];
            Assert.False(ConnectResponsePayload.TryParse(short_, out _));
        }

        // ------------------------------------------------------ CONNECT_ACCEPTED 0x04

        private const string ConnectAcceptedHex = "02 01 06 05 04 03 08 07 0C 0B 0A 09";

        [Fact]
        public void ConnectAccepted_RoundTripsThroughTheExpectedBytes()
        {
            var payload = new ConnectAcceptedPayload(
                connectionId: 0x0102, serverTick: 0x03040506,
                mapId: 0x0708, myPlayerId: 0x090A0B0C);

            Span<byte> buffer = stackalloc byte[ConnectAcceptedPayload.Size];
            Assert.Equal(12, payload.Write(buffer));
            Assert.Equal(ConnectAcceptedHex, Hex.ToHex(buffer));

            Assert.True(ConnectAcceptedPayload.TryParse(
                Hex.FromHex(ConnectAcceptedHex), out ConnectAcceptedPayload parsed));
            Assert.Equal(0x0102, parsed.ConnectionId);
            Assert.Equal(0x03040506u, parsed.ServerTick);
            Assert.Equal(0x0708, parsed.MapId);
            Assert.Equal(0x090A0B0Cu, parsed.MyPlayerId);
        }

        // -------------------------------------------------------- CONNECT_DENIED 0x05

        [Theory]
        [InlineData(ConnectDenyReason.ServerFull, "01")]
        [InlineData(ConnectDenyReason.ProtocolVersionMismatch, "02")]
        [InlineData(ConnectDenyReason.InvalidTicket, "03")]
        [InlineData(ConnectDenyReason.Banned, "04")]
        [InlineData(ConnectDenyReason.ServerShuttingDown, "05")]
        [InlineData(ConnectDenyReason.AlreadyConnected, "06")]
        public void ConnectDenied_EveryReasonCode_RoundTrips(
            ConnectDenyReason reason, string expectedHex)
        {
            Span<byte> buffer = stackalloc byte[ConnectDeniedPayload.Size];
            Assert.Equal(1, new ConnectDeniedPayload(reason).Write(buffer));
            Assert.Equal(expectedHex, Hex.ToHex(buffer));

            Assert.True(ConnectDeniedPayload.TryParse(
                Hex.FromHex(expectedHex), out ConnectDeniedPayload parsed));
            Assert.Equal(reason, parsed.Reason);
        }

        // ---------------------------------------------------------- S_HIT_CONFIRM 0x43

        // targetActorId 100, damage 34.5 (345 fixed), hitbox Head, flags Killed|Headshot.
        private const string HitConfirmHex = "64 00 59 01 01 03";

        [Fact]
        public void HitConfirm_RoundTripsThroughTheExpectedBytes()
        {
            var message = new HitConfirmMessage(
                targetActorId: 100,
                damageFixed: HitConfirmMessage.PackDamage(34.5f),
                hitboxType: HitboxType.Head,
                flags: HitFlags.Killed | HitFlags.Headshot);

            Span<byte> buffer = stackalloc byte[HitConfirmMessage.Size];
            Assert.Equal(6, message.Write(buffer));
            Assert.Equal(HitConfirmHex, Hex.ToHex(buffer));

            Assert.True(HitConfirmMessage.TryParse(
                Hex.FromHex(HitConfirmHex), out HitConfirmMessage parsed));
            Assert.Equal(100, parsed.TargetActorId);
            Assert.Equal(345, parsed.DamageFixed);
            Assert.Equal(34.5f, parsed.Damage);
            Assert.Equal(HitboxType.Head, parsed.HitboxType);
            Assert.True(parsed.Killed);
            Assert.True(parsed.Headshot);
        }

        // --------------------------------------------------------------- S_DEATH 0x44

        // victim 10, killer 0xFFFF (environment), cause Explosion, force (1000,-1000,0),
        // hitboxHit 2.
        private const string DeathHex = "0A 00 FF FF 01 E8 03 18 FC 00 00 02";

        [Fact]
        public void Death_RoundTripsThroughTheExpectedBytes()
        {
            var message = new DeathMessage(
                victimActorId: 10,
                killerActorId: DeathMessage.EnvironmentKiller,
                cause: CauseOfDeath.Explosion,
                forceX: 1000, forceY: -1000, forceZ: 0,
                hitboxHit: 2);

            Span<byte> buffer = stackalloc byte[DeathMessage.Size];
            Assert.Equal(12, message.Write(buffer));
            Assert.Equal(DeathHex, Hex.ToHex(buffer));

            Assert.True(DeathMessage.TryParse(Hex.FromHex(DeathHex), out DeathMessage parsed));
            Assert.Equal(10, parsed.VictimActorId);
            Assert.True(parsed.KilledByEnvironment);
            Assert.Equal(CauseOfDeath.Explosion, parsed.Cause);
            Assert.Equal(1000, parsed.ForceX);
            Assert.Equal(-1000, parsed.ForceY);
            Assert.Equal(0, parsed.ForceZ);
            Assert.Equal(2, parsed.HitboxHit);
        }

        // --------------------------------------------------------- S_WEAPON_FIRE 0x49

        private const string WeaponFireHex = "05 00 07 FF 7F 00 80 00 00";

        [Fact]
        public void WeaponFire_RoundTripsThroughTheExpectedBytes()
        {
            var message = new WeaponFireMessage(
                shooterActorId: 5, weaponId: 7,
                dirX: short.MaxValue, dirY: short.MinValue, dirZ: 0);

            Span<byte> buffer = stackalloc byte[WeaponFireMessage.Size];
            Assert.Equal(9, message.Write(buffer));
            Assert.Equal(WeaponFireHex, Hex.ToHex(buffer));

            Assert.True(WeaponFireMessage.TryParse(
                Hex.FromHex(WeaponFireHex), out WeaponFireMessage parsed));
            Assert.Equal(5, parsed.ShooterActorId);
            Assert.Equal(7, parsed.WeaponId);
            Assert.Equal(short.MaxValue, parsed.DirX);
            Assert.Equal(short.MinValue, parsed.DirY);
            Assert.Equal(0, parsed.DirZ);
        }

        // ------------------------------------------------------- message type numbers

        /// <summary>
        /// The msgType values themselves are protocol surface — a renumbering that the
        /// other three people do not pick up routes every message to the wrong handler.
        /// </summary>
        [Fact]
        public void MessageTypeValues_MatchSpecSection41()
        {
            Assert.Equal(0x20, (byte)ClientMessageType.Input);
            Assert.Equal(0x22, (byte)ClientMessageType.LoadoutSelect);
            Assert.Equal(0x23, (byte)ClientMessageType.SpawnRequest);
            Assert.Equal(0x24, (byte)ClientMessageType.Chat);
            Assert.Equal(0x25, (byte)ClientMessageType.Ping);
            Assert.Equal(0x26, (byte)ClientMessageType.SeatRequest);
            Assert.Equal(0x27, (byte)ClientMessageType.AckBaseline);

            Assert.Equal(0x40, (byte)ServerMessageType.Snapshot);
            Assert.Equal(0x41, (byte)ServerMessageType.SpawnActor);
            Assert.Equal(0x42, (byte)ServerMessageType.DespawnActor);
            Assert.Equal(0x43, (byte)ServerMessageType.HitConfirm);
            Assert.Equal(0x44, (byte)ServerMessageType.Death);
            Assert.Equal(0x45, (byte)ServerMessageType.MatchState);
            Assert.Equal(0x46, (byte)ServerMessageType.CapturePoint);
            Assert.Equal(0x47, (byte)ServerMessageType.Chat);
            Assert.Equal(0x48, (byte)ServerMessageType.Pong);
            Assert.Equal(0x49, (byte)ServerMessageType.WeaponFire);
            Assert.Equal(0x4A, (byte)ServerMessageType.Explosion);
            Assert.Equal(0x4B, (byte)ServerMessageType.PlayerList);
        }

        [Fact]
        public void PacketTypeValues_MatchSpecSection3()
        {
            Assert.Equal(0x01, (byte)PacketType.ConnectRequest);
            Assert.Equal(0x02, (byte)PacketType.ConnectChallenge);
            Assert.Equal(0x03, (byte)PacketType.ConnectResponse);
            Assert.Equal(0x04, (byte)PacketType.ConnectAccepted);
            Assert.Equal(0x05, (byte)PacketType.ConnectDenied);
            Assert.Equal(0x06, (byte)PacketType.Disconnect);
            Assert.Equal(0x07, (byte)PacketType.Keepalive);
            Assert.Equal(0x10, (byte)PacketType.Payload);
            Assert.Equal(0x11, (byte)PacketType.Fragment);
        }

        [Fact]
        public void ErrorCodeValues_MatchSpecSection13()
        {
            Assert.Equal(0, (ushort)ErrorCode.Ok);
            Assert.Equal(1000, (ushort)ErrorCode.WrongCredentials);
            Assert.Equal(1001, (ushort)ErrorCode.UsernameTaken);
            Assert.Equal(1002, (ushort)ErrorCode.InvalidUsername);
            Assert.Equal(1003, (ushort)ErrorCode.SessionExpired);
            Assert.Equal(1004, (ushort)ErrorCode.WrongClientVersion);
            Assert.Equal(2000, (ushort)ErrorCode.RoomNotFound);
            Assert.Equal(2001, (ushort)ErrorCode.RoomFull);
            Assert.Equal(2002, (ushort)ErrorCode.WrongRoomPassword);
            Assert.Equal(2003, (ushort)ErrorCode.MatchAlreadyStarted);
            Assert.Equal(2004, (ushort)ErrorCode.AlreadyInAnotherRoom);
            Assert.Equal(3000, (ushort)ErrorCode.NoGameServerAvailable);
            Assert.Equal(3001, (ushort)ErrorCode.GameServerNotResponding);
            Assert.Equal(9000, (ushort)ErrorCode.InternalServerError);
            Assert.Equal(9001, (ushort)ErrorCode.RateLimited);
        }
    }
}
