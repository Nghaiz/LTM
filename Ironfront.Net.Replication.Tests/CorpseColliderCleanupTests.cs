using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Protocol-10 handoff § 7, last paragraph, and § 10.3: a corpse's collider is disabled and
    /// no longer blocks a vehicle pad after cleanup — while a genuinely living body still may.
    /// </summary>
    /// <remarks>
    /// <b>The symptom was a pad that stopped working for a whole round.</b> Island logged a
    /// spawn refused thirty times running by <c>Bone_002</c> on layer <c>Hitbox</c>, a ragdoll
    /// bone belonging to an actor that had been dead for minutes; <c>VehicleSpawner</c> gives up
    /// after its retries, so the map simply loses a vehicle. The engine side of the repair —
    /// switching the colliders off — lives on <c>NetServerActor</c> where the colliders are.
    /// What is graded here is the rule: which layers matter, when a corpse counts as late, and
    /// which bodies are exempt.
    /// </remarks>
    public sealed class CorpseColliderCleanupTests
    {
        private const ushort ActorId = 5;

        /// <summary>
        /// The mask is <c>VehicleSpawner.SPAWN_BLOCK_MASK</c>, restated because
        /// <c>Assembly-CSharp</c> is a predefined assembly no <c>.asmdef</c> and no test project
        /// can reference. <b>This assertion is the only thing standing between the two copies and
        /// a silent divergence</b>, so it pins the literal the spawner carries rather than
        /// recomputing it from the same shift expression the constant is written with.
        /// </summary>
        [Fact]
        public void TheMaskMatchesTheOneTheVehicleSpawnerTests()
        {
            Assert.Equal(5376, CorpseColliderLedger.SpawnBlockMask);

            Assert.True(CorpseColliderLedger.BlocksVehicleSpawn(8));    // Hitbox
            Assert.True(CorpseColliderLedger.BlocksVehicleSpawn(10));   // Ragdoll
            Assert.True(CorpseColliderLedger.BlocksVehicleSpawn(12));   // Vehicle
        }

        /// <summary>
        /// <c>SeatedHitbox</c> is not in the pad mask, so it is not what this verdict is about.
        /// Island's report names <c>Hitbox</c>/<c>SeatedHitbox</c> together and it would be easy
        /// to add the bit; a mask that disagreed with the spawner's would report corpses that
        /// were never blocking anything.
        /// </summary>
        [Fact]
        public void SeatedHitboxIsNotAPadBlockingLayer()
        {
            Assert.False(CorpseColliderLedger.BlocksVehicleSpawn(16));   // SeatedHitbox
            Assert.False(CorpseColliderLedger.BlocksVehicleSpawn(0));    // Default
            Assert.False(CorpseColliderLedger.BlocksVehicleSpawn(9));    // Player
        }

        /// <summary>A layer outside the 32 Unity has is refused rather than shifting past the word.</summary>
        [Fact]
        public void AnImpossibleLayerIsNotABlockingLayer()
        {
            Assert.False(CorpseColliderLedger.BlocksVehicleSpawn(-1));
            Assert.False(CorpseColliderLedger.BlocksVehicleSpawn(32));
            Assert.False(CorpseColliderLedger.BlocksVehicleSpawn(9999));
        }

        /// <summary>
        /// § 10.3: a cleaned-up corpse stops blocking. Cleanup inside the deadline is never
        /// stale, and neither is cleanup after it — the verdict is about colliders still being
        /// there, not about how long they took to go.
        /// </summary>
        [Fact]
        public void ACleanedCorpseIsNeverStale()
        {
            var ledger = new CorpseColliderLedger();

            ledger.NoteDeath(ActorId, 100f);
            ledger.NoteCollidersDisabled(ActorId);

            Assert.True(ledger.CollidersDisabled(ActorId));
            Assert.False(ledger.IsStale(ActorId, 100f + CorpseColliderLedger.CleanupDeadlineSeconds));
            Assert.False(ledger.IsStale(ActorId, 100f + 600f));
            Assert.Equal(0L, ledger.StaleObservations);
        }

        /// <summary>
        /// A corpse whose colliders are still there past the deadline is the defect, and it is
        /// named rather than left as a pad that quietly stopped working.
        /// </summary>
        [Fact]
        public void AnUncleanedCorpseIsStaleOnceTheDeadlinePasses()
        {
            var ledger = new CorpseColliderLedger();

            ledger.NoteDeath(ActorId, 100f);

            Assert.False(ledger.IsStale(ActorId, 100f));
            Assert.False(
                ledger.IsStale(ActorId, 100f + CorpseColliderLedger.CleanupDeadlineSeconds - 0.01f));
            Assert.Equal(0L, ledger.StaleObservations);

            Assert.True(ledger.IsStale(ActorId, 100f + CorpseColliderLedger.CleanupDeadlineSeconds));
            Assert.True(ledger.IsStale(ActorId, 100f + 30f));
            Assert.Equal(2L, ledger.StaleObservations);
        }

        /// <summary>
        /// <b>A living actor standing on a pad is allowed to block it</b>, and § 7 says so in as
        /// many words. This is the assertion that keeps the repair from becoming a jeep spawned
        /// inside a player: a body that has not died is not in the ledger, so no deadline can
        /// ever expire for it.
        /// </summary>
        [Fact]
        public void ALivingActorOnAPadIsNotStale()
        {
            var ledger = new CorpseColliderLedger();

            Assert.False(ledger.IsCorpse(ActorId));
            Assert.False(ledger.IsStale(ActorId, 1_000f));
            Assert.Equal(0L, ledger.StaleObservations);
        }

        /// <summary>
        /// A respawned body is a living one again — including on the pad it died on. Without the
        /// clear, the deadline started by its death would go on reporting the player who is now
        /// standing there alive.
        /// </summary>
        [Fact]
        public void ARespawnedActorIsNoLongerACorpse()
        {
            var ledger = new CorpseColliderLedger();

            ledger.NoteDeath(ActorId, 100f);
            ledger.NoteCollidersDisabled(ActorId);
            ledger.NoteRespawn(ActorId);

            Assert.False(ledger.IsCorpse(ActorId));
            Assert.False(ledger.CollidersDisabled(ActorId));
            Assert.False(ledger.IsStale(ActorId, 100f + 600f));
        }

        /// <summary>
        /// A second death report inside one life does not restart the deadline, for
        /// <see cref="ServerRespawnGate.TryBeginDeath"/>'s reason: two damage paths reach one
        /// actor, and a corpse that reset its own clock on each of them could stay just inside
        /// the deadline forever.
        /// </summary>
        [Fact]
        public void ARepeatDeathDoesNotRestartTheCleanupDeadline()
        {
            var ledger = new CorpseColliderLedger();

            ledger.NoteDeath(ActorId, 100f);
            ledger.NoteDeath(ActorId, 105f);

            Assert.True(ledger.IsStale(ActorId, 100f + CorpseColliderLedger.CleanupDeadlineSeconds));
        }

        /// <summary>
        /// The next death starts a fresh deadline, and a corpse cleaned up in its previous life
        /// is not credited for this one.
        /// </summary>
        [Fact]
        public void TheNextDeathStartsAFreshDeadline()
        {
            var ledger = new CorpseColliderLedger();

            ledger.NoteDeath(ActorId, 100f);
            ledger.NoteCollidersDisabled(ActorId);
            ledger.NoteRespawn(ActorId);

            ledger.NoteDeath(ActorId, 200f);

            Assert.False(ledger.CollidersDisabled(ActorId));
            Assert.False(ledger.IsStale(ActorId, 200f));
            Assert.True(ledger.IsStale(ActorId, 200f + CorpseColliderLedger.CleanupDeadlineSeconds));
        }

        /// <summary>
        /// The deadline is shorter than the respawn delay, so a corpse cleaned up on time cannot
        /// cost a pad even one of <c>VehicleSpawner</c>'s one-second retries.
        /// </summary>
        [Fact]
        public void TheDeadlineLandsInsideTheRespawnDelay()
        {
            Assert.True(CorpseColliderLedger.CleanupDeadlineSeconds > 0f);
            Assert.True(CorpseColliderLedger.CleanupDeadlineSeconds < ServerRespawnGate.RespawnSeconds);
        }

        /// <summary>A round teardown forgets every corpse and the count of the late ones.</summary>
        [Fact]
        public void AMatchResetForgetsEveryCorpse()
        {
            var ledger = new CorpseColliderLedger();

            ledger.NoteDeath(1, 0f);
            ledger.NoteDeath(2, 0f);
            Assert.True(ledger.IsStale(1, 100f));

            ledger.Reset();

            Assert.False(ledger.IsCorpse(1));
            Assert.False(ledger.IsCorpse(2));
            Assert.False(ledger.IsStale(1, 100f));
            Assert.Equal(0L, ledger.StaleObservations);
        }
    }
}
