using Ironfront.Net.Replication.Projectiles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Exactly one side applies a projectile's damage, in every flag configuration.
    /// debt-closure phase 2 task 2e, ledger C-1, acceptance criterion 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failure this exists to make impossible.</b> <c>AuthoritativeFlight</c> turns on the
    /// library's ballistic stepper, which resolves into <c>IActorDamageSink.ApplyDamage</c>. The
    /// engine path — <c>Hitbox.ProjectileHit</c> and <c>ActorManager.Explode</c> — has been
    /// applying the same damage since phase-05. Flipping the flag without removing the engine
    /// call runs both, and every hit does double damage. Until this phase the only thing
    /// preventing that was a paragraph of prose on the flag's own remark.
    /// </para>
    /// <para>
    /// <b>What this test can and cannot prove.</b> It proves the partition: for every role and
    /// flag pair there is exactly one owner, and the two predicates the call sites read cannot
    /// both be true. It cannot prove that <c>Assembly-CSharp</c> actually consults it — nothing
    /// in this assembly can see a Unity file. That half is <c>ClientWiringGate</c>'s G5, which
    /// asserts the engine damage call sites are guarded.
    /// </para>
    /// </remarks>
    public sealed class ProjectileDamageOwnershipTests
    {
        /// <summary>Every (isClient, isOffline) pair that can actually occur.</summary>
        /// <remarks>
        /// A build is exactly one of client, offline, or dedicated server — never both of the
        /// first two — so the impossible pair is deliberately not enumerated rather than being
        /// given an arbitrary expected answer.
        /// </remarks>
        public static TheoryData<bool, bool> Roles => new TheoryData<bool, bool>
        {
            { true,  false },   // networked client
            { false, true  },   // single-player
            { false, false },   // dedicated server
        };

        [Theory]
        [MemberData(nameof(Roles))]
        public void ExactlyOneSideAppliesDamageWithTheFlagOff(bool isClient, bool isOffline)
        {
            bool engine = ProjectileDamageOwnership.EngineApplies(isClient, isOffline, false);
            bool library = ProjectileDamageOwnership.LibraryApplies(isClient, isOffline, false);

            Assert.False(engine && library);
        }

        [Theory]
        [MemberData(nameof(Roles))]
        public void ExactlyOneSideAppliesDamageWithTheFlagOn(bool isClient, bool isOffline)
        {
            bool engine = ProjectileDamageOwnership.EngineApplies(isClient, isOffline, true);
            bool library = ProjectileDamageOwnership.LibraryApplies(isClient, isOffline, true);

            Assert.False(engine && library);
        }

        [Fact]
        public void FlagOffOnAServerMeansTheEngineAppliesItAndTheLibraryDoesNot()
        {
            Assert.Equal(
                ProjectileDamageOwner.Engine,
                ProjectileDamageOwnership.OwnerFor(isClient: false, isOffline: false, authoritativeFlight: false));

            Assert.True(ProjectileDamageOwnership.EngineApplies(false, false, false));
            Assert.False(ProjectileDamageOwnership.LibraryApplies(false, false, false));
        }

        [Fact]
        public void FlagOnOnAServerMeansTheLibraryAppliesItAndTheEngineCallIsGone()
        {
            Assert.Equal(
                ProjectileDamageOwner.Library,
                ProjectileDamageOwnership.OwnerFor(isClient: false, isOffline: false, authoritativeFlight: true));

            Assert.True(ProjectileDamageOwnership.LibraryApplies(false, false, true));
            Assert.False(ProjectileDamageOwnership.EngineApplies(false, false, true));
        }

        [Fact]
        public void AClientAppliesNothingInEitherConfiguration()
        {
            // Health only ever moves because a snapshot said so. A client that applied a
            // projectile's damage would double-count against the value already on its way, and
            // the two would disagree for the rest of the life.
            foreach (bool flag in new[] { false, true })
            {
                Assert.Equal(
                    ProjectileDamageOwner.Nobody,
                    ProjectileDamageOwnership.OwnerFor(isClient: true, isOffline: false, authoritativeFlight: flag));
                Assert.False(ProjectileDamageOwnership.EngineApplies(true, false, flag));
                Assert.False(ProjectileDamageOwnership.LibraryApplies(true, false, flag));
            }
        }

        [Fact]
        public void OfflineIgnoresTheFlagEntirely()
        {
            // Single-player has no stepper -- ServerProjectileBridge is never constructed -- so
            // an offline build that read the flag as "the library owns this" would silently stop
            // doing damage. Same carve-out phase-05 D9 put on Actor.Damage.
            foreach (bool flag in new[] { false, true })
            {
                Assert.Equal(
                    ProjectileDamageOwner.Engine,
                    ProjectileDamageOwnership.OwnerFor(isClient: false, isOffline: true, authoritativeFlight: flag));
            }
        }
    }
}
