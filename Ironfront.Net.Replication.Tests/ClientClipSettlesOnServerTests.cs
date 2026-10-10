using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The predicted clip settles on the server's once the server has processed every frame a
    /// shot was predicted on.
    /// </summary>
    /// <remarks>
    /// Owner report 2026-10-11, reproduced against the live Dustbowl server the same day: the
    /// guided launcher predicted a launch the server refused. The client then read 0 and the
    /// server 1, a drift of one inside <see cref="ClientCombatState.AmmoResyncThreshold"/>, and
    /// the server's weapon field never changed again to correct it, so the player held a missile
    /// they could neither fire nor reload ("it has ammo but will not fire").
    /// </remarks>
    public sealed class ClientClipSettlesOnServerTests
    {
        private const ushort LocalActor = 1;
        private const float Now = 10f;
        private static readonly Vec3 Aim = new Vec3(0f, 0f, 1f);

        private static ActorSnapshotEntry Clip(byte weaponId, byte ammo)
            => new ActorSnapshotEntry
            {
                ActorId    = LocalActor,
                ChangeMask = SnapshotField.Weapon,
                WeaponId   = weaponId,
                AmmoInClip = ammo,
            };

        // What most snapshots are: the encoder masks the weapon field on change, so a server
        // whose clip did not move says nothing about it at all.
        private static ActorSnapshotEntry Unchanged() => new ActorSnapshotEntry { ActorId = LocalActor };

        private static ClientCombatState Holding(byte weaponId, byte clip, uint processed)
        {
            var state = new ClientCombatState { LocalActorId = LocalActor };
            state.EquipWeapon(weaponId);
            state.ApplySnapshot(Clip(weaponId, clip), Now, processed, processed);
            return state;
        }

        [Fact]
        public void ALaunchTheServerRefusedComesBackOnceTheServerHasSeenItsFrame()
        {
            var state = Holding(WeaponIds.BIL_SCALPEL, 1, processed: 99);
            Assert.Equal(FireRejection.None, state.PredictFire(Now, 100, 100, in Aim));
            Assert.Equal(0, state.AmmoInClip);
            long corrections = state.SnapshotAmmoCorrections;

            // Frame 100 is still on its way: the prediction stands.
            state.ApplySnapshot(Unchanged(), Now + 0.03f, 99, 99);
            Assert.Equal(0, state.AmmoInClip);

            // Processed and refused. The server's clip still holds the missile, and nothing on
            // the wire changes to say so: the settle is the only thing that can give it back.
            state.ApplySnapshot(Unchanged(), Now + 0.07f, 100, 100);
            Assert.Equal(1, state.AmmoInClip);
            Assert.Equal(corrections + 1, state.SnapshotAmmoCorrections);
        }

        [Fact]
        public void ALaunchTheServerMadeStaysSpent()
        {
            var state = Holding(WeaponIds.BIL_SCALPEL, 1, processed: 99);
            state.PredictFire(Now, 100, 100, in Aim);
            long corrections = state.SnapshotAmmoCorrections;

            state.ApplySnapshot(Clip(WeaponIds.BIL_SCALPEL, 0), Now + 0.07f, 100, 100);
            state.ApplySnapshot(Unchanged(), Now + 0.1f, 101, 101);

            Assert.Equal(0, state.AmmoInClip);
            Assert.Equal(corrections, state.SnapshotAmmoCorrections);
        }

        [Fact]
        public void ABurstStillInFlightKeepsItsPredictionSoTheHudDoesNotFlicker()
        {
            WeaponConfig rifle = WeaponCatalog.For(WeaponIds.RK44);
            var state = Holding(WeaponIds.RK44, rifle.ClipSize, processed: 99);
            state.PredictFire(Now, 100, 100, in Aim);
            state.PredictFire(Now + rifle.Cooldown, 101, 101, in Aim);

            // The server has seen frame 100 but not 101: its clip is behind by the shot in flight,
            // and taking it would flicker the counter up and back down.
            state.ApplySnapshot(Clip(WeaponIds.RK44, (byte)(rifle.ClipSize - 1)), Now + 0.05f, 100, 100);
            Assert.Equal(rifle.ClipSize - 2, state.AmmoInClip);

            // Both seen, but the server fired only one of them: its count is the count.
            state.ApplySnapshot(Unchanged(), Now + 0.09f, 101, 101);
            Assert.Equal(rifle.ClipSize - 1, state.AmmoInClip);
        }

        [Fact]
        public void TheLastWeaponsClipIsNeverSettledOntoTheNextOne()
        {
            var state = Holding(WeaponIds.RK44, 7, processed: 99);
            state.EquipWeapon(WeaponIds.SIND7);
            byte drawn = state.AmmoInClip;

            // The server still reports the rifle's clip until a snapshot names the pistol.
            state.ApplySnapshot(Unchanged(), Now + 0.1f, 150, 150);

            Assert.Equal(drawn, state.AmmoInClip);
            Assert.NotEqual(7, state.AmmoInClip);
        }
    }
}
