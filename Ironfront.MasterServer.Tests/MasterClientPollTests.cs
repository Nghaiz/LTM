using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.MasterServer.Tests
{
    public sealed class MasterClientPollTests
    {
        [Fact]
        public async Task ResponseCompletesOnlyWhenPollRuns()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new MasterClient.MasterClient();

            Task connect = client.ConnectAsync("127.0.0.1", port);
            using TcpClient server = await listener.AcceptTcpClientAsync();
            await connect;

            Task<RoomInfo[]> request = client.GetRoomsAsync();
            await ReadFrameAsync(server);
            await WriteFrameAsync(server, MspMessageType.RoomListResponse, "{\"rooms\":[]}");
            await Task.Delay(20);

            Assert.False(request.IsCompleted);
            int pollThread = Environment.CurrentManagedThreadId;
            client.Poll();
            Assert.Equal(pollThread, Environment.CurrentManagedThreadId);
            Assert.Empty(await request);
        }

        [Fact]
        public async Task ErrorPushCompletesPendingRequestDuringPoll()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var client = new MasterClient.MasterClient();

            Task connect = client.ConnectAsync("127.0.0.1", port);
            using TcpClient server = await listener.AcceptTcpClientAsync();
            await connect;

            int events = 0;
            client.OnError += (code, message) =>
            {
                Assert.Equal(1003, code);
                Assert.Equal("Login is required.", message);
                events++;
            };

            Task<RoomInfo[]> request = client.GetRoomsAsync();
            await ReadFrameAsync(server);
            await WriteFrameAsync(server, MspMessageType.ErrorPush, "{\"code\":1003,\"message\":\"Login is required.\"}");
            await Task.Delay(20);

            Assert.False(request.IsCompleted);
            client.Poll();
            MasterServerException error = await Assert.ThrowsAsync<MasterServerException>(() => request);
            Assert.Equal(1003, error.ErrorCode);
            Assert.Equal(1, events);
        }

        private static async Task ReadFrameAsync(TcpClient client)
        {
            var prefix = new byte[4];
            await ReadExactlyAsync(client.GetStream(), prefix);
            int length = checked((int)Endian.ReadU32BE(prefix, 0));
            var payload = new byte[length];
            await ReadExactlyAsync(client.GetStream(), payload);
        }

        private static async Task WriteFrameAsync(TcpClient client, MspMessageType type, string json)
        {
            byte[] body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[MspFrame.FrameSizeFor(body.Length)];
            Assert.Equal(frame.Length, MspFrame.Write(frame, type, body));
            await client.GetStream().WriteAsync(frame, 0, frame.Length);
        }

        private static async Task ReadExactlyAsync(NetworkStream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset);
                if (read == 0) throw new InvalidOperationException("Peer closed the connection.");
                offset += read;
            }
        }
    }
}
