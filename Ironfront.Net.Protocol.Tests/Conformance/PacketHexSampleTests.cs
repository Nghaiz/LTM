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
            Assert.Equal(0x21, (byte)ClientMessageType.VehicleInput);
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
            Assert.Equal(0x4C, (byte)ServerMessageType.VehicleSnapshot);
            Assert.Equal(0x4D, (byte)ServerMessageType.VehicleSpawn);
            Assert.Equal(0x4E, (byte)ServerMessageType.VehicleDespawn);
            Assert.Equal(0x4F, (byte)ServerMessageType.ProjectileSpawn);
            Assert.Equal(0x50, (byte)ServerMessageType.SeatChange);
        }

        // -------------------------------------------------------- S_MATCH_STATE 0x45 (v5)

        // Written out from the layout, not captured from the implementation. Little-endian
        // throughout, and in declaration order:
        //   phase                 u8   02  = MatchPhase.Playing
        //   score0                u16  8A 00 = 138        ASCENDING (was a descending ticket
        //   score1                u16  2C 00 = 44          count before v5 -- same two byte
        //                                                  positions, inverted meaning)
        //   phaseSecondsRemaining u16  00 00 = 0          Playing has no clock
        //   humanPlayerCount      u8   0C  = 12
        //   victoryPoints         u16  C8 00 = 200        NEW in v5, appended -> Size 8 -> 10
        private const string MatchStateHex = "02 8A 00 2C 00 00 00 0C C8 00";

        [Fact]
        public void MatchState_Serializes_ToTheExpectedBytes()
        {
            var message = new MatchStateMessage(
                MatchPhase.Playing, score0: 138, score1: 44,
                phaseSecondsRemaining: 0, humanPlayerCount: 12, victoryPoints: 200);

            Span<byte> buffer = stackalloc byte[MatchStateMessage.Size];
            Assert.Equal(MatchStateMessage.Size, message.Write(buffer));
            Assert.Equal(MatchStateHex, Hex.ToHex(buffer));
        }

        [Fact]
        public void MatchState_Parses_FromTheExpectedBytes()
        {
            byte[] bytes = Hex.FromHex(MatchStateHex);

            Assert.True(MatchStateMessage.TryParse(bytes, out MatchStateMessage message));
            Assert.Equal(MatchPhase.Playing, message.Phase);
            Assert.Equal(138, message.Score0);
            Assert.Equal(44, message.Score1);
            Assert.Equal(0, message.PhaseSecondsRemaining);
            Assert.Equal(12, message.HumanPlayerCount);
            Assert.Equal(200, message.VictoryPoints);
        }

        [Fact]
        public void MatchState_IsTenBytes()
            => Assert.Equal(10, MatchStateMessage.Size);

        /// <summary>
        /// The half of the v5 bump that a size check cannot see. A v4 sender packed its two
        /// bytes at the same offsets, so these ten bytes parse cleanly -- and mean the opposite
        /// thing. That is why the version was bumped for the meaning as well as for the size:
        /// the failure this pins is silent, and a mismatched PROTOCOL_VERSION turns it into
        /// CONNECT_DENIED code 2 instead.
        /// </summary>
        [Fact]
        public void MatchState_TenBytesFromAV4SenderWouldDecodeBackwards()
        {
            // A v4 server one second into a round: tickets 199 / 200, DESCENDING. Read as v5
            // those same bytes say team 1 is a point ahead on an ascending score, when in truth
            // team 1 had just lost somebody.
            byte[] v4Bytes = Hex.FromHex("02 C7 00 C8 00 00 00 0C C8 00");

            Assert.True(MatchStateMessage.TryParse(v4Bytes, out MatchStateMessage message));
            Assert.Equal(199, message.Score0);
            Assert.Equal(200, message.Score1);
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

        // ==================================================================== phase-V3
        //
        // Every string below was written out from the byte tables in protocol-spec.md
        // section 4.10 by hand, little-endian, field by field. None of it was captured from
        // the implementation's output — a sample recorded from the code proves only that the
        // code agrees with itself, and a mis-sized field would be baked into the "expected"
        // value along with everything else.

        // ------------------------------------------------- C_VEHICLE_INPUT 0x21 (16 B)
        //   u32 tick        1234       = 0x000004D2 -> D2 04 00 00
        //   u16 vehicleId   7                       -> 07 00
        //   i8  throttle    127                     -> 7F
        //   i8  steer       -64                     -> C0
        //   i8  pitchAxis   0                       -> 00
        //   i8  auxAxis     -1                      -> FF
        //   u16 turretYaw   32768      = 0x8000     -> 00 80
        //   i16 turretPitch -4096      = 0xF000     -> 00 F0
        //   u16 buttons     0x0001                  -> 01 00
        private const string VehicleInputHex =
            "D2 04 00 00 07 00 7F C0 00 FF 00 80 00 F0 01 00";

        [Fact]
        public void VehicleInput_Serializes_ToTheExpectedBytes()
        {
            var message = new VehicleInputMessage(
                tick: 1234, vehicleId: 7,
                throttle: 127, steer: -64, pitchAxis: 0, auxAxis: -1,
                turretYaw: 32768, turretPitch: -4096, buttons: 1);

            Span<byte> buffer = stackalloc byte[VehicleInputMessage.Size];
            Assert.Equal(VehicleInputMessage.Size, message.Write(buffer));
            Assert.Equal(VehicleInputHex, Hex.ToHex(buffer));
        }

        [Fact]
        public void VehicleInput_Parses_FromTheExpectedBytes()
        {
            Assert.True(VehicleInputMessage.TryParse(
                Hex.FromHex(VehicleInputHex), out VehicleInputMessage message));

            Assert.Equal(1234u, message.Tick);
            Assert.Equal(7, message.VehicleId);
            Assert.Equal(127, message.Throttle);
            Assert.Equal(-64, message.Steer);
            Assert.Equal(0, message.PitchAxis);
            Assert.Equal(-1, message.AuxAxis);
            Assert.Equal(32768, message.TurretYaw);
            Assert.Equal(-4096, message.TurretPitch);
            Assert.Equal(1, message.Buttons);
        }

        // -------------------------------------------------- C_SEAT_REQUEST 0x26 (4 B)
        //   u16 vehicleId 7 -> 07 00 · u8 seatIndex 2 -> 02 · u8 action Enter(0) -> 00
        private const string SeatRequestHex = "07 00 02 00";

        [Fact]
        public void SeatRequest_Serializes_ToTheExpectedBytes()
        {
            var message = new SeatRequestMessage(7, 2, SeatAction.Enter);

            Span<byte> buffer = stackalloc byte[SeatRequestMessage.Size];
            Assert.Equal(SeatRequestMessage.Size, message.Write(buffer));
            Assert.Equal(SeatRequestHex, Hex.ToHex(buffer));
        }

        [Fact]
        public void SeatRequest_Parses_FromTheExpectedBytes()
        {
            Assert.True(SeatRequestMessage.TryParse(
                Hex.FromHex(SeatRequestHex), out SeatRequestMessage message));

            Assert.Equal(7, message.VehicleId);
            Assert.Equal(2, message.SeatIndex);
            Assert.Equal(SeatAction.Enter, message.Action);
        }

        // ------------------------------------------------- S_VEHICLE_SPAWN 0x4D (16 B)
        //   u16 vehicleId     7                  -> 07 00
        //   u8  kind          Tank(1)            -> 01
        //   u8  networkTypeId VehicleIds.TANK(5) -> 05
        //   i16 posX          256   = 0x0100     -> 00 01
        //   i16 posY          -256  = 0xFF00     -> 00 FF
        //   i16 posZ          0                  -> 00 00
        //   u32 rotation      0xC0000000         -> 00 00 00 C0
        //   u8  seatCount     3                  -> 03
        //   u8  flags         0                  -> 00
        private const string VehicleSpawnHex =
            "07 00 01 05 00 01 00 FF 00 00 00 00 00 C0 03 00";

        [Fact]
        public void VehicleSpawn_Serializes_ToTheExpectedBytes()
        {
            var message = new VehicleSpawnMessage(
                vehicleId: 7, kind: VehicleKind.Tank, networkTypeId: VehicleIds.TANK,
                posX: 256, posY: -256, posZ: 0, rotation: 0xC0000000u,
                seatCount: 3, flags: 0);

            Span<byte> buffer = stackalloc byte[VehicleSpawnMessage.Size];
            Assert.Equal(VehicleSpawnMessage.Size, message.Write(buffer));
            Assert.Equal(VehicleSpawnHex, Hex.ToHex(buffer));
        }

        [Fact]
        public void VehicleSpawn_Parses_FromTheExpectedBytes()
        {
            Assert.True(VehicleSpawnMessage.TryParse(
                Hex.FromHex(VehicleSpawnHex), out VehicleSpawnMessage message));

            Assert.Equal(7, message.VehicleId);
            Assert.Equal(VehicleKind.Tank, message.Kind);
            Assert.Equal(VehicleIds.TANK, message.NetworkTypeId);
            Assert.Equal(256, message.PosX);
            Assert.Equal(-256, message.PosY);
            Assert.Equal(0, message.PosZ);
            Assert.Equal(0xC0000000u, message.Rotation);
            Assert.Equal(3, message.SeatCount);
        }

        // ----------------------------------------------- S_VEHICLE_DESPAWN 0x4E (3 B)
        //   u16 vehicleId 7 -> 07 00 · u8 reason WorldReset(1) -> 01
        private const string VehicleDespawnHex = "07 00 01";

        [Fact]
        public void VehicleDespawn_Serializes_ToTheExpectedBytes()
        {
            var message = new VehicleDespawnMessage(7, VehicleDespawnReason.WorldReset);

            Span<byte> buffer = stackalloc byte[VehicleDespawnMessage.Size];
            Assert.Equal(VehicleDespawnMessage.Size, message.Write(buffer));
            Assert.Equal(VehicleDespawnHex, Hex.ToHex(buffer));
        }

        [Fact]
        public void VehicleDespawn_Parses_FromTheExpectedBytes()
        {
            Assert.True(VehicleDespawnMessage.TryParse(
                Hex.FromHex(VehicleDespawnHex), out VehicleDespawnMessage message));

            Assert.Equal(7, message.VehicleId);
            Assert.Equal(VehicleDespawnReason.WorldReset, message.Reason);
        }

        // ---------------------------------------------- S_PROJECTILE_SPAWN 0x4F (20 B)
        //   u16 projectileId 7              -> 07 00
        //   u16 ownerActorId 9              -> 09 00
        //   u8  kind         Rocket(1)      -> 01
        //   i16 originX/Y/Z  16 / 32 / 48   -> 10 00 · 20 00 · 30 00
        //   i16 velX/Y/Z     256 / -128 / 0 -> 00 01 · 80 FF · 00 00
        //   u16 spawnTick    1234           -> D2 04
        //   u8  remainingDs  20 (2.0 s)     -> 14
        private const string ProjectileSpawnHex =
            "07 00 09 00 01 10 00 20 00 30 00 00 01 80 FF 00 00 D2 04 14";

        [Fact]
        public void ProjectileSpawn_Serializes_ToTheExpectedBytes()
        {
            var message = new ProjectileSpawnMessage(
                projectileId: 7, ownerActorId: 9, kind: ProjectileKind.Rocket,
                originX: 16, originY: 32, originZ: 48,
                velX: 256, velY: -128, velZ: 0, spawnTick: 1234,
                remainingLifetimeDeciseconds: 20);

            Span<byte> buffer = stackalloc byte[ProjectileSpawnMessage.Size];
            Assert.Equal(ProjectileSpawnMessage.Size, message.Write(buffer));
            Assert.Equal(ProjectileSpawnHex, Hex.ToHex(buffer));
        }

        [Fact]
        public void ProjectileSpawn_Parses_FromTheExpectedBytes()
        {
            Assert.True(ProjectileSpawnMessage.TryParse(
                Hex.FromHex(ProjectileSpawnHex), out ProjectileSpawnMessage message));

            Assert.Equal(7, message.ProjectileId);
            Assert.Equal(20, message.RemainingLifetimeDeciseconds);
            Assert.Equal(9, message.OwnerActorId);
            Assert.Equal(ProjectileKind.Rocket, message.Kind);
            Assert.Equal(16, message.OriginX);
            Assert.Equal(32, message.OriginY);
            Assert.Equal(48, message.OriginZ);
            Assert.Equal(256, message.VelX);
            Assert.Equal(-128, message.VelY);
            Assert.Equal(0, message.VelZ);
            Assert.Equal(1234u, message.SpawnTick);
        }

        // -------------------------------------------------- S_SEAT_CHANGE 0x50 (6 B)
        //   u16 actorId 12 -> 0C 00 · u16 vehicleId 7 -> 07 00
        //   u8  seatIndex 1 -> 01 · u8 result Entered(0) -> 00
        private const string SeatChangeHex = "0C 00 07 00 01 00";

        [Fact]
        public void SeatChange_Serializes_ToTheExpectedBytes()
        {
            var message = new SeatChangeMessage(12, 7, 1, SeatChangeResult.Entered);

            Span<byte> buffer = stackalloc byte[SeatChangeMessage.Size];
            Assert.Equal(SeatChangeMessage.Size, message.Write(buffer));
            Assert.Equal(SeatChangeHex, Hex.ToHex(buffer));
        }

        [Fact]
        public void SeatChange_Parses_FromTheExpectedBytes()
        {
            Assert.True(SeatChangeMessage.TryParse(
                Hex.FromHex(SeatChangeHex), out SeatChangeMessage message));

            Assert.Equal(12, message.ActorId);
            Assert.Equal(7, message.VehicleId);
            Assert.Equal(1, message.SeatIndex);
            Assert.Equal(SeatChangeResult.Entered, message.Result);
        }

        // ---------------------------------------------- S_VEHICLE_SNAPSHOT 0x4C (43 B)
        //
        // Deliberately MIXED: one 30-byte full entry followed by one 4-byte stationary one.
        // A body of uniform entries would still decode correctly with EntrySize off by a
        // constant; only a mixed body forces the parser to land on the second entry at the
        // right offset.
        //
        //   header    u32 serverTick   100 -> 64 00 00 00
        //             u32 baselineTick  99 -> 63 00 00 00
        //             u8  vehicleCount   2 -> 02
        //   entry 1   u16 vehicleId      1 -> 01 00
        //             u16 changeMask  Full = 0x00FF -> FF 00
        //             pos      i16 x3  256 / 512 / 768   -> 00 01 · 00 02 · 00 03
        //             rotation u32     0x3FF00000        -> 00 00 F0 3F
        //             linVel   i16 x3  100 / -100 / 0    -> 64 00 · 9C FF · 00 00
        //             angVel   i8  x3  1 / -1 / 0        -> 01 FF 00
        //             health   u8      200               -> C8
        //             flags    u8      Burning|Airborne = 0x0A -> 0A
        //             turret   u16+i8  16384 / -32       -> 00 40 · E0
        //             subtype  u8 x2   0x11 / 0x22       -> 11 22
        //   entry 2   u16 vehicleId      2 -> 02 00
        //             u16 changeMask  None -> 00 00        (a vehicle that has not moved)
        private const string VehicleSnapshotHex =
            "64 00 00 00 63 00 00 00 02 "
            + "01 00 FF 00 00 01 00 02 00 03 00 00 F0 3F 64 00 9C FF 00 00 "
            + "01 FF 00 C8 0A 00 40 E0 11 22 "
            + "02 00 00 00";

        private static VehicleSnapshotEntry[] MixedVehicleEntries() => new[]
        {
            new VehicleSnapshotEntry
            {
                VehicleId   = 1,
                ChangeMask  = VehicleField.Full,
                PosX = 256, PosY = 512, PosZ = 768,
                Rotation    = 0x3FF00000u,
                VelX = 100, VelY = -100, VelZ = 0,
                AngVelX = 1, AngVelY = -1, AngVelZ = 0,
                Health      = 200,
                Flags       = VehicleStateFlags.Burning | VehicleStateFlags.Airborne,
                TurretYaw   = 16384,
                TurretPitch = -32,
                SubtypeA    = 0x11,
                SubtypeB    = 0x22,
            },
            new VehicleSnapshotEntry { VehicleId = 2, ChangeMask = VehicleField.None },
        };

        [Fact]
        public void VehicleSnapshot_Serializes_ToTheExpectedBytes()
        {
            VehicleSnapshotEntry[] entries = MixedVehicleEntries();
            var header = new VehicleSnapshotHeader(100, 99, 2);

            Span<byte> buffer = stackalloc byte[128];
            int written = VehicleSnapshotMessage.Write(buffer, in header, entries);

            Assert.Equal(9 + 30 + 4, written);
            Assert.Equal(VehicleSnapshotHex, Hex.ToHex(buffer.Slice(0, written)));
        }

        [Fact]
        public void VehicleSnapshot_Parses_FromTheExpectedBytes()
        {
            byte[] bytes = Hex.FromHex(VehicleSnapshotHex);
            var parsed = new VehicleSnapshotEntry[ProtocolConstants.MAX_VEHICLES];

            Assert.True(VehicleSnapshotMessage.TryParse(
                bytes, parsed, out VehicleSnapshotHeader header, out int count));

            Assert.Equal(100u, header.ServerTick);
            Assert.Equal(99u, header.BaselineTick);
            Assert.False(header.IsFullSnapshot);
            Assert.Equal(2, count);

            Assert.Equal(1, parsed[0].VehicleId);
            Assert.Equal(VehicleField.Full, parsed[0].ChangeMask);
            Assert.Equal(256, parsed[0].PosX);
            Assert.Equal(512, parsed[0].PosY);
            Assert.Equal(768, parsed[0].PosZ);
            Assert.Equal(0x3FF00000u, parsed[0].Rotation);
            Assert.Equal(100, parsed[0].VelX);
            Assert.Equal(-100, parsed[0].VelY);
            Assert.Equal(1, parsed[0].AngVelX);
            Assert.Equal(-1, parsed[0].AngVelY);
            Assert.Equal(200, parsed[0].Health);
            Assert.Equal(
                VehicleStateFlags.Burning | VehicleStateFlags.Airborne, parsed[0].Flags);
            Assert.Equal(16384, parsed[0].TurretYaw);
            Assert.Equal(-32, parsed[0].TurretPitch);
            Assert.Equal(0x11, parsed[0].SubtypeA);
            Assert.Equal(0x22, parsed[0].SubtypeB);

            // The second entry landing here at all is the assertion: a mis-sized full entry
            // would have consumed the wrong number of bytes and read this id out of the middle
            // of the first one.
            Assert.Equal(2, parsed[1].VehicleId);
            Assert.Equal(VehicleField.None, parsed[1].ChangeMask);
        }

        // ------------------------------------- S_SNAPSHOT with SeatInfo, 23-byte entry
        //
        //   header  u32 serverTick 100 -> 64 00 00 00
        //           u32 lastInput   99 -> 63 00 00 00
        //           u32 baselineTick 0 -> 00 00 00 00   (full snapshot)
        //           u8  actorCount   1 -> 01
        //   entry   u16 actorId      5 -> 05 00
        //           u8  changeMask Full = 0xFF -> FF
        //           pos    i16 x3  256 / 512 / 768 -> 00 01 · 00 02 · 00 03
        //           rot    u16+i8  32768 / 10      -> 00 80 · 0A
        //           vel    i8  x3  1 / -1 / 0      -> 01 FF 00
        //           flags  u8      IsAlive|IsSeated = 0x81 -> 81
        //           health u8      100             -> 64
        //           weapon u8+u8   1 / 30          -> 01 1E
        //           team   u8      0               -> 00
        //           seat   u16+u8  vehicleId 7 / seatIndex 2 -> 07 00 · 02
        private const string SeatedActorSnapshotHex =
            "64 00 00 00 63 00 00 00 00 00 00 00 01 "
            + "05 00 FF 00 01 00 02 00 03 00 80 0A 01 FF 00 81 64 01 1E 00 07 00 02";

        [Fact]
        public void SeatedActorEntry_Serializes_ToTwentyThreeBytes()
        {
            var entry = new ActorSnapshotEntry
            {
                ActorId    = 5,
                ChangeMask = SnapshotField.Full,
                PosX = 256, PosY = 512, PosZ = 768,
                Yaw = 32768, Pitch = 10,
                VelX = 1, VelY = -1, VelZ = 0,
                StateFlags = ActorStateFlags.IsAlive | ActorStateFlags.IsSeated,
                Health = 100,
                WeaponId = 1, AmmoInClip = 30,
                Team = 0,
                VehicleId = 7, SeatIndex = 2,
            };

            Assert.Equal(23, SnapshotMessage.EntrySize(SnapshotField.Full));

            var header = new SnapshotHeader(100, 99, 0, 1);
            Span<byte> buffer = stackalloc byte[64];
            int written = SnapshotMessage.Write(buffer, in header, new[] { entry });

            Assert.Equal(SnapshotHeader.Size + 23, written);
            Assert.Equal(SeatedActorSnapshotHex, Hex.ToHex(buffer.Slice(0, written)));
        }

        [Fact]
        public void SeatedActorEntry_Parses_FromTheExpectedBytes()
        {
            byte[] bytes = Hex.FromHex(SeatedActorSnapshotHex);
            var parsed = new ActorSnapshotEntry[ProtocolConstants.MAX_ACTORS];

            Assert.True(SnapshotMessage.TryParse(
                bytes, parsed, out SnapshotHeader header, out int count));

            Assert.Equal(1, count);
            Assert.True(header.IsFullSnapshot);

            Assert.True(parsed[0].Has(SnapshotField.SeatInfo));
            Assert.Equal(7, parsed[0].VehicleId);
            Assert.Equal(2, parsed[0].SeatIndex);
            Assert.True((parsed[0].StateFlags & ActorStateFlags.IsSeated) != 0);
        }

        // ---------------------------------------------------- S_PLAYER_LIST 0x4B (12 B)
        //   u8 count 2
        //   row 1  u8 actorId 5 · u8 len 3 · "Bob"  -> 05 03 42 6F 62
        //   row 2  u8 actorId 9 · u8 len 4 · "Anna" -> 09 04 41 6E 6E 61
        private const string PlayerListHex = "02 05 03 42 6F 62 09 04 41 6E 6E 61";

        [Fact]
        public void PlayerList_Serializes_ToTheExpectedBytes()
        {
            var entries = new[]
            {
                new PlayerListEntry
                {
                    ActorId = 5, Name = System.Text.Encoding.UTF8.GetBytes("Bob"),
                },
                new PlayerListEntry
                {
                    ActorId = 9, Name = System.Text.Encoding.UTF8.GetBytes("Anna"),
                },
            };

            var buffer = new byte[PlayerListMessage.MaxBodySize];
            int written = PlayerListMessage.Write(buffer, entries);

            Assert.Equal(12, written);
            Assert.Equal(PlayerListHex, Hex.ToHex(buffer.AsSpan(0, written)));
        }

        [Fact]
        public void PlayerList_Parses_FromTheExpectedBytes()
        {
            byte[] bytes = Hex.FromHex(PlayerListHex);
            var parsed = new PlayerListEntry[ProtocolConstants.MAX_ACTORS];

            Assert.True(PlayerListMessage.TryParse(
                bytes, 0, bytes.Length, parsed, out int count));

            Assert.Equal(2, count);
            Assert.Equal(5, parsed[0].ActorId);
            Assert.Equal("Bob", PlayerListMessage.NameOf(in parsed[0]));
            Assert.Equal(9, parsed[1].ActorId);
            Assert.Equal("Anna", PlayerListMessage.NameOf(in parsed[1]));
        }

    }
}
