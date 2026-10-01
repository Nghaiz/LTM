using System;
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ironfront.MasterServer.Net
{
    /// <summary>What <see cref="ProxyProtocolHeader.TryParse"/> made of the bytes so far.</summary>
    public enum ProxyHeaderParse
    {
        /// <summary>Every byte so far fits a header that is not finished yet. Read more.</summary>
        NeedMoreData,

        /// <summary>A whole header. Its length is the <c>consumed</c> out-parameter.</summary>
        Complete,

        /// <summary>Not a PROXY header, or a malformed one. The connection is closed.</summary>
        Invalid,
    }

    /// <summary>
    /// The PROXY protocol header (HAProxy's specification, versions 1 and 2) that a TCP proxy
    /// sends ahead of a connection to say which address the connection really came from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the master reads it.</b> On fly the edge terminates TLS and opens its own
    /// connection to the machine, so every socket the master accepts comes from the proxy:
    /// on 2026-10-01 all five live connections, three game servers and two players, came from
    /// 172.16.44.162. The per-IP connection cap and the per-IP login rate then counted every
    /// player and every game server as one client. With a cap of five and three game servers
    /// registered, a third player could not connect at all. With <c>proxy_proto</c> on the
    /// fly service, the edge sends this header first, carrying the address it accepted the
    /// connection from.
    /// </para>
    /// <para>
    /// <b>Strict on purpose.</b> When the host trusts these headers, every connection must
    /// start with one, so a client cannot name its own address: the edge's header always comes
    /// first, and anything a client sends after it is MSP. Only the forms the specification
    /// allows are accepted, and an unfinished header is bounded by <see cref="MaxLength"/>.
    /// </para>
    /// </remarks>
    public static class ProxyProtocolHeader
    {
        /// <summary>The longest version 1 line the specification allows, CRLF included.</summary>
        public const int MaxV1Length = 107;

        /// <summary>The 16-byte fixed part of a version 2 header.</summary>
        public const int V2FixedLength = 16;

        /// <summary>
        /// The largest version 2 address block accepted: the 36 bytes of an IPv6 pair plus room
        /// for type-length-value extensions. The specification allows 65,535, which a client
        /// could otherwise make the master buffer before it has proved anything.
        /// </summary>
        public const int MaxV2AddressBlock = 512;

        /// <summary>The most bytes a header can take. A reader needs no larger buffer.</summary>
        public const int MaxLength = V2FixedLength + MaxV2AddressBlock;

        private static ReadOnlySpan<byte> V2Signature =>
            new byte[] { 0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A };

        private static ReadOnlySpan<byte> V1Prefix => "PROXY "u8;

        /// <summary>
        /// Reads a header from the start of <paramref name="data"/>.
        /// </summary>
        /// <param name="data">The bytes received so far, from the first byte of the connection.</param>
        /// <param name="consumed">On <see cref="ProxyHeaderParse.Complete"/>, the header's length;
        /// any bytes after it belong to the connection. Otherwise 0.</param>
        /// <param name="source">The client's address and port, or null when the header names
        /// none: a version 2 <c>LOCAL</c> header (the proxy speaking for itself, such as a health
        /// check), an <c>UNSPEC</c> family, or version 1 <c>UNKNOWN</c>. The caller then keeps the
        /// socket's own address.</param>
        public static ProxyHeaderParse TryParse(ReadOnlySpan<byte> data, out int consumed, out IPEndPoint? source)
        {
            consumed = 0;
            source = null;
            if (data.IsEmpty) return ProxyHeaderParse.NeedMoreData;
            if (data[0] == V2Signature[0]) return TryParseV2(data, out consumed, out source);
            if (data[0] == V1Prefix[0]) return TryParseV1(data, out consumed, out source);
            return ProxyHeaderParse.Invalid;
        }

        private static ProxyHeaderParse TryParseV2(ReadOnlySpan<byte> data, out int consumed, out IPEndPoint? source)
        {
            consumed = 0;
            source = null;

            int signatureBytes = Math.Min(data.Length, V2Signature.Length);
            if (!data.Slice(0, signatureBytes).SequenceEqual(V2Signature.Slice(0, signatureBytes)))
                return ProxyHeaderParse.Invalid;
            if (data.Length < V2FixedLength) return ProxyHeaderParse.NeedMoreData;

            int version = data[12] >> 4;
            int command = data[12] & 0x0F;            // 0 = LOCAL, 1 = PROXY
            if (version != 2 || command > 1) return ProxyHeaderParse.Invalid;

            int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(14, 2));
            if (length > MaxV2AddressBlock) return ProxyHeaderParse.Invalid;
            if (data.Length < V2FixedLength + length) return ProxyHeaderParse.NeedMoreData;

            // LOCAL: the proxy speaking for itself, such as a health check. The specification has
            // the receiver skip the family and the address block and keep the socket's endpoints.
            if (command == 0)
            {
                consumed = V2FixedLength + length;
                return ProxyHeaderParse.Complete;
            }

            ReadOnlySpan<byte> block = data.Slice(V2FixedLength, length);
            switch (data[13])
            {
                case 0x00:                              // UNSPEC: the proxy does not know
                    break;
                case 0x11:                              // TCP over IPv4: src, dst, sport, dport
                    if (length < 12) return ProxyHeaderParse.Invalid;
                    source = new IPEndPoint(new IPAddress(block.Slice(0, 4)), BinaryPrimitives.ReadUInt16BigEndian(block.Slice(8, 2)));
                    break;
                case 0x21:                              // TCP over IPv6
                    if (length < 36) return ProxyHeaderParse.Invalid;
                    source = new IPEndPoint(new IPAddress(block.Slice(0, 16)), BinaryPrimitives.ReadUInt16BigEndian(block.Slice(32, 2)));
                    break;
                default:                                // UDP or a Unix socket, on a TCP listener
                    return ProxyHeaderParse.Invalid;
            }

            consumed = V2FixedLength + length;
            return ProxyHeaderParse.Complete;
        }

        private static ProxyHeaderParse TryParseV1(ReadOnlySpan<byte> data, out int consumed, out IPEndPoint? source)
        {
            consumed = 0;
            source = null;

            int prefixBytes = Math.Min(data.Length, V1Prefix.Length);
            if (!data.Slice(0, prefixBytes).SequenceEqual(V1Prefix.Slice(0, prefixBytes)))
                return ProxyHeaderParse.Invalid;

            ReadOnlySpan<byte> window = data.Slice(0, Math.Min(data.Length, MaxV1Length));
            int lineFeed = window.IndexOf((byte)'\n');
            if (lineFeed < 0)
                return data.Length >= MaxV1Length ? ProxyHeaderParse.Invalid : ProxyHeaderParse.NeedMoreData;
            if (lineFeed == 0 || data[lineFeed - 1] != (byte)'\r') return ProxyHeaderParse.Invalid;

            ReadOnlySpan<byte> line = data.Slice(0, lineFeed - 1);
            foreach (byte b in line)
            {
                if (b < 0x20 || b > 0x7E) return ProxyHeaderParse.Invalid;
            }

            string[] fields = Encoding.ASCII.GetString(line).Split(' ');

            // "PROXY UNKNOWN" may carry anything after it; the receiver ignores the rest.
            if (fields.Length >= 2 && fields[1] == "UNKNOWN")
            {
                consumed = lineFeed + 1;
                return ProxyHeaderParse.Complete;
            }

            if (fields.Length != 6) return ProxyHeaderParse.Invalid;

            AddressFamily family;
            if (fields[1] == "TCP4") family = AddressFamily.InterNetwork;
            else if (fields[1] == "TCP6") family = AddressFamily.InterNetworkV6;
            else return ProxyHeaderParse.Invalid;

            if (!TryParseAddress(fields[2], family, out IPAddress? client) ||
                !TryParseAddress(fields[3], family, out _) ||
                !TryParsePort(fields[4], out int clientPort) ||
                !TryParsePort(fields[5], out _))
            {
                return ProxyHeaderParse.Invalid;
            }

            source = new IPEndPoint(client!, clientPort);
            consumed = lineFeed + 1;
            return ProxyHeaderParse.Complete;
        }

        private static bool TryParseAddress(string text, AddressFamily family, out IPAddress? address)
            => IPAddress.TryParse(text, out address) && address.AddressFamily == family;

        // Decimal only, no sign, no leading zeros beyond "0" itself: what the specification writes.
        private static bool TryParsePort(string text, out int port)
        {
            port = 0;
            if (text.Length == 0 || text.Length > 5 || (text.Length > 1 && text[0] == '0')) return false;
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port <= ushort.MaxValue;
        }
    }
}
