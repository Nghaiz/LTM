using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Vehicles;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// V6 tasks 2 and 3, the Unity half: which role owns a turret's aim and a mounted weapon's
    /// trigger.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Here rather than in the xunit suite because <c>NetContext.Role</c> is the subject.</b>
    /// Everything these seams DECIDE is engine-free and graded by <c>dotnet test</c>; what is left
    /// is the role branch itself, and a role is a Unity static. The alternative was to leave the
    /// D9 offline no-op — the one whose failure is a silent single-player regression — asserted by
    /// nothing at all.
    /// </para>
    /// <para>
    /// Every test restores <c>NetRole.Offline</c> in <c>TearDown</c>. With domain reload disabled
    /// a leaked role survives into the next test and into the next Play session, which is exactly
    /// the failure <c>NetContext.ResetOnLoad</c> exists for.
    /// </para>
    /// </remarks>
    public sealed class MountedWeaponSeamTests
    {
        private const ushort VehicleId = 4;
        private const byte GunnerSeat = 1;

        [TearDown]
        public void Restore()
        {
            NetContext.Clear();
            NetTurretAim.Clear();
            NetWeaponAuthority.Clear();
        }

        private static MountedWeaponDeclaration Gun(float cooldown = 0.25f, byte clip = 10)
            => new MountedWeaponDeclaration(
                WeaponIds.NONE, clip, spareAmmo: 30, cooldown: cooldown, spendsAmmo: true);

        // ------------------------------------------------------------------------- D9

        /// <summary>
        /// D9: single-player mounted-weapon behaviour is unchanged, and no directory can change it.
        /// </summary>
        /// <remarks>
        /// <b>The one at risk score 15 in the phase's own § 5.</b> A <c>CanFire</c> guard that
        /// leaked into offline play would be a single-player regression nobody notices until
        /// somebody plays offline — so the offline branch is checked BEFORE the directory is even
        /// consulted, and this asserts that with a directory installed that refuses everything.
        /// </remarks>
        [Test]
        public void OfflineMountedWeaponBehaviourIsUnchanged()
        {
            NetContext.Clear();
            NetWeaponAuthority.Directory = new AlwaysRefuse();

            Assert.IsTrue(
                NetWeaponAuthority.MayFire(VehicleId, GunnerSeat, false, true),
                "offline must never consult a directory");

            Assert.IsTrue(NetWeaponAuthority.GameplayHalfRunsHere);
            Assert.IsTrue(NetWeaponAuthority.CosmeticHalfRunsHere);
        }

        [Test]
        public void OfflineTurretAimIsAlwaysLocal()
        {
            NetContext.Clear();
            NetTurretAim.Directory = new AlwaysRemote();

            Assert.IsFalse(
                NetTurretAim.TryResolve(VehicleId, GunnerSeat, false, out _, out _, out _),
                "offline must never consult a directory");
        }

        // ---------------------------------------------------------------------- client

        [Test]
        public void AClientCannotFireARemoteActorsMountedWeapon()
        {
            NetContext.SetRole(NetRole.Client);

            Assert.IsFalse(
                NetWeaponAuthority.MayFire(VehicleId, GunnerSeat, locallyOccupied: false, true),
                "a remote actor's mounted weapon is driven by S_WEAPON_FIRE, not by CanFire");

            Assert.IsTrue(
                NetWeaponAuthority.MayFire(VehicleId, GunnerSeat, locallyOccupied: true, true),
                "the local player still predicts their own");
        }

        [Test]
        public void AClientRunsNoGameplayHalf()
        {
            NetContext.SetRole(NetRole.Client);

            // The tank's recoil impulse and the horn's Highlight() are both behind this. Applying
            // either on a client would double the first and duplicate the second.
            Assert.IsFalse(NetWeaponAuthority.GameplayHalfRunsHere);
            Assert.IsTrue(NetWeaponAuthority.CosmeticHalfRunsHere);
        }

        // ---------------------------------------------------------------------- server

        [Test]
        public void AServerRunsNoCosmeticHalf()
        {
            NetContext.SetRole(NetRole.Server);

            // Muzzle flash, casing, audio and the animator trigger all touch components a
            // stripped headless prefab does not carry. Two of the section 3.6 NREs are exactly
            // this, and they are why a headless build could not survive a mounted shot.
            Assert.IsTrue(NetWeaponAuthority.GameplayHalfRunsHere);
            Assert.IsFalse(NetWeaponAuthority.CosmeticHalfRunsHere);
        }

        [Test]
        public void AServerWithNoDirectoryDoesNotJamTheGun()
        {
            NetContext.SetRole(NetRole.Server);

            // An unregistered weapon behaves as it does offline. Refusing instead would present
            // as "the gun does not work" on a vehicle the netcode simply has not reached yet,
            // with nothing anywhere saying why.
            Assert.IsTrue(NetWeaponAuthority.MayFire(VehicleId, GunnerSeat, false, true));
        }

        [Test]
        public void AServerSpendsAndRefusesThroughTheRegistry()
        {
            NetContext.SetRole(NetRole.Server);

            var registry = new MountedWeaponRegistry();
            float now = 10f;
            var directory = new ServerMountedWeaponDirectory(registry, () => now);
            NetWeaponAuthority.Directory = directory;

            NetWeaponAuthority.Declare(VehicleId, GunnerSeat, Gun(cooldown: 0.25f, clip: 1));
            Assert.IsTrue(registry.IsTracked(VehicleId, GunnerSeat));

            Assert.IsTrue(NetWeaponAuthority.MayFire(VehicleId, GunnerSeat, false, true));

            var authority = new MountedWeaponAuthority(registry, MountedSpareAmmoPool.Instance);
            authority.Step(
                VehicleId, GunnerSeat,
                new InputFrame(0, 0, 0, 0, InputButtons.Fire), true, now);

            // Same tick: the server's cooldown has not elapsed and the clip is empty besides.
            Assert.IsFalse(NetWeaponAuthority.MayFire(VehicleId, GunnerSeat, false, true));
        }

        [Test]
        public void ADeclarationDoesNotReArmAHalfEmptyGun()
        {
            NetContext.SetRole(NetRole.Server);

            var registry = new MountedWeaponRegistry();
            float now = 10f;
            NetWeaponAuthority.Directory = new ServerMountedWeaponDirectory(registry, () => now);

            NetWeaponAuthority.Declare(VehicleId, GunnerSeat, Gun(clip: 10));

            var authority = new MountedWeaponAuthority(registry, MountedSpareAmmoPool.Instance);
            authority.Step(
                VehicleId, GunnerSeat,
                new InputFrame(0, 0, 0, 0, InputButtons.Fire), true, now);

            // A weapon declares itself on every CanFire, which runs several times per trigger
            // pull. A declaration that re-armed would make the clip infinite and the whole
            // authority decorative.
            NetWeaponAuthority.Declare(VehicleId, GunnerSeat, Gun(clip: 10));

            registry.TryGetState(VehicleId, GunnerSeat, out WeaponRuntimeState state);
            Assert.AreEqual(9, state.AmmoInClip);
        }

        [Test]
        public void AServerTurretResolvesToItsAuthoritativePose()
        {
            NetContext.SetRole(NetRole.Server);

            var turrets = new ServerTurretAuthority();
            NetTurretAim.Directory = new ServerTurretDirectory(turrets);

            var limits = new TurretAimLimits
            {
                YawRateDegPerSec = 90f, PitchRateDegPerSec = 90f, PitchMin = -40f, PitchMax = 15f,
            };

            NetTurretAim.Declare(VehicleId, GunnerSeat, limits, seedYaw: 30f, seedPitch: 0f);

            Assert.IsTrue(NetTurretAim.TryResolve(
                VehicleId, GunnerSeat, false,
                out TurretAimSource source, out float yaw, out _));

            Assert.AreEqual(TurretAimSource.ServerTarget, source);
            Assert.AreEqual(30f, yaw, 0.01f);
        }

        [Test]
        public void AnUndeclaredTurretFallsBackToLocalAim()
        {
            NetContext.SetRole(NetRole.Server);
            NetTurretAim.Directory = new ServerTurretDirectory(new ServerTurretAuthority());

            // A vehicle the registry has not reached yet aims as it does offline, rather than
            // freezing at due north — the same "no opinion" contract the weapon seam holds.
            Assert.IsFalse(
                NetTurretAim.TryResolve(VehicleId, GunnerSeat, false, out _, out _, out _));
        }

        [Test]
        public void TheLocalAimSurvivesUntilTheSeatIsLeft()
        {
            NetTurretAim.PublishLocal(45f, -10f);

            Assert.IsTrue(NetTurretAim.TryGetLocal(out float yaw, out float pitch));
            Assert.AreEqual(45f, yaw, 0.01f);
            Assert.AreEqual(-10f, pitch, 0.01f);

            // Left standing, the next C_VEHICLE_INPUT sent from a DIFFERENT turret would open
            // with the previous one's pose.
            NetTurretAim.ClearLocal();
            Assert.IsFalse(NetTurretAim.TryGetLocal(out _, out _));
        }

        private sealed class AlwaysRefuse : IWeaponFireDirectory
        {
            public bool TryMayFire(ushort v, byte s, bool alive, out bool mayFire)
            {
                mayFire = false;
                return true;
            }

            public void Declare(ushort v, byte s, in MountedWeaponDeclaration d) { }
        }

        private sealed class AlwaysRemote : ITurretAimDirectory
        {
            public bool TryResolve(
                ushort v, byte s, bool locallyOccupied,
                out TurretAimSource source, out float yaw, out float pitch)
            {
                source = TurretAimSource.RemotePose;
                yaw    = 180f;
                pitch  = 0f;
                return true;
            }

            public void Declare(
                ushort v, byte s, in TurretAimLimits limits, float seedYaw, float seedPitch) { }
        }
    }
}
