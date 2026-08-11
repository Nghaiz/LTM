using System;
using System.Collections.Generic;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// protocol-spec.md section 14, checklist item 9:
    /// "A full 64-actor snapshot fragments correctly and reassembles bit-for-bit".
    /// </summary>
    public class FragmentationTests
    {
        private const long T0 = 1_000_000L;   // arbitrary monotonic clock origin

        /// <summary>
        /// Builds the worst realistic case: every one of the 64 actors present with every
        /// v1 field set, which is what a client receives as its first baseline on join.
        /// </summary>
        private static byte[] BuildFullSnapshot()
        {
            var entries = new ActorSnapshotEntry[ProtocolConstants.MAX_ACTORS];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new ActorSnapshotEntry
                {
                    ActorId    = (ushort)i,
                    ChangeMask = SnapshotField.FullNoSeat,
                    PosX = (short)(i * 100), PosY = (short)(-i * 50), PosZ = (short)(i * 7),
                    Yaw = (ushort)(i * 1000), Pitch = (sbyte)(i % 90),
                    VelX = (sbyte)(i % 100), VelY = (sbyte)(-(i % 100)), VelZ = 0,
                    StateFlags = ActorStateFlags.IsAlive,
                    Health = (byte)(i % 101),
                    WeaponId = (byte)(i % 12), AmmoInClip = (byte)(i % 31),
                    Team = (byte)(i % 2),
                };
            }

            var header = new SnapshotHeader(
                serverTick: 12345, lastProcessedInputTick: 12340,
                baselineTick: 0, actorCount: (byte)entries.Length);

            var buffer = new byte[SnapshotMessage.SizeFor(entries)];
            int written = SnapshotMessage.Write(buffer, header, entries);
            Assert.Equal(buffer.Length, written);
            return buffer;
        }

        [Fact]
        public void AFullSnapshotExceedsOneDatagram()
        {
            byte[] snapshot = BuildFullSnapshot();

            // 13 header + 64 actors * 20 bytes = 1293, over the 1184-byte payload limit.
            Assert.Equal(13 + 64 * 20, snapshot.Length);
            Assert.Equal(1293, snapshot.Length);
            Assert.True(Fragmenter.NeedsFragmentation(snapshot.Length));
        }

        [Fact]
        public void AFullSnapshotSplitsIntoTwoFragments()
        {
            byte[] snapshot = BuildFullSnapshot();

            Assert.Equal(1180, FragmentHeader.PayloadCapacity);
            Assert.Equal(2, Fragmenter.FragmentCount(snapshot.Length));
        }

        /// <summary>The checklist item itself: split, reassemble, compare bit-for-bit.</summary>
        [Fact]
        public void AFullSnapshot_FragmentsAndReassemblesBitForBit()
        {
            byte[] snapshot = BuildFullSnapshot();
            int count = Fragmenter.FragmentCount(snapshot.Length);

            var reassembler = new FragmentReassembler();
            byte[]? completed = null;

            for (byte i = 0; i < count; i++)
            {
                var datagramPayload = new byte[ProtocolConstants.MAX_PAYLOAD];
                int written = Fragmenter.WriteFragmentPayload(
                    datagramPayload, snapshot, groupId: 1, index: i);

                Assert.True(written > 0);
                Assert.True(written <= ProtocolConstants.MAX_PAYLOAD);

                // Parse it back the way the receiver would.
                Assert.True(FragmentHeader.TryParse(datagramPayload, out FragmentHeader header));
                Assert.Equal(1, header.GroupId);
                Assert.Equal(i, header.Index);
                Assert.Equal(count, header.Count);

                ReadOnlySpan<byte> data = datagramPayload.AsSpan(
                    FragmentHeader.Size, written - FragmentHeader.Size);

                FragmentAddResult result = reassembler.Add(header, data, T0, out byte[]? output);

                if (i < count - 1)
                {
                    Assert.Equal(FragmentAddResult.Buffered, result);
                    Assert.Null(output);
                }
                else
                {
                    Assert.Equal(FragmentAddResult.Completed, result);
                    completed = output;
                }
            }

            Assert.NotNull(completed);
            Assert.Equal(snapshot.Length, completed!.Length);
            Assert.Equal(snapshot, completed);          // bit-for-bit
            Assert.Equal(0, reassembler.PendingGroupCount);
        }

        [Fact]
        public void FragmentsArrivingOutOfOrder_StillReassembleCorrectly()
        {
            // UDP gives no ordering guarantee, so the reassembler must not assume the
            // fragments show up in index order.
            byte[] payload = new byte[3000];
            new Random(42).NextBytes(payload);

            int count = Fragmenter.FragmentCount(payload.Length);
            Assert.Equal(3, count);

            var order = new byte[] { 2, 0, 1 };
            var reassembler = new FragmentReassembler();
            byte[]? completed = null;

            foreach (byte index in order)
            {
                Assert.True(Fragmenter.TrySliceFragment(payload, index, out ReadOnlySpan<byte> slice));
                var header = new FragmentHeader(7, index, (byte)count);
                FragmentAddResult result = reassembler.Add(header, slice, T0, out byte[]? output);
                if (output != null) completed = output;

                Assert.NotEqual(FragmentAddResult.Rejected, result);
            }

            Assert.NotNull(completed);
            Assert.Equal(payload, completed);
        }

        [Fact]
        public void ADuplicateFragmentIsIgnored()
        {
            byte[] payload = new byte[2000];
            var reassembler = new FragmentReassembler();

            Assert.True(Fragmenter.TrySliceFragment(payload, 0, out ReadOnlySpan<byte> first));
            var header = new FragmentHeader(1, 0, 2);

            Assert.Equal(FragmentAddResult.Buffered, reassembler.Add(header, first, T0, out _));
            Assert.Equal(FragmentAddResult.Duplicate, reassembler.Add(header, first, T0, out _));
            Assert.Equal(1, reassembler.PendingGroupCount);
        }

        /// <summary>
        /// The anti-DoS cap from section 6. An attacker announcing many groups and
        /// completing none must not be able to grow the buffer without bound.
        /// </summary>
        [Fact]
        public void MoreThanEightPendingGroups_EvictsTheOldest()
        {
            var reassembler = new FragmentReassembler();
            var data = new byte[16];

            for (ushort groupId = 0; groupId < 20; groupId++)
            {
                // Every group claims 2 fragments and only ever sends one.
                var header = new FragmentHeader(groupId, 0, 2);
                reassembler.Add(header, data, T0 + groupId, out _);
            }

            Assert.Equal(ProtocolConstants.MAX_PENDING_FRAGMENT_GROUPS,
                         reassembler.PendingGroupCount);
        }

        [Fact]
        public void AnIncompleteGroupExpiresAfterTheTimeout()
        {
            var reassembler = new FragmentReassembler();
            var data = new byte[16];

            reassembler.Add(new FragmentHeader(1, 0, 2), data, T0, out _);
            Assert.Equal(1, reassembler.PendingGroupCount);

            // Just before the deadline it is still held.
            Assert.Equal(0, reassembler.PruneExpired(T0 + ProtocolConstants.FRAGMENT_TIMEOUT_MS - 1));
            Assert.Equal(1, reassembler.PendingGroupCount);

            // At the deadline it is discarded and the memory released.
            Assert.Equal(1, reassembler.PruneExpired(T0 + ProtocolConstants.FRAGMENT_TIMEOUT_MS));
            Assert.Equal(0, reassembler.PendingGroupCount);
        }

        [Fact]
        public void AFragmentCountChangingMidGroupIsRejected()
        {
            var reassembler = new FragmentReassembler();
            var data = new byte[16];

            Assert.Equal(FragmentAddResult.Buffered,
                         reassembler.Add(new FragmentHeader(1, 0, 4), data, T0, out _));

            // Same group id, different count — corruption, or an attempt to make the
            // reassembler index past the array it already sized.
            Assert.Equal(FragmentAddResult.Rejected,
                         reassembler.Add(new FragmentHeader(1, 5, 8), data, T0, out _));
        }

        [Theory]
        [InlineData(0, 0)]      // count must be at least 1
        [InlineData(0, 65)]     // count above MAX_FRAGMENTS
        [InlineData(5, 5)]      // index equal to count
        [InlineData(9, 3)]      // index beyond count
        public void MalformedFragmentHeadersAreRejected(byte index, byte count)
        {
            Span<byte> raw = stackalloc byte[FragmentHeader.Size];
            Endian.WriteU16LE(raw, 0, 1);
            raw[2] = index;
            raw[3] = count;

            Assert.False(FragmentHeader.TryParse(raw, out _));
        }

        [Fact]
        public void FragmentHeaderRoundTrips()
        {
            Span<byte> raw = stackalloc byte[FragmentHeader.Size];
            var header = new FragmentHeader(0xBEEF, 3, 10);

            Assert.True(header.TryWrite(raw));
            Assert.Equal("EF BE 03 0A", Hex.ToHex(raw));

            Assert.True(FragmentHeader.TryParse(raw, out FragmentHeader parsed));
            Assert.Equal(0xBEEF, parsed.GroupId);
            Assert.Equal(3, parsed.Index);
            Assert.Equal(10, parsed.Count);
        }

        [Fact]
        public void APayloadTooLargeForSixtyFourFragmentsIsRefused()
        {
            // 64 * 1180 = 75,520 is the ceiling the spec's MAX_FRAGMENTS implies.
            int maximum = ProtocolConstants.MAX_FRAGMENTS * FragmentHeader.PayloadCapacity;

            Assert.Equal(75_520, maximum);
            Assert.Equal(ProtocolConstants.MAX_FRAGMENTS, Fragmenter.FragmentCount(maximum));
            Assert.Equal(-1, Fragmenter.FragmentCount(maximum + 1));
        }

        [Fact]
        public void SeparateGroupsReassembleIndependently()
        {
            var reassembler = new FragmentReassembler();
            var completed = new List<byte[]>();

            byte[] a = { 1, 2, 3, 4 };
            byte[] b = { 9, 8, 7, 6 };

            // Interleave the two groups the way a real socket would deliver them.
            reassembler.Add(new FragmentHeader(1, 0, 2), a, T0, out _);
            reassembler.Add(new FragmentHeader(2, 0, 2), b, T0, out _);
            reassembler.Add(new FragmentHeader(1, 1, 2), a, T0, out byte[]? firstDone);
            reassembler.Add(new FragmentHeader(2, 1, 2), b, T0, out byte[]? secondDone);

            Assert.NotNull(firstDone);
            Assert.NotNull(secondDone);
            completed.Add(firstDone!);
            completed.Add(secondDone!);

            Assert.Equal(new byte[] { 1, 2, 3, 4, 1, 2, 3, 4 }, completed[0]);
            Assert.Equal(new byte[] { 9, 8, 7, 6, 9, 8, 7, 6 }, completed[1]);
            Assert.Equal(0, reassembler.PendingGroupCount);
        }
    }
}
