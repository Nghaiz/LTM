using System;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// protocol-spec.md § 4.3 and § 10.1: the v10 weapon field, byte for byte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every expected string in this file was written from the spec's byte table by hand, not
    /// captured from this implementation. A round-trip test agrees with itself when both the
    /// writer and the reader are wrong in the same direction — which is exactly the failure a
    /// reserve field is prone to, because a byte-swapped <c>u16</c> round-trips perfectly and
    /// only shows up as a HUD reading 8,961 instead of 291.
    /// </para>
    /// <para>
    /// The reserves used here are chosen for that reason: 0x0123 has two different bytes, so a
    /// little-endian mistake cannot hide, and the two sentinels sit one apart so a stray
    /// <c>(ushort)(short)-1</c> cast swapping no-resupply for infinite is a red test rather
    /// than a bazooka that refuses to reload.
    /// </para>
    /// </remarks>
    public class WeaponFieldTests
    {
        private const long T0 = 1_000_000L;   // arbitrary monotonic clock origin

        // ------------------------------------------------------------ 1. hard-coded hex
        //
        //   header  u32 serverTick 7 -> 07 00 00 00
        //           u32 lastInput  6 -> 06 00 00 00
        //           u32 baseline   0 -> 00 00 00 00
        //           u8  actorCount 1 -> 01
        //   entry   u16 actorId   11 -> 0B 00
        //           u8  changeMask Weapon only = 0x20 -> 20
        //           u8  weaponId          3 -> 03
        //           u8  ammoInClip        7 -> 07
        //           u16 spareAmmo    0x0123 -> 23 01   (little-endian, two distinct bytes)
        //           u8  weaponStateFlags Reloading -> 01
        private const string WeaponOnlyHex =
            "07 00 00 00 06 00 00 00 00 00 00 00 01 0B 00 20 03 07 23 01 01";

        [Fact]
        public void AWeaponOnlyEntrySerializesToTheExpectedBytes()
        {
            var entry = new ActorSnapshotEntry
            {
                ActorId          = 11,
                ChangeMask       = SnapshotField.Weapon,
                WeaponId         = 3,
                AmmoInClip       = 7,
                SpareAmmoEncoded = 0x0123,
                WeaponStateFlags = WeaponStateFlags.Reloading,
            };

            var header = new SnapshotHeader(7, 6, 0, 1);
            Span<byte> buffer = stackalloc byte[64];
            int written = SnapshotMessage.Write(buffer, in header, new[] { entry });

            // 13 header + 3 entry header + 5 weapon bytes.
            Assert.Equal(SnapshotHeader.Size + 3 + 5, written);
            Assert.Equal(21, written);
            Assert.Equal(WeaponOnlyHex, Hex.ToHex(buffer.Slice(0, written)));
        }

        [Fact]
        public void AWeaponOnlyEntryParsesFromTheExpectedBytes()
        {
            var parsed = new ActorSnapshotEntry[ProtocolConstants.MAX_ACTORS];
            Assert.True(SnapshotMessage.TryParse(
                Hex.FromHex(WeaponOnlyHex), parsed, out SnapshotHeader header, out int count));

            Assert.Equal(1, count);
            Assert.True(header.IsFullSnapshot);

            ActorSnapshotEntry e = parsed[0];
            Assert.Equal(11, e.ActorId);
            Assert.Equal(SnapshotField.Weapon, e.ChangeMask);
            Assert.Equal(3, e.WeaponId);
            Assert.Equal(7, e.AmmoInClip);

            // 0x0123 and not 0x2301. The two bytes differ, so this assertion fails on a
            // big-endian reader rather than passing by symmetry.
            Assert.Equal(0x0123, e.SpareAmmoEncoded);
            Assert.Equal(291, e.SpareAmmoEncoded);
            Assert.Equal(WeaponStateFlags.Reloading, e.WeaponStateFlags);
        }

        // ------------------------------------------------- 2. finite and both sentinels

        [Theory]
        [InlineData(0, 0x0000)]
        [InlineData(1, 0x0001)]
        [InlineData(291, 0x0123)]
        [InlineData(SpareAmmo.MaxFiniteRounds, 0xFFFD)]
        public void AFiniteReserveRoundTripsThroughTheCodecAndTheWire(int rounds, int encoded)
        {
            SpareAmmo reserve = SpareAmmo.Finite(rounds);
            Assert.Equal((ushort)encoded, reserve.Encode());
            Assert.Equal(reserve, SpareAmmo.Decode(reserve.Encode()));
            Assert.Equal(SpareAmmoKind.Finite, SpareAmmo.Decode(reserve.Encode()).Kind);
            Assert.Equal(rounds, SpareAmmo.Decode(reserve.Encode()).Rounds);

            Assert.Equal(reserve, SpareAmmo.Decode(WriteAndReadBackReserve(reserve.Encode())));
        }

        // The two sentinels as literal bytes rather than as a round-trip. This is the pair a
        // stray (ushort)(short)-1 would silently swap: the model spells no-resupply -1 and
        // infinite -2, a pool's Remaining spells infinite -1, and a round-trip through a
        // writer and reader that both made the same substitution passes.
        private const string NoResupplyEntryHex =
            "07 00 00 00 06 00 00 00 00 00 00 00 01 0B 00 20 03 00 FE FF 00";

        private const string InfiniteEntryHex =
            "07 00 00 00 06 00 00 00 00 00 00 00 01 0B 00 20 03 00 FF FF 00";

        [Fact]
        public void NoResupplyIsFFFEOnTheWireAndInfiniteIsFFFF()
        {
            Assert.Equal(0xFFFE, SpareAmmo.NoResupply.Encode());
            Assert.Equal(0xFFFF, SpareAmmo.Infinite.Encode());

            Assert.Equal(SpareAmmoKind.NoResupply, SpareAmmo.Decode(0xFFFE).Kind);
            Assert.Equal(SpareAmmoKind.Infinite, SpareAmmo.Decode(0xFFFF).Kind);

            // The model's two spellings, which disagree with the pool's, resolved once.
            Assert.Equal(SpareAmmo.NoResupply, SpareAmmo.FromConfigured(-1));
            Assert.Equal(SpareAmmo.Infinite, SpareAmmo.FromConfigured(-2));

            // And a finite reserve one below the sentinel band is still finite, so the two
            // reserved values cost exactly two values and not a range.
            Assert.Equal(SpareAmmoKind.Finite, SpareAmmo.Decode(0xFFFD).Kind);
            Assert.Equal(65533, SpareAmmo.Decode(0xFFFD).Rounds);
        }

        [Theory]
        [InlineData(NoResupplyEntryHex, 0xFFFE, SpareAmmoKind.NoResupply)]
        [InlineData(InfiniteEntryHex, 0xFFFF, SpareAmmoKind.Infinite)]
        public void ASentinelReserveSurvivesARealSnapshotWriteAndParse(
            string expectedHex, int encoded, SpareAmmoKind kind)
        {
            var entry = new ActorSnapshotEntry
            {
                ActorId          = 11,
                ChangeMask       = SnapshotField.Weapon,
                WeaponId         = 3,
                AmmoInClip       = 0,
                SpareAmmoEncoded = (ushort)encoded,
                WeaponStateFlags = WeaponStateFlags.None,
            };

            var header = new SnapshotHeader(7, 6, 0, 1);
            Span<byte> buffer = stackalloc byte[64];
            int written = SnapshotMessage.Write(buffer, in header, new[] { entry });

            Assert.Equal(expectedHex, Hex.ToHex(buffer.Slice(0, written)));

            var parsed = new ActorSnapshotEntry[1];
            Assert.True(SnapshotMessage.TryParse(buffer.Slice(0, written), parsed, out _, out _));
            Assert.Equal((ushort)encoded, parsed[0].SpareAmmoEncoded);
            Assert.Equal(kind, SpareAmmo.Decode(parsed[0].SpareAmmoEncoded).Kind);
        }

        // ------------------------------------------- 6. C_INPUT is untouched by v10
        //
        //   u32 startTick 300 -> 2C 01 00 00
        //   u8  frameCount  3 -> 03
        //   frame  i8 moveX 100 -> 64 · i8 moveZ -100 -> 9C
        //          u16 yaw 0x4000 -> 00 40 · i16 pitch -4096 -> 00 F0
        //          u16 buttons Fire|Reload = bit0|bit2 = 0x0005 -> 05 00
        private const string ThreeFrameInputHex =
            "2C 01 00 00 03 "
            + "64 9C 00 40 00 F0 05 00 "
            + "64 9C 00 40 00 F0 05 00 "
            + "64 9C 00 40 00 F0 05 00";

        [Fact]
        public void AThreeFrameInputIsStillExactlyTwentyNineBytes()
        {
            // v10 widened a snapshot field and raised the vehicle cap. It changed no input
            // byte, and this is the assertion that says so — written against hand-made bytes
            // so that a well-meant "while we are here" edit to InputFrame fails here rather
            // than surfacing as a client whose aim is 90 degrees off on a v10 server.
            Assert.Equal(8, InputFrame.Size);
            Assert.Equal(5, ClientInputMessage.HeaderSize);
            Assert.Equal(29, ClientInputMessage.SizeFor(3));
            Assert.Equal(3, ProtocolConstants.INPUT_REDUNDANCY);

            var frame = new InputFrame(
                moveX: 100, moveZ: -100, yaw: 0x4000, pitch: -4096,
                buttons: InputButtons.Fire | InputButtons.Reload);

            Span<InputFrame> frames = stackalloc InputFrame[3];
            frames[0] = frame;
            frames[1] = frame;
            frames[2] = frame;

            Span<byte> buffer = stackalloc byte[ClientInputMessage.SizeFor(3)];
            int written = ClientInputMessage.Write(buffer, startTick: 300, frames);

            Assert.Equal(29, written);
            Assert.Equal(ThreeFrameInputHex, Hex.ToHex(buffer));

            Span<InputFrame> parsed = stackalloc InputFrame[ClientInputMessage.MaxFrames];
            Assert.True(ClientInputMessage.TryParse(
                Hex.FromHex(ThreeFrameInputHex), parsed, out uint startTick, out int count));

            Assert.Equal(300u, startTick);
            Assert.Equal(3, count);
            for (int i = 0; i < count; i++)
            {
                Assert.Equal(100, parsed[i].MoveX);
                Assert.Equal(-100, parsed[i].MoveZ);
                Assert.Equal(0x4000, parsed[i].Yaw);
                Assert.Equal(-4096, parsed[i].Pitch);
                Assert.Equal(InputButtons.Fire | InputButtons.Reload, parsed[i].Buttons);
            }
        }

        // -------------------------------------------- 5. 64 actors, seated, bit for bit

        [Fact]
        public void ASeatedSixtyFourActorSnapshotIsSixteenSeventySevenAndReassemblesBitForBit()
        {
            byte[] snapshot = BuildSeatedFullSnapshot();

            // 13 + 64 * 26. The join baseline's worst case, and the number every bandwidth
            // figure downstream of § 4.3 is computed from.
            Assert.Equal(SnapshotHeader.Size + ProtocolConstants.MAX_ACTORS * 26, snapshot.Length);
            Assert.Equal(1677, snapshot.Length);
            Assert.True(Fragmenter.NeedsFragmentation(snapshot.Length));

            int count = Fragmenter.FragmentCount(snapshot.Length);
            Assert.Equal(2, count);

            var reassembler = new FragmentReassembler();
            byte[]? completed = null;

            for (byte i = 0; i < count; i++)
            {
                var datagramPayload = new byte[ProtocolConstants.MAX_PAYLOAD];
                int written = Fragmenter.WriteFragmentPayload(
                    datagramPayload, snapshot, groupId: 9, index: i);
                Assert.True(written > 0);

                Assert.True(FragmentHeader.TryParse(datagramPayload, out FragmentHeader header));
                ReadOnlySpan<byte> data = datagramPayload.AsSpan(
                    FragmentHeader.Size, written - FragmentHeader.Size);

                FragmentAddResult result = reassembler.Add(header, data, T0, out byte[]? output);
                if (i < count - 1) Assert.Equal(FragmentAddResult.Buffered, result);
                else
                {
                    Assert.Equal(FragmentAddResult.Completed, result);
                    completed = output;
                }
            }

            Assert.NotNull(completed);

            // The whole buffer, not a field. A per-field comparison would pass on a payload
            // that reassembled with one fragment's worth of bytes at the wrong offset as long
            // as the parser happened to resynchronise.
            Assert.Equal(snapshot, completed);

            // And it still parses, so "bit for bit" is not bit-for-bit nonsense.
            var parsed = new ActorSnapshotEntry[ProtocolConstants.MAX_ACTORS];
            Assert.True(SnapshotMessage.TryParse(completed!, parsed, out _, out int actors));
            Assert.Equal(ProtocolConstants.MAX_ACTORS, actors);
            Assert.Equal(0xFFFF, parsed[1].SpareAmmoEncoded);
        }

        private static byte[] BuildSeatedFullSnapshot()
        {
            var entries = new ActorSnapshotEntry[ProtocolConstants.MAX_ACTORS];
            for (int i = 0; i < entries.Length; i++)
            {
                // Every third actor carries a sentinel reserve, so the seated worst case also
                // exercises the values a naive cast would mangle.
                ushort reserve = (i % 3) switch
                {
                    0 => (ushort)(i * 37),
                    1 => SpareAmmo.InfiniteEncoded,
                    _ => SpareAmmo.NoResupplyEncoded,
                };

                entries[i] = new ActorSnapshotEntry
                {
                    ActorId    = (ushort)(i + 1),
                    ChangeMask = SnapshotField.Full,
                    PosX = (short)(i * 100), PosY = (short)(-i * 50), PosZ = (short)(i * 7),
                    Yaw = (ushort)(i * 1000), Pitch = (sbyte)(i % 90),
                    VelX = (sbyte)(i % 100), VelY = (sbyte)(-(i % 100)), VelZ = 0,
                    StateFlags = ActorStateFlags.IsAlive | ActorStateFlags.IsSeated,
                    Health = (byte)(i % 101),
                    WeaponId = (byte)(i % 12), AmmoInClip = (byte)(i % 31),
                    SpareAmmoEncoded = reserve,
                    WeaponStateFlags = (i % 2) == 0
                        ? WeaponStateFlags.None
                        : WeaponStateFlags.Reloading,
                    Team = (byte)(i % 2),
                    VehicleId = (ushort)((i % ProtocolConstants.MAX_VEHICLES) + 1),
                    SeatIndex = (byte)(i % 4),
                };
            }

            var header = new SnapshotHeader(
                serverTick: 12345, lastProcessedInputTick: 12340,
                baselineTick: 0, actorCount: (byte)entries.Length);

            var buffer = new byte[SnapshotMessage.SizeFor(entries)];
            Assert.Equal(buffer.Length, SnapshotMessage.Write(buffer, header, entries));
            return buffer;
        }

        // ------------------------------------------------ 7. the 24-vehicle worst case

        [Fact]
        public void AFullTwentyFourVehicleSnapshotIsSevenTwentyNineAndFitsOneDatagram()
        {
            Assert.Equal(24, ProtocolConstants.MAX_VEHICLES);

            var entries = new VehicleSnapshotEntry[ProtocolConstants.MAX_VEHICLES];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i].VehicleId  = (ushort)(i + 1);
                entries[i].ChangeMask = VehicleField.Full;
            }

            var header = new VehicleSnapshotHeader(100, 0, (byte)entries.Length);
            var buffer = new byte[ProtocolConstants.MAX_PAYLOAD];
            int written = VehicleSnapshotMessage.Write(buffer, in header, entries);

            Assert.Equal(729, written);
            Assert.Equal(VehicleSnapshotMessage.MaxBodySize, written);

            // The point of a bounded vehicle body: it never fragments, so the actor body is
            // the only elastic thing in the datagram and the budget split stays arithmetic.
            Assert.False(Fragmenter.NeedsFragmentation(written));
            Assert.True(written < ProtocolConstants.MAX_CHANNEL_PAYLOAD);
        }

        // -------------------------------------- 8. a weapon payload that ends mid-field

        [Theory]
        // Ends immediately after weaponId — the clip byte is missing.
        [InlineData("07 00 00 00 06 00 00 00 00 00 00 00 01 0B 00 20 03")]
        // Ends after the clip — no reserve at all.
        [InlineData("07 00 00 00 06 00 00 00 00 00 00 00 01 0B 00 20 03 07")]
        // Ends ONE BYTE into the u16 reserve. This is the one a reader that advances its
        // cursor before checking the bound gets wrong: it reads 0x23 plus whatever follows
        // the buffer and reports a plausible reserve.
        [InlineData("07 00 00 00 06 00 00 00 00 00 00 00 01 0B 00 20 03 07 23")]
        // The reserve is whole but the flags byte is missing.
        [InlineData("07 00 00 00 06 00 00 00 00 00 00 00 01 0B 00 20 03 07 23 01")]
        public void ATruncatedWeaponPayloadIsRefusedRatherThanRead(string truncatedHex)
        {
            byte[] truncated = Hex.FromHex(truncatedHex);
            var entries = new ActorSnapshotEntry[ProtocolConstants.MAX_ACTORS];

            // Refused, and refused by returning false: a parser that threw here would turn a
            // malformed datagram from a hostile peer into a dropped server tick.
            Assert.False(SnapshotMessage.TryParse(truncated, entries, out _, out int count));
            Assert.Equal(0, count);
        }

        [Fact]
        public void ATruncatedWeaponPayloadDoesNotReadPastItsBuffer()
        {
            // The bound is the SLICE, not the array. Handing the parser a short span inside a
            // longer, non-zero array is what distinguishes "stopped at the end of the buffer"
            // from "kept reading and happened to find zeros".
            byte[] backing = new byte[64];
            backing.AsSpan().Fill(0xCC);

            byte[] valid = Hex.FromHex(WeaponOnlyHex);
            valid.CopyTo(backing, 0);

            var entries = new ActorSnapshotEntry[ProtocolConstants.MAX_ACTORS];

            // Full length parses; every prefix that cuts into the weapon field does not.
            Assert.True(SnapshotMessage.TryParse(
                backing.AsSpan(0, valid.Length), entries, out _, out _));

            for (int length = SnapshotHeader.Size + 3; length < valid.Length; length++)
                Assert.False(
                    SnapshotMessage.TryParse(backing.AsSpan(0, length), entries, out _, out _),
                    $"a {length}-byte body ending inside the weapon field was accepted");
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// Puts a reserve through a real snapshot write and parse, rather than through
        /// <see cref="SpareAmmo.Encode"/> alone — the codec being right does not prove the
        /// entry writer hands it the same sixteen bits.
        /// </summary>
        private static ushort WriteAndReadBackReserve(ushort encoded)
        {
            var entry = new ActorSnapshotEntry
            {
                ActorId          = 4,
                ChangeMask       = SnapshotField.Weapon,
                WeaponId         = 1,
                AmmoInClip       = 2,
                SpareAmmoEncoded = encoded,
            };

            Span<byte> buffer = stackalloc byte[64];
            var header = new SnapshotHeader(1, 1, 0, 1);
            int written = SnapshotMessage.Write(buffer, in header, new[] { entry });

            var parsed = new ActorSnapshotEntry[1];
            Assert.True(SnapshotMessage.TryParse(buffer.Slice(0, written), parsed, out _, out _));
            return parsed[0].SpareAmmoEncoded;
        }
    }
}
