using System;
using System.Net;
using Ironfront.MasterServer.Net;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests.Net
{
    /// <summary>
    /// <see cref="ProxyProtocolHeader"/> against HAProxy's PROXY protocol specification: what a
    /// proxy may send must parse, and nothing else may.
    /// </summary>
    /// <remarks>
    /// The second half matters as much as the first. With trusted headers the master takes a
    /// client's address from these bytes, so a lenient parser is a way for a client to choose
    /// the address the per-IP limits count it under.
    /// </remarks>
    public class ProxyProtocolHeaderTests
    {
        private static (ProxyHeaderParse Result, int Consumed, IPEndPoint? Source) Parse(byte[] data)
        {
            ProxyHeaderParse result = ProxyProtocolHeader.TryParse(data, out int consumed, out IPEndPoint? source);
            return (result, consumed, source);
        }

        // ------------------------------------------------------------------------- version 2

        [Fact]
        public void V2Tcp4NamesTheClient()
        {
            var (result, consumed, source) = Parse(ProxyHeaders.V2Tcp4("203.0.113.7", 51234));

            Assert.Equal(ProxyHeaderParse.Complete, result);
            Assert.Equal(28, consumed);
            Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 51234), source);
        }

        [Fact]
        public void V2Tcp6NamesTheClient()
        {
            byte[] header = ProxyHeaders.V2(ProxyHeaders.Proxy, ProxyHeaders.Tcp6,
                ProxyHeaders.AddressBlock("2001:db8::7", "2001:db8::1", 4000, 27000));

            var (result, consumed, source) = Parse(header);

            Assert.Equal(ProxyHeaderParse.Complete, result);
            Assert.Equal(52, consumed);
            Assert.Equal(new IPEndPoint(IPAddress.Parse("2001:db8::7"), 4000), source);
        }

        /// <summary>
        /// LOCAL is the proxy speaking for itself, such as a health check. The specification has
        /// the receiver ignore the family and the block, so a short block is still a header.
        /// </summary>
        [Theory]
        [InlineData(ProxyHeaders.Unspec, 0)]
        [InlineData(ProxyHeaders.Tcp4, 0)]
        [InlineData(ProxyHeaders.Tcp4, 12)]
        public void V2LocalKeepsTheSocketsAddress(byte family, int blockLength)
        {
            var (result, consumed, source) = Parse(ProxyHeaders.V2(ProxyHeaders.Local, family, new byte[blockLength]));

            Assert.Equal(ProxyHeaderParse.Complete, result);
            Assert.Equal(16 + blockLength, consumed);
            Assert.Null(source);
        }

        [Fact]
        public void V2UnspecKeepsTheSocketsAddress()
        {
            var (result, consumed, source) = Parse(ProxyHeaders.V2(ProxyHeaders.Proxy, ProxyHeaders.Unspec, Array.Empty<byte>()));

            Assert.Equal(ProxyHeaderParse.Complete, result);
            Assert.Equal(16, consumed);
            Assert.Null(source);
        }

        [Fact]
        public void V2SkipsExtensionsAfterTheAddresses()
        {
            byte[] block = ProxyHeaders.Concat(
                ProxyHeaders.AddressBlock("198.51.100.4", "10.0.0.1", 6000, 27000),
                new byte[] { 0x05, 0x00, 0x04, 0xDE, 0xAD, 0xBE, 0xEF });          // a unique-id TLV
            var (result, consumed, source) = Parse(ProxyHeaders.V2(ProxyHeaders.Proxy, ProxyHeaders.Tcp4, block));

            Assert.Equal(ProxyHeaderParse.Complete, result);
            Assert.Equal(16 + block.Length, consumed);
            Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.4"), 6000), source);
        }

        [Fact]
        public void V2ArrivingInPiecesNeedsMoreDataUntilItIsWhole()
        {
            byte[] header = ProxyHeaders.V2Tcp4("203.0.113.9", 1234);

            for (int length = 1; length < header.Length; length++)
                Assert.Equal(ProxyHeaderParse.NeedMoreData, Parse(header[..length]).Result);

            Assert.Equal(ProxyHeaderParse.Complete, Parse(header).Result);
        }

        [Fact]
        public void BytesAfterTheHeaderAreNotPartOfIt()
        {
            byte[] header = ProxyHeaders.V2Tcp4("203.0.113.9", 1234);
            var (result, consumed, _) = Parse(ProxyHeaders.Concat(header, new byte[] { 1, 2, 3, 4, 5 }));

            Assert.Equal(ProxyHeaderParse.Complete, result);
            Assert.Equal(header.Length, consumed);
        }

        [Fact]
        public void V2WithAWrongSignatureIsInvalidAsSoonAsItDiffers()
        {
            byte[] header = ProxyHeaders.V2Tcp4("203.0.113.9", 1234);
            header[5] ^= 0xFF;

            // Refused on the sixth byte, not after waiting for a whole header that will not come.
            Assert.Equal(ProxyHeaderParse.Invalid, Parse(header[..6]).Result);
            Assert.Equal(ProxyHeaderParse.Invalid, Parse(header).Result);
        }

        [Theory]
        [InlineData(1, ProxyHeaders.Proxy)]    // version 1 in the binary form
        [InlineData(3, ProxyHeaders.Proxy)]    // a version that does not exist
        [InlineData(2, 0x2)]                    // a command that does not exist
        public void V2WithAnotherVersionOrCommandIsInvalid(byte version, byte command)
        {
            byte[] header = ProxyHeaders.V2(command, ProxyHeaders.Tcp4,
                ProxyHeaders.AddressBlock("203.0.113.9", "10.0.0.1", 1, 2), version);

            Assert.Equal(ProxyHeaderParse.Invalid, Parse(header).Result);
        }

        /// <summary>
        /// The length field could ask for 65,535 bytes. Refused from the fixed part alone, so a
        /// client cannot make the master hold a buffer that size open on its behalf.
        /// </summary>
        [Fact]
        public void V2DeclaringAnOversizedBlockIsInvalidBeforeTheBlockArrives()
        {
            byte[] header = ProxyHeaders.V2(ProxyHeaders.Proxy, ProxyHeaders.Tcp4,
                new byte[ProxyProtocolHeader.MaxV2AddressBlock + 1]);

            Assert.Equal(ProxyHeaderParse.Invalid, Parse(header[..16]).Result);
            Assert.Equal(ProxyHeaderParse.Complete,
                Parse(ProxyHeaders.V2(ProxyHeaders.Proxy, ProxyHeaders.Tcp4, new byte[ProxyProtocolHeader.MaxV2AddressBlock])).Result);
        }

        [Fact]
        public void V2UdpIsInvalidOnATcpListener()
        {
            byte[] header = ProxyHeaders.V2(ProxyHeaders.Proxy, ProxyHeaders.Udp4,
                ProxyHeaders.AddressBlock("203.0.113.9", "10.0.0.1", 1, 2));

            Assert.Equal(ProxyHeaderParse.Invalid, Parse(header).Result);
        }

        [Theory]
        [InlineData(ProxyHeaders.Tcp4, 11)]
        [InlineData(ProxyHeaders.Tcp6, 35)]
        public void V2WithABlockTooShortForItsFamilyIsInvalid(byte family, int blockLength)
        {
            Assert.Equal(ProxyHeaderParse.Invalid,
                Parse(ProxyHeaders.V2(ProxyHeaders.Proxy, family, new byte[blockLength])).Result);
        }

        // ------------------------------------------------------------------------- version 1

        [Fact]
        public void V1Tcp4NamesTheClient()
        {
            byte[] header = ProxyHeaders.V1("PROXY TCP4 198.51.100.22 10.0.0.1 35646 27000\r\n");
            var (result, consumed, source) = Parse(header);

            Assert.Equal(ProxyHeaderParse.Complete, result);
            Assert.Equal(header.Length, consumed);
            Assert.Equal(new IPEndPoint(IPAddress.Parse("198.51.100.22"), 35646), source);
        }

        [Fact]
        public void V1Tcp6NamesTheClient()
        {
            byte[] header = ProxyHeaders.V1("PROXY TCP6 2001:db8::22 2001:db8::1 443 27000\r\n");
            var (result, _, source) = Parse(header);

            Assert.Equal(ProxyHeaderParse.Complete, result);
            Assert.Equal(new IPEndPoint(IPAddress.Parse("2001:db8::22"), 443), source);
        }

        [Theory]
        [InlineData("PROXY UNKNOWN\r\n")]
        [InlineData("PROXY UNKNOWN ffff:f:f:f:f:f:f:f ffff:f:f:f:f:f:f:f 65535 65535\r\n")]
        public void V1UnknownKeepsTheSocketsAddress(string line)
        {
            byte[] header = ProxyHeaders.V1(line);
            var (result, consumed, source) = Parse(header);

            Assert.Equal(ProxyHeaderParse.Complete, result);
            Assert.Equal(header.Length, consumed);
            Assert.Null(source);
        }

        [Fact]
        public void V1ArrivingInPiecesNeedsMoreDataUntilItIsWhole()
        {
            byte[] header = ProxyHeaders.V1("PROXY TCP4 198.51.100.22 10.0.0.1 35646 27000\r\n");

            for (int length = 1; length < header.Length; length++)
                Assert.Equal(ProxyHeaderParse.NeedMoreData, Parse(header[..length]).Result);
        }

        [Fact]
        public void V1WithNoLineEndWithin107BytesIsInvalid()
        {
            byte[] header = ProxyHeaders.V1("PROXY TCP4 " + new string('1', 120));

            Assert.Equal(ProxyHeaderParse.Invalid, Parse(header).Result);
        }

        [Theory]
        [InlineData("PROXY TCP4 198.51.100.22 10.0.0.1 70000 27000\r\n")]       // port out of range
        [InlineData("PROXY TCP4 198.51.100.22 10.0.0.1 +80 27000\r\n")]         // signed port
        [InlineData("PROXY TCP4 198.51.100.22 10.0.0.1 080 27000\r\n")]         // leading zero
        [InlineData("PROXY TCP4 2001:db8::22 10.0.0.1 80 27000\r\n")]           // v6 under TCP4
        [InlineData("PROXY TCP6 198.51.100.22 2001:db8::1 80 27000\r\n")]       // v4 under TCP6
        [InlineData("PROXY TCP5 198.51.100.22 10.0.0.1 80 27000\r\n")]          // no such protocol
        [InlineData("PROXY TCP4 198.51.100.22 10.0.0.1 80\r\n")]                // a field missing
        [InlineData("PROXY TCP4 198.51.100.22 10.0.0.1 80 27000 9\r\n")]        // a field too many
        [InlineData("PROXY TCP4 198.51.100.22  10.0.0.1 80 27000\r\n")]         // a double space
        [InlineData("PROXY TCP4 198.51.100.22 10.0.0.1 80 27000\n")]            // LF without CR
        [InlineData("PROXY TCP4 198.51.100.22 10.0.0.1 80 27000\t\r\n")]        // a control byte
        [InlineData("proxy TCP4 198.51.100.22 10.0.0.1 80 27000\r\n")]          // the wrong case
        public void V1MalformedIsInvalid(string line)
        {
            Assert.Equal(ProxyHeaderParse.Invalid, Parse(ProxyHeaders.V1(line)).Result);
        }

        // ------------------------------------------------------------------------- neither

        [Fact]
        public void AnMspFrameIsNotAHeader()
        {
            var frame = new byte[MspFrame.MinFrameSize];
            MspFrame.Write(frame, MspMessageType.Heartbeat, ReadOnlySpan<byte>.Empty);

            Assert.Equal(ProxyHeaderParse.Invalid, Parse(frame).Result);
        }

        [Theory]
        [InlineData(new byte[] { 0x16, 0x03, 0x01 })]                          // a TLS ClientHello
        [InlineData(new byte[] { (byte)'G', (byte)'E', (byte)'T', (byte)' ' })] // HTTP
        [InlineData(new byte[] { (byte)'P', (byte)'R', (byte)'O', (byte)'X', (byte)'I' })]
        public void OtherProtocolsAreInvalid(byte[] data)
        {
            Assert.Equal(ProxyHeaderParse.Invalid, Parse(data).Result);
        }

        [Fact]
        public void NothingYetNeedsMoreData()
        {
            Assert.Equal(ProxyHeaderParse.NeedMoreData, Parse(Array.Empty<byte>()).Result);
        }
    }
}
