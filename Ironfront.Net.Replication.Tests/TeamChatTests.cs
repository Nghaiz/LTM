using System;
using System.Text;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Team chat (owner request 2026-09-29): a line said to the team reaches the speaker's side
    /// and nobody on the other one, and the channel survives both routers.
    /// </summary>
    public sealed class TeamChatTests
    {
        private const byte Blue = TeamId.Team0;
        private const byte Red = TeamId.Team1;

        // ---------------------------------------------------------------- who hears it

        [Fact]
        public void AnAllChatLine_ReachesBothSides()
        {
            Assert.True(ChatAudience.Hears(ChatChannel.All, Blue, Blue, isSpeaker: false));
            Assert.True(ChatAudience.Hears(ChatChannel.All, Blue, Red, isSpeaker: false));
        }

        /// <summary>The privacy the owner asked for: the other side never receives a team line.</summary>
        [Fact]
        public void ATeamLine_ReachesTheSpeakersSide_AndNeverTheOtherOne()
        {
            Assert.True(ChatAudience.Hears(ChatChannel.Team, Blue, Blue, isSpeaker: false));
            Assert.False(ChatAudience.Hears(ChatChannel.Team, Blue, Red, isSpeaker: false));
            Assert.False(ChatAudience.Hears(ChatChannel.Team, Red, Blue, isSpeaker: false));
            Assert.False(ChatAudience.Hears(ChatChannel.Team, Blue, TeamId.None, isSpeaker: false));
        }

        [Fact]
        public void TheSpeakerAlwaysHearsTheirOwnLine()
        {
            Assert.True(ChatAudience.Hears(ChatChannel.Team, Blue, Blue, isSpeaker: true));
            Assert.True(ChatAudience.Hears(ChatChannel.Team, TeamId.None, TeamId.None, isSpeaker: true));
        }

        /// <summary>A speaker with no side has no team to address; guessing one would leak.</summary>
        [Fact]
        public void ASpeakerWithNoSide_ReachesNobodyElseOnTheTeamChannel()
        {
            Assert.False(ChatAudience.Hears(ChatChannel.Team, TeamId.None, TeamId.None, isSpeaker: false));
            Assert.False(ChatAudience.Hears(ChatChannel.Team, TeamId.None, Blue, isSpeaker: false));
        }

        [Fact]
        public void AChannelThisBuildDoesNotDefine_ReachesNobodyButTheSpeaker()
        {
            Assert.False(ChatAudience.Hears((ChatChannel)7, Blue, Blue, isSpeaker: false));
            Assert.True(ChatAudience.Hears((ChatChannel)7, Blue, Blue, isSpeaker: true));
        }

        // ---------------------------------------------------------------- the server router

        [Fact]
        public void TheServerRouter_HandsOnTheChannelTheClientAskedFor()
        {
            var router = new ServerMessageRouter();
            var handler = new RecordingChatHandler();
            router.Chat = handler;

            byte[] body = new byte[ChatTextMessage.MaxClientBodySize];
            int length = ChatTextMessage.WriteClient(body, ChatChannel.Team, Encoding.UTF8.GetBytes("push B"));
            Assert.True(length > 0);

            Assert.Equal(1, router.Route(Frame((byte)ClientMessageType.Chat, body, length), new ClientSession(7, 3)));

            Assert.Equal(ChatChannel.Team, handler.LastChannel);
            Assert.Equal("push B", handler.LastText);
        }

        /// <summary>
        /// An unknown channel is malformed, never widened to everybody: that fallback is the
        /// one place a team line could leak.
        /// </summary>
        [Fact]
        public void TheServerRouter_RefusesAnUnknownChannel()
        {
            var router = new ServerMessageRouter();
            var handler = new RecordingChatHandler();
            router.Chat = handler;

            byte[] body = { 9, 2, (byte)'h', (byte)'i' };

            Assert.Equal(0, router.Route(Frame((byte)ClientMessageType.Chat, body, body.Length), new ClientSession(7, 3)));

            Assert.Equal(1, router.MalformedMessages);
            Assert.Null(handler.LastText);
        }

        // ---------------------------------------------------------------- the client router

        [Fact]
        public void TheClientRouter_RaisesTheChannelTheServerSent()
        {
            var router = new ClientMessageRouter();
            ChatChannel? heard = null;
            string? text = null;
            router.OnChat += (actor, channel, line) => { heard = channel; text = line; };

            var payload = new byte[ProtocolConstants.MAX_PAYLOAD];
            var scratch = new byte[ChatTextMessage.MaxServerBodySize];
            int written = ServerEventWriter.WriteChat(payload, scratch, 5, ChatChannel.Team, Encoding.UTF8.GetBytes("on me"));
            Assert.True(written > 0);

            Assert.Equal(1, router.Route(new ReadOnlySpan<byte>(payload, 0, written)));

            Assert.Equal(ChatChannel.Team, heard);
            Assert.Equal("on me", text);
        }

        [Fact]
        public void TheClientRouter_RefusesAnUnknownChannel()
        {
            var router = new ClientMessageRouter();
            int raised = 0;
            router.OnChat += (_, _, _) => raised++;

            byte[] body = { 5, 9, 2, (byte)'h', (byte)'i' };
            var payload = new byte[ProtocolConstants.MAX_PAYLOAD];
            var writer = new PayloadFrameWriter(payload, ChannelId.ReliableOrdered);
            Assert.True(writer.WriteMessage(ServerMessageType.Chat, body));
            Assert.True(writer.TryFinish(out int total));

            Assert.Equal(0, router.Route(new ReadOnlySpan<byte>(payload, 0, total)));

            Assert.Equal(0, raised);
            Assert.Equal(1, router.MalformedMessages);
        }

        private static byte[] Frame(byte msgType, byte[] body, int length)
        {
            var buffer = new byte[ProtocolConstants.MAX_PAYLOAD];
            var writer = new PayloadFrameWriter(buffer, ChannelId.ReliableOrdered);

            Assert.True(writer.WriteMessage(msgType, new ReadOnlySpan<byte>(body, 0, length)));
            Assert.True(writer.TryFinish(out int total));

            return new ReadOnlySpan<byte>(buffer, 0, total).ToArray();
        }

        private sealed class RecordingChatHandler : IChatHandler
        {
            public ChatChannel? LastChannel { get; private set; }

            public string? LastText { get; private set; }

            public void OnChat(ClientSession session, ChatChannel channel, ReadOnlySpan<byte> textUtf8)
            {
                LastChannel = channel;
                LastText = ChatTextMessage.TextOf(textUtf8);
            }
        }
    }
}
