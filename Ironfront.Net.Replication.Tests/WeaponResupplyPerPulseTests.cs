using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Every weapon an ammo bag can refill adds rounds per pulse online. Until 2026-10-03 the
    /// server seeded every slot with zero, so a bag refilled nothing.
    /// </summary>
    public sealed class WeaponResupplyPerPulseTests
    {
        [Fact]
        public void EveryRefillableWeaponAddsRoundsPerPulse()
        {
            for (int id = 1; id <= WeaponIds.MAX_ASSIGNED; id++)
            {
                WeaponConfig config = WeaponCatalog.For((byte)id);
                short perPulse = WeaponCatalog.ResupplyPerPulse((byte)id);
                bool refillable = config.SpareAmmo > 0;

                if (refillable)
                {
                    Assert.True(perPulse > 0, $"weapon {id} carries {config.SpareAmmo} spare rounds but a pulse adds none");
                    Assert.True(perPulse <= config.SpareAmmo, $"weapon {id} would refill past its own spare");
                }
                else
                {
                    Assert.Equal(0, perPulse);
                }
            }
        }

        [Fact]
        public void APulseRefillsASeededSlotUpToItsCap()
        {
            var pool = new ActorSpareAmmoPool();
            short spare = WeaponCatalog.For(WeaponIds.RK44).SpareAmmo;
            pool.SetLoadout(1, 0, 0, spare, WeaponCatalog.ResupplyPerPulse(WeaponIds.RK44));

            Assert.Equal(15, pool.Give(1, 0));

            var state = default(WeaponRuntimeState);
            Assert.Equal(15, pool.Remaining(1, 0, in state));

            for (int i = 0; i < 20; i++) pool.Give(1, 0);
            Assert.Equal(spare, pool.Remaining(1, 0, in state));
        }
    }
}
