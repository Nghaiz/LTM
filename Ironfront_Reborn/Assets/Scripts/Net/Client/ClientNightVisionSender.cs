using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Sends <c>C_NIGHT_VISION</c> (14.0.4): this player's night vision went on or off, once when
    /// the connection opens (so the server knows this game reports it) and at every toggle.
    /// Achievements v2: NAKED EYE and CREATURE OF THE NIGHT.
    /// </summary>
    internal sealed class ClientNightVisionSender
    {
        private readonly NetClientBootstrap _client;
        private readonly byte[] _body = new byte[1];
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        public ClientNightVisionSender(NetClientBootstrap client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>Messages sent; a diagnostic.</summary>
        public int Sent { get; private set; }

        public void Send(bool on)
        {
            if (!_client.IsConnected) return;
            _body[0] = on ? (byte)1 : (byte)0;

            var writer = new PayloadFrameWriter(_payload, ChannelId.ReliableOrdered);
            if (!writer.WriteMessage(ClientMessageType.NightVision, new ReadOnlySpan<byte>(_body, 0, 1))) return;
            if (!writer.TryFinish(out int total)) return;

            _client.Send(ChannelId.ReliableOrdered, new ReadOnlySpan<byte>(_payload, 0, total), reliable: true);
            Sent++;
        }
    }
}
