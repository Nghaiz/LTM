using System;
using System.Net;
using System.Net.Sockets;

namespace Ironfront.Net.Transport
{
    /// <summary>
    /// ReceiveFrom endpoint that reuses its managed instance. The socket API calls Create after
    /// each datagram; returning this object avoids allocating a new IPEndPoint for every packet.
    /// It is owned by UdpPeer and is valid only during PacketReceived.
    /// </summary>
    internal sealed class ReusableIpv4EndPoint : EndPoint
    {
        private readonly SocketAddress _serialized =
            new SocketAddress(AddressFamily.InterNetwork, 16);

        public uint Address { get; private set; }

        public ushort Port { get; private set; }

        public override AddressFamily AddressFamily => AddressFamily.InterNetwork;

        public override SocketAddress Serialize() => _serialized;

        public override EndPoint Create(SocketAddress socketAddress)
        {
            if (socketAddress.Family != AddressFamily.InterNetwork || socketAddress.Size < 8)
                throw new ArgumentException("The socket address is not IPv4.", nameof(socketAddress));

            for (int i = 0; i < socketAddress.Size && i < _serialized.Size; i++)
                _serialized[i] = socketAddress[i];

            Port = (ushort)((socketAddress[2] << 8) | socketAddress[3]);
            Address = (uint)(socketAddress[4]
                | (socketAddress[5] << 8)
                | (socketAddress[6] << 16)
                | (socketAddress[7] << 24));
            return this;
        }

        public SocketAddress SerializedAddress => _serialized;

        public override string ToString()
            => $"{Address & 0xff}.{(Address >> 8) & 0xff}."
             + $"{(Address >> 16) & 0xff}.{(Address >> 24) & 0xff}:{Port}";

        public IPEndPoint ToIPEndPoint()
            => new IPEndPoint(
                new IPAddress(new byte[]
                {
                    (byte)Address,
                    (byte)(Address >> 8),
                    (byte)(Address >> 16),
                    (byte)(Address >> 24),
                }),
                Port);
    }

    /// <summary>Allocation-free IPv4 endpoint key for the server routing tables.</summary>
    internal readonly struct EndpointKey : IEquatable<EndpointKey>
    {
        public EndpointKey(uint address, ushort port)
        {
            Address = address;
            Port = port;
        }

        public uint Address { get; }

        public ushort Port { get; }

        public static bool TryCreate(EndPoint endpoint, out EndpointKey key)
        {
            key = default;
            if (endpoint is ReusableIpv4EndPoint reusable)
            {
                key = new EndpointKey(reusable.Address, reusable.Port);
                return true;
            }

            if (!(endpoint is IPEndPoint ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return false;

            Span<byte> bytes = stackalloc byte[16];
            if (!ip.Address.TryWriteBytes(bytes, out int written) || written != 4)
                return false;

            uint address = (uint)(bytes[0]
                | (bytes[1] << 8)
                | (bytes[2] << 16)
                | (bytes[3] << 24));
            key = new EndpointKey(address, (ushort)ip.Port);
            return true;
        }

        public bool Equals(EndpointKey other) => Address == other.Address && Port == other.Port;

        public override bool Equals(object? obj) => obj is EndpointKey other && Equals(other);

        public override int GetHashCode() => unchecked((int)(Address * 397u) ^ Port);

        public static bool Equals(EndPoint left, EndPoint right)
            => TryCreate(left, out EndpointKey a)
            && TryCreate(right, out EndpointKey b)
            && a.Equals(b);
    }
}
