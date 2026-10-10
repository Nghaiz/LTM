using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The carried guns' handling (owner's list of 2026-10-11, item 8): aimed, a gun keeps its
    /// prefab's spread; from the hip and on the move it opens as in CS2 and PUBG, and the shooter's
    /// game rolls exactly the cone the server's weapon table holds.
    /// </summary>
    public sealed class WeaponHandlingTests
    {
        private static readonly byte[] Guns =
        {
            WeaponIds.RK44, WeaponIds.SIND7, WeaponIds.SIND7_SUPPRESSED, WeaponIds.EAGLE_76,
            WeaponIds.SL_DEFENDER, WeaponIds.SIGNAL_DMR, WeaponIds.RECON_LRR,
        };

        [Test]
        public void AnAimedShotKeepsThePrefabsSpread()
        {
            foreach (byte id in Guns)
            {
                WeaponConfig config = WeaponCatalog.For(id);
                Assert.AreEqual(config.Spread, WeaponHandling.ShotSpread(id, 99f, aiming: true, moving: false), 1e-6f, $"weapon {id}");
            }
        }

        [Test]
        public void FromTheHipAndOnTheMoveTheConeOpensAsTheTableSays()
        {
            foreach (byte id in Guns)
            {
                WeaponConfig config = WeaponCatalog.For(id);
                float hip = WeaponHandling.ShotSpread(id, 99f, aiming: false, moving: false);
                float hipMoving = WeaponHandling.ShotSpread(id, 99f, aiming: false, moving: true);
                Assert.AreEqual(System.Math.Max(config.Spread, config.HipSpread), hip, 1e-6f, $"weapon {id} hip");
                Assert.Greater(hip, config.Spread, $"weapon {id}: a shot from the hip is as tight as an aimed one");
                Assert.Greater(hipMoving, hip, $"weapon {id}: moving costs nothing");
            }
        }

        [Test]
        public void ABoltActionFiredUnscopedIsNoSniperShot()
        {
            // v4.6.1: SL-DEFENDER (spread 0) and RECON LRR (0.0003 rad) were pinpoint from the hip.
            Assert.GreaterOrEqual(WeaponHandling.ShotSpread(WeaponIds.SL_DEFENDER, 0f, aiming: false, moving: false), 0.05f);
            Assert.GreaterOrEqual(WeaponHandling.ShotSpread(WeaponIds.RECON_LRR, 0f, aiming: false, moving: false), 0.02f);
        }

        [Test]
        public void AGunTheTableDoesNotHoldKeepsItsOwnSpread()
        {
            Assert.AreEqual(0.04f, WeaponHandling.ShotSpread(WeaponIds.FRAG, 0.04f, aiming: false, moving: true));
        }
    }
}
