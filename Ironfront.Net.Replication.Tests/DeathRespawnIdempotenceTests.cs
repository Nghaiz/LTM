using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Protocol-10 handoff § 7 and § 10.3: one death edge per life, and a respawn that carries
    /// nothing over from the life before it.
    /// </summary>
    /// <remarks>
    /// <b>The gate was already idempotent before this, and that was the defect.</b>
    /// <c>MarkDeath</c> ignored a second stamp — correctly, so the countdown did not jump — but
    /// it returned nothing, so <c>ServerTickLoop.EmitDeath</c> could not tell a first death from
    /// a repeat and broadcast <c>S_DEATH</c>, wrote a killfeed line and moved a ticket on every
    /// call. Two damage paths reach one actor, so the repeat is ordinary. These tests grade the
    /// edge the gate now reports.
    /// </remarks>
    public sealed class DeathRespawnIdempotenceTests
    {
        private const ushort ActorId = 7;

        /// <summary>
        /// § 10.3: the death event fires once. Everything <c>EmitDeath</c> does exactly once per
        /// life sits behind this boolean, so a false that should have been true is a missing
        /// killfeed line and a true that should have been false is a double kill.
        /// </summary>
        [Fact]
        public void TheDeathEdgeIsReportedExactlyOncePerLife()
        {
            var gate = new ServerRespawnGate();

            Assert.True(gate.TryBeginDeath(ActorId, 10f));
            Assert.False(gate.TryBeginDeath(ActorId, 10f));
            Assert.False(gate.TryBeginDeath(ActorId, 10.2f));

            Assert.Equal(2L, gate.DuplicateDeathsSuppressed);
        }

        /// <summary>
        /// A repeat death does not move the countdown. This is <c>MarkDeath</c>'s original
        /// property and it survives the change — a re-stamp would push the respawn out by the gap
        /// between the two damage paths, which reads to the player as the clock going backwards.
        /// </summary>
        [Fact]
        public void ARepeatDeathDoesNotRestampTheClock()
        {
            var gate = new ServerRespawnGate();

            gate.TryBeginDeath(ActorId, 10f);
            gate.TryBeginDeath(ActorId, 12f);

            Assert.True(gate.MayRespawn(ActorId, 10f + ProtocolConstants.RESPAWN_SECONDS));
        }

        /// <summary>
        /// <c>MarkDeath</c> still exists and still behaves, because callers that do not care
        /// about the edge should not have to read a boolean to ignore it.
        /// </summary>
        [Fact]
        public void MarkDeathStillStampsTheSameClock()
        {
            var gate = new ServerRespawnGate();

            gate.MarkDeath(ActorId, 10f);

            Assert.True(gate.IsDead(ActorId));
            Assert.False(gate.TryBeginDeath(ActorId, 10f));
        }

        /// <summary>
        /// § 7's transition, walked end to end:
        /// <c>Alive -&gt; Dead -&gt; RespawnPending -&gt; Alive</c>.
        /// </summary>
        /// <remarks>
        /// The phase is derived from the death stamp rather than stored beside it, so there is no
        /// state machine to get stuck in — which is what makes the transition idempotent rather
        /// than merely careful.
        /// </remarks>
        [Fact]
        public void TheLifeCycleWalksAliveDeadPendingAlive()
        {
            var gate = new ServerRespawnGate();

            Assert.Equal(ActorLifePhase.Alive, gate.PhaseOf(ActorId, 0f));

            gate.TryBeginDeath(ActorId, 10f);
            Assert.Equal(ActorLifePhase.Dead, gate.PhaseOf(ActorId, 10f));
            Assert.Equal(
                ActorLifePhase.Dead,
                gate.PhaseOf(ActorId, 10f + ProtocolConstants.RESPAWN_SECONDS - 0.1f));

            Assert.Equal(
                ActorLifePhase.RespawnPending,
                gate.PhaseOf(ActorId, 10f + ProtocolConstants.RESPAWN_SECONDS));

            gate.MarkRespawned(ActorId);
            Assert.Equal(ActorLifePhase.Alive, gate.PhaseOf(ActorId, 20f));
        }

        /// <summary>
        /// <c>RespawnPending</c> is still dead. § 7 puts the respawn after a REQUEST, so an actor
        /// whose delay has elapsed and who has not asked is a corpse — and a phase that answered
        /// Alive there would let the body be shot for points it cannot lose.
        /// </summary>
        [Fact]
        public void RespawnPendingIsStillDead()
        {
            var gate = new ServerRespawnGate();
            gate.TryBeginDeath(ActorId, 0f);

            float elapsed = ProtocolConstants.RESPAWN_SECONDS + 5f;

            Assert.Equal(ActorLifePhase.RespawnPending, gate.PhaseOf(ActorId, elapsed));
            Assert.True(gate.IsDead(ActorId));
        }

        /// <summary>
        /// § 10.3: a respawn carries over no death state. The next death is a fresh edge, which
        /// is what makes the second life's killfeed line get sent at all.
        /// </summary>
        [Fact]
        public void ARespawnCarriesNoDeathStateIntoTheNextLife()
        {
            var gate = new ServerRespawnGate();

            gate.TryBeginDeath(ActorId, 0f);
            gate.MarkRespawned(ActorId);

            Assert.False(gate.IsDead(ActorId));
            Assert.Equal(0f, gate.SecondsUntilRespawn(ActorId, 0f));
            Assert.True(gate.TryBeginDeath(ActorId, 30f));
        }

        /// <summary>
        /// The actor id is not released because the actor died — § 7 says the respawn uses the
        /// same one. The gate is keyed on that id across both lives, which is the whole of what
        /// "same actor" means on this side.
        /// </summary>
        [Fact]
        public void TheActorKeepsItsIdAcrossADeath()
        {
            var gate = new ServerRespawnGate();

            gate.TryBeginDeath(ActorId, 0f);
            gate.MarkRespawned(ActorId);
            gate.TryBeginDeath(ActorId, 30f);

            Assert.True(gate.IsDead(ActorId));
            Assert.Equal(ActorLifePhase.Dead, gate.PhaseOf(ActorId, 30f));
        }

        /// <summary>A match reset forgets every death, including the counter.</summary>
        [Fact]
        public void AMatchResetForgetsEveryDeath()
        {
            var gate = new ServerRespawnGate();

            gate.TryBeginDeath(1, 0f);
            gate.TryBeginDeath(1, 0f);
            gate.TryBeginDeath(2, 0f);

            gate.Reset();

            Assert.False(gate.IsDead(1));
            Assert.False(gate.IsDead(2));
            Assert.Equal(0L, gate.DuplicateDeathsSuppressed);
            Assert.True(gate.TryBeginDeath(1, 0f));
        }

        /// <summary>
        /// An id past the table is refused rather than throwing. The bound is
        /// <see cref="ProtocolConstants.MAX_ACTORS"/> and the wire carries a <c>u16</c>, so a
        /// malformed or hostile id reaches here — and an exception on this path would take the
        /// tick loop down from a packet.
        /// </summary>
        [Fact]
        public void AnOutOfRangeActorIdIsRefusedRatherThanThrowing()
        {
            var gate = new ServerRespawnGate();

            Assert.False(gate.TryBeginDeath((ushort)(ProtocolConstants.MAX_ACTORS + 10), 0f));
            Assert.Equal(ActorLifePhase.Alive, gate.PhaseOf((ushort)(ProtocolConstants.MAX_ACTORS + 10), 0f));
        }
    }
}
