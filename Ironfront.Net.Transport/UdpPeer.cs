using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport.Simulation;

namespace Ironfront.Net.Transport
{
    /// <summary>
    /// Non-blocking UDP datagram peer. It owns only socket plumbing; reliability and
    /// connection state belong to <see cref="Connection"/>.
    /// </summary>
    /// <remarks>
    /// <para>Call <see cref="Poll"/> from the owning thread once per frame.</para>
    /// <para>
    /// Packet memory is backed by a pool and is valid only during the
    /// <see cref="PacketReceived"/> callback. Copy it if it must outlive that callback.
    /// </para>
    /// </remarks>
    public sealed class UdpPeer : IDisposable
    {
        private readonly Socket _socket;
        private readonly BufferPool _pool;
        private readonly NetworkSimulator<EndPoint> _simulator;
#if NET8_0_OR_GREATER
        private readonly SocketAddress _receiveSocketAddress =
            new SocketAddress(AddressFamily.InterNetwork, 16);
        private readonly Dictionary<EndpointKey, SocketAddress> _sendSocketAddresses
            = new Dictionary<EndpointKey, SocketAddress>();
#endif
        private readonly Action<byte[], int, EndPoint> _rawSendCallback;
        private readonly ReusableIpv4EndPoint _receiveEndpoint = new ReusableIpv4EndPoint();
        private EndPoint? _connectedEndpoint;
        private bool _disposed;

        public UdpPeer(
            int bindPort,
            SimulatorConfig? simulatorConfig = null,
            int poolCapacity = 256)
        {
            if (bindPort < 0 || bindPort > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(bindPort));

            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                Blocking = false,
                ReceiveBufferSize = 1 << 20,
                SendBufferSize = 1 << 20,
            };

            DisableIcmpPortUnreachable();
            _socket.Bind(new IPEndPoint(IPAddress.Any, bindPort));

            _pool = new BufferPool(poolCapacity, ProtocolConstants.MTU_SAFE);
            _simulator = new NetworkSimulator<EndPoint>(
                simulatorConfig ?? SimulatorConfig.FromEnvironment(), _pool);
            _rawSendCallback = RawSendPooled;
        }

        /// <summary>Raised for a valid GSP datagram. Memory is callback-scoped.</summary>
        public event Action<GspHeader, ReadOnlyMemory<byte>, EndPoint>? PacketReceived;

        public EndPoint LocalEndPoint => _socket.LocalEndPoint!;

        public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

        public BufferPool Pool => _pool;

        public long DroppedBySimulator => _simulator.DroppedCount;

        /// <summary>
        /// Connects the UDP socket to one peer. The OS then supplies the remote endpoint and the
        /// send/receive path can use the allocation-free connected socket overloads.
        /// </summary>
        public void Connect(EndPoint destination)
        {
            ThrowIfDisposed();
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            _socket.Connect(destination);
            _connectedEndpoint = destination is IPEndPoint ip
                ? new IPEndPoint(ip.Address, ip.Port)
                : destination;
        }

        /// <summary>
        /// Sends a complete datagram. When simulation is enabled the simulator owns a copy and
        /// the real socket is written later from <see cref="Poll"/>.
        /// </summary>
        public void Send(ReadOnlySpan<byte> datagram, EndPoint destination, double nowMs)
        {
            ThrowIfDisposed();
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (datagram.Length < GspHeader.Size || datagram.Length > ProtocolConstants.MTU_SAFE)
                throw new ArgumentOutOfRangeException(nameof(datagram));

            if (_simulator.ShouldSend(datagram, destination, nowMs))
                RawSend(datagram.ToArray(), datagram.Length, destination);
        }

        /// <summary>Allocation-free send overload for pooled transport buffers.</summary>
        public void Send(byte[] datagram, int length, EndPoint destination, double nowMs)
        {
            ThrowIfDisposed();
            if (datagram == null) throw new ArgumentNullException(nameof(datagram));
            if (length < GspHeader.Size || length > ProtocolConstants.MTU_SAFE || length > datagram.Length)
                throw new ArgumentOutOfRangeException(nameof(length));
            if (destination == null) throw new ArgumentNullException(nameof(destination));

            if (_simulator.ShouldSend(datagram.AsSpan(0, length), destination, nowMs))
                RawSend(datagram, length, destination);
        }

        /// <summary>Flushes delayed packets, then drains all currently available datagrams.</summary>
        public void Poll(double nowMs)
        {
            ThrowIfDisposed();
            _simulator.Flush(nowMs, _rawSendCallback);

            // Socket.Available allocates on the netstandard2.1/.NET 8 socket path used by
            // Unity-compatible builds. Poll(0, SelectRead) checks readiness without creating
            // a per-frame managed value, while the socket remains non-blocking below.
            while (_socket.Poll(0, SelectMode.SelectRead))
            {
                byte[] buffer = _pool.Rent();
                int received;
                EndPoint remote = _receiveEndpoint;

                try
                {
                    if (_connectedEndpoint != null)
                    {
                        received = _socket.Receive(buffer, 0, buffer.Length, SocketFlags.None);
                        remote = _connectedEndpoint;
                    }
                    else
                    {
#if NET8_0_OR_GREATER
                        received = _socket.ReceiveFrom(
                            buffer.AsSpan(), SocketFlags.None, _receiveSocketAddress);
                        _receiveEndpoint.Create(_receiveSocketAddress);
                        remote = _receiveEndpoint;
#else
                        received = _socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref remote);
#endif
                    }
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.WouldBlock)
                {
                    _pool.Return(buffer);
                    break;
                }
                catch (SocketException ex)
                {
                    NetLog.Warn($"UDP receive failed: {ex.SocketErrorCode}");
                    _pool.Return(buffer);
                    continue;
                }

                if (GspHeader.TryParse(buffer.AsSpan(0, received), out GspHeader header))
                {
                    ReadOnlyMemory<byte> view = new ReadOnlyMemory<byte>(buffer, 0, received);
                    try
                    {
                        PacketReceived?.Invoke(header, view, remote);
                    }
                    finally
                    {
                        _pool.Return(buffer);
                    }
                }
                else
                {
                    // Invalid headers are deliberately silent: replying would turn a port scan
                    // or forged packet into an amplification primitive.
                    _pool.Return(buffer);
                }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _simulator.Clear();
#if NET8_0_OR_GREATER
            _sendSocketAddresses.Clear();
#endif
            _socket.Dispose();
        }

        private void RawSendPooled(byte[] data, int length, EndPoint destination)
            => RawSend(data, length, destination);

        private void RawSend(byte[] datagram, int length, EndPoint destination)
        {
            try
            {
                if (_connectedEndpoint != null)
                    _socket.Send(datagram, 0, length, SocketFlags.None);
                else
#if NET8_0_OR_GREATER
                    _socket.SendTo(
                        datagram.AsSpan(0, length),
                        SocketFlags.None,
                        GetSendSocketAddress(destination));
#else
                    _socket.SendTo(datagram, 0, length, SocketFlags.None, destination);
#endif
            }
            catch (SocketException ex)
            {
                NetLog.Warn($"UDP send failed: {ex.SocketErrorCode}");
            }
        }

#if NET8_0_OR_GREATER
        private SocketAddress GetSendSocketAddress(EndPoint destination)
        {
            if (destination is ReusableIpv4EndPoint reusable)
                return reusable.SerializedAddress;

            if (EndpointKey.TryCreate(destination, out EndpointKey key)
                && destination is IPEndPoint ip)
            {
                if (_sendSocketAddresses.TryGetValue(key, out SocketAddress? address))
                    return address;

                address = ip.Serialize();
                _sendSocketAddresses.Add(key, address);
                return address;
            }

            throw new NotSupportedException("Only IPv4 endpoints are supported.");
        }
#endif

        private void DisableIcmpPortUnreachable()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

            // SIO_UDP_CONNRESET prevents an ICMP Port Unreachable from aborting the next
            // ReceiveFrom call. This is a Windows socket behaviour, not a protocol response.
            const int sioUdpConnReset = -1744830452;
            _socket.IOControl(sioUdpConnReset, new byte[] { 0, 0, 0, 0 }, null);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(UdpPeer));
        }
    }
}
