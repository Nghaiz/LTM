using System;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.Net.Protocol.Tests.Conformance
{
    /// <summary>
    /// The three messages protocol-spec.md section 4.1 declares and section 4 never lays out:
    /// S_SPAWN_ACTOR (0x41), S_DESPAWN_ACTOR (0x42) and S_EXPLOSION (0x4A).
    /// </summary>
    /// <remarks>
    /// These layouts are defined in <c>ActorLifecycleMessages.cs</c>, not in the frozen spec —
    /// the same situation, and the same handling, as C_ACK_BASELINE in phase 01. The tests
    /// exist so that whatever the section 2 review settles on, a change to the layout is a
    /// failing test rather than a silent wire break.
    /// </remarks>
    public sealed class ActorLifecycleMessageTests
    {
        // ------------------------------------------------------------------ S_SPAWN_ACTOR

        [Fact]
        public void SpawnRoundTripsEveryField()
        {
            var original = new SpawnActorMessage(
                actorId: 4242, team: 1, flags: SpawnFlags.IsBot | SpawnFlags.IsLocalPlayer,
                posX: -1000, posY: 250, posZ: 32767, yaw: 40000, health: 87, weaponId: 5);

            Span<byte> buffer = stackalloc byte[SpawnActorMessage.Size];
            Assert.Equal(SpawnActorMessage.Size, original.Write(buffer));
            Assert.True(SpawnActorMessage.TryParse(buffer, out SpawnActorMessage parsed));

            Assert.Equal(original.ActorId, parsed.ActorId);
            Assert.Equal(original.Team, parsed.Team);
            Assert.Equal(original.Flags, parsed.Flags);
            Assert.Equal(original.PosX, parsed.PosX);
            Assert.Equal(original.PosY, parsed.PosY);
            Assert.Equal(original.PosZ, parsed.PosZ);
            Assert.Equal(original.Yaw, parsed.Yaw);
            Assert.Equal(original.Health, parsed.Health);
            Assert.Equal(original.WeaponId, parsed.WeaponId);
        }

        [Fact]
        public void SpawnIsFourteenBytes()
        {
            Assert.Equal(14, SpawnActorMessage.Size);
        }

        [Fact]
        public void SpawnFlagsDecodeIndependently()
        {
            var bot = new SpawnActorMessage(1, 0, SpawnFlags.IsBot, 0, 0, 0, 0, 100, 0);
            var self = new SpawnActorMessage(1, 0, SpawnFlags.IsLocalPlayer, 0, 0, 0, 0, 100, 0);

            Assert.True(bot.IsBot);
            Assert.False(bot.IsLocalPlayer);
            Assert.False(self.IsBot);
            Assert.True(self.IsLocalPlayer);
        }

        [Fact]
        public void SpawnNegativePositionsSurviveAsSigned()
        {
            // Position is i16 quantized about the origin, so half the map is negative. Reading
            // it back unsigned would put every actor west of the origin at ~+2000 m.
            var original = new SpawnActorMessage(1, 0, SpawnFlags.None, -32768, -1, -500, 0, 100, 0);

            Span<byte> buffer = stackalloc byte[SpawnActorMessage.Size];
            original.Write(buffer);
            Assert.True(SpawnActorMessage.TryParse(buffer, out SpawnActorMessage parsed));

            Assert.Equal(-32768, parsed.PosX);
            Assert.Equal(-1, parsed.PosY);
            Assert.Equal(-500, parsed.PosZ);
        }

        [Fact]
        public void SpawnRefusesATooSmallBuffer()
        {
            var message = new SpawnActorMessage(1, 0, SpawnFlags.None, 0, 0, 0, 0, 100, 0);
            Span<byte> tooSmall = stackalloc byte[SpawnActorMessage.Size - 1];

            Assert.Equal(-1, message.Write(tooSmall));
        }

        [Fact]
        public void SpawnRefusesATruncatedPacket()
        {
            Span<byte> truncated = stackalloc byte[SpawnActorMessage.Size - 1];

            Assert.False(SpawnActorMessage.TryParse(truncated, out _));
        }

        [Fact]
        public void SpawnQuantizedPositionSurvivesTheRoundTrip()
        {
            short packed = Quantize.PackPos(123.45f);
            var message = new SpawnActorMessage(1, 0, SpawnFlags.None, packed, 0, 0, 0, 100, 0);

            Span<byte> buffer = stackalloc byte[SpawnActorMessage.Size];
            message.Write(buffer);
            SpawnActorMessage.TryParse(buffer, out SpawnActorMessage parsed);

            // Within one 6.25 cm quantum of where it started.
            // Against the quantizer's real bound, not a decimal place. At 6.25 cm steps the
            // round-trip error is up to 3.1 cm, which straddles the first decimal of 123.45 --
            // so the old assertion passed on where the window happened to sit rather than on
            // the round trip, and X-53's move made that visible.
            Assert.True(Math.Abs(Quantize.UnpackPos(parsed.PosX) - 123.45f) < 0.07f);
        }

        // ------------------------------------------------------------------ S_DESPAWN_ACTOR

        [Fact]
        public void DespawnRoundTrips()
        {
            var original = new DespawnActorMessage(4242, DespawnReason.Destroyed);

            Span<byte> buffer = stackalloc byte[DespawnActorMessage.Size];
            Assert.Equal(DespawnActorMessage.Size, original.Write(buffer));
            Assert.True(DespawnActorMessage.TryParse(buffer, out DespawnActorMessage parsed));

            Assert.Equal(4242, parsed.ActorId);
            Assert.Equal(DespawnReason.Destroyed, parsed.Reason);
        }

        [Fact]
        public void DespawnIsThreeBytes()
        {
            Assert.Equal(3, DespawnActorMessage.Size);
        }

        [Theory]
        [InlineData(DespawnReason.Left)]
        [InlineData(DespawnReason.Destroyed)]
        [InlineData(DespawnReason.Culled)]
        public void EveryDespawnReasonSurvives(DespawnReason reason)
        {
            Span<byte> buffer = stackalloc byte[DespawnActorMessage.Size];
            new DespawnActorMessage(7, reason).Write(buffer);

            Assert.True(DespawnActorMessage.TryParse(buffer, out DespawnActorMessage parsed));
            Assert.Equal(reason, parsed.Reason);
        }

        [Fact]
        public void DespawnRefusesATruncatedPacket()
        {
            Span<byte> truncated = stackalloc byte[2];

            Assert.False(DespawnActorMessage.TryParse(truncated, out _));
        }

        // ------------------------------------------------------------------ S_EXPLOSION

        [Fact]
        public void ExplosionRoundTripsEveryField()
        {
            var original = new ExplosionMessage(
                sourceActorId: 77, posX: -900, posY: 40, posZ: 1200,
                radiusMetres: 12, kind: ExplosionKind.Rocket);

            Span<byte> buffer = stackalloc byte[ExplosionMessage.Size];
            Assert.Equal(ExplosionMessage.Size, original.Write(buffer));
            Assert.True(ExplosionMessage.TryParse(buffer, out ExplosionMessage parsed));

            Assert.Equal(77, parsed.SourceActorId);
            Assert.Equal(-900, parsed.PosX);
            Assert.Equal(40, parsed.PosY);
            Assert.Equal(1200, parsed.PosZ);
            Assert.Equal(12, parsed.RadiusMetres);
            Assert.Equal(ExplosionKind.Rocket, parsed.Kind);
        }

        [Fact]
        public void ExplosionIsTenBytes()
        {
            Assert.Equal(10, ExplosionMessage.Size);
        }

        [Fact]
        public void AnEnvironmentalExplosionUsesTheSameSentinelAsDeath()
        {
            // One "no killer" sentinel across the protocol, not two.
            var message = new ExplosionMessage(
                DeathMessage.EnvironmentKiller, 0, 0, 0, 5, ExplosionKind.Environment);

            Span<byte> buffer = stackalloc byte[ExplosionMessage.Size];
            message.Write(buffer);
            ExplosionMessage.TryParse(buffer, out ExplosionMessage parsed);

            Assert.Equal(DeathMessage.EnvironmentKiller, parsed.SourceActorId);
        }

        [Fact]
        public void ExplosionRefusesATruncatedPacket()
        {
            Span<byte> truncated = stackalloc byte[ExplosionMessage.Size - 1];

            Assert.False(ExplosionMessage.TryParse(truncated, out _));
        }

        // ------------------------------------------------------------------ C_SPAWN_REQUEST

        [Fact]
        public void SpawnRequestRoundTripsEveryField()
        {
            var original = new SpawnRequestMessage(
                primary: 1, secondary: 7, gear1: 15, gear2: 0, gear3: 3, spawnPointIndex: 4);

            Span<byte> buffer = stackalloc byte[SpawnRequestMessage.Size];
            Assert.Equal(SpawnRequestMessage.Size, original.Write(buffer));
            Assert.True(SpawnRequestMessage.TryParse(buffer, out SpawnRequestMessage parsed));

            Assert.Equal(original.Primary, parsed.Primary);
            Assert.Equal(original.Secondary, parsed.Secondary);
            Assert.Equal(original.Gear1, parsed.Gear1);
            Assert.Equal(original.Gear2, parsed.Gear2);
            Assert.Equal(original.Gear3, parsed.Gear3);
            Assert.Equal(original.SpawnPointIndex, parsed.SpawnPointIndex);
        }

        [Fact]
        public void SpawnRequestIsSixBytes()
        {
            Assert.Equal(6, SpawnRequestMessage.Size);
        }

        [Fact]
        public void SpawnRequestDefaultsToNoSpawnPointPreference()
        {
            // The constructor's default parameter is what every sender writes today -- the
            // minimap-driven spawn choice is not yet wired across the network (see the type's
            // own remark) -- so this is the shape every real C_SPAWN_REQUEST currently has.
            var message = new SpawnRequestMessage(primary: 1, secondary: 0, gear1: 0, gear2: 0, gear3: 0);

            Assert.Equal(SpawnRequestMessage.NoSpawnPointPreference, message.SpawnPointIndex);
        }

        [Fact]
        public void SpawnRequestZeroSlotMeansEmptyNotWeaponZero()
        {
            // 0 is WeaponManager's reserved "no/unknown weapon" id (protocol-spec.md § 4.8), so a
            // slot the client left unset must round-trip as 0, not silently substitute a real id.
            var message = new SpawnRequestMessage(primary: 1, secondary: 0, gear1: 0, gear2: 0, gear3: 0);

            Span<byte> buffer = stackalloc byte[SpawnRequestMessage.Size];
            message.Write(buffer);
            Assert.True(SpawnRequestMessage.TryParse(buffer, out SpawnRequestMessage parsed));

            Assert.Equal(0, parsed.Secondary);
            Assert.Equal(0, parsed.Gear1);
            Assert.Equal(0, parsed.Gear2);
            Assert.Equal(0, parsed.Gear3);
        }

        [Fact]
        public void SpawnRequestRefusesATooSmallBuffer()
        {
            var message = new SpawnRequestMessage(1, 0, 0, 0, 0);
            Span<byte> tooSmall = stackalloc byte[SpawnRequestMessage.Size - 1];

            Assert.Equal(-1, message.Write(tooSmall));
        }

        [Fact]
        public void SpawnRequestRefusesATruncatedPacket()
        {
            // The v7 shape this replaces: an EMPTY body. A v7 client talking to this v8 parser
            // must be counted as malformed rather than silently accepted -- see protocol-spec.md
            // § 4.14 and the PROTOCOL_VERSION bump this message's own row records.
            Assert.False(SpawnRequestMessage.TryParse(ReadOnlySpan<byte>.Empty, out _));
        }

        // ------------------------------------------------------------------ msgType table

        [Fact]
        public void TheMessageTypesMatchTheSpecTable()
        {
            // These three ARE in the frozen spec (section 4.1's table) even though their bodies
            // are not. The ids must not drift.
            Assert.Equal(0x41, (byte)ServerMessageType.SpawnActor);
            Assert.Equal(0x42, (byte)ServerMessageType.DespawnActor);
            Assert.Equal(0x4A, (byte)ServerMessageType.Explosion);
        }

        [Fact]
        public void TheseLayoutsAreStillNotWhatMovedTheProtocolVersion()
        {
            // Documenting an unspecified message is not changing a specified one, so these three
            // layouts did not move the version — the phase-01 precedent for C_ACK_BASELINE,
            // applied again on purpose. Pinned so a future bump has to be deliberate; each one
            // that lands is recorded here with what actually caused it.
            //
            //   v2  the channel envelope (protocol-spec § 5.1) and the widened CONNECT_RESPONSE.
            //   v3  the vehicle wire (§ 4.10): six new opcodes, a second entity stream, and
            //       SnapshotField.SeatInfo finished on the actor entry. S_EXPLOSION's layout was
            //       not touched by any of it and still is not what moved the number.
            //   v10 the actor entry's weapon field (§ 4.3) went 2 -> 5 bytes and MAX_VEHICLES
            //       went 16 -> 24. S_SPAWN_ACTOR, S_DESPAWN_ACTOR and S_EXPLOSION all sit
            //       outside the snapshot entry, so none of their three layouts moved.
            Assert.Equal(10, ProtocolConstants.PROTOCOL_VERSION);
        }
    }
}
