using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A player who is dead when a round resets can deploy in the next round at once.
    /// </summary>
    /// <remarks>
    /// Measured on the Azure Island server on 2026-09-28: the round reset cleared the respawn
    /// gate while the player's body stayed dead, <c>ServerCombatBridge.TryRespawn</c> refused a
    /// death the gate no longer held, and three deploy requests from one client went unanswered
    /// for a whole round.
    /// </remarks>
    public sealed class RespawnAcrossRoundResetTests
    {
        private const ushort Player = 5;
        private const ushort Other = 6;

        [Fact]
        public void APlayerDeadAtTheResetMayDeployAtOnce()
        {
            var gate = new ServerRespawnGate();
            gate.MarkDeath(Player, 100f);

            // One second after the death -- well inside the respawn delay -- the round resets.
            gate.ResetForNewRound(new[] { Player }, 101f);

            Assert.True(gate.MayRespawn(Player, 101f));
            Assert.Equal(ActorLifePhase.RespawnPending, gate.PhaseOf(Player, 101f));
            Assert.Equal(0f, gate.SecondsUntilRespawn(Player, 101f));
        }

        [Fact]
        public void APlainResetIsWhatLockedThemOut()
        {
            // Reset alone forgets the death; the body is still dead, and the gate now refuses
            // the only request that could bring it back. The reason ResetForNewRound exists.
            var gate = new ServerRespawnGate();
            gate.MarkDeath(Player, 100f);

            gate.Reset();

            Assert.False(gate.MayRespawn(Player, 1000f));
        }

        [Fact]
        public void EveryoneNotStillDeadIsForgotten()
        {
            var gate = new ServerRespawnGate();
            gate.MarkDeath(Player, 100f);
            gate.MarkDeath(Other, 100f);

            gate.ResetForNewRound(new[] { Player }, 101f);

            Assert.True(gate.IsDead(Player));
            Assert.False(gate.IsDead(Other));
            Assert.False(gate.MayRespawn(Other, 1000f));
        }

        [Fact]
        public void TheCarriedDeathEndsOnRespawnLikeAnyOther()
        {
            var gate = new ServerRespawnGate();
            gate.ResetForNewRound(new[] { Player }, 50f);

            gate.MarkRespawned(Player);

            Assert.False(gate.IsDead(Player));
            Assert.True(gate.TryBeginDeath(Player, 60f));
        }

        [Fact]
        public void NoListAndOutOfRangeIdsAreIgnored()
        {
            var gate = new ServerRespawnGate();

            gate.ResetForNewRound(null!, 10f);
            gate.ResetForNewRound(new ushort[] { ushort.MaxValue }, 10f);

            Assert.False(gate.IsDead(Player));
        }
    }
}
