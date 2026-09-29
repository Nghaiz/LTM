using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A snapshot that reports the dead local body alive, when this client never asked to be
    /// deployed, is not a respawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Playtest 2026-09-28, bug 1: "my helicopter was shot down, and instead of dying I jumped
    /// out of the cockpit and reloaded my rifle in mid-air -- the respawn animation -- and only then
    /// got the deploy screen".</b> Reproduced live on 2026-09-29 by flying a helicopter into the
    /// ground from a scripted client; the client logged, inside one frame: snapshot 785579
    /// (alive, seated), S_DEATH, snapshot 785580 (alive, NOT seated), snapshot 785582 (dead). The
    /// middle snapshot was captured before the vehicle's burn killed its crew and sent after the
    /// S_DEATH that killing broadcast, and <c>OnRespawned</c> -- the whole deploy: first-person
    /// camera, a re-armed loadout, input -- ran on the corpse.
    /// </para>
    /// <para>
    /// The sequences below are that one, and the ones the rule must not break.
    /// </para>
    /// </remarks>
    public sealed class ClientUnrequestedRevivalTests
    {
        private const ushort LocalActor = 1;
        private const float Now = 100f;

        private static ActorSnapshotEntry Entry(ActorStateFlags flags, byte health = 100)
            => new ActorSnapshotEntry
            {
                ActorId = LocalActor,
                ChangeMask = SnapshotField.Health | SnapshotField.StateFlags,
                Health = health,
                StateFlags = flags,
            };

        /// <summary>A snapshot that mentions the actor but not its flags (unchanged against the baseline).</summary>
        private static ActorSnapshotEntry Quiet()
            => new ActorSnapshotEntry { ActorId = LocalActor, ChangeMask = SnapshotField.None };

        private static DeathMessage Death()
            => new DeathMessage(LocalActor, DeathMessage.EnvironmentKiller, CauseOfDeath.Bullet, 0, 0, 0, 0);

        private sealed class Counts
        {
            public int Died;
            public int Respawned;
        }

        private static (ClientCombatState state, Counts counts) Alive()
        {
            var state = new ClientCombatState { LocalActorId = LocalActor };
            state.EquipWeapon(WeaponIds.RK44);
            var counts = new Counts();
            state.OnDied += () => counts.Died++;
            state.OnRespawned += () => counts.Respawned++;
            state.ApplySnapshot(Entry(ActorStateFlags.IsAlive | ActorStateFlags.IsSeated), Now);
            return (state, counts);
        }

        [Fact]
        public void TheSnapshotCapturedBeforeTheCrewDiedDoesNotRespawnTheCorpse()
        {
            (ClientCombatState state, Counts counts) = Alive();

            // The live sequence, in its order.
            state.ApplyDeath(Death(), Now);
            state.ApplySnapshot(Entry(ActorStateFlags.IsAlive), Now);
            state.ApplySnapshot(Entry(ActorStateFlags.IsRagdoll, health: 0), Now);

            Assert.False(state.IsAlive);
            Assert.Equal(1, counts.Died);
            Assert.Equal(0, counts.Respawned);
            Assert.Equal(1, state.UnrequestedRevivalsHeld);
        }

        [Fact]
        public void TheContradictedClaimStaysHeldForGood()
        {
            (ClientCombatState state, Counts counts) = Alive();

            state.ApplyDeath(Death(), Now);
            state.ApplySnapshot(Entry(ActorStateFlags.IsAlive), Now);
            state.ApplySnapshot(Entry(ActorStateFlags.IsRagdoll, health: 0), Now + 0.05f);

            // Long past the window, on snapshots that no longer carry the flags at all: the claim
            // was withdrawn, so nothing is believed late either.
            state.ApplySnapshot(Quiet(), Now + 5f);

            Assert.False(state.IsAlive);
            Assert.Equal(0, counts.Respawned);
        }

        [Fact]
        public void ADeployRequestIsAnsweredAtOnce()
        {
            (ClientCombatState state, Counts counts) = Alive();
            state.ApplyDeath(Death(), Now);

            state.NoteDeployRequested();
            state.ApplySnapshot(Entry(ActorStateFlags.IsAlive), Now + 4f);

            Assert.True(state.IsAlive);
            Assert.Equal(1, counts.Respawned);
            Assert.Equal(0, state.UnrequestedRevivalsHeld);
        }

        [Fact]
        public void ARequestMadeWhileTheClaimIsHeldReleasesTheNextSnapshot()
        {
            (ClientCombatState state, Counts counts) = Alive();
            state.ApplyDeath(Death(), Now);
            state.ApplySnapshot(Entry(ActorStateFlags.IsAlive), Now);
            Assert.False(state.IsAlive);

            state.NoteDeployRequested();
            state.ApplySnapshot(Entry(ActorStateFlags.IsAlive), Now + 0.1f);

            Assert.True(state.IsAlive);
            Assert.Equal(1, counts.Respawned);
        }

        [Fact]
        public void ARevivalNobodyAskedForIsBelievedOnceItHolds()
        {
            // The server does not revive a body on its own, but if it ever does, the client must
            // not stay dead for the rest of the match: the flags are sent only when they change,
            // so the one snapshot that said so will not be repeated.
            (ClientCombatState state, Counts counts) = Alive();
            state.ApplyDeath(Death(), Now);

            state.ApplySnapshot(Entry(ActorStateFlags.IsAlive), Now);
            state.ApplySnapshot(Quiet(), Now + ClientCombatState.UnrequestedRevivalSeconds * 0.5f);
            Assert.False(state.IsAlive);

            state.ApplySnapshot(Quiet(), Now + ClientCombatState.UnrequestedRevivalSeconds);

            Assert.True(state.IsAlive);
            Assert.Equal(1, counts.Respawned);
        }

        [Fact]
        public void TheFirstDeployIsNotHeld()
        {
            // A joining client opens alive, is told its parked body is dead, then asks to deploy.
            var state = new ClientCombatState { LocalActorId = LocalActor };
            state.EquipWeapon(WeaponIds.RK44);
            int respawned = 0;
            state.OnRespawned += () => respawned++;

            state.ApplySnapshot(Entry(ActorStateFlags.IsRagdoll, health: 0), Now);
            state.NoteDeployRequested();
            state.ApplySnapshot(Entry(ActorStateFlags.IsAlive), Now + 1f);

            Assert.True(state.IsAlive);
            Assert.Equal(1, respawned);
        }

        [Fact]
        public void AReconnectIntoALivingBodyNeedsNoRequest()
        {
            var state = new ClientCombatState { LocalActorId = LocalActor };
            state.EquipWeapon(WeaponIds.RK44);
            int died = 0;
            int respawned = 0;
            state.OnDied += () => died++;
            state.OnRespawned += () => respawned++;

            state.ApplySnapshot(Entry(ActorStateFlags.IsAlive), Now);

            Assert.True(state.IsAlive);
            Assert.Equal(0, died);
            Assert.Equal(0, respawned);
        }

        [Fact]
        public void ANewDeathForgetsTheLastLifesRequest()
        {
            (ClientCombatState state, Counts counts) = Alive();
            state.ApplyDeath(Death(), Now);
            state.NoteDeployRequested();
            state.ApplySnapshot(Entry(ActorStateFlags.IsAlive), Now + 4f);
            Assert.Equal(1, counts.Respawned);

            // Killed again in a seat: the request that answered the last death is spent.
            state.ApplyDeath(Death(), Now + 30f);
            state.ApplySnapshot(Entry(ActorStateFlags.IsAlive), Now + 30f);

            Assert.False(state.IsAlive);
            Assert.Equal(1, counts.Respawned);
            Assert.Equal(2, counts.Died);
        }

        [Fact]
        public void ResetDropsAHeldClaim()
        {
            (ClientCombatState state, Counts counts) = Alive();
            state.ApplyDeath(Death(), Now);
            state.ApplySnapshot(Entry(ActorStateFlags.IsAlive), Now);

            state.Reset();
            state.ApplySnapshot(Entry(ActorStateFlags.IsRagdoll, health: 0), Now + 10f);
            state.ApplySnapshot(Quiet(), Now + 20f);

            Assert.False(state.IsAlive);
            Assert.Equal(0, counts.Respawned);
        }
    }
}
