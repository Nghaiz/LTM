using System;
using Ironfront.Net.LoadHarness;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.LoadHarness.Tests
{
    /// <summary>
    /// <c>--server-id</c>: the harness against a game server that has registered with a master.
    /// </summary>
    /// <remarks>
    /// A registered server adopts the id the master gave it and refuses any other
    /// (<c>TicketValidator.TryAdmit</c>, <c>WrongServer</c>). Every ticket the harness minted
    /// carried server id 0, so it could only measure a standalone server — never the deployed
    /// one. These tests pin that the flag reaches the signed bytes and that the real validator
    /// then admits them, in both directions.
    /// </remarks>
    public class ServerIdTicketTests
    {
        private const string Secret = "server-id-ticket-tests-secret";
        private const ushort RegisteredId = 38;

        [Fact]
        public void TheFlagParsesAndDefaultsToZero()
        {
            Assert.True(HarnessOptions.TryParse(new[] { "--clients", "1", "--seconds", "1" },
                out HarnessOptions defaults, out _));
            Assert.Equal((ushort)0, defaults.ServerId);

            Assert.True(HarnessOptions.TryParse(
                new[] { "--clients", "1", "--seconds", "1", "--server-id", "38" },
                out HarnessOptions registered, out string error), error);
            Assert.Equal(RegisteredId, registered.ServerId);
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("65536")]
        [InlineData("abc")]
        public void AnIdOutsideTheWireRangeIsRefused(string value)
        {
            Assert.False(HarnessOptions.TryParse(
                new[] { "--clients", "1", "--seconds", "1", "--server-id", value },
                out _, out string error));
            Assert.Contains("--server-id", error);
        }

        [Fact]
        public void TheMintedTicketCarriesTheId()
        {
            byte[] ticket = JoinTicketSource.Resolve(Secret, RegisteredId).Mint(clientIndex: 0);

            Assert.True(JoinTicket.TryReadFields(ticket, out _, out ushort serverId, out _,
                out _, out _, out _));
            Assert.Equal(RegisteredId, serverId);
        }

        [Fact]
        public void ARegisteredServerAdmitsItsOwnIdAndRefusesZero()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            var validator = new TicketValidator(System.Text.Encoding.UTF8.GetBytes(Secret));
            validator.AdoptServerId(RegisteredId);

            byte[] matching = JoinTicketSource.Resolve(Secret, RegisteredId).Mint(clientIndex: 0);
            Assert.True(validator.TryAdmit(matching, now, out _, out TicketRejection admitted));
            Assert.Equal(TicketRejection.None, admitted);

            byte[] standalone = JoinTicketSource.Resolve(Secret).Mint(clientIndex: 1);
            Assert.False(validator.TryAdmit(standalone, now, out _, out TicketRejection refused));
            Assert.Equal(TicketRejection.WrongServer, refused);
        }
    }
}
