using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Handoff section 3.2: the client reads the authoritative reserve and reload state that
    /// protocol 10 put on the wire, instead of keeping a second copy of both beside the clip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Until v10 the reserve and the reload state were authoritative on the server and simply
    /// never crossed the wire, so the client held Ravenfield's own pool beside the clip it was
    /// told about: two sources of one number, free to drift, and most visibly wrong on the
    /// clip-of-one weapons where a single round is the whole magazine. The wire was widened
    /// first; <see cref="ClientCombatState"/> reading it is the other half, and these are the
    /// tests for that half alone.
    /// </para>
    /// <para>
    /// <b>Every assertion about a sentinel is on <see cref="SpareAmmo.Kind"/>, never on
    /// <see cref="SpareAmmo.Rounds"/>.</b> Both sentinels carry zero rounds, so a test that
    /// asserted <c>Rounds == 0</c> would pass identically for infinite, for no-resupply and for
    /// a genuinely empty reserve — a green that could not tell apart the three things this
    /// type exists to tell apart.
    /// </para>
    /// </remarks>
    public sealed class ClientReserveReconcileTests
    {
        private const ushort LocalActor = 1;

        /// <summary>The clock reading for calls that need one but are not testing timing.</summary>
        private const float Now = 10f;

        private static WeaponConfig Rifle => WeaponCatalog.For(WeaponIds.RK44);

        private static ClientCombatState Equipped()
        {
            var state = new ClientCombatState { LocalActorId = LocalActor };
            state.EquipWeapon(WeaponIds.RK44);
            return state;
        }

        /// <summary>A full weapon field: id, clip, reserve and reload state all present.</summary>
        private static ActorSnapshotEntry WeaponEntry(
            byte ammo,
            ushort spareEncoded,
            WeaponStateFlags flags = WeaponStateFlags.None)
            => new ActorSnapshotEntry
            {
                ActorId          = LocalActor,
                ChangeMask       = SnapshotField.Weapon,
                WeaponId         = WeaponIds.RK44,
                AmmoInClip       = ammo,
                SpareAmmoEncoded = spareEncoded,
                WeaponStateFlags = flags,
            };

        /// <summary>
        /// A delta carrying everything EXCEPT the weapon field — the shape every real snapshot
        /// has once the weapon state stops changing, because the encoder masks on change.
        /// </summary>
        private static ActorSnapshotEntry NoWeaponEntry()
            => new ActorSnapshotEntry
            {
                ActorId    = LocalActor,
                ChangeMask = SnapshotField.Health | SnapshotField.StateFlags,
                Health     = 90,
                StateFlags = ActorStateFlags.IsAlive,
            };

        [Fact]
        public void BeforeAnySnapshotTheReserveIsTheNoResupplySentinelAndNotAnEmptyFiniteOne()
        {
            var state = new ClientCombatState();

            Assert.Equal(SpareAmmoKind.NoResupply, state.SpareAmmo.Kind);

            // The point of the assertion above, spelled out: these two render differently on a
            // HUD -- "/ --" against "/ 0" -- and opening at Finite(0) would tell every player
            // their rifle was out of spare rounds for the first snapshot interval of the match.
            Assert.NotEqual(SpareAmmoKind.Finite, state.SpareAmmo.Kind);
            Assert.False(state.ServerSaysReloading);
        }

        [Fact]
        public void AFiniteReserveLandsWithItsCount()
        {
            var state = Equipped();

            state.ApplySnapshot(WeaponEntry(ammo: 30, spareEncoded: 90), Now);

            Assert.Equal(SpareAmmoKind.Finite, state.SpareAmmo.Kind);
            Assert.Equal(90, state.SpareAmmo.Rounds);
        }

        [Fact]
        public void TheNoResupplySentinelRoundTripsAsAKindAndNotAsSixtyFiveThousand()
        {
            var state = Equipped();

            state.ApplySnapshot(
                WeaponEntry(ammo: 1, spareEncoded: SpareAmmo.NoResupplyEncoded), Now);

            Assert.Equal(SpareAmmoKind.NoResupply, state.SpareAmmo.Kind);

            // The failure this pins: an inline (ushort) cast at the call site instead of
            // SpareAmmo.Decode reads 0xFFFE as a count, and the HUD shows 65534 spare rounds.
            Assert.NotEqual(SpareAmmo.NoResupplyEncoded, state.SpareAmmo.Rounds);
        }

        [Fact]
        public void TheInfiniteSentinelRoundTripsAsAKind()
        {
            var state = Equipped();

            state.ApplySnapshot(
                WeaponEntry(ammo: 30, spareEncoded: SpareAmmo.InfiniteEncoded), Now);

            Assert.Equal(SpareAmmoKind.Infinite, state.SpareAmmo.Kind);
            Assert.True(state.SpareAmmo.CanFeedAReload);
        }

        [Fact]
        public void ASparseDeltaWithoutTheWeaponFieldLeavesTheReserveAlone()
        {
            var state = Equipped();
            state.ApplySnapshot(WeaponEntry(ammo: 30, spareEncoded: 90), Now);

            state.ApplySnapshot(NoWeaponEntry(), Now);

            // A delta that says nothing about the weapon is not a delta saying the reserve is
            // zero. Reading SpareAmmoEncoded outside the Has(Weapon) guard would empty the
            // reserve on every snapshot after the one that filled it, because the field keeps
            // its default when it is not on the wire.
            Assert.Equal(SpareAmmoKind.Finite, state.SpareAmmo.Kind);
            Assert.Equal(90, state.SpareAmmo.Rounds);
            Assert.Equal(90, state.Health);
        }

        [Fact]
        public void TheReloadingFlagSetsAndClearsServerSaysReloading()
        {
            var state = Equipped();

            state.ApplySnapshot(
                WeaponEntry(ammo: 4, spareEncoded: 90, flags: WeaponStateFlags.Reloading), Now);
            Assert.True(state.ServerSaysReloading);

            state.ApplySnapshot(WeaponEntry(ammo: 30, spareEncoded: 64), Now);
            Assert.False(state.ServerSaysReloading);
        }

        [Fact]
        public void AServerReloadInFlightKeepsTheLocalPredictionRunning()
        {
            var state = Equipped();
            state.ApplySnapshot(WeaponEntry(ammo: 12, spareEncoded: 90), Now);
            state.BeginReload(Now);

            state.ApplySnapshot(
                WeaponEntry(ammo: 12, spareEncoded: 90, flags: WeaponStateFlags.Reloading), Now);

            // The pre-v10 behaviour was to clear the local reload on the FIRST snapshot after
            // BeginReload, because nothing on the wire could say the reload was still running.
            // It now can, and a reload the server has accepted must survive the snapshot that
            // reports it -- otherwise the animation stops a fraction of a second after it starts.
            Assert.True(state.IsReloading);
            Assert.True(state.ServerSaysReloading);
        }

        [Fact]
        public void TheServerClearingReloadingEndsALocalReloadWithoutFillingTheClip()
        {
            var state = Equipped();
            state.ApplySnapshot(WeaponEntry(ammo: 3, spareEncoded: 0), Now);
            state.BeginReload(Now);
            Assert.True(state.IsReloading);

            // The server refused it -- an empty finite reserve has nothing to feed a reload --
            // so the flag never sets and the clip never moves.
            state.ApplySnapshot(WeaponEntry(ammo: 3, spareEncoded: 0), Now);

            Assert.False(state.ServerSaysReloading);
            Assert.False(state.IsReloading);

            // Ended, not completed. Filling to ClipSize here would render a full magazine for a
            // reload that was refused, and the next snapshot would take it straight back to 3.
            Assert.Equal(3, state.AmmoInClip);
            Assert.Equal(Rifle.ClipSize, state.ClipSize);
        }

        [Fact]
        public void AServerReloadSuspendsTheAntiFlickerRuleForTheClip()
        {
            var state = Equipped();
            state.ApplySnapshot(WeaponEntry(ammo: 12, spareEncoded: 90), Now);
            Assert.Equal(FireRejection.None, state.PredictFire(Now));
            Assert.Equal(11, state.AmmoInClip);

            long correctionsBefore = state.SnapshotAmmoCorrections;

            // Drift of exactly AmmoResyncThreshold: without the reload in flight the predicted
            // 11 would win, which is the whole job of that threshold. Mid-reload the server's
            // count is the only one worth showing, because the clip is about to jump anyway.
            state.ApplySnapshot(
                WeaponEntry(ammo: 9, spareEncoded: 90, flags: WeaponStateFlags.Reloading), Now);

            Assert.Equal(9, state.AmmoInClip);
            Assert.Equal(correctionsBefore + 1, state.SnapshotAmmoCorrections);
        }

        [Fact]
        public void TheReserveIsTakenVerbatimWithNoThresholdOfItsOwn()
        {
            var state = Equipped();
            state.ApplySnapshot(WeaponEntry(ammo: 30, spareEncoded: 90), Now);

            // A resupply crate is a large single-snapshot jump with no local counterpart, and it
            // must land whole on the first snapshot that carries it. The clip's anti-flicker rule
            // exists to protect a locally predicted lead; there is no predicted reserve, so there
            // is nothing here for a threshold to do except delay the correct number.
            state.ApplySnapshot(WeaponEntry(ammo: 30, spareEncoded: 210), Now);

            Assert.Equal(210, state.SpareAmmo.Rounds);
        }

        [Fact]
        public void ResetReturnsTheReserveToTheSentinelRatherThanToZero()
        {
            var state = Equipped();
            state.ApplySnapshot(
                WeaponEntry(ammo: 8, spareEncoded: 45, flags: WeaponStateFlags.Reloading), Now);
            Assert.Equal(SpareAmmoKind.Finite, state.SpareAmmo.Kind);
            Assert.True(state.ServerSaysReloading);

            state.Reset();

            Assert.Equal(SpareAmmoKind.NoResupply, state.SpareAmmo.Kind);
            Assert.False(state.ServerSaysReloading);
        }
    }
}
