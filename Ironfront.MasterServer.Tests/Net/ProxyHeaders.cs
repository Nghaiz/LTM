using System;
using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace Ironfront.MasterServer.Tests.Net
{
    /// <summary>
    /// Builds PROXY protocol headers byte by byte from the specification, not from the parser,
    /// so the tests check the parser against the format rather than against itself.
    /// </summary>
    internal static class ProxyHeaders
    {
        public static readonly byte[] V2Signature =
            { 0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A };

        public const byte Local = 0x0, Proxy = 0x1;
        public const byte Unspec = 0x00, Tcp4 = 0x11, Tcp6 = 0x21, Udp4 = 0x12;

        public static byte[] V2(byte command, byte family, ReadOnlySpan<byte> block, byte version = 2)
        {
            var header = new byte[16 + block.Length];
            V2Signature.CopyTo(header, 0);
            header[12] = (byte)((version << 4) | command);
            header[13] = family;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14), (ushort)block.Length);
            block.CopyTo(header.AsSpan(16));
            return header;
        }

        /// <summary>A version 2 PROXY header for a TCP-over-IPv4 client, as fly's edge sends one.</summary>
        public static byte[] V2Tcp4(string client, int clientPort, string server = "172.19.44.162", int serverPort = 27000)
            => V2(Proxy, Tcp4, AddressBlock(client, server, clientPort, serverPort));

        public static byte[] AddressBlock(string client, string server, int clientPort, int serverPort)
        {
            byte[] source = IPAddress.Parse(client).GetAddressBytes();
            byte[] destination = IPAddress.Parse(server).GetAddressBytes();
            var block = new byte[source.Length * 2 + 4];
            source.CopyTo(block, 0);
            destination.CopyTo(block, source.Length);
            BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(source.Length * 2), (ushort)clientPort);
            BinaryPrimitives.WriteUInt16BigEndian(block.AsSpan(source.Length * 2 + 2), (ushort)serverPort);
            return block;
        }

        public static byte[] V1(string line) => Encoding.ASCII.GetBytes(line);

        public static byte[] Concat(byte[] first, byte[] second)
        {
            var joined = new byte[first.Length + second.Length];
            first.CopyTo(joined, 0);
            second.CopyTo(joined, first.Length);
            return joined;
        }
    }
}
