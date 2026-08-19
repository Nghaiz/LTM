using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Combat
{
    /// <summary>
    /// The server's reload rules. phase-05 task 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this class had to exist before the reported reload bug could close.</b> The
    /// client predicts a reload and then waits for the snapshot's
    /// <c>SnapshotField.Weapon</c> to answer it. That field is masked on change
    /// (<c>DeltaEncoder.ComputeChangeMask</c>), and until this class existed the server never
    /// changed the ammo count of anything — so the field never appeared, the client's
    /// <c>_reloadPending</c> never cleared, and the two sides disagreed for the rest of the
    /// life. The fix is not to make the client stop waiting; it is to make the server actually
    /// reload, which is what makes the field move.
    /// </para>
    /// <para>
    /// <b>Deliberately the same shape as <see cref="Client.ClientCombatState"/>'s predicted
    /// reload,</b> and reading the same <see cref="ProtocolConstants.RELOAD_SECONDS"/>. Two
    /// copies of a duration is the classic prediction bug: they agree until someone tunes one
    /// of them, and the only symptom is a clip that refills at a slightly different moment on
    /// each side.
    /// </para>
    /// <para>
    /// Static, allocation-free, and with no clock of its own — the caller passes the server
    /// time in, which is what lets a test drive a two-second reload in two statements.
    /// </para>
    /// </remarks>
    public static class ServerReloadPolicy
    {
        /// <summary>Seconds a reload takes. The shared constant, not a local literal (D3).</summary>
        public const float ReloadSeconds = ProtocolConstants.RELOAD_SECONDS;

        /// <summary>Why a reload intent was accepted or thrown away.</summary>
        public enum Rejection : byte
        {
            /// <summary>Accepted; the reload is now running.</summary>
            None = 0,

            /// <summary>One is already in flight. Re-pressing R does not restart it.</summary>
            AlreadyReloading = 1,

            /// <summary>The clip is already full.</summary>
            ClipFull = 2,

            /// <summary>The weapon is lowered — sprinting, or mid-swap.</summary>
            Holstered = 3,

            /// <summary>The player is dead. A corpse's queued input must not reload.</summary>
            Dead = 4,
        }

        /// <summary>
        /// Starts a reload, if the same pre-conditions the client checked still hold.
        /// </summary>
        /// <param name="state">Mutated on acceptance: the flag is set and the clock is stamped.</param>
        /// <param name="nowSeconds">Server time.</param>
        public static Rejection BeginReload(
            ref WeaponRuntimeState state, in WeaponConfig config, bool shooterIsAlive,
            float nowSeconds)
        {
            if (!shooterIsAlive) return Rejection.Dead;
            if (!state.Unholstered) return Rejection.Holstered;
            if (state.Reloading) return Rejection.AlreadyReloading;
            if (state.AmmoInClip >= config.ClipSize) return Rejection.ClipFull;

            state.Reloading = true;
            state.ReloadStartedAt = nowSeconds;
            return Rejection.None;
        }

        /// <summary>
        /// Fills the clip once <see cref="ReloadSeconds"/> has elapsed on the server clock.
        /// </summary>
        /// <remarks>
        /// Call once per tick before anything reads the ammo count.
        /// <see cref="ServerCombatAuthority.Step"/> does exactly that, so a trigger pull
        /// arriving on the tick a reload finishes is not rejected as
        /// <see cref="FireRejection.Reloading"/> — the same courtesy
        /// <c>ClientCombatState.PredictFire</c> extends on the other side, and it has to be the
        /// same or the two disagree by one frame every reload.
        /// </remarks>
        /// <returns>True when this call completed a reload — i.e. the ammo count just changed.</returns>
        public static bool CompleteReloadIfElapsed(
            ref WeaponRuntimeState state, in WeaponConfig config, float nowSeconds)
            => CompleteReloadIfElapsed(
                ref state, in config, nowSeconds,
                UnlimitedSpareAmmoPool.Instance, ownerId: 0, slot: 0);

        /// <summary>
        /// Fills the clip from <paramref name="pool"/> once <see cref="ReloadSeconds"/> has
        /// elapsed on the server clock. V6-D6.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The overload above refilled to <see cref="WeaponConfig.ClipSize"/> unconditionally,
        /// which is correct only for an infinite pool</b> — and until V6 that was every weapon the
        /// server modelled, so the shortcut was invisible. It is preserved verbatim as
        /// <see cref="UnlimitedSpareAmmoPool"/> rather than deleted, so no phase-05 behaviour
        /// moves; the mounted path passes a real pool instead.
        /// </para>
        /// <para>
        /// <b>The arithmetic mirrors <c>Weapon.ReloadDone()</c> exactly</b> — ask for
        /// <c>ClipSize - AmmoInClip</c>, add back however many the pool actually granted. Rounding
        /// it any other way is how a partially-supplied reload ends up with a full clip on one
        /// side and a partial one on the other.
        /// </para>
        /// </remarks>
        /// <param name="pool">Where the rounds come from. Never null.</param>
        /// <param name="ownerId">Whose pool — an <c>actorId</c> for the infantry pool.</param>
        /// <param name="slot">The loadout slot, for a pool that keeps more than one.</param>
        public static bool CompleteReloadIfElapsed(
            ref WeaponRuntimeState state, in WeaponConfig config, float nowSeconds,
            ISpareAmmoPool pool, ushort ownerId, byte slot)
        {
            if (pool == null) throw new System.ArgumentNullException(nameof(pool));

            if (!state.Reloading) return false;
            if (nowSeconds - state.ReloadStartedAt < ReloadSeconds) return false;

            state.Reloading = false;
            state.ReloadStartedAt = float.NegativeInfinity;

            int wanted = config.ClipSize - state.AmmoInClip;
            if (wanted <= 0) return false;

            int granted = pool.Take(ownerId, slot, ref state, wanted);
            if (granted <= 0) return false;

            state.AmmoInClip = (byte)(state.AmmoInClip + granted);
            return true;
        }

        /// <summary>Seconds left on a running reload, for a HUD or a test. 0 when none is.</summary>
        public static float SecondsRemaining(in WeaponRuntimeState state, float nowSeconds)
        {
            if (!state.Reloading) return 0f;

            float remaining = ReloadSeconds - (nowSeconds - state.ReloadStartedAt);
            return remaining > 0f ? remaining : 0f;
        }

        /// <summary>
        /// Cancels a running reload without filling the clip. Called on death.
        /// </summary>
        /// <remarks>
        /// Not called on fire — see decision D7. A shot arriving mid-reload is refused and
        /// leaves the reload running, because that is what the client already does, and the one
        /// rule about these two implementations is that they change together or not at all.
        /// </remarks>
        public static void Abort(ref WeaponRuntimeState state)
        {
            state.Reloading = false;
            state.ReloadStartedAt = float.NegativeInfinity;
        }
    }
}
