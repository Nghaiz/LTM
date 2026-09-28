using System;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.Net.Protocol.Tests.Conformance
{
    /// <summary>
    /// Pins the <c>ProjectileKind</c> value space declared in protocol-spec.md § 4.10.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The names are in wire order, and the number IS the wire.</b> <c>Kind</c> is a <c>u8</c>
    /// in <c>S_PROJECTILE_SPAWN</c>, so renumbering a member re-labels every projectile a client
    /// draws. And <c>_prefabsByKind</c> is indexed by <c>(byte)ProjectileKind</c> in both maps and
    /// in both scenes, so a member inserted in the MIDDLE would shift every prefab after it while
    /// the asset gate's count check still passed — every kind then pointing at the wrong object,
    /// with nothing red anywhere. That is the failure this test exists for, and it is why the
    /// members are asserted to equal their own index rather than merely to be distinct.
    /// </para>
    /// <para>
    /// <b>Appending is the only change this tolerates, which is the rule the enum's own doc
    /// states.</b> <c>Spearhead</c> = 7 was appended 2026-09-25 for exactly that reason; a
    /// reordering would have moved <c>Grenade</c> and every grenade in flight with it.
    /// </para>
    /// <para>
    /// <c>tools/ClientWiringGate</c> reads the member COUNT off this same enum and demands one
    /// <c>_prefabsByKind</c> entry per member, so adding a value without authoring it into the
    /// scenes already fails the build. What no gate covers is the numbers, and those are the
    /// contract — the spec's § 4.10 list is the third copy and <c>SpecChecker</c> does not parse
    /// it.
    /// </para>
    /// </remarks>
    public sealed class ProjectileKindTests
    {
        /// <summary>The value space, in wire order, as § 4.10 declares it.</summary>
        private static readonly string[] WireOrder =
        {
            "Shell",
            "Rocket",
            "GuidedMissile",
            "Grenade",
            "AmmoBag",
            "Medipack",
            "Bullet",
            "Spearhead",
        };

        [Fact]
        public void TheValuesAreContiguousFromZeroInWireOrder()
        {
            Array declared = Enum.GetValues(typeof(ProjectileKind));

            Assert.Equal(WireOrder.Length, declared.Length);

            for (int i = 0; i < WireOrder.Length; i++)
            {
                object member = declared.GetValue(i)!;

                Assert.Equal(WireOrder[i], member.ToString());

                // Ties the NAME to the NUMBER, which is the half a count check cannot see.
                Assert.Equal((byte)i, (byte)member);
            }
        }

        [Fact]
        public void EveryMemberIsInTheTable()
        {
            // The other direction, so a member added to the enum without a row above fails here
            // rather than silently shrinking what the loop covers.
            foreach (ProjectileKind kind in Enum.GetValues(typeof(ProjectileKind)))
            {
                Assert.Contains(kind.ToString(), WireOrder);
            }
        }
    }
}
