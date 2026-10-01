using System;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Ironfront.MasterServer.Auth;
using Ironfront.MasterServer.Net;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests.Net
{
    /// <summary>
    /// <see cref="TcpListenerHostOptions.TrustProxyProtocol"/> over real sockets: the per-IP
    /// limit and the login rate count the client a PROXY header names, not the proxy.
    /// </summary>
    /// <remarks>
    /// Every connection here comes from 127.0.0.1, exactly as every connection on fly comes
    /// from the edge's one address. That is the whole point of the setup: without the header,
    /// a per-IP limit of one would refuse the second connection in every test below.
    /// </remarks>
    [Collection(SocketTestCollection.Name)]
    public class ProxyProtocolHostTests
    {
        private static byte[] HeartbeatFrame()
        {
            var buffer = new byte[MspFrame.MinFrameSize];
            MspFrame.Write(buffer, MspMessageType.Heartbeat, ReadOnlySpan<byte>.Empty);
            return buffer;
        }

        private static async Task<TcpClient> ConnectAsync(MasterHostHarness harness, byte[] firstBytes)
        {
            TcpClient client = await harness.ConnectAsync();
            await client.GetStream().WriteAsync(firstBytes);
            return client;
        }

        private static MasterHostHarness Harness(int maxPerIp = 1) => new MasterHostHarness(o =>
        {
            o.TrustProxyProtocol  = true;
            o.MaxConnectionsPerIp = maxPerIp;
        });

        [Fact]
        public async Task ConnectionsAreCountedOnTheAddressTheirHeaderNames()
        {
            await using MasterHostHarness harness = Harness(maxPerIp: 1);

            await ConnectAsync(harness, ProxyHeaders.V2Tcp4("203.0.113.1", 40001));
            await ConnectAsync(harness, ProxyHeaders.V2Tcp4("203.0.113.2", 40002));
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 2),
                "two clients the headers name apart were not both admitted");
            Assert.Equal(0, harness.Host.TotalRejectedByIpLimit);

            // A second connection from a client already at its limit is refused, after the header.
            await ConnectAsync(harness, ProxyHeaders.V2Tcp4("203.0.113.1", 40003));
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalRejectedByIpLimit == 1),
                "a second connection for the same client was not refused");
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 2));
        }

        [Fact]
        public async Task ASlotIsReleasedOnTheAddressItWasTakenOn()
        {
            await using MasterHostHarness harness = Harness(maxPerIp: 1);

            TcpClient first = await ConnectAsync(harness, ProxyHeaders.V2Tcp4("198.51.100.9", 50000));
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1));
            first.Dispose();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 0));

            await ConnectAsync(harness, ProxyHeaders.V2Tcp4("198.51.100.9", 50001));
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1),
                "the client's slot was not given back when its first connection closed");
            Assert.Equal(0, harness.Host.TotalRejectedByIpLimit);
        }

        [Fact]
        public async Task AConnectionThatDoesNotOpenWithAHeaderIsClosed()
        {
            await using MasterHostHarness harness = Harness();

            await ConnectAsync(harness, HeartbeatFrame());

            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalProxyHeaderRejections == 1),
                "a connection with no PROXY header was not refused");
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 0));
            Assert.Equal(0, harness.Host.TotalHeartbeats);
        }

        /// <summary>
        /// A client cannot name its own address: the proxy's header comes first, so a second
        /// header a client writes after it is read as MSP, where its first four bytes declare a
        /// 218 MB frame and the connection is closed.
        /// </summary>
        [Fact]
        public async Task ASecondHeaderIsNotReadAsOne()
        {
            await using MasterHostHarness harness = Harness(maxPerIp: 1);

            // Fills the one slot of the address the client is about to smuggle.
            await ConnectAsync(harness, ProxyHeaders.V2Tcp4("192.0.2.77", 2000));
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1));

            await ConnectAsync(harness, ProxyHeaders.Concat(
                ProxyHeaders.V2Tcp4("203.0.113.50", 1000), ProxyHeaders.V2Tcp4("192.0.2.77", 2001)));

            // Counted on the first header's address, so no per-IP refusal although the smuggled
            // address is full, and then closed by the frame cap rather than as a bad header.
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalDisconnected == 1),
                "the connection carrying a second header was not closed");
            Assert.Equal(0, harness.Host.TotalRejectedByIpLimit);
            Assert.Equal(0, harness.Host.TotalProxyHeaderRejections);
            Assert.Equal(1, harness.Host.ConnectionCount);
        }

        /// <summary>
        /// A health check connects and leaves without a byte. It is no PROXY failure and keeps
        /// no slot.
        /// </summary>
        [Fact]
        public async Task ASilentConnectionIsNoFailureAndKeepsNoSlot()
        {
            await using MasterHostHarness harness = Harness(maxPerIp: 1);

            TcpClient probe = await harness.ConnectAsync();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1));
            probe.Dispose();
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 0));
            Assert.Equal(0, harness.Host.TotalProxyHeaderRejections);

            await ConnectAsync(harness, ProxyHeaders.V2Tcp4("127.0.0.1", 50000));
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1),
                "the silent connection left a slot taken");
        }

        [Fact]
        public async Task AFrameInTheSameWriteAsTheHeaderIsHandled()
        {
            await using MasterHostHarness harness = Harness();

            await ConnectAsync(harness, ProxyHeaders.Concat(ProxyHeaders.V2Tcp4("203.0.113.3", 7000), HeartbeatFrame()));

            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalHeartbeats == 1),
                "the frame that came in behind the header was lost");
        }

        [Fact]
        public async Task AHeaderArrivingInPiecesIsStillRead()
        {
            await using MasterHostHarness harness = Harness();
            byte[] header = ProxyHeaders.V1("PROXY TCP4 198.51.100.40 10.0.0.1 35646 27000\r\n");

            TcpClient client = await harness.ConnectAsync();
            NetworkStream stream = client.GetStream();
            for (int i = 0; i < header.Length; i += 7)
            {
                await stream.WriteAsync(header.AsMemory(i, Math.Min(7, header.Length - i)));
                await stream.FlushAsync();
                await Task.Delay(5);
            }
            await stream.WriteAsync(HeartbeatFrame());

            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalHeartbeats == 1));
            Assert.Equal(0, harness.Host.TotalProxyHeaderRejections);
        }

        /// <summary>
        /// A header without an address (LOCAL, as for a health check) is the proxy itself, and is
        /// counted on the proxy's own address.
        /// </summary>
        [Fact]
        public async Task ALocalHeaderIsCountedOnTheProxysAddress()
        {
            await using MasterHostHarness harness = Harness(maxPerIp: 1);
            byte[] local = ProxyHeaders.V2(ProxyHeaders.Local, ProxyHeaders.Unspec, Array.Empty<byte>());

            await ConnectAsync(harness, local);
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.ConnectionCount == 1));

            await ConnectAsync(harness, local);
            Assert.True(await MasterHostHarness.WaitUntilAsync(() => harness.Host.TotalRejectedByIpLimit == 1));
        }

        /// <summary>
        /// The production failure itself: the login rate. Behind fly every player shared one
        /// budget of five attempts a minute, so five wrong passwords from anyone locked everyone
        /// out. With the header each client spends its own.
        /// </summary>
        [Fact]
        public async Task EachClientSpendsItsOwnLoginBudget()
        {
            await using var server = new Phase03ServerHarness(
                configure: o => o.TrustProxyProtocol = true);

            using TcpClient first = new TcpClient();
            await first.ConnectAsync(System.Net.IPAddress.Loopback, server.Port);
            await first.GetStream().WriteAsync(ProxyHeaders.V2Tcp4("198.51.100.1", 41000));

            for (int attempt = 0; attempt < AuthService.DefaultRatePerMinute; attempt++)
                Assert.Equal(ErrorCode.WrongCredentials, await LoginAsync(first));
            Assert.Equal(ErrorCode.RateLimited, await LoginAsync(first));

            using TcpClient second = new TcpClient();
            await second.ConnectAsync(System.Net.IPAddress.Loopback, server.Port);
            await second.GetStream().WriteAsync(ProxyHeaders.V2Tcp4("198.51.100.2", 41001));

            Assert.Equal(ErrorCode.WrongCredentials, await LoginAsync(second));

            // And the budget is the client's, not the connection's: the first client again, on a
            // new connection, is still limited.
            using TcpClient third = new TcpClient();
            await third.ConnectAsync(System.Net.IPAddress.Loopback, server.Port);
            await third.GetStream().WriteAsync(ProxyHeaders.V2Tcp4("198.51.100.1", 41002));

            Assert.Equal(ErrorCode.RateLimited, await LoginAsync(third));
        }

        /// <summary>Sends a LOGIN_REQ for an account that does not exist and reads the code back.</summary>
        private static async Task<ErrorCode> LoginAsync(TcpClient client)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                username = "nobody",
                passwordHash = new string('a', 64),
                clientVersion = (int)ProtocolConstants.PROTOCOL_VERSION,
            });
            var frame = new byte[MspFrame.FrameSizeFor(body.Length)];
            MspFrame.Write(frame, MspMessageType.LoginRequest, body);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(frame);

            var reader = new MspFrameReader();
            var buffer = new byte[4096];
            while (true)
            {
                int read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                if (read == 0) throw new InvalidOperationException("the server closed the connection");
                if (TryReadLoginCode(reader, buffer, read, out ErrorCode code)) return code;
            }
        }

        // Synchronous because a span cannot live in an async method on C# 12.
        private static bool TryReadLoginCode(MspFrameReader reader, byte[] buffer, int count, out ErrorCode code)
        {
            code = default;
            reader.Append(buffer.AsSpan(0, count));
            if (reader.TryReadFrame(out MspMessageType type, out ReadOnlySpan<byte> response) != MspReadResult.Frame)
                return false;
            Assert.Equal(MspMessageType.LoginResponse, type);
            using JsonDocument json = JsonDocument.Parse(Encoding.UTF8.GetString(response));
            code = (ErrorCode)json.RootElement.GetProperty("errorCode").GetUInt16();
            return true;
        }
    }
}
