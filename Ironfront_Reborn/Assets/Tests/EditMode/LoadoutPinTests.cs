using System;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Pins the loadout override seam — ledger <b>X-27</b>, the reason two lane-B runs of one
    /// programme were not comparable shot-for-shot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `AiActorController.GetLoadout` draws each slot with `Random.Range` over a private static
    /// name array, and a networked player's server-side body comes through it. Measured across
    /// three runs with the spawn already pinned: weapon 1, 1 and 15 — 30 shots against 14.
    /// </para>
    /// <para>
    /// <b>What this file can and cannot reach.</b> `AiActorController` compiles into
    /// `Assembly-CSharp`, which no asmdef can reference, so the call site itself is not
    /// testable from here — the same wall <see cref="ISpawnPointDirectory"/> is built around.
    /// What IS testable is the directory, and the call site's one load-bearing property is
    /// structural rather than behavioural: the drawn name is passed as an ARGUMENT, so the
    /// `Random.Range` call is evaluated before `PinnedOr` is entered and no edit inside it can
    /// skip the draw. That is what keeps a pinned run's RNG sequence identical to an unpinned
    /// one's, and it is guaranteed by C#'s evaluation order rather than by a test.
    /// </para>
    /// </remarks>
    public sealed class LoadoutPinTests
    {
        // ------------------------------------------------------------------- it narrows only

        /// <summary>The pinned slot answers with the pinned name.</summary>
        [Test]
        public void APinnedSlotReturnsItsName()
        {
            var directory = new PinnedLoadoutDirectory("RK-44");

            Assert.AreEqual("RK-44", directory.OverrideFor(LoadoutSlot.Primary));
        }

        /// <summary>
        /// Every slot the directory was NOT given defers, so pinning a primary cannot silently
        /// change what lands in a gear slot.
        /// </summary>
        /// <remarks>
        /// This is the property that makes a partial pin safe to install, and it is the same
        /// narrow-never-widen guarantee <see cref="PinnedSpawnPointDirectory"/> has for
        /// eligibility. Without it, installing a directory to fix the weapon would quietly
        /// disarm the gear the checks do not care about — a change nobody asked for, visible
        /// only as a body that throws no grenade.
        /// </remarks>
        [Test]
        public void AnUnpinnedSlotDefersToTheDraw()
        {
            var directory = new PinnedLoadoutDirectory("RK-44");

            Assert.IsNull(directory.OverrideFor(LoadoutSlot.Secondary));
            Assert.IsNull(directory.OverrideFor(LoadoutSlot.Gear1));
        }

        /// <summary>Each slot is answered independently, not by position or by accident.</summary>
        [Test]
        public void EachSlotAnswersWithItsOwnName()
        {
            var directory = new PinnedLoadoutDirectory("RK-44", "S-IND7", "FRAG");

            Assert.AreEqual("RK-44", directory.OverrideFor(LoadoutSlot.Primary));
            Assert.AreEqual("S-IND7", directory.OverrideFor(LoadoutSlot.Secondary));
            Assert.AreEqual("FRAG", directory.OverrideFor(LoadoutSlot.Gear1));
        }

        /// <summary>A slot pinned to whitespace defers rather than arming nothing.</summary>
        /// <remarks>
        /// An empty string reaching `WeaponManager.EntryNamed` matches no entry and produces an
        /// empty slot — an unarmed body, which is exactly what X-11 looked like and cost a full
        /// investigation. Treating blank as "no opinion" keeps a stray `-Weapon ""` from
        /// disarming the run it was meant to pin.
        /// </remarks>
        [TestCase("")]
        [TestCase("   ")]
        [TestCase(null)]
        public void ABlankNameDefersRatherThanArmingNothing(string blank)
        {
            var directory = new PinnedLoadoutDirectory("RK-44", blank, blank);

            Assert.IsNull(directory.OverrideFor(LoadoutSlot.Secondary));
            Assert.IsNull(directory.OverrideFor(LoadoutSlot.Gear1));
        }

        /// <summary>Surrounding whitespace is trimmed, not passed through to the name lookup.</summary>
        [Test]
        public void ANameIsTrimmed()
        {
            var directory = new PinnedLoadoutDirectory("  RK-44 ");

            Assert.AreEqual("RK-44", directory.OverrideFor(LoadoutSlot.Primary));
        }

        // --------------------------------------------------------- it cannot pin nothing

        /// <summary>
        /// A directory that overrides no slot is rejected at construction.
        /// </summary>
        /// <remarks>
        /// It would be indistinguishable from no directory at all, while the harness logged
        /// "the loadout is pinned" — which is precisely the shape of X-22, where a pin that
        /// never took still reported itself as installed and two runs were graded as pinned
        /// when neither was.
        /// </remarks>
        [Test]
        public void ADirectoryThatPinsNothingIsRejected()
        {
            Assert.Throws<ArgumentException>(
                () => new PinnedLoadoutDirectory(null, "  ", string.Empty));
        }

        // ------------------------------------------------------------------ unknown slots

        /// <summary>
        /// A slot value this directory has never heard of defers instead of throwing.
        /// </summary>
        /// <remarks>
        /// Adding a `LoadoutSlot` must not turn a pinned lane-B run into a crash on a path the
        /// pin has no opinion about. Deferring is the same answer an unpinned slot gets.
        /// </remarks>
        [Test]
        public void AnUnknownSlotDefers()
        {
            var directory = new PinnedLoadoutDirectory("RK-44");

            Assert.IsNull(directory.OverrideFor((LoadoutSlot)99));
        }

        // ---------------------------------------------------- the default changes nothing

        /// <summary>
        /// With no directory installed — every shipped configuration — the seam is inert, and
        /// `NetServerBindings.Clear` puts it back that way.
        /// </summary>
        [Test]
        public void ClearRemovesAnInstalledDirectory()
        {
            NetServerBindings.Loadouts = new PinnedLoadoutDirectory("RK-44");

            NetServerBindings.Clear();

            Assert.IsNull(NetServerBindings.Loadouts);
        }
    }
}
