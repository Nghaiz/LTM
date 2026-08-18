using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// The local player's combat state: health, alive/dead, respawn timing, and the ammo
    /// count the client predicts ahead of the server. phase-02 tasks 3 and 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the local player only.</b> Remote actors' health and ammo are read straight
    /// off the snapshot by whatever draws them; nothing about them is predicted, so nothing
    /// about them needs state. Callers filter by actor id before handing anything here — see
    /// <see cref="KillfeedModel"/> for the everybody-else half.
    /// </para>
    /// <para>
    /// <b>Damage is never applied locally.</b> Health only ever moves because a snapshot said
    /// so. A client that subtracted <c>S_HIT_CONFIRM</c> damage from its own health would be
    /// double-counting, since the same damage is already reflected in the next snapshot — and
    /// the two would disagree for the rest of the life, because the server owns the true
    /// number and never re-sends the ones the client missed.
    /// </para>
    /// <para>
    /// <b>The fire pre-conditions are the server's own predicate.</b>
    /// <see cref="PredictFire"/> calls <see cref="ServerFireResolver.CheckCanFire"/> rather
    /// than re-implementing the cooldown/ammo/reload/holster rules. A second copy of those
    /// rules is the classic prediction bug: the two drift by one edge case, the client
    /// predicts a shot the server rejects, and the only symptom is an ammo count that
    /// occasionally jumps back up.
    /// </para>
    /// </remarks>
    public sealed class ClientCombatState
    {
        /// <summary>
        /// How far the predicted ammo count may sit from the snapshot's before the snapshot
        /// wins. phase-02 trap 4 / phase-03 task 4.
        /// </summary>
        public const byte AmmoResyncThreshold = 2;

        /// <summary>Seconds after death before a respawn may be requested.</summary>
        /// <remarks>
        /// The shared constant, not a local literal (phase-05 D3).
        /// <see cref="ServerRespawnGate"/> reads the same one, so the moment this client's
        /// respawn button lights up is the moment the server starts accepting the request
        /// rather than a moment that happens to be close to it.
        /// </remarks>
        public const float DefaultRespawnDelaySeconds = ProtocolConstants.RESPAWN_SECONDS;

        /// <summary>
        /// Seconds a predicted reload takes before the clip is treated as full.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The shared constant (phase-05 D3), read by <see cref="ServerReloadPolicy"/> too.
        /// </para>
        /// <para>
        /// <b>Historical note, because the comment that used to be here was the bug report.</b>
        /// Until phase-05 the server had no reload model: <c>InputButtons.Reload</c> was packed
        /// and sent and nothing read it, so <c>SnapshotField.Weapon</c> never changed — the
        /// delta encoder masks on change — and <see cref="_reloadPending"/> never cleared. The
        /// fix was not to make this side stop waiting; it was to give the server a reload, which
        /// is what makes the field move. <see cref="ServerReloadPolicy"/> is that model, and it
        /// mirrors this one exactly: fire is refused while reloading and does not cancel the
        /// reload (D7). Changing either side's rules is a change to both in one commit.
        /// </para>
        /// </remarks>
        public const float DefaultReloadSeconds = ProtocolConstants.RELOAD_SECONDS;

        // Inert, not a rifle, until a snapshot or a loadout names the weapon. Predicting with
        // rifle numbers for a weapon that is not a rifle is the client half of the bug phase-V2
        // closes on the server: every shot would reconcile against a different clip size and
        // SnapshotAmmoCorrections would climb at the rate of PredictedShots, which is precisely
        // what that counter documents as "client and server disagreeing about the weapon".
        private WeaponConfig _weapon = WeaponCatalog.Inert;
        private WeaponRuntimeState _runtime = WeaponRuntimeState.Loaded(WeaponCatalog.Inert);

        /// <summary>Set by a reload, cleared by the first snapshot that carries an ammo count.</summary>
        private bool _reloadPending;

        private float _diedAtSeconds = float.NegativeInfinity;

        /// <summary>Whether <see cref="_diedAtSeconds"/> holds a real clock reading this life.</summary>
        private bool _deathStamped;

        /// <summary>When the predicted reload started, or NaN when none is running.</summary>
        private float _reloadStartedAt = float.NaN;

        /// <summary>Seconds after death before <see cref="CanRequestRespawn"/> turns true.</summary>
        public float RespawnDelaySeconds { get; set; } = DefaultRespawnDelaySeconds;

        /// <summary>How long a predicted reload takes. See <see cref="DefaultReloadSeconds"/>.</summary>
        public float ReloadSeconds { get; set; } = DefaultReloadSeconds;

        /// <summary>
        /// The actor this client drives. <see cref="ApplyDeath"/> ignores everyone else's death.
        /// </summary>
        /// <remarks>
        /// Zero until the server names one, and nothing matches zero, so a caller that wires
        /// <c>router.OnDeath</c> straight to <see cref="ApplyDeath"/> before the id is known
        /// reports no local death rather than reporting every death in the match as this
        /// player's. The snapshot's IsAlive bit still lands either way.
        /// </remarks>
        public ushort LocalActorId { get; set; }

        /// <summary>0..100, straight from the snapshot.</summary>
        public byte Health { get; private set; } = 100;

        /// <summary>From the snapshot's <see cref="ActorStateFlags.IsAlive"/> bit.</summary>
        public bool IsAlive { get; private set; } = true;

        /// <summary>Predicted between snapshots; corrected by <see cref="ReconcileAmmo"/>.</summary>
        public byte AmmoInClip => _runtime.AmmoInClip;

        /// <summary>The equipped weapon's clip size, for a "27 / 30" HUD.</summary>
        public byte ClipSize => _weapon.ClipSize;

        /// <summary>From the snapshot, or from <see cref="EquipWeapon"/> before one arrives.</summary>
        public byte WeaponId { get; private set; }

        /// <summary>True between <see cref="BeginReload"/> and the snapshot that answers it.</summary>
        public bool IsReloading => _runtime.Reloading;

        /// <summary>Trigger pulls the client predicted. The denominator for the next figure.</summary>
        public long PredictedShots { get; private set; }

        /// <summary>
        /// Times the snapshot overrode the predicted ammo count.
        /// </summary>
        /// <remarks>
        /// Non-zero is not a fault — a reload resyncs by design. Non-zero and climbing at
        /// roughly the rate of <see cref="PredictedShots"/> is: it means the prediction is
        /// wrong every shot, which is what a client and server disagreeing about the weapon
        /// looks like from here.
        /// </remarks>
        public long SnapshotAmmoCorrections { get; private set; }

        /// <summary>Health changed. Carries (previous, current) — a drop drives the damage indicator.</summary>
        public event Action<byte, byte>? OnHealthChanged;

        /// <summary>The local player died, per the snapshot or an S_DEATH naming them.</summary>
        public event Action? OnDied;

        /// <summary>The local player is alive again, per the snapshot.</summary>
        public event Action? OnRespawned;

        /// <summary>Swaps the weapon and loads a full clip. Call on loadout selection.</summary>
        /// <remarks>
        /// Takes the id alone: the numbers come from <see cref="WeaponCatalog"/>, which is the
        /// same table the server resolves against, so the two sides cannot be handed different
        /// configs for the same weapon.
        /// </remarks>
        public void EquipWeapon(byte weaponId)
        {
            WeaponId = weaponId;
            _weapon = WeaponCatalog.For(weaponId);
            _runtime = WeaponRuntimeState.Loaded(_weapon);
            _reloadStartedAt = float.NaN;

            // A weapon swap resyncs on the next snapshot rather than trusting the fresh clip:
            // the server may have handed out a partially-loaded weapon, and the predicted
            // count here is a guess until it says otherwise.
            _reloadPending = true;
        }

        /// <summary>
        /// Predicts one trigger pull: stamps the cooldown and decrements ammo locally.
        /// </summary>
        /// <remarks>
        /// The effects a caller plays on <see cref="FireRejection.None"/> are muzzle flash,
        /// recoil, a cosmetic tracer and the ammo decrement — never a raycast and never
        /// damage. Whether anything was hit is the server's answer, and it arrives as
        /// S_HIT_CONFIRM (decision AD-3).
        /// </remarks>
        /// <returns><see cref="FireRejection.None"/> when the shot may be shown.</returns>
        public FireRejection PredictFire(float nowSeconds)
        {
            CompleteReloadIfElapsed(nowSeconds);

            FireRejection rejection =
                ServerFireResolver.CheckCanFire(in _runtime, in _weapon, IsAlive, nowSeconds);

            if (rejection != FireRejection.None) return rejection;

            _runtime.LastFiredTime = nowSeconds;
            _runtime.AmmoInClip--;
            PredictedShots++;
            return FireRejection.None;
        }

        /// <summary>
        /// Marks a reload in flight, so the next snapshot's ammo count is taken verbatim.
        /// </summary>
        /// <remarks>
        /// The reload itself is the server's: it decides when the clip is full and says so in
        /// the snapshot. All this does is suspend the anti-flicker rule, because a reload is
        /// exactly the case where a large predicted/authoritative gap is correct rather than
        /// suspicious.
        /// </remarks>
        public void BeginReload(float nowSeconds)
        {
            if (_runtime.Reloading) return;
            if (_runtime.AmmoInClip >= _weapon.ClipSize) return;

            _runtime.Reloading = true;
            _reloadPending = true;
            _reloadStartedAt = nowSeconds;
        }

        /// <summary>
        /// Advances the predicted reload. Call once a frame while alive.
        /// </summary>
        /// <remarks>
        /// <see cref="PredictFire"/> runs the same check first, so a trigger pull on the exact
        /// frame a reload finishes is not rejected by a caller that forgot this. Everything else
        /// - the HUD reading <see cref="AmmoInClip"/>, <see cref="IsReloading"/> driving an
        /// animation - needs it called.
        /// </remarks>
        public void Tick(float nowSeconds) => CompleteReloadIfElapsed(nowSeconds);

        /// <summary>
        /// Fills the clip once the reload duration has elapsed.
        /// </summary>
        /// <remarks>
        /// Predicted, exactly like the ammo decrement, and for the same reason: waiting for the
        /// server puts a visible delay on the HUD. Unlike the decrement it is not corrected by
        /// the server, because the server has no reload - see
        /// <see cref="DefaultReloadSeconds"/>. Until it grows one the authoritative ammo will
        /// disagree after a reload and the anti-flicker rule will hand the snapshot's lower
        /// count back. That is the honest rendering of a server that does not know the player
        /// reloaded, and it is visible rather than silent.
        /// </remarks>
        private void CompleteReloadIfElapsed(float nowSeconds)
        {
            if (!_runtime.Reloading) return;
            if (float.IsNaN(_reloadStartedAt)) return;
            if (nowSeconds - _reloadStartedAt < ReloadSeconds) return;

            _runtime.Reloading = false;
            _runtime.AmmoInClip = _weapon.ClipSize;
            _reloadStartedAt = float.NaN;
        }

        /// <summary>
        /// Folds one snapshot entry for the local actor into this state.
        /// </summary>
        /// <remarks>
        /// Takes the entry, not the whole <see cref="WorldSnapshot"/>, because finding the
        /// local actor in it is the caller's job and it already has the index.
        /// </remarks>
        public void ApplySnapshot(in ActorSnapshotEntry entry, float nowSeconds)
        {
            if (entry.Has(SnapshotField.Health)) SetHealth(entry.Health);

            if (entry.Has(SnapshotField.StateFlags))
                SetAlive((entry.StateFlags & ActorStateFlags.IsAlive) != 0, nowSeconds);

            if (!entry.Has(SnapshotField.Weapon)) return;

            if (entry.WeaponId != WeaponId)
            {
                WeaponId = entry.WeaponId;

                // The clip size the ammo below is reconciled against belongs to the NEW weapon.
                // Re-resolving here rather than only in EquipWeapon is what keeps a server-side
                // weapon swap — a respawn with a different loadout, a pickup — from leaving this
                // side predicting with the previous gun's numbers.
                _weapon = WeaponCatalog.For(WeaponId);
            }

            byte reconciled = ReconcileAmmo(_runtime.AmmoInClip, entry.AmmoInClip, _reloadPending);
            if (reconciled != _runtime.AmmoInClip) SnapshotAmmoCorrections++;

            _runtime.AmmoInClip = reconciled;

            // The snapshot has now answered the reload, whichever way it went: either the clip
            // came back full or it did not, and in both cases the next divergence is the
            // client's own prediction rather than a reload in flight.
            if (_reloadPending)
            {
                _reloadPending = false;
                _runtime.Reloading = false;
                _reloadStartedAt = float.NaN;
            }
        }

        /// <summary>
        /// Applies an S_DEATH naming the local player, stamping the respawn clock.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Deaths of other actors are ignored - see <see cref="LocalActorId"/>. The event is a
        /// broadcast, because the killfeed is global, so a handler wired straight to
        /// <c>ClientMessageRouter.OnDeath</c> receives every death in the match.
        /// </para>
        /// <para>
        /// The snapshot's IsAlive bit says the same thing a fraction of a second apart, and
        /// either can land first: S_DEATH is reliable and the snapshot is not, but they are
        /// produced on the same tick. <see cref="OnDied"/> fires once whichever way round they
        /// arrive; the respawn clock is stamped by whichever gets here first and not moved by
        /// the second.
        /// </para>
        /// </remarks>
        /// <returns>Whether the message named this client's actor.</returns>
        public bool ApplyDeath(in DeathMessage message, float nowSeconds)
        {
            if (message.VictimActorId != LocalActorId) return false;

            SetAlive(false, nowSeconds);
            return true;
        }

        /// <summary>Whether the respawn delay has elapsed. False while alive.</summary>
        public bool CanRequestRespawn(float nowSeconds)
            => !IsAlive && _deathStamped && nowSeconds - _diedAtSeconds >= RespawnDelaySeconds;

        /// <summary>Seconds left on the respawn clock, for the death screen. 0 when ready or alive.</summary>
        public float SecondsUntilRespawn(float nowSeconds)
        {
            if (IsAlive) return 0f;
            if (!_deathStamped) return RespawnDelaySeconds;

            float remaining = RespawnDelaySeconds - (nowSeconds - _diedAtSeconds);
            return remaining > 0f ? remaining : 0f;
        }

        /// <summary>Drops everything. Call on disconnect or when leaving a match.</summary>
        public void Reset()
        {
            _weapon = WeaponCatalog.Inert;
            _runtime = WeaponRuntimeState.Loaded(WeaponCatalog.Inert);
            _reloadPending = false;
            _reloadStartedAt = float.NaN;
            _diedAtSeconds = float.NegativeInfinity;
            _deathStamped = false;
            Health = 100;
            IsAlive = true;
            WeaponId = 0;
            PredictedShots = 0;
            SnapshotAmmoCorrections = 0;
        }

        /// <summary>
        /// phase-02 trap 4, in one place: the client's predicted ammo wins unless a reload is
        /// in flight or the two have drifted further than <see cref="AmmoResyncThreshold"/>.
        /// </summary>
        /// <remarks>
        /// Without this, a client that has predicted one shot ahead of the server reads 29
        /// while the snapshot still says 30, takes the snapshot, predicts 29 again on the next
        /// frame, and the HUD reads 30, 29, 30, 29 for as long as the player keeps firing. The
        /// threshold is what distinguishes "one or two shots in flight", which is the normal
        /// operating condition, from "these two numbers are about a different clip", which is
        /// the only case worth a visible correction.
        /// </remarks>
        public static byte ReconcileAmmo(byte predicted, byte fromSnapshot, bool reloadPending)
        {
            if (reloadPending) return fromSnapshot;

            int drift = predicted - fromSnapshot;
            if (drift < 0) drift = -drift;

            return drift > AmmoResyncThreshold ? fromSnapshot : predicted;
        }

        private void SetHealth(byte health)
        {
            if (health == Health) return;

            byte previous = Health;
            Health = health;
            OnHealthChanged?.Invoke(previous, health);
        }

        /// <param name="nowSeconds">
        /// The death timestamp, or <see cref="float.NaN"/> when the caller has no clock —
        /// which is the snapshot path. A NaN leaves the existing stamp alone, so an S_DEATH
        /// that arrived first keeps its more accurate one and a snapshot-only death falls back
        /// to a respawn clock that is ready immediately rather than never.
        /// </param>
        private void SetAlive(bool alive, float nowSeconds)
        {
            // The stamp is taken outside the edge check on purpose: death arrives twice, once as
            // S_DEATH and once as the snapshot's IsAlive bit, and either can be first — S_DEATH
            // is reliable and the snapshot is not, but both are produced on the same tick. The
            // event is idempotent and the timestamp is not, so the first arrival stamps and the
            // second leaves it alone rather than pushing the respawn out by the gap between them.
            if (!alive && !_deathStamped)
            {
                _diedAtSeconds = nowSeconds;
                _deathStamped = true;
            }

            if (alive == IsAlive) return;

            IsAlive = alive;

            if (alive)
            {
                _diedAtSeconds = float.NegativeInfinity;
                _deathStamped = false;
                _runtime = WeaponRuntimeState.Loaded(_weapon);
                _reloadPending = true;
                _reloadStartedAt = float.NaN;
                OnRespawned?.Invoke();
                return;
            }

            OnDied?.Invoke();
        }
    }
}
