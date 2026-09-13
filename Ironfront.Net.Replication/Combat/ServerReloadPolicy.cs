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

            /// <summary>
            /// The reserve cannot feed a reload: empty, or a weapon that has no reserve at all.
            /// </summary>
            /// <remarks>
            /// Distinct from <see cref="ClipFull"/> because the client shows them differently —
            /// an empty reserve is a reload the player will keep asking for, and a clip that is
            /// already full is one they will stop asking for the moment they look at the HUD.
            /// </remarks>
            NoReserve = 5,

            /// <summary>
            /// The server cannot say which loadout slot is active, so it cannot say which
            /// reserve a reload would spend. Handoff section 4.5.
            /// </summary>
            /// <remarks>
            /// Refusing is the whole point: the alternative is to assume slot 0 and drain a
            /// magazine belonging to a weapon the player is not holding. A separate code from
            /// <see cref="NoReserve"/> because this one is a SERVER inconsistency and should be
            /// counted and logged as one, not shown to the player as an empty pouch.
            /// </remarks>
            LoadoutSlotUnknown = 6,
        }

        /// <summary>
        /// Starts a reload, if the same pre-conditions the client checked still hold.
        /// </summary>
        /// <param name="state">Mutated on acceptance: the flag is set and the clock is stamped.</param>
        /// <param name="nowSeconds">Server time.</param>
        public static Rejection BeginReload(
            ref WeaponRuntimeState state, in WeaponConfig config, bool shooterIsAlive,
            float nowSeconds)
            => BeginReload(
                ref state, in config, shooterIsAlive, nowSeconds,
                ActorAmmoSource.Unlimited(), Protocol.SpareAmmo.Infinite);

        /// <summary>
        /// Starts a reload only if the reserve can actually feed one. Handoff section 5.3.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The reserve is checked at BEGIN, not only at completion,</b> because the snapshot
        /// carries <c>WeaponStateFlags.Reloading</c> from the moment the server accepts. A
        /// reload accepted against an empty pouch would play a full reload animation on every
        /// client and then hand back the same clip — the two sides disagreeing for two seconds
        /// about something the server already knew.
        /// </para>
        /// <para>
        /// <b>An unknown slot refuses rather than falling back.</b> See
        /// <see cref="Rejection.LoadoutSlotUnknown"/>.
        /// </para>
        /// </remarks>
        /// <param name="ammo">Which pool and slot this weapon draws from.</param>
        /// <param name="reserve">
        /// That source's answer, already resolved through <see cref="ActorAmmoSource.Reserve"/>.
        /// Passed in rather than recomputed so the number the caller reported on the wire and
        /// the number this rule reads are the same number.
        /// </param>
        public static Rejection BeginReload(
            ref WeaponRuntimeState state, in WeaponConfig config, bool shooterIsAlive,
            float nowSeconds, in ActorAmmoSource ammo, in Protocol.SpareAmmo reserve)
        {
            if (!shooterIsAlive) return Rejection.Dead;
            if (!ammo.SlotIsKnown) return Rejection.LoadoutSlotUnknown;
            if (!state.Unholstered) return Rejection.Holstered;
            if (state.Reloading) return Rejection.AlreadyReloading;
            if (state.AmmoInClip >= config.ClipSize) return Rejection.ClipFull;
            if (!reserve.CanFeedAReload) return Rejection.NoReserve;

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
            in ActorAmmoSource ammo)
        {
            if (!state.Reloading) return false;
            if (nowSeconds - state.ReloadStartedAt < ReloadSeconds) return false;

            state.Reloading = false;
            state.ReloadStartedAt = float.NegativeInfinity;

            int wanted = config.ClipSize - state.AmmoInClip;
            if (wanted <= 0) return false;

            int granted = ammo.Take(ref state, wanted);
            if (granted <= 0) return false;

            state.AmmoInClip = (byte)(state.AmmoInClip + granted);
            return true;
        }

        /// <summary>
        /// The pool/owner/slot form, for callers that hold the three separately.
        /// </summary>
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
