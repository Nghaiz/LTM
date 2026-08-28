using System;
using System.IO;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// orphan-closure O4 — a weapon switch stops handing out a free magazine. Ledger <b>X-43</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the row was.</b> <c>ServerCombatBridge.AdoptTheWeaponTheBodyIsHolding</c> re-pointed
    /// the session at the weapon the body was holding and then called <c>ResetWeapon()</c>, because
    /// a full clip was the only weapon state reachable from there: <c>ClientSession.Weapon</c> is a
    /// single <c>WeaponRuntimeState</c>, and <c>NetServerActor.AmmoInClip</c> mirrors the session
    /// rather than the body, so it could not supply the missing half either.
    /// </para>
    /// <para>
    /// <b>Three of these test an EXPLOIT rather than an inconvenience</b> —
    /// <see cref="ASwitchDoesNotResetTheCooldown"/>,
    /// <see cref="AReloadRunningWhenTheWeaponIsPutAwayDoesNotFinishInTheBag"/> and
    /// <see cref="SwitchingAwayAndBackDoesNotRefillTheClip"/>. Each is a free magazine or a free
    /// shot arriving by a different door, and the first two only exist because the fix parks the
    /// whole runtime state instead of the ammo count alone.
    /// </para>
    /// </remarks>
    public sealed class WeaponSwitchClipTests
    {
        private const byte Rifle = WeaponIds.RK44;
        private const byte Sidearm = WeaponIds.SIND7;

        [Fact]
        public void SwitchingAwayAndBackDoesNotRefillTheClip()
        {
            // The row, in four lines. Before X-43 the last assertion read ClipSize.
            var session = Armed(Rifle);

            session.Weapon.AmmoInClip = 7;

            session.SwitchWeaponTo(Sidearm);
            session.SwitchWeaponTo(Rifle);

            Assert.Equal(7, session.Weapon.AmmoInClip);
        }

        [Fact]
        public void AWeaponReachedForTheFirstTimeThisLifeIsFull()
        {
            // The other direction, and it is not symmetric bookkeeping: a loadout hands the body
            // loaded weapons, so a sidearm drawn for the first time must NOT arrive at whatever
            // the rifle happened to have.
            var session = Armed(Rifle);
            session.Weapon.AmmoInClip = 3;

            session.SwitchWeaponTo(Sidearm);

            Assert.Equal(WeaponCatalog.For(Sidearm).ClipSize, session.Weapon.AmmoInClip);
        }

        [Fact]
        public void ASwitchDoesNotResetTheCooldown()
        {
            // Parking the ammo and forgetting LastFiredTime would leave a quick-switch rapid-fire
            // exploit: fire, switch away, switch back, fire again inside the cooldown the server
            // believes it is enforcing -- and FireRateViolations would not move for it, because
            // as far as the resolver is concerned this weapon has never been fired.
            var session = Armed(Rifle);
            session.Weapon.LastFiredTime = 100f;

            session.SwitchWeaponTo(Sidearm);
            session.SwitchWeaponTo(Rifle);

            Assert.Equal(100f, session.Weapon.LastFiredTime);

            WeaponConfig config = session.WeaponConfig;
            Assert.Equal(
                FireRejection.OnCooldown,
                ServerFireResolver.CheckCanFire(
                    in session.Weapon, in config, shooterIsAlive: true,
                    nowSeconds: 100f + config.Cooldown * 0.5f));
        }

        [Fact]
        public void AReloadRunningWhenTheWeaponIsPutAwayDoesNotFinishInTheBag()
        {
            // The same free magazine by a different door: park a reloading weapon with its
            // ReloadStartedAt intact and the reload completes on its own while the weapon is in a
            // bag, so switching away, waiting, and switching back is a full clip.
            var session = Armed(Rifle);
            session.Weapon.AmmoInClip = 2;
            session.Weapon.Reloading = true;
            session.Weapon.ReloadStartedAt = 50f;

            session.SwitchWeaponTo(Sidearm);
            session.SwitchWeaponTo(Rifle);

            Assert.False(session.Weapon.Reloading);
            Assert.Equal(float.NegativeInfinity, session.Weapon.ReloadStartedAt);
            Assert.Equal(2, session.Weapon.AmmoInClip);

            // And a completion attempt long after the reload "would" have landed changes nothing,
            // because there is no reload to complete.
            WeaponConfig config = session.WeaponConfig;
            Assert.False(
                ServerReloadPolicy.CompleteReloadIfElapsed(
                    ref session.Weapon, in config, nowSeconds: 500f));
            Assert.Equal(2, session.Weapon.AmmoInClip);
        }

        [Fact]
        public void TheRestoredWeaponIsUnholstered()
        {
            // A holstered weapon cannot fire (FireRejection.Holstered). Parking sets the flag
            // false because that is what a weapon in a bag is; restoring has to undo it, or a
            // switch would be a permanent disarm and read as a netcode fault.
            var session = Armed(Rifle);

            session.SwitchWeaponTo(Sidearm);
            session.SwitchWeaponTo(Rifle);

            Assert.True(session.Weapon.Unholstered);
        }

        [Fact]
        public void SwitchingToTheWeaponAlreadyHeldDoesNotCancelARunningReload()
        {
            // The ordinary case, and the assertion that matters is the RELOAD one.
            //
            // The ammo half of this passes with or without the early return, because parking and
            // immediately restoring the same weapon round-trips the clip unchanged -- checked by
            // mutation, not assumed. What does NOT round-trip is a running reload: parking
            // cancels it deliberately (a weapon in a bag is not reloading), so a same-weapon
            // switch without the guard would cancel a reload the player never interrupted.
            var session = Armed(Rifle);
            session.Weapon.AmmoInClip = 4;
            session.Weapon.Reloading = true;
            session.Weapon.ReloadStartedAt = 50f;

            session.SwitchWeaponTo(Rifle);

            Assert.Equal(4, session.Weapon.AmmoInClip);
            Assert.Equal(Rifle, session.WeaponId);
            Assert.True(session.Weapon.Reloading,
                        "a switch to the weapon already held cancelled a reload nobody interrupted");
            Assert.Equal(50f, session.Weapon.ReloadStartedAt);
        }

        [Fact]
        public void ARespawnReArmsEverythingAndForgetsThePreviousLife()
        {
            // A life's worth of parked clips does not survive a death.
            //
            // THE ORDER HERE IS THE TEST, and the obvious version of it proves nothing: if the
            // respawn re-arms the same weapon that is parked, the next switch AWAY re-parks it at
            // full and overwrites the stale entry before anything reads it -- so the assertion
            // passes with or without the clear. Written that way this test stayed green under the
            // mutation that deletes Array.Clear.
            //
            // So the player must die holding the OTHER weapon, and reach the stale entry by
            // switching INTO it: life 1 leaves the rifle parked at 2, the respawn arms the
            // sidearm, and the first switch back to the rifle is the read.
            var session = Armed(Rifle);
            session.Weapon.AmmoInClip = 2;
            session.SwitchWeaponTo(Sidearm);

            // Died holding the sidearm; respawn re-arms it.
            session.ResetWeapon();
            Assert.Equal(WeaponCatalog.For(Sidearm).ClipSize, session.Weapon.AmmoInClip);

            session.SwitchWeaponTo(Rifle);

            Assert.Equal(
                WeaponCatalog.For(Rifle).ClipSize, session.Weapon.AmmoInClip);
        }

        [Fact]
        public void AnUnassignedWeaponIdIsSurvivedRatherThanIndexed()
        {
            // WeaponId is a byte and the parked table is MAX_ASSIGNED + 1 long. An id past the
            // end is not a legal weapon, but arriving at an IndexOutOfRangeException on the
            // 30 Hz switch path would take the server down for one.
            var session = Armed(Rifle);
            session.Weapon.AmmoInClip = 5;

            session.SwitchWeaponTo(byte.MaxValue);
            Assert.Equal(byte.MaxValue, session.WeaponId);

            session.SwitchWeaponTo(Rifle);
            Assert.Equal(5, session.Weapon.AmmoInClip);
        }

        // ------------------------------------------------ the Unity half, pinned as text

        [Fact]
        public void TheBridgeSwitchesRatherThanReArming()
        {
            string bridge = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Net/Server/ServerCombatBridge.cs");

            int adopt = bridge.IndexOf(
                "private static void AdoptTheWeaponTheBodyIsHolding", StringComparison.Ordinal);
            Assert.True(adopt >= 0, "AdoptTheWeaponTheBodyIsHolding is gone; re-read X-43.");

            string body = bridge.Substring(adopt, 900);

            Assert.Contains("session.SwitchWeaponTo(actor.WeaponId);", body, StringComparison.Ordinal);
            Assert.DoesNotContain("session.ResetWeapon();", body, StringComparison.Ordinal);
        }

        // ------------------------------------------------ helpers

        /// <summary>A session holding <paramref name="weaponId"/> with a full clip.</summary>
        private static ClientSession Armed(byte weaponId)
        {
            var session = new ClientSession(connectionId: 1, actorId: 41);
            session.WeaponId = weaponId;
            session.ResetWeapon();
            return session;
        }

        private static string ReadUnitySource(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
