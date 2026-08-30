using Ironfront.Net.Transport;
using Ironfront.Net.Unity.Client;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The slot that carries a connected game transport from the shell scene into the map
    /// scene. P8 task 3.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every assertion here is about an ownership rule rather than about networking, and each
    /// one names a failure that would otherwise be invisible until a second match: a socket
    /// adopted twice, a stale socket adopted by an unrelated scene, or a connection whose
    /// <c>ConnectResult</c> was dropped on the way across — the last of which leaves a client
    /// with a live link and a <c>ConnectionId</c> of 0, which reads on screen as a server that
    /// never answered.
    /// </para>
    /// <para>
    /// <b>The slot is static, so every test clears it first.</b> xUnit runs the methods of one
    /// class sequentially, but not in a defined order, and a residue left by one test would be
    /// adopted by the next — which is exactly the production bug <c>Clear</c> exists to prevent,
    /// and it would show up here as an unrelated failure rather than as itself.
    /// </para>
    /// </remarks>
    public sealed class MatchTransportHandoffTests
    {
        public MatchTransportHandoffTests() => MatchTransportHandoff.Clear();

        [Fact]
        public void WithNothingOnOfferTheMapSceneDialsForItself()
        {
            MatchTransportHandoff.Clear();

            Assert.False(MatchTransportHandoff.HasOffer);
            Assert.False(MatchTransportHandoff.TryTake(out ITransportClient taken, out ConnectResult _));
            Assert.Null(taken);
        }

        [Fact]
        public void AnOfferCarriesBothTheSocketAndTheAcceptItArrivedWith()
        {
            var transport = new FakeTransportClient();
            var result = new ConnectResult(connectionId: 7, serverTick: 1234);

            MatchTransportHandoff.Offer(transport, result);

            Assert.True(MatchTransportHandoff.HasOffer);
            Assert.True(MatchTransportHandoff.TryTake(out ITransportClient taken, out ConnectResult carried));

            Assert.Same(transport, taken);

            // Without these two the adopting bootstrap has no connection id and no server tick to
            // seed the prediction clock with, because the accept fired before it existed.
            Assert.Equal(7, carried.ConnectionId);
            Assert.Equal(1234u, carried.ServerTick);
        }

        [Fact]
        public void TakingTheOfferConsumesIt()
        {
            MatchTransportHandoff.Offer(new FakeTransportClient(), default);

            Assert.True(MatchTransportHandoff.TryTake(out ITransportClient _, out ConnectResult _));

            // A second map load in the same process -- a rematch, or a scene reloaded by hand --
            // must dial for itself rather than adopt the socket the last match is still using.
            Assert.False(MatchTransportHandoff.HasOffer);
            Assert.False(MatchTransportHandoff.TryTake(out ITransportClient _, out ConnectResult _));
        }

        [Fact]
        public void AFailedJunctionsOfferIsNotLeftForTheNextSceneToAdopt()
        {
            MatchTransportHandoff.Offer(new FakeTransportClient(), new ConnectResult(9, 5));

            MatchTransportHandoff.Clear();

            Assert.False(MatchTransportHandoff.HasOffer);
            Assert.False(MatchTransportHandoff.TryTake(out ITransportClient _, out ConnectResult _));
        }

        [Fact]
        public void ASecondOfferReplacesAnUnclaimedFirstOne()
        {
            var abandoned = new FakeTransportClient();
            var live = new FakeTransportClient();

            MatchTransportHandoff.Offer(abandoned, new ConnectResult(1, 1));
            MatchTransportHandoff.Offer(live, new ConnectResult(2, 2));

            Assert.True(MatchTransportHandoff.TryTake(out ITransportClient taken, out ConnectResult carried));

            // Keeping the first would hand the next match the socket of a connection that never
            // reached a scene -- a connect that failed after the accept, or a load a disconnect
            // cancelled.
            Assert.Same(live, taken);
            Assert.Equal(2, carried.ConnectionId);
        }
    }
}
