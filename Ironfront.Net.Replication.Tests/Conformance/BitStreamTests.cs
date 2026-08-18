using System;
using Ironfront.Net.Replication.Serialization;
using Xunit;

namespace Ironfront.Net.Replication.Tests.Conformance
{
    /// <summary>
    /// Verifies the transport track's <see cref="BitWriter"/> and <see cref="BitReader"/>.
    /// </summary>
    /// <remarks>
    /// Closes phase-00 acceptance criterion 4, which sat at half-done because the bit stream
    /// did not exist yet.
    /// <para>
    /// The expected bytes here are hand-written, not produced by running the code. That
    /// distinction is the entire value of this file: a round-trip test passes just as happily
    /// when the writer and reader are wrong in the same direction, which is exactly what
    /// happens when one person writes both.
    /// </para>
    /// </remarks>
    public sealed class BitStreamTests
    {
        // ------------------------------------------------------------------ bit order

        [Fact]
        public void WritesLsbFirst_MatchesHandWrittenHex()
        {
            // The worked example from replication/phase-00 § Task 3.
            // 0b101 into 3 bits fills bits 0..2; 0b11 into 2 bits fills bits 3..4.
            // bit0=1 bit1=0 bit2=1 bit3=1 bit4=1  ->  1 + 4 + 8 + 16 = 29 = 0b00011101.
            Span<byte> buffer = stackalloc byte[4];
            var writer = new BitWriter(buffer);

            writer.WriteBits(0b101, 3);
            writer.WriteBits(0b11, 2);
            writer.AlignToByte();

            Assert.True(writer.Ok);
            Assert.Equal(0b00011101, buffer[0]);
            Assert.Equal(1, writer.BytesWritten);
        }

        [Fact]
        public void SingleBitsFillByteFromLowEndUpward()
        {
            Span<byte> buffer = stackalloc byte[1];
            var writer = new BitWriter(buffer);

            // 1,0,1,1,0,0,0,1 written in order -> bit0=1 bit2=1 bit3=1 bit7=1 -> 0b10001101.
            writer.WriteBool(true);
            writer.WriteBool(false);
            writer.WriteBool(true);
            writer.WriteBool(true);
            writer.WriteBool(false);
            writer.WriteBool(false);
            writer.WriteBool(false);
            writer.WriteBool(true);

            Assert.True(writer.Ok);
            Assert.Equal(0b10001101, buffer[0]);
        }

        [Fact]
        public void ByteAlignedMultiByteValuesMatchTheLittleEndianWireLayout()
        {
            // The protocol is little-endian everywhere (protocol-spec.md § 0), so a
            // byte-aligned u32 written through the bit stream must produce exactly the bytes
            // Endian.WriteU32LE would: low byte first.
            Span<byte> buffer = stackalloc byte[8];
            var writer = new BitWriter(buffer);

            writer.WriteUInt32(0x12345678u);
            writer.WriteUInt16(0xABCD);

            Assert.True(writer.Ok);
            Assert.Equal(0x78, buffer[0]);
            Assert.Equal(0x56, buffer[1]);
            Assert.Equal(0x34, buffer[2]);
            Assert.Equal(0x12, buffer[3]);
            Assert.Equal(0xCD, buffer[4]);
            Assert.Equal(0xAB, buffer[5]);
            Assert.Equal(6, writer.BytesWritten);
        }

        [Fact]
        public void ValuesSpanningAByteBoundaryAreSplitLowBitsFirst()
        {
            // 6 bits of padding, then 0b1111_0000_1111 (0xF0F) in 12 bits.
            // Byte 0 keeps bits 6..7 = the low 2 bits of the value (0b11) -> 0b11000000.
            // Byte 1 takes the next 8 bits (0b11000011 >> ... ) -> verified against the
            // arithmetic below rather than trusting the reader.
            Span<byte> buffer = stackalloc byte[4];
            var writer = new BitWriter(buffer);

            writer.WriteBits(0, 6);
            writer.WriteBits(0xF0F, 12);
            writer.AlignToByte();

            Assert.True(writer.Ok);

            // 0xF0F = 0b1111_0000_1111. Low 2 bits (0b11) land in byte0 bits 6..7.
            Assert.Equal(0b11000000, buffer[0]);
            // Next 8 bits of the value are (0xF0F >> 2) & 0xFF = 0b11000011.
            Assert.Equal(0b11000011, buffer[1]);
            // Remaining 2 bits are (0xF0F >> 10) & 0b11 = 0b11.
            Assert.Equal(0b00000011, buffer[2]);
            Assert.Equal(3, writer.BytesWritten);
        }

        // ------------------------------------------------------------------ round trips

        [Theory]
        [InlineData(0u, 1)]
        [InlineData(1u, 1)]
        [InlineData(0u, 7)]
        [InlineData(127u, 7)]
        [InlineData(255u, 8)]
        [InlineData(0u, 32)]
        [InlineData(uint.MaxValue, 32)]
        [InlineData(0x5A5A5A5Au, 32)]
        [InlineData(31u, 5)]
        [InlineData(1023u, 10)]
        public void RoundTripsAnyWidth(uint value, int bitCount)
        {
            Span<byte> buffer = stackalloc byte[8];
            var writer = new BitWriter(buffer);
            writer.WriteBits(value, bitCount);
            Assert.True(writer.Ok);

            var reader = new BitReader(buffer);
            uint read = reader.ReadBits(bitCount);

            Assert.True(reader.Ok);
            Assert.Equal(value, read);
        }

        [Fact]
        public void RoundTripsAMixedSequenceInOrder()
        {
            Span<byte> buffer = stackalloc byte[16];
            var writer = new BitWriter(buffer);

            writer.WriteBits(5, 3);
            writer.WriteBool(true);
            writer.WriteByte(0xDE);
            writer.WriteUInt16(0xBEEF);
            writer.WriteBits(100, 7);
            writer.WriteUInt32(0xCAFEF00D);
            writer.WriteSByte(-42);
            writer.WriteInt16(-1234);
            Assert.True(writer.Ok);

            var reader = new BitReader(buffer);

            Assert.Equal(5u, reader.ReadBits(3));
            Assert.True(reader.ReadBool());
            Assert.Equal(0xDE, reader.ReadByte());
            Assert.Equal(0xBEEF, reader.ReadUInt16());
            Assert.Equal(100u, reader.ReadBits(7));
            Assert.Equal(0xCAFEF00D, reader.ReadUInt32());
            Assert.Equal(-42, reader.ReadSByte());
            Assert.Equal(-1234, reader.ReadInt16());
            Assert.True(reader.Ok);
        }

        [Fact]
        public void AlignToByteSkipsToTheSamePlaceOnBothSides()
        {
            Span<byte> buffer = stackalloc byte[4];
            var writer = new BitWriter(buffer);
            writer.WriteBits(0b101, 3);
            writer.AlignToByte();
            writer.WriteByte(0x7F);
            Assert.True(writer.Ok);

            var reader = new BitReader(buffer);
            Assert.Equal(0b101u, reader.ReadBits(3));
            reader.AlignToByte();
            Assert.Equal(0x7F, reader.ReadByte());
            Assert.True(reader.Ok);
        }

        [Fact]
        public void AlignToByteIsANoOpWhenAlreadyAligned()
        {
            Span<byte> buffer = stackalloc byte[2];
            var writer = new BitWriter(buffer);
            writer.WriteByte(0xAA);
            int before = writer.BitsWritten;
            writer.AlignToByte();

            Assert.Equal(before, writer.BitsWritten);
        }

        // ------------------------------------------------------------------ hostile input

        [Fact]
        public void WriterMasksBitsAboveTheDeclaredWidth()
        {
            // Writing 0xFF into 3 bits must not smear five stray bits into the next field.
            Span<byte> buffer = stackalloc byte[2];
            var writer = new BitWriter(buffer);
            writer.WriteBits(0xFF, 3);
            writer.WriteBits(0, 5);

            Assert.True(writer.Ok);
            Assert.Equal(0b00000111, buffer[0]);
        }

        [Fact]
        public void WriterLatchesNotOkOnOverrunAndStopsWriting()
        {
            Span<byte> buffer = stackalloc byte[1];
            var writer = new BitWriter(buffer);

            writer.WriteByte(0x11);
            Assert.True(writer.Ok);

            writer.WriteByte(0x22);
            Assert.False(writer.Ok);

            // Still not OK, and the first byte was not corrupted by the failed write.
            writer.WriteBool(true);
            Assert.False(writer.Ok);
            Assert.Equal(0x11, buffer[0]);
            Assert.True(writer.Written.IsEmpty);
        }

        [Fact]
        public void ReaderLatchesNotOkPastTheEndOfATruncatedPacket()
        {
            // A hostile or truncated packet must not throw — conventions.md § 3.2.
            ReadOnlySpan<byte> truncated = stackalloc byte[1] { 0xFF };
            var reader = new BitReader(truncated);

            Assert.Equal(0xFFu, reader.ReadBits(8));
            Assert.True(reader.Ok);

            Assert.Equal(0u, reader.ReadBits(1));
            Assert.False(reader.Ok);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(33)]
        public void RejectsOutOfRangeWidthsWithoutThrowing(int bitCount)
        {
            Span<byte> buffer = stackalloc byte[8];
            var writer = new BitWriter(buffer);
            writer.WriteBits(1, bitCount);
            Assert.False(writer.Ok);

            var reader = new BitReader(buffer);
            Assert.Equal(0u, reader.ReadBits(bitCount));
            Assert.False(reader.Ok);
        }

        [Fact]
        public void BytesWrittenRoundsAPartialByteUp()
        {
            Span<byte> buffer = stackalloc byte[4];
            var writer = new BitWriter(buffer);

            writer.WriteBits(1, 1);
            Assert.Equal(1, writer.BytesWritten);

            writer.WriteBits(0, 7);
            Assert.Equal(1, writer.BytesWritten);

            writer.WriteBits(1, 1);
            Assert.Equal(2, writer.BytesWritten);
        }

        [Fact]
        public void ReportsByteAlignmentAccurately()
        {
            Span<byte> buffer = stackalloc byte[4];
            var writer = new BitWriter(buffer);

            Assert.True(writer.IsByteAligned);
            writer.WriteBits(1, 3);
            Assert.False(writer.IsByteAligned);
            writer.AlignToByte();
            Assert.True(writer.IsByteAligned);
        }
    }
}
