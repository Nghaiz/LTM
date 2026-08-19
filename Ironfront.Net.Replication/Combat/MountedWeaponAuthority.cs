using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>What one mounted weapon's step did. V6 task 3.</summary>
    public readonly struct MountedFireResult
    {
        /// <summary>Why the trigger was refused, or <see cref="FireRejection.None"/>.</summary>
        /// <remarks>
        /// <b>None does not mean a shot was taken</b> — it also means no trigger was pulled. Read
        /// <see cref="Fired"/> for that. The same distinction <see cref="CombatTickResult"/> draws,
        /// and it exists for the same reason: collapsing the two would broadcast an
        /// <c>S_WEAPON_FIRE</c> on every tick a gunner sat in a seat doing nothing.
        /// </remarks>
        public readonly FireRejection Rejection;

        /// <summary>True when a shot was taken and the clip was spent.</summary>
        public readonly bool Fired;

        /// <summary>True when the ammo count changed — a shot, or a reload that landed.</summary>
        public readonly bool AmmoChanged;

        /// <summary>True when a reload finished on this step.</summary>
        public readonly bool ReloadCompleted;

        public MountedFireResult(
            FireRejection rejection, bool fired, bool ammoChanged, bool reloadCompleted)
        {
            Rejection       = rejection;
            Fired           = fired;
            AmmoChanged     = ammoChanged;
            ReloadCompleted = reloadCompleted;
        }
    }

    /// <summary>
    /// Server authority for weapons bolted to a vehicle: the ammo, the cooldown and the reload,
    /// keyed by seat rather than by shooter. V6 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The <see cref="ServerCombatAuthority"/> shape, one owner over.</b> The same
    /// load-bearing ordering — complete a running reload, then accept a reload intent, then the
    /// trigger — and deliberately the same <see cref="FireRejection"/> enum rather than a cloned
    /// one, because two rejection vocabularies for the same three refusals is how a HUD ends up
    /// saying "reloading" when the server said "on cooldown".
    /// </para>
    /// <para>
    /// <b>What it does NOT do: resolve the shot.</b> An infantry weapon is hitscan and resolves
    /// here; a mounted weapon launches a projectile, and projectile flight is V7. This decides
    /// whether the trigger is honoured and what it costs. The muzzle it launches from is settled
    /// earlier in the same tick by <c>ServerTurretAuthority.Step</c> — see the ordering note in
    /// the phase plan, and note that getting it backwards is silent: shots simply leave from
    /// where the turret pointed one tick ago.
    /// </para>
    /// <para>
    /// <b>Fire intent rides the existing <see cref="InputButtons.Fire"/> bit on <c>C_INPUT</c>
    /// (V6-D1).</b> A seated player already sends one. A second fire bit would create two paths to
    /// the same authority check, and the one nobody tests is the one that gets exploited.
    /// </para>
    /// <para>
    /// Nothing here allocates: the registry and the pool are constructor fields and there is no
    /// closure on the path.
    /// </para>
    /// </remarks>
    public sealed class MountedWeaponAuthority
    {
        private readonly MountedWeaponRegistry _registry;
        private readonly ISpareAmmoPool _pool;

        /// <param name="pool">
        /// Where a reload draws from. <see cref="MountedSpareAmmoPool.Instance"/> for the real
        /// server — a mounted weapon's spare rounds live on the weapon (V6-D6), and handing this
        /// an <see cref="ActorSpareAmmoPool"/> would drain the gunner's rifle magazines to refill
        /// a coaxial.
        /// </param>
        public MountedWeaponAuthority(MountedWeaponRegistry registry, ISpareAmmoPool pool)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _pool     = pool ?? throw new ArgumentNullException(nameof(pool));
        }

        /// <summary>Trigger pulls accepted.</summary>
        public long ShotsFired { get; private set; }

        /// <summary>Trigger pulls refused because the server's cooldown had not elapsed.</summary>
        public long FireRateViolations { get; private set; }

        /// <summary>Reload intents accepted.</summary>
        public long ReloadsStarted { get; private set; }

        /// <summary>Reloads that ran to completion and put rounds back in a clip.</summary>
        public long ReloadsCompleted { get; private set; }

        /// <summary>
        /// Steps one mounted weapon for one accepted input frame.
        /// </summary>
        /// <param name="vehicleId">The vehicle the weapon is bolted to.</param>
        /// <param name="seatIndex">The seat that operates it.</param>
        /// <param name="frame">
        /// The occupant's frame exactly as it arrived, so <see cref="InputButtons.Fire"/> and
        /// <see cref="InputButtons.Reload"/> are still present (V6-D1).
        /// </param>
        /// <param name="gunnerIsAlive">A corpse's queued input must not fire.</param>
        /// <param name="nowSeconds">The server clock, derived from the tick.</param>
        public MountedFireResult Step(
            ushort vehicleId, byte seatIndex, in InputFrame frame,
            bool gunnerIsAlive, float nowSeconds)
        {
            ref WeaponRuntimeState state = ref _registry.StateRef(vehicleId, seatIndex, out bool found);
            if (!found)
                return new MountedFireResult(FireRejection.None, false, false, false);

            WeaponConfig config = _registry.ConfigOf(vehicleId, seatIndex);
            byte ammoBefore = state.AmmoInClip;

            // 1. A running reload lands first, before anything reads the ammo count, so a trigger
            //    arriving on the tick a reload finishes is not rejected as Reloading. Same
            //    courtesy ServerCombatAuthority extends, and it has to be the same or the two
            //    disagree by one tick every reload.
            bool reloadCompleted = ServerReloadPolicy.CompleteReloadIfElapsed(
                ref state, in config, nowSeconds, _pool, vehicleId, seatIndex);
            if (reloadCompleted) ReloadsCompleted++;

            // 2. A fresh reload intent.
            if (frame.IsPressed(InputButtons.Reload)
                && ServerReloadPolicy.BeginReload(ref state, in config, gunnerIsAlive, nowSeconds)
                   == ServerReloadPolicy.Rejection.None)
                ReloadsStarted++;

            if (!frame.IsPressed(InputButtons.Fire))
                return new MountedFireResult(
                    FireRejection.None, fired: false,
                    ammoChanged: state.AmmoInClip != ammoBefore,
                    reloadCompleted);

            // 3. The trigger, against the SERVER's cooldown. A client sending ten frames inside
            //    one tick gets one shot and nine OnCooldown refusals.
            FireRejection rejection = CheckCanFire(in state, in config, gunnerIsAlive, nowSeconds);

            if (rejection != FireRejection.None)
            {
                if (rejection == FireRejection.OnCooldown) FireRateViolations++;

                return new MountedFireResult(
                    rejection, fired: false,
                    ammoChanged: state.AmmoInClip != ammoBefore,
                    reloadCompleted);
            }

            state.LastFiredTime = nowSeconds;

            // The horn's trigger costs nothing (V6 task 5) — CarHorn.Shoot never reaches
            // `ammo--`. The guard on > 0 is separate and covers a clip that is already empty on a
            // weapon whose ClipSize is 0, which must not underflow the byte.
            if (config.SpendsAmmo && state.AmmoInClip > 0) state.AmmoInClip--;

            ShotsFired++;

            return new MountedFireResult(
                FireRejection.None, fired: true,
                ammoChanged: state.AmmoInClip != ammoBefore,
                reloadCompleted);
        }

        /// <summary>
        /// The server's half of <c>Weapon.CanFire()</c>: the ammo, reload, holster and cooldown
        /// gate every mounted subclass already funnels through.
        /// </summary>
        /// <remarks>
        /// Static and side-effect free so the Unity seam can ask the same question without
        /// stepping anything — <c>NetWeaponAuthority.MayFire</c> is a predicate, and a predicate
        /// that spends ammo is a trap.
        /// </remarks>
        public static FireRejection CheckCanFire(
            in WeaponRuntimeState state, in WeaponConfig config,
            bool gunnerIsAlive, float nowSeconds)
        {
            if (!gunnerIsAlive) return FireRejection.ShooterDead;
            if (!state.Unholstered) return FireRejection.Holstered;
            if (state.Reloading) return FireRejection.Reloading;

            // ClipSize 0 means "no magazine" (the horn), not "empty". Only a weapon that HAS a
            // magazine can run out of it.
            if (config.ClipSize > 0 && state.AmmoInClip == 0) return FireRejection.NoAmmo;

            if (nowSeconds - state.LastFiredTime < config.Cooldown) return FireRejection.OnCooldown;

            return FireRejection.None;
        }

        /// <summary>Zeroes the counters. The registry keeps its weapons.</summary>
        public void ResetStatistics()
        {
            ShotsFired         = 0;
            FireRateViolations = 0;
            ReloadsStarted     = 0;
            ReloadsCompleted   = 0;
        }
    }
}
