using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The client half of combat: fire prediction, local combat state, the ammo anti-flicker
    /// rule, and the event models behind the hitmarker and the killfeed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// phase-02 criteria 3, 4 and 5 are all two-client video criteria — hitmarkers appearing,
    /// hits landing on a strafing target at 150 ms, ragdolls falling — and none of them can be
    /// graded here. What can be graded here is every decision those criteria depend on, which
    /// is the whole reason this state lives outside a <c>MonoBehaviour</c>.
    /// </para>
    /// <para>
    /// The routing tests below are the ones that matter most today: before them the server
    /// wrote S_HIT_CONFIRM, S_DEATH and S_WEAPON_FIRE and the client counted all three as
    /// unknown message types, so a confirmed hit reached the client and was thrown away.
    /// </para>
    /// </remarks>
    public sealed class ClientCombatTests
    {
        private const float ClipSize = 30f;

        private static WeaponConfig Rifle => WeaponConfig.Rifle;

        /// <summary>A time far enough past the cooldown that every shot is legal.</summary>
        private static float ShotTime(int index) => 10f + index * (Rifle.Cooldown + 0.01f);

        private static ActorSnapshotEntry LocalEntry(
            byte health = 100, byte ammo = 30, bool alive = true, byte weaponId = 0)
            => new ActorSnapshotEntry
            {
                ActorId = 1,
                ChangeMask = SnapshotField.Health | SnapshotField.StateFlags | SnapshotField.Weapon,
                Health = health,
                StateFlags = alive ? ActorStateFlags.IsAlive : ActorStateFlags.None,
                WeaponId = weaponId,
                AmmoInClip = ammo,
            };

        // --------------------------------------------------------- ammo anti-flicker

        [Theory]
        [InlineData(29, 30, 29)]   // one shot in flight — the classic 30,29,30 flicker
        [InlineData(28, 30, 28)]   // two in flight: exactly the threshold, still the client's
        [InlineData(27, 30, 30)]   // three apart is "more than 2" — the snapshot takes over
        [InlineData(26, 30, 30)]   // four apart — these are about different clips
        [InlineData(30, 26, 26)]   // drift the other way corrects just the same
        public void PredictedAmmoWinsUntilItDriftsPastTheThreshold(
            int predicted, int fromSnapshot, int expected)
        {
            byte result = ClientCombatState.ReconcileAmmo((byte)predicted, (byte)fromSnapshot, false);

            Assert.Equal((byte)expected, result);
        }

        [Fact]
        public void AReloadTakesTheSnapshotCountVerbatim()
        {
            // The one case where a large gap is correct rather than suspicious.
            Assert.Equal(30, ClientCombatState.ReconcileAmmo(2, 30, reloadPending: true));
        }

        [Fact]
        public void TheHudDoesNotFlickerWhileFiringAgainstALaggingSnapshot()
        {
            // The failure this rule exists to stop, driven end to end: the client fires, the
            // snapshot is always one shot behind, and the displayed number must never go back up.
            var state = new ClientCombatState();
            var seen = new List<byte>();

            for (int shot = 0; shot < 8; shot++)
            {
                Assert.Equal(FireRejection.None, state.PredictFire(ShotTime(shot)));
                seen.Add(state.AmmoInClip);

                state.ApplySnapshot(LocalEntry(ammo: (byte)(ClipSize - shot)));
                seen.Add(state.AmmoInClip);
            }

            for (int i = 1; i < seen.Count; i++)
                Assert.True(seen[i] <= seen[i - 1], $"ammo went back up at index {i}: {string.Join(",", seen)}");

            Assert.Equal(0, state.SnapshotAmmoCorrections);
        }

        [Fact]
        public void ASnapshotFurtherOutThanTheThresholdIsCountedAsACorrection()
        {
            var state = new ClientCombatState();
            state.ApplySnapshot(LocalEntry(ammo: 30));   // clears the equip resync

            state.ApplySnapshot(LocalEntry(ammo: 12));

            Assert.Equal(12, state.AmmoInClip);
            Assert.Equal(1, state.SnapshotAmmoCorrections);
        }

        // --------------------------------------------------------- fire prediction

        [Fact]
        public void FirePredictionUsesTheServersOwnPreConditions()
        {
            var state = new ClientCombatState();

            Assert.Equal(FireRejection.None, state.PredictFire(ShotTime(0)));
            Assert.Equal(29, state.AmmoInClip);

            // Same instant, so the cooldown has not elapsed — the server would reject this too.
            Assert.Equal(FireRejection.OnCooldown, state.PredictFire(ShotTime(0)));
            Assert.Equal(29, state.AmmoInClip);
        }

        [Fact]
        public void ARejectedShotConsumesNothing()
        {
            var state = new ClientCombatState();
            for (int shot = 0; shot < 30; shot++)
                Assert.Equal(FireRejection.None, state.PredictFire(ShotTime(shot)));

            Assert.Equal(0, state.AmmoInClip);
            Assert.Equal(FireRejection.NoAmmo, state.PredictFire(ShotTime(30)));
            Assert.Equal(30, state.PredictedShots);
        }

        [Fact]
        public void ACorpseDoesNotFire()
        {
            var state = new ClientCombatState();
            state.ApplySnapshot(LocalEntry(health: 0, alive: false));

            Assert.Equal(FireRejection.ShooterDead, state.PredictFire(ShotTime(0)));
        }

        // --------------------------------------------------------- health, death, respawn

        [Fact]
        public void HealthComesOnlyFromTheSnapshotAndReportsTheDrop()
        {
            var state = new ClientCombatState();
            byte previous = 0, current = 0;
            state.OnHealthChanged += (from, to) => { previous = from; current = to; };

            // A hit confirm on someone else must not touch local health, even though it carries
            // a damage number — the same damage is already in the snapshot.
            state.ApplySnapshot(LocalEntry(health: 100));
            Assert.Equal(100, state.Health);

            state.ApplySnapshot(LocalEntry(health: 65));

            Assert.Equal(65, state.Health);
            Assert.Equal(100, previous);
            Assert.Equal(65, current);
        }

        [Fact]
        public void DeathStampsTheRespawnClockAndTheDelayHasToElapse()
        {
            var state = new ClientCombatState { RespawnDelaySeconds = 3f };
            int died = 0;
            state.OnDied += () => died++;

            state.ApplyDeath(new DeathMessage(1, 2, CauseOfDeath.Bullet, 0, 0, 0, (byte)HitboxType.Head), 100f);

            Assert.False(state.IsAlive);
            Assert.Equal(1, died);
            Assert.False(state.CanRequestRespawn(102f));
            Assert.Equal(1f, state.SecondsUntilRespawn(102f), 3);
            Assert.True(state.CanRequestRespawn(103f));
            Assert.Equal(0f, state.SecondsUntilRespawn(103f));
        }

        [Fact]
        public void TheSnapshotArrivingFirstDoesNotFireDiedTwice()
        {
            // S_DEATH and the snapshot's IsAlive bit say the same thing a fraction apart.
            var state = new ClientCombatState();
            int died = 0;
            state.OnDied += () => died++;

            state.ApplySnapshot(LocalEntry(health: 0, alive: false));
            state.ApplyDeath(new DeathMessage(1, 2, CauseOfDeath.Bullet, 0, 0, 0, 0), 100f);

            Assert.Equal(1, died);
        }

        [Fact]
        public void RespawnRefillsTheClipAndResyncsFromTheNextSnapshot()
        {
            var state = new ClientCombatState();
            int respawned = 0;
            state.OnRespawned += () => respawned++;

            state.ApplySnapshot(LocalEntry(ammo: 30));
            for (int shot = 0; shot < 5; shot++) state.PredictFire(ShotTime(shot));
            state.ApplySnapshot(LocalEntry(health: 0, alive: false, ammo: 25));

            state.ApplySnapshot(LocalEntry(health: 100, alive: true, ammo: 30));

            Assert.Equal(1, respawned);
            Assert.True(state.IsAlive);
            Assert.Equal(30, state.AmmoInClip);
        }

        [Fact]
        public void AReloadResyncsOnceAndThenGoesBackToTrustingTheClient()
        {
            var state = new ClientCombatState();
            state.ApplySnapshot(LocalEntry(ammo: 30));
            for (int shot = 0; shot < 20; shot++) state.PredictFire(ShotTime(shot));
            Assert.Equal(10, state.AmmoInClip);

            state.BeginReload();
            Assert.True(state.IsReloading);

            state.ApplySnapshot(LocalEntry(ammo: 30));
            Assert.Equal(30, state.AmmoInClip);
            Assert.False(state.IsReloading);

            // Back to normal: one predicted shot ahead of the snapshot keeps the client's count.
            state.PredictFire(ShotTime(21));
            state.ApplySnapshot(LocalEntry(ammo: 30));
            Assert.Equal(29, state.AmmoInClip);
        }

        // --------------------------------------------------------- hitmarker

        [Fact]
        public void AKillOutranksAHeadshot()
        {
            Assert.Equal(HitmarkerSeverity.Normal, HitmarkerEvent.SeverityOf(false, false));
            Assert.Equal(HitmarkerSeverity.Headshot, HitmarkerEvent.SeverityOf(false, true));
            Assert.Equal(HitmarkerSeverity.Kill, HitmarkerEvent.SeverityOf(true, false));
            Assert.Equal(HitmarkerSeverity.Kill, HitmarkerEvent.SeverityOf(true, true));
        }

        [Fact]
        public void TheHitmarkerExpiresOnItsOwnClock()
        {
            var model = new HitmarkerModel();
            Assert.False(model.IsVisible(0f));

            model.Push(new HitConfirmMessage(7, HitConfirmMessage.PackDamage(25f), HitboxType.Body, HitFlags.None), 900, 5f);

            Assert.True(model.IsVisible(5f));
            Assert.True(model.IsVisible(5f + HitmarkerModel.DefaultDisplaySeconds - 0.01f));
            Assert.False(model.IsVisible(5f + HitmarkerModel.DefaultDisplaySeconds));
            Assert.Equal(25f, model.Current.Damage, 3);
            Assert.Equal(900u, model.Current.AtTick);
        }

        [Fact]
        public void ANewerQuieterHitReplacesALouderOne()
        {
            // Holding the kill marker up would freeze it over a target who is still alive.
            var model = new HitmarkerModel();
            model.Push(new HitConfirmMessage(7, 250, HitboxType.Head, HitFlags.Killed | HitFlags.Headshot), 900, 5f);
            model.Push(new HitConfirmMessage(8, 250, HitboxType.Body, HitFlags.None), 903, 5.1f);

            Assert.Equal(HitmarkerSeverity.Normal, model.Current.Severity);
            Assert.Equal(8, model.Current.TargetActorId);
            Assert.Equal(2, model.HitCount);
        }

        // --------------------------------------------------------- killfeed

        [Fact]
        public void TheKillfeedIsNewestFirstAndDropsTheOldestWhenFull()
        {
            var feed = new KillfeedModel(capacity: 5);
            for (ushort i = 0; i < 7; i++)
                feed.Push(new DeathMessage(i, (ushort)(100 + i), CauseOfDeath.Bullet, 0, 0, 0, 0), 1f);

            Assert.Equal(5, feed.Count);
            Assert.Equal(7, feed.TotalKills);
            Assert.Equal(6, feed[0].VictimActorId);      // newest
            Assert.Equal(2, feed[4].VictimActorId);      // oldest still held
        }

        [Fact]
        public void KillfeedLinesExpireOldestFirst()
        {
            var feed = new KillfeedModel();
            feed.Push(new DeathMessage(1, 2, CauseOfDeath.Bullet, 0, 0, 0, 0), 0f);
            feed.Push(new DeathMessage(3, 4, CauseOfDeath.Explosion, 0, 0, 0, 0), 4f);

            feed.Prune(5.5f);   // the first is 5.5 s old, the second only 1.5 s

            Assert.Equal(1, feed.Count);
            Assert.Equal(3, feed[0].VictimActorId);

            feed.Prune(9.5f);
            Assert.Equal(0, feed.Count);
        }

        [Fact]
        public void AnEnvironmentKillAndAHeadshotAreBothReadableFromTheEntry()
        {
            var feed = new KillfeedModel();
            feed.Push(
                new DeathMessage(9, DeathMessage.EnvironmentKiller, CauseOfDeath.Fall, 0, 0, 0, (byte)HitboxType.Body),
                0f);
            feed.Push(
                new DeathMessage(9, 3, CauseOfDeath.Bullet, 0, 0, 0, (byte)HitboxType.Head),
                0f);

            Assert.True(feed[0].Headshot);
            Assert.False(feed[0].KilledByEnvironment);
            Assert.True(feed[1].KilledByEnvironment);
            Assert.Equal(CauseOfDeath.Fall, feed[1].Cause);
        }

        [Fact]
        public void ReadingPastTheHeldCountThrows()
        {
            var feed = new KillfeedModel();
            Assert.Throws<ArgumentOutOfRangeException>(() => feed[0]);
        }

        // --------------------------------------------------------- routing

        [Fact]
        public void TheRouterDeliversHitConfirmDeathAndWeaponFire()
        {
            // Before this, all three were counted as unknown message types and dropped.
            var router = new ClientMessageRouter();
            HitConfirmMessage? hit = null;
            DeathMessage? death = null;
            WeaponFireMessage? fire = null;

            router.OnHitConfirm += m => hit = m;
            router.OnDeath += m => death = m;
            router.OnWeaponFire += m => fire = m;

            Span<byte> buffer = stackalloc byte[256];

            int written = ServerEventWriter.WriteHitConfirm(
                buffer, new HitConfirmMessage(7, HitConfirmMessage.PackDamage(40f), HitboxType.Head, HitFlags.Headshot));
            Assert.Equal(1, router.Route(buffer.Slice(0, written)));

            written = ServerEventWriter.WriteDeath(
                buffer, new DeathMessage(7, 3, CauseOfDeath.Bullet, 10, 20, 30, (byte)HitboxType.Head));
            Assert.Equal(1, router.Route(buffer.Slice(0, written)));

            written = ServerEventWriter.WriteWeaponFire(buffer, new WeaponFireMessage(3, 2, 1, 2, 3));
            Assert.Equal(1, router.Route(buffer.Slice(0, written)));

            Assert.Equal(0, router.UnknownMessages);
            Assert.Equal(0, router.MalformedMessages);

            Assert.True(hit.HasValue);
            Assert.Equal(40f, hit!.Value.Damage, 3);
            Assert.True(hit.Value.Headshot);

            Assert.True(death.HasValue);
            Assert.Equal(7, death!.Value.VictimActorId);
            Assert.Equal(20, death.Value.ForceY);

            Assert.True(fire.HasValue);
            Assert.Equal(3, fire!.Value.ShooterActorId);
            Assert.Equal(2, fire.Value.WeaponId);
        }

        [Fact]
        public void ATruncatedCombatMessageIsCountedNotThrown()
        {
            // Same contract as every other handler: bytes off the network, so a bad one is
            // routine. Frame a real hit confirm, then lie about the body length.
            var router = new ClientMessageRouter();
            int raised = 0;
            router.OnHitConfirm += _ => raised++;

            Span<byte> buffer = stackalloc byte[256];
            Span<byte> body = stackalloc byte[HitConfirmMessage.Size - 1];
            var writer = new PayloadFrameWriter(buffer, ChannelId.ReliableOrdered);
            Assert.True(writer.WriteMessage(ServerMessageType.HitConfirm, body));
            Assert.True(writer.TryFinish(out int total));

            Assert.Equal(0, router.Route(buffer.Slice(0, total)));
            Assert.Equal(0, raised);
            Assert.Equal(1, router.MalformedMessages);
        }
    }
}
