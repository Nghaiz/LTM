using System;
using System.Text;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// The six event messages phase-V3 adds, plus <c>S_PLAYER_LIST</c>.
    /// protocol-spec.md sections 4.10 and 4.11.
    /// </summary>
    /// <remarks>
    /// Every <c>Size</c> is asserted against the sum of its own fields, not against the number
    /// in the constant. A constant that agrees with itself proves nothing; a constant that
    /// agrees with the field table is the contract.
    /// </remarks>
    public class VehicleMessageTests
    {
        [Fact]
        public void EverySizeMatchesItsFieldSum()
        {
            Assert.Equal(4 + 2 + 1 + 1 + 1 + 1 + 2 + 2 + 2, VehicleInputMessage.Size);   // 16
            Assert.Equal(2 + 1 + 1,                         SeatRequestMessage.Size);    // 4
            Assert.Equal(2 + 1 + 1 + 6 + 4 + 1 + 1,         VehicleSpawnMessage.Size);   // 16
            Assert.Equal(2 + 1,                             VehicleDespawnMessage.Size); // 3
            Assert.Equal(2 + 1 + 6 + 6 + 4,                 ProjectileSpawnMessage.Size);// 19
            Assert.Equal(2 + 2 + 1 + 1,                     SeatChangeMessage.Size);     // 6
        }

        [Fact]
        public void VehicleInputRoundTrips()
        {
            var message = new VehicleInputMessage(
                tick: 1234, vehicleId: 7,
                throttle: 127, steer: -64, pitchAxis: 0, auxAxis: -1,
                turretYaw: 32768, turretPitch: -4096, buttons: 1);

            Span<byte> buffer = stackalloc byte[VehicleInputMessage.Size];
            Assert.Equal(VehicleInputMessage.Size, message.Write(buffer));

            Assert.True(VehicleInputMessage.TryParse(buffer, out VehicleInputMessage parsed));
            Assert.Equal(1234u, parsed.Tick);
            Assert.Equal(7, parsed.VehicleId);
            Assert.Equal(127, parsed.Throttle);
            Assert.Equal(-64, parsed.Steer);
            Assert.Equal(0, parsed.PitchAxis);
            Assert.Equal(-1, parsed.AuxAxis);
            Assert.Equal(32768, parsed.TurretYaw);
            Assert.Equal(-4096, parsed.TurretPitch);
            Assert.Equal(1, parsed.Buttons);
        }

        [Fact]
        public void SeatRequestRoundTrips()
        {
            var message = new SeatRequestMessage(7, 2, SeatAction.Enter);

            Span<byte> buffer = stackalloc byte[SeatRequestMessage.Size];
            Assert.Equal(SeatRequestMessage.Size, message.Write(buffer));

            Assert.True(SeatRequestMessage.TryParse(buffer, out SeatRequestMessage parsed));
            Assert.Equal(7, parsed.VehicleId);
            Assert.Equal(2, parsed.SeatIndex);
            Assert.Equal(SeatAction.Enter, parsed.Action);
        }

        [Fact]
        public void VehicleSpawnRoundTrips()
        {
            var message = new VehicleSpawnMessage(
                vehicleId: 7, kind: VehicleKind.Tank, networkTypeId: VehicleIds.TANK,
                posX: 256, posY: -256, posZ: 0, rotation: 0xC0000000u,
                seatCount: 3, flags: 0);

            Span<byte> buffer = stackalloc byte[VehicleSpawnMessage.Size];
            Assert.Equal(VehicleSpawnMessage.Size, message.Write(buffer));

            Assert.True(VehicleSpawnMessage.TryParse(buffer, out VehicleSpawnMessage parsed));
            Assert.Equal(7, parsed.VehicleId);
            Assert.Equal(VehicleKind.Tank, parsed.Kind);
            Assert.Equal(VehicleIds.TANK, parsed.NetworkTypeId);
            Assert.Equal(256, parsed.PosX);
            Assert.Equal(-256, parsed.PosY);
            Assert.Equal(0, parsed.PosZ);
            Assert.Equal(0xC0000000u, parsed.Rotation);
            Assert.Equal(3, parsed.SeatCount);
            Assert.Equal(0, parsed.Flags);
        }

        [Fact]
        public void VehicleDespawnRoundTrips()
        {
            var message = new VehicleDespawnMessage(7, VehicleDespawnReason.WorldReset);

            Span<byte> buffer = stackalloc byte[VehicleDespawnMessage.Size];
            Assert.Equal(VehicleDespawnMessage.Size, message.Write(buffer));

            Assert.True(VehicleDespawnMessage.TryParse(buffer, out VehicleDespawnMessage parsed));
            Assert.Equal(7, parsed.VehicleId);
            Assert.Equal(VehicleDespawnReason.WorldReset, parsed.Reason);
        }

        [Fact]
        public void ProjectileSpawnRoundTrips()
        {
            var message = new ProjectileSpawnMessage(
                ownerActorId: 9, kind: ProjectileKind.Rocket,
                originX: 16, originY: 32, originZ: 48,
                velX: 256, velY: -128, velZ: 0, spawnTick: 1234);

            Span<byte> buffer = stackalloc byte[ProjectileSpawnMessage.Size];
            Assert.Equal(ProjectileSpawnMessage.Size, message.Write(buffer));

            Assert.True(ProjectileSpawnMessage.TryParse(buffer, out ProjectileSpawnMessage parsed));
            Assert.Equal(9, parsed.OwnerActorId);
            Assert.Equal(ProjectileKind.Rocket, parsed.Kind);
            Assert.Equal(16, parsed.OriginX);
            Assert.Equal(32, parsed.OriginY);
            Assert.Equal(48, parsed.OriginZ);
            Assert.Equal(256, parsed.VelX);
            Assert.Equal(-128, parsed.VelY);
            Assert.Equal(0, parsed.VelZ);
            Assert.Equal(1234u, parsed.SpawnTick);
        }

        [Fact]
        public void SeatChangeRoundTripsIncludingARejection()
        {
            var granted = new SeatChangeMessage(12, 7, 1, SeatChangeResult.Entered);
            var refused = new SeatChangeMessage(12, 0, 1, SeatChangeResult.RejectedOccupied);

            Span<byte> buffer = stackalloc byte[SeatChangeMessage.Size];

            Assert.Equal(SeatChangeMessage.Size, granted.Write(buffer));
            Assert.True(SeatChangeMessage.TryParse(buffer, out SeatChangeMessage parsedGranted));
            Assert.Equal(7, parsedGranted.VehicleId);
            Assert.Equal(SeatChangeResult.Entered, parsedGranted.Result);

            Assert.Equal(SeatChangeMessage.Size, refused.Write(buffer));
            Assert.True(SeatChangeMessage.TryParse(buffer, out SeatChangeMessage parsedRefused));
            Assert.Equal(0, parsedRefused.VehicleId);
            Assert.Equal(SeatChangeResult.RejectedOccupied, parsedRefused.Result);
        }

        [Fact]
        public void EveryParserRefusesItsOwnBodyOneByteShort()
        {
            Assert.False(VehicleInputMessage.TryParse(
                new byte[VehicleInputMessage.Size - 1], out VehicleInputMessage a));
            Assert.Equal(default, a.Tick);

            Assert.False(SeatRequestMessage.TryParse(
                new byte[SeatRequestMessage.Size - 1], out SeatRequestMessage b));
            Assert.Equal(default, b.VehicleId);

            Assert.False(VehicleSpawnMessage.TryParse(
                new byte[VehicleSpawnMessage.Size - 1], out VehicleSpawnMessage c));
            Assert.Equal(default, c.VehicleId);

            Assert.False(VehicleDespawnMessage.TryParse(
                new byte[VehicleDespawnMessage.Size - 1], out VehicleDespawnMessage d));
            Assert.Equal(default, d.VehicleId);

            Assert.False(ProjectileSpawnMessage.TryParse(
                new byte[ProjectileSpawnMessage.Size - 1], out ProjectileSpawnMessage e));
            Assert.Equal(default, e.OwnerActorId);

            Assert.False(SeatChangeMessage.TryParse(
                new byte[SeatChangeMessage.Size - 1], out SeatChangeMessage f));
            Assert.Equal(default, f.ActorId);
        }

        // ------------------------------------------------------------- S_PLAYER_LIST

        [Fact]
        public void PlayerListRoundTripsNames()
        {
            byte[] bob  = Encoding.UTF8.GetBytes("Bob");
            byte[] anna = Encoding.UTF8.GetBytes("Anna");

            var entries = new[]
            {
                new PlayerListEntry { ActorId = 5, Name = bob },
                new PlayerListEntry { ActorId = 9, Name = anna },
            };

            Assert.Equal(1 + (2 + 3) + (2 + 4), PlayerListMessage.SizeFor(entries));

            var buffer = new byte[PlayerListMessage.MaxBodySize];
            int written = PlayerListMessage.Write(buffer, entries);
            Assert.Equal(12, written);

            var parsed = new PlayerListEntry[ProtocolConstants.MAX_ACTORS];
            Assert.True(PlayerListMessage.TryParse(buffer, 0, written, parsed, out int count));
            Assert.Equal(2, count);

            Assert.Equal(5, parsed[0].ActorId);
            Assert.Equal("Bob", PlayerListMessage.NameOf(in parsed[0]));
            Assert.Equal(9, parsed[1].ActorId);
            Assert.Equal("Anna", PlayerListMessage.NameOf(in parsed[1]));
        }

        [Fact]
        public void AnOverlongNameIsRefusedRatherThanTruncated()
        {
            // Cutting UTF-8 at a fixed byte count splits multi-byte code points, and the result
            // renders as replacement characters rather than as a shorter name. The caller clips
            // at a character boundary, where it still knows what the characters are.
            var entries = new[]
            {
                new PlayerListEntry
                {
                    ActorId = 1,
                    Name = new byte[PlayerListMessage.MaxNameBytes + 1],
                },
            };

            Assert.Equal(-1, PlayerListMessage.Write(new byte[256], entries));
        }

        [Fact]
        public void AnOverlongNameLengthOnTheWireIsRefused()
        {
            // Hand-built: count 1, actorId 1, nameLength 200, no name bytes behind it.
            var hostile = new byte[] { 0x01, 0x01, 0xC8 };

            var parsed = new PlayerListEntry[8];
            Assert.False(PlayerListMessage.TryParse(hostile, 0, hostile.Length, parsed, out int count));
            Assert.Equal(0, count);
        }

        [Fact]
        public void AnOffsetAndLengthThatOverflowAreRefusedRatherThanThrown()
        {
            // `offset + length > src.Length` is int arithmetic and WRAPS: offset 2 with length
            // int.MaxValue sums to -2147483647, which passes any `>` test, and the Span
            // constructor behind it then throws. A TryParse that throws is the one thing this
            // whole IO layer is built to avoid — conventions.md § 3.2, exceptions are not for
            // routine conditions, and a truncated packet on a UDP socket is routine.
            var src = new byte[100];
            var entries = new PlayerListEntry[8];

            Assert.False(PlayerListMessage.TryParse(src, 2, int.MaxValue, entries, out int a));
            Assert.Equal(0, a);

            Assert.False(PlayerListMessage.TryParse(src, int.MaxValue, 2, entries, out int b));
            Assert.Equal(0, b);

            Assert.False(PlayerListMessage.TryParse(src, -1, 10, entries, out int c));
            Assert.Equal(0, c);

            Assert.False(PlayerListMessage.TryParse(src, 10, -1, entries, out int d));
            Assert.Equal(0, d);

            Assert.False(PlayerListMessage.TryParse(src, 99, 2, entries, out int e));
            Assert.Equal(0, e);

            Assert.False(PlayerListMessage.TryParse(null!, 0, 0, entries, out int f));
            Assert.Equal(0, f);
        }

        [Fact]
        public void TheWorstCasePlayerListFitsOneUnfragmentedPayload()
        {
            Assert.Equal(1 + 64 * 18, PlayerListMessage.MaxBodySize);
            Assert.True(PlayerListMessage.MaxBodySize < ProtocolConstants.MAX_CHANNEL_PAYLOAD);
        }

        [Fact]
        public void TheActorIdByteIsWideEnoughForTheActorIdSpace()
        {
            // PlayerListEntry.ActorId is a u8 while every other message uses a u16. Safe only
            // because actorIds are allocated from 0..MAX_ACTORS-1 (section 4.3.1). Pinned here
            // so raising MAX_ACTORS past 256 fails the build instead of silently truncating an
            // id and naming the wrong player on the scoreboard.
            Assert.True(ProtocolConstants.MAX_ACTORS <= 256);
        }
    }
}
