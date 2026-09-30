using NUnit.Framework;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// The bot-release gate across a round reset: players carried into the new round alive
    /// anchor it, nobody carried leaves it for the first deploy.
    /// </summary>
    /// <remarks>
    /// B2, live test 2026-09-30: the reset re-armed the gate and only a deploy anchored it, so a
    /// round that ended with its players alive opened a round in which no bot was ever released.
    /// <c>NetBotRelease</c> is static, so every test leaves it re-armed.
    /// </remarks>
    public sealed class BotReleaseRoundResetTests
    {
        [SetUp]
        public void SetUp() => NetBotRelease.ResetForNewRound();

        [TearDown]
        public void TearDown() => NetBotRelease.ResetForNewRound();

        [Test]
        public void PlayersCarriedIntoTheRoundAliveStartTheReleaseClock()
        {
            Assert.IsFalse(NetBotRelease.HasPlayerSpawned, "a re-armed gate has no anchor");

            NetBotRelease.NotifyPlayersCarriedIntoRound(2);

            Assert.IsTrue(NetBotRelease.HasPlayerSpawned);
            Assert.AreEqual(NetBotRelease.DelaySeconds, NetBotRelease.SecondsUntilRelease, 0.5f);
        }

        [Test]
        public void NobodyCarriedLeavesTheGateForTheFirstDeploy()
        {
            NetBotRelease.NotifyPlayersCarriedIntoRound(0);

            Assert.IsFalse(NetBotRelease.HasPlayerSpawned);
            Assert.IsTrue(float.IsPositiveInfinity(NetBotRelease.SecondsUntilRelease));
        }

        [Test]
        public void AnAnchorAlreadySetIsNotMovedLater()
        {
            NetBotRelease.NotifyPlayerSpawned();
            float before = NetBotRelease.SecondsUntilRelease;

            NetBotRelease.NotifyPlayersCarriedIntoRound(3);

            Assert.LessOrEqual(NetBotRelease.SecondsUntilRelease, before);
        }
    }
}
