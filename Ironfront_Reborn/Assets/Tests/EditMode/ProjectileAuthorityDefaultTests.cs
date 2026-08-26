using NUnit.Framework;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Phase 5 acceptance criterion 2: <c>AuthoritativeFlight</c>'s default is asserted by a test,
    /// not by a comment. Ledger C-1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this file exists at all.</b> The flag shipped default-off since V7 behind a
    /// paragraph of prose saying that turning it on without first deleting the engine-side damage
    /// call would apply every hit twice. <c>ProjectileDamageOwnershipTests</c> in the library
    /// proves the partition — engine and library are never both the owner — but it takes
    /// <c>authoritativeFlight</c> as a <i>parameter</i>. It therefore proves the function and says
    /// nothing about which value ships. Phase 5 decided OFF; that decision needs an instrument, or
    /// it is another remark.
    /// </para>
    /// <para>
    /// <b>The second test is the one that matters, and the first alone would be a green that
    /// proves nothing.</b> Asserting a bool immediately after calling the method that sets it is
    /// circular on its face. It is included because <see cref="NetProjectileAuthority.Clear"/> is
    /// not an arbitrary writer — it is what
    /// <c>[RuntimeInitializeOnLoadMethod(SubsystemRegistration)]</c> runs before any gameplay, so
    /// it genuinely IS the mechanism that establishes the shipped runtime default. The second test
    /// then asserts the <i>consequence</i>: at that default, on a dedicated server, the engine
    /// still owns projectile damage and the library stepper does not. That is what "off" means to
    /// the three call sites in <c>Assembly-CSharp</c>, and it is what would silently stop being
    /// true if someone flipped the default without re-running Phase 5's proof obligation.
    /// </para>
    /// <para>
    /// <b>Not a pinned baseline.</b> Per <c>pinned-baseline-test-companion.md</c>, nothing here
    /// pins a currently-broken value to keep the suite green. OFF is the decided, shipped state;
    /// when Phase 5 is reopened and the flag flips, these tests are <i>inverted</i>, not re-pinned,
    /// and the reopening condition in the ledger names the number that would justify it.
    /// </para>
    /// <para>
    /// <b>Statics, so teardown is mandatory.</b> Both <c>NetContext.Role</c> and
    /// <c>NetProjectileAuthority.AuthoritativeFlight</c> are process-wide; a test that left either
    /// set would change the answer for whatever ran next in the same domain.
    /// </para>
    /// </remarks>
    public sealed class ProjectileAuthorityDefaultTests
    {
        [SetUp]
        [TearDown]
        public void ResetRoleAndFlag()
        {
            NetContext.Clear();
            NetProjectileAuthority.Clear();
        }

        [Test]
        public void TheShippedDefaultIsOff()
        {
            // Drift first, so the assertion below cannot pass on an untouched static that merely
            // happened to be false already.
            NetProjectileAuthority.AuthoritativeFlight = true;

            NetProjectileAuthority.Clear();

            Assert.False(
                NetProjectileAuthority.AuthoritativeFlight,
                "AuthoritativeFlight must be off at subsystem registration. Phase 5 decided OFF "
                + "because two of its three evidence inputs could not be produced by a harness "
                + "whose synthetic client cannot fire (X-34). Flipping this default is that "
                + "decision being reopened, and the ledger's C-1 row states the number that "
                + "would justify it.");
        }

        [Test]
        public void AtTheDefaultTheEngineStillOwnsProjectileDamageOnAServer()
        {
            NetContext.SetRole(NetRole.Server);
            NetProjectileAuthority.Clear();

            Assert.True(
                NetProjectileAuthority.EngineAppliesProjectileDamage,
                "With the flag at its shipped default a dedicated server's ENGINE applies "
                + "projectile damage — the phase-05/V1 path through Hitbox.ProjectileHit and "
                + "ActorManager.Explode that works today. If this is false the engine-side call "
                + "has been switched off while the library stepper is still disabled, so hits "
                + "would do no damage at all.");

            Assert.False(
                NetProjectileAuthority.LibraryOwnsProjectileDamage,
                "With the flag at its shipped default the library stepper does NOT own damage. "
                + "If this is true while the engine-side call is still present — and G7 exists to "
                + "keep it present — every hit is applied twice (ledger C-1).");
        }
    }
}
