using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Xunit;

namespace Ironfront.Net.Transport.Tests
{
    public sealed class ChannelAndFragmentTests
    {
        [Fact]
        public void UnsequencedChannelDeliversDuplicates()
        {
            var channels = new ChannelSet();
            int delivered = 0;
            channels.Receive(0, 0, new byte[] { 1 }, _ => delivered++);
            channels.Receive(0, 0, new byte[] { 1 }, _ => delivered++);

            Assert.Equal(2, delivered);
        }

        [Fact]
        public void SnapshotChannelDropsOlderPackets()
        {
            var channels = new ChannelSet();
            var delivered = new List<byte>();
            channels.Receive(1, 3, new byte[] { 3 }, p => delivered.Add(p.Span[0]));
            channels.Receive(1, 1, new byte[] { 1 }, p => delivered.Add(p.Span[0]));
            channels.Receive(1, 2, new byte[] { 2 }, p => delivered.Add(p.Span[0]));

            Assert.Equal(new byte[] { 3 }, delivered);
            Assert.Equal(2, channels.StalePacketsDropped);
        }

        [Fact]
        public void OrderedChannelBuffersAnEarlyPacketUntilTheGapArrives()
        {
            var channels = new ChannelSet();
            var delivered = new List<byte>();
            channels.Receive(2, 1, new byte[] { 1 }, p => delivered.Add(p.Span[0]));
            Assert.Empty(delivered);
            channels.Receive(2, 0, new byte[] { 0 }, p => delivered.Add(p.Span[0]));

            Assert.Equal(new byte[] { 0, 1 }, delivered);
            Assert.Equal(0, channels.PendingOrderedCount);
        }

        [Fact]
        public void OrderedChannelCopiesDataThatWaitsInTheBuffer()
        {
            var pool = new BufferPool(1, ProtocolConstants.MTU_SAFE);
            var channels = new ChannelSet(pool);
            byte[] source = { 9 };
            channels.Receive(2, 1, source, _ => { });
            source[0] = 4;
            var delivered = new List<byte>();
            channels.Receive(2, 0, new byte[] { 0 }, p => delivered.Add(p.Span[0]));

            Assert.Equal(new byte[] { 0, 9 }, delivered);
        }

        [Fact]
        public void OrderedChannelDropsPacketsOutsideItsWindow()
        {
            var channels = new ChannelSet();

            Assert.False(channels.Receive(2, 300, new byte[] { 1 }, _ => { }));
            Assert.Equal(0, channels.PendingOrderedCount);
        }

        [Fact]
        public void FragmentAssemblerReassemblesOutOfOrderParts()
        {
            var assembler = new FragmentAssembler();
            byte[] result = Array.Empty<byte>();
            int length = 0;
            assembler.TryReassemble(7, 1, 2, new byte[] { 2 }, 0, out result, out length);
            bool complete = assembler.TryReassemble(7, 0, 2, new byte[] { 1 }, 1, out result, out length);

            Assert.True(complete);
            Assert.Equal(new byte[] { 1, 2 }, result);
            Assert.Equal(2, length);
            Assert.Equal(0, assembler.PendingGroupCount);
        }

        [Fact]
        public void DuplicateFragmentDoesNotCompleteTheGroup()
        {
            var assembler = new FragmentAssembler();
            Assert.False(assembler.TryReassemble(1, 0, 2, new byte[] { 1 }, 0, out _, out _));
            Assert.False(assembler.TryReassemble(1, 0, 2, new byte[] { 1 }, 1, out _, out _));
            Assert.Equal(1, assembler.PendingGroupCount);
        }

        [Fact]
        public void InconsistentFragmentCountIsRejectedAndFreed()
        {
            var assembler = new FragmentAssembler();
            assembler.TryReassemble(1, 0, 2, new byte[] { 1 }, 0, out _, out _);
            Assert.False(assembler.TryReassemble(1, 1, 3, new byte[] { 2 }, 1, out _, out _));
            Assert.Equal(0, assembler.PendingGroupCount);
        }

        [Fact]
        public void FragmentGroupsAreCappedAtEight()
        {
            var assembler = new FragmentAssembler();
            for (ushort id = 0; id < 20; id++)
                assembler.TryReassemble(id, 0, 64, new byte[] { 1 }, id, out _, out _);

            Assert.Equal(ProtocolConstants.MAX_PENDING_FRAGMENT_GROUPS, assembler.PendingGroupCount);
        }

        [Fact]
        public void FragmentGroupsExpireAndReturnTheirBuffers()
        {
            var pool = new BufferPool(8, ProtocolConstants.MTU_SAFE);
            var assembler = new FragmentAssembler(pool);
            assembler.TryReassemble(1, 0, 2, new byte[] { 1 }, 0, out _, out _);
            Assert.Equal(1, pool.RentedCount);

            assembler.Update(ProtocolConstants.FRAGMENT_TIMEOUT_MS);

            Assert.Equal(0, assembler.PendingGroupCount);
            Assert.Equal(0, pool.RentedCount);
        }

        [Fact]
        public void TwentyKilobytePayloadReassemblesByteForByte()
        {
            var assembler = new FragmentAssembler();
            byte[] original = new byte[20_000];
            for (int i = 0; i < original.Length; i++) original[i] = (byte)(i * 31);

            int count = Fragmenter.FragmentCount(original.Length);
            var parts = new List<(byte index, byte[] data)>();
            for (byte index = 0; index < count; index++)
            {
                Assert.True(Fragmenter.TrySliceFragment(original, index, out ReadOnlySpan<byte> slice));
                parts.Add((index, slice.ToArray()));
            }

            // Feed reverse order to exercise the group index rather than list order.
            byte[] full = Array.Empty<byte>();
            int length = 0;
            for (int i = parts.Count - 1; i >= 0; i--)
            {
                bool complete = assembler.TryReassemble(
                    44,
                    parts[i].index,
                    (byte)count,
                    parts[i].data,
                    i,
                    out full,
                    out length);
                if (i != 0) Assert.False(complete);
            }

            Assert.Equal(original.Length, length);
            Assert.Equal(original, full);
        }
    }
}
