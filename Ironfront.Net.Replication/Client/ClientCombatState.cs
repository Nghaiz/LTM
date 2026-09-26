using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;

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
    /// <para>
    /// <b>And so is the sprint rule, since protocol 10.</b> <see cref="ApplySprint"/> advances
    /// <see cref="EffectiveTriggerPolicy"/>'s own block and <see cref="PredictFire"/> reads it,
    /// so the trigger this side predicts against and the trigger the server enforces are one
    /// implementation. What the second copy cost when it did not exist at all is written at the
    /// gate itself.
    /// </para>
    /// <para>
    /// <b>And so is the semi-auto edge.</b> <see cref="ApplyTrigger"/> calls
    /// <see cref="EffectiveTriggerPolicy.AdvanceHeldTrigger"/>, which is the same member the
    /// server's <see cref="EffectiveTriggerPolicy.Advance"/> is written in terms of — so
    /// "one press, one round" is one rule rather than two that agree today. Before it existed
    /// this side predicted a round on every frame the trigger was down, whatever the weapon
    /// was.
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
        private WeaponRuntimeState _serverRuntime = WeaponRuntimeState.Loaded(WeaponCatalog.Inert);
        private readonly PredictedWeaponCommandBuffer _weaponCommands =
            new PredictedWeaponCommandBuffer();

        /// <summary>
        /// The sprint block, advanced by <see cref="ApplySprint"/> and read by
        /// <see cref="PredictFire"/>. The server's own struct, not a client-side echo of it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two of the three fields are used here. <see cref="EffectiveTrigger.SprintFireBlockedUntil"/>
        /// is stamped by <see cref="ApplySprint"/>, and <see cref="EffectiveTrigger.WasEffective"/>
        /// by <see cref="ApplyTrigger"/> — the semi-auto edge, which this side predicts against
        /// since it was measured predicting a magazine the server never spent.
        /// <see cref="EffectiveTrigger.LoweredBySprint"/> is the one that stays the server's:
        /// it belongs to the holster mutation in
        /// <see cref="EffectiveTriggerPolicy.Advance"/>, which this side does not run, and the
        /// policy's own remark says why.
        /// </para>
        /// <para>
        /// Holding the whole struct rather than a bare float and a bare bool is what lets both
        /// sides call one implementation: a <c>float _sprintBlockedUntil</c> plus a
        /// <c>bool _wasFiring</c> here would each need their own arithmetic, which is the copy
        /// this exists to avoid.
        /// </para>
        /// </remarks>
        private EffectiveTrigger _trigger = EffectiveTrigger.Idle;

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

        /// <summary>
        /// The clip the SERVER last reported, verbatim. Meaningless until
        /// <see cref="HasServerAmmo"/> is true.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A second property beside <see cref="AmmoInClip"/> rather than a replacement for
        /// it, exactly as <see cref="ServerSaysReloading"/> is beside <see cref="IsReloading"/>.</b>
        /// A HUD wants the predicted clip; anything MEASURING the server wants this one, and
        /// the two are not interchangeable: <see cref="ReconcileAmmo"/> deliberately KEEPS the
        /// prediction while it is within <see cref="AmmoResyncThreshold"/> of the snapshot, so
        /// <see cref="AmmoInClip"/> can sit up to two rounds off the server's and stay there —
        /// the bias is sticky by design and never converges on its own.
        /// </para>
        /// <para>
        /// <b>The measurement that made this necessary.</b> On 2026-09-14 the lane-B grader
        /// counted rounds off <see cref="AmmoInClip"/> and produced both errors from it in one
        /// afternoon: a FAIL on a semi-auto press where the server had correctly fired once,
        /// and — worse — a PASS on a sprint window where the server fired NOTHING (97 attempts,
        /// 97 refused <c>Holstered</c>) because the predicted clip had dipped by one. A red
        /// gets investigated; a green ends the question. Nothing local ever writes this field,
        /// so no amount of prediction slack can move it.
        /// </para>
        /// </remarks>
        public byte ServerAmmoInClip { get; private set; }

        /// <summary>
        /// Whether <see cref="ServerAmmoInClip"/> holds a snapshot reading yet. Check this
        /// first.
        /// </summary>
        /// <remarks>
        /// Separate from the count for the reason <see cref="SpareAmmo"/>'s kind is separate
        /// from its rounds: before the first snapshot the honest answer is "not measured", and
        /// a plain <c>0</c> reads identically to "the clip is empty". A reader that believes
        /// the byte without this grades an unopened match as a dry magazine.
        /// </remarks>
        public bool HasServerAmmo { get; private set; }

        /// <summary>
        /// The authoritative reserve from the most recent snapshot, for the "/ 90" half of the
        /// HUD. <see cref="SpareAmmoKind.NoResupply"/> until one arrives.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Not <c>Finite(0)</c> before the first snapshot, and the difference is the whole
        /// reason the sentinel exists.</b> A HUD renders an empty-but-refillable reserve as
        /// <c>/ 0</c> and a weapon that has no reserve at all as <c>/ —</c>; opening at
        /// <c>Finite(0)</c> would tell every player their rifle was out of spare rounds for the
        /// first 50 ms of the match.
        /// </para>
        /// <para>
        /// <b>Nothing here predicts it.</b> See <see cref="ApplySnapshot"/> for why that makes
        /// it unconditional where the clip is not.
        /// </para>
        /// </remarks>
        public SpareAmmo SpareAmmo { get; private set; } = SpareAmmo.NoResupply;

        /// <summary>From the snapshot, or from <see cref="EquipWeapon"/> before one arrives.</summary>
        public byte WeaponId { get; private set; }

        /// <summary>True between <see cref="BeginReload"/> and the snapshot that answers it.</summary>
        public bool IsReloading => _runtime.Reloading;

        /// <summary>
        /// What the SERVER says about reloading, from <see cref="WeaponStateFlags.Reloading"/>.
        /// </summary>
        /// <remarks>
        /// Deliberately a second property beside <see cref="IsReloading"/> rather than a
        /// replacement for it. A HUD wants the predicted one, because waiting a round-trip to
        /// start the animation is the visible delay prediction exists to remove; a grader wants
        /// the authoritative one. Collapsing them into a single flag would throw away the
        /// disagreement between the two, which is the only thing in this file that can show a
        /// reload the server refused.
        /// </remarks>
        public bool ServerSaysReloading { get; private set; }

        /// <summary>Predicted or authoritative delayed release currently in flight.</summary>
        public bool IsReleasePending => _runtime.PendingRelease;

        /// <summary>The pending-release bit from the newest authoritative weapon snapshot.</summary>
        public bool ServerSaysReleasePending { get; private set; }

        /// <summary>Unacknowledged local throwable commands awaiting replay.</summary>
        public int PredictedCommandCount => _weaponCommands.Count;

        /// <summary>Throwable uses not already reserved by the throw currently in hand.</summary>
        public int TotalThrowableUsesAvailable
        {
            get
            {
                int reserve = SpareAmmo.Kind == SpareAmmoKind.Finite ? SpareAmmo.Rounds : 0;
                int total = _runtime.AmmoInClip + reserve;
                return _runtime.PendingRelease && total > 0 ? total - 1 : total;
            }
        }

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
            _serverRuntime = _runtime;
            _weaponCommands.Clear();
            ServerSaysReleasePending = false;
            _reloadStartedAt = float.NaN;

            // The new weapon's first shot is a trigger pull, not a continuation of the one the
            // player was already holding — the same rule ClientSession.SwitchWeaponTo applies on
            // the server, and for the same reason. Without it a player who switches with Fire
            // held is holding a semi-automatic that has already spent its edge, and the only way
            // out is to release and press again.
            //
            // The SPRINT block is deliberately not cleared here: sprinting is a fact about the
            // body, not about the gun in its hands, and clearing it would hand a free shot to
            // anyone who swapped weapons mid-sprint. EffectiveTrigger's own remark says so.
            _trigger.ReArm();

            // A weapon swap resyncs on the next snapshot rather than trusting the fresh clip:
            // the server may have handed out a partially-loaded weapon, and the predicted
            // count here is a guess until it says otherwise.
            _reloadPending = true;
        }

        /// <summary>
        /// Advances the sprint gate by one frame. Call EVERY frame, sprinting or not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Every frame, and not only the frames the trigger is down.</b> The block runs from
        /// the LAST sprinting frame, so a player who sprints without firing and pulls the
        /// trigger the instant they release Shift must still be refused for
        /// <see cref="ProtocolConstants.SPRINT_FIRE_BLOCK_SECONDS"/>. Called under a
        /// fire-pressed guard there would be nothing stamped at that moment and this side would
        /// predict a shot the server refuses — the original disagreement, one release later.
        /// </para>
        /// <para>
        /// <b>A separate call rather than a parameter on <see cref="PredictFire"/> or
        /// <see cref="Tick"/>.</b> Both of those are called from places that know nothing about
        /// sprinting, and a defaulted <c>sprinting: false</c> parameter would let a caller that
        /// forgot it read as a caller that meant it — which is exactly the shape of the defect
        /// being closed. The sprint bit has one reader, at the seam where the input is read.
        /// </para>
        /// </remarks>
        public void ApplySprint(bool sprinting, float nowSeconds)
            => EffectiveTriggerPolicy.AdvanceSprintBlock(ref _trigger, sprinting, nowSeconds);

        /// <summary>
        /// Advances the semi-auto edge by one frame. Call EVERY frame, trigger down or not.
        /// </summary>
        /// <param name="fireHeld">
        /// Whether the trigger is down AND the actor may shoot at all. A dead player passes
        /// false: that is not a release, but it is not an effective trigger either, and
        /// re-arming across a death is what <see cref="SetAlive"/> does anyway.
        /// </param>
        /// <returns>
        /// Whether <see cref="PredictFire"/> should be called this frame: every held frame for
        /// an automatic, the rising edge only for a semi-automatic.
        /// </returns>
        /// <remarks>
        /// <para>
        /// <b>The defect this closes was measured, not feared.</b> Until this existed
        /// <see cref="PredictFire"/> was gated on the cooldown alone, which is the right rule
        /// for an automatic and the wrong one for everything else. On 2026-09-14 a lane-B run
        /// held a SIGNAL DMR's trigger for two five-second presses: the server fired the two
        /// rounds it owed — 298 <c>[shot]</c> lines, exactly 2 with <c>fired=True</c> — and
        /// this side predicted 29 more and took 9 ammo corrections being handed them back. A
        /// player watches that as a clip dropping and snapping back on a rifle that fired once.
        /// </para>
        /// <para>
        /// <b>Every frame, including the frames the trigger is up, and a caller that skips
        /// those breaks the fix silently.</b> The RELEASE is what re-arms the edge. Folded
        /// under a <c>FirePressed()</c> guard this would leave
        /// <see cref="EffectiveTrigger.WasEffective"/> true for the rest of the life, and the
        /// semi-automatic would fire its first round and then nothing ever again — which is a
        /// worse bug than the one being fixed, and one a grader reading a flat clip could
        /// easily read as the edge working. The call site is the <c>if</c> condition itself
        /// for exactly that reason: there is no path that predicts without advancing.
        /// </para>
        /// <para>
        /// <b>A separate call rather than a parameter on <see cref="PredictFire"/>,</b> for the
        /// reason <see cref="ApplySprint"/> gives one paragraph up: a defaulted parameter lets
        /// a caller that forgot it read as a caller that meant it. It also keeps
        /// <see cref="PredictFire"/> callable on its own by every test that predicts a single
        /// shot without modelling a trigger at all.
        /// </para>
        /// <para>
        /// <b>The sprint block is part of the effective trigger here, not just of
        /// <see cref="PredictFire"/>'s answer — and it takes a clock for that reason alone.</b>
        /// The server composes its effective trigger the same way, so coming out of a sprint
        /// with Fire still held is a rising EDGE on both sides:
        /// <c>SemiAutoTriggerEdgeTests.EnteringSprintReArmsTheSemiAutoEdge</c> is that rule.
        /// Passing the raw Fire bit instead would hold <see cref="EffectiveTrigger.WasEffective"/>
        /// true straight through the sprint, so the server would fire the round it owes at the
        /// end of the window and this side would predict nothing — and a one-round gap is
        /// inside <see cref="AmmoResyncThreshold"/>, so <see cref="ReconcileAmmo"/> would KEEP
        /// the wrong prediction rather than correct it. That is a permanent silent bias, which
        /// is strictly worse than the flicker the threshold exists to stop.
        /// </para>
        /// <para>
        /// Call <see cref="ApplySprint"/> FIRST each frame: the block this reads is the one it
        /// stamps, and reading it beforehand tests a window that is one frame stale.
        /// </para>
        /// </remarks>
        public bool ApplyTrigger(bool fireHeld, float nowSeconds)
            => EffectiveTriggerPolicy.AdvanceHeldTrigger(
                ref _trigger,
                fireHeld && EffectiveTriggerPolicy.SprintAllowsFire(in _trigger, nowSeconds),
                _weapon.Automatic);

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

            // The sprint rule, read from the server's own predicate rather than restated. Before
            // this line the client predicted a shot on every frame the trigger was down, sprint
            // or no sprint, and the server refused every one of them: 51 predicted shots across
            // one six-second lane-B window with the clip still sitting at 30 and
            // SnapshotAmmoCorrections climbing 1 -> 19.
            //
            // Nothing here is protecting the server, and the shot log of that window says so
            // precisely: 181 of 303 attempts refused Holstered by the server's sprint rule, and
            // all 30 that WERE accepted carried the Sprint bit clear. Every round the server
            // spent was legal. What this refuses is a prediction whose only possible outcome is
            // a correction -- the magazine a player watches drain and snap back is this side's
            // number, handed back by the next snapshot.
            //
            // Holstered, and it is the server's word rather than a near-miss. On a sprinting
            // frame the sprint rule lowers the weapon and ServerCombatAuthority reports exactly
            // this. Over the release window the server reports None instead — the weapon is back
            // up and its answer means "no trigger pull happened", which it can afford because it
            // carries BlockedBySprint in a separate field. This method has one return value and
            // None here means "play the muzzle flash", so None would render a shot that never
            // leaves the barrel: the defect with an extra step.
            if (!EffectiveTriggerPolicy.SprintAllowsFire(in _trigger, nowSeconds))
                return FireRejection.Holstered;

            FireRejection rejection =
                ServerFireResolver.CheckCanFire(in _runtime, in _weapon, IsAlive, nowSeconds);

            if (rejection != FireRejection.None) return rejection;

            _runtime.LastFiredTime = nowSeconds;
            _runtime.AmmoInClip--;
            PredictedShots++;
            return FireRejection.None;
        }

        /// <summary>
        /// Predicts a fire command with the sequence data required to replay delayed throwables
        /// after snapshot reconciliation. Ordinary weapons retain the established ammo path.
        /// </summary>
        public FireRejection PredictFire(
            float nowSeconds, uint inputTick, uint localTick, in Vec3 aim)
        {
            if (!_weapon.HasDelayedRelease) return PredictFire(nowSeconds);
            if (!EffectiveTriggerPolicy.SprintAllowsFire(in _trigger, nowSeconds))
                return FireRejection.Holstered;
            if (!IsAlive) return FireRejection.ShooterDead;

            ThrowableRejection rejection = ThrowableLifecycle.TryBegin(
                ref _runtime, in _weapon, inputTick, localTick, in aim);
            FireRejection mapped = MapThrowableRejection(rejection);
            if (mapped != FireRejection.None) return mapped;

            _weaponCommands.Add(inputTick, localTick, in aim);
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

            // The server's own reload is the one that actually fills the clip -- see
            // ApplySnapshot's reload-delivered branch. Ravenfield's local timer
            // (DefaultReloadSeconds, 1.8 s on ak.prefab) finishes before the server's
            // (ProtocolConstants.RELOAD_SECONDS, 2.0 s) plus RTT, so completing here while the
            // server still says Reloading would render a full magazine the server has not
            // granted yet -- the 30 -> 1 -> 30 blink in the S4 diagnosis. Waiting for the flag
            // to fall is what CompleteReloadIfElapsed cannot see on its own; ApplySnapshot ends
            // the local reload the moment it does.
            if (ServerSaysReloading) return;

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
            => ApplySnapshot(in entry, nowSeconds, 0, 0);

        /// <summary>Applies snapshot truth and replays throwable commands newer than its ack.</summary>
        public void ApplySnapshot(
            in ActorSnapshotEntry entry, float nowSeconds,
            uint lastProcessedInputTick, uint serverTick)
        {
            if (entry.Has(SnapshotField.Health)) SetHealth(entry.Health);

            if (entry.Has(SnapshotField.StateFlags))
                SetAlive((entry.StateFlags & ActorStateFlags.IsAlive) != 0, nowSeconds);

            _weaponCommands.RemoveAcknowledged(lastProcessedInputTick);

            if (!entry.Has(SnapshotField.Weapon))
            {
                if (_weapon.HasDelayedRelease) RebuildThrowablePrediction();
                return;
            }

            if (entry.WeaponId != WeaponId)
            {
                WeaponId = entry.WeaponId;

                // The clip size the ammo below is reconciled against belongs to the NEW weapon.
                // Re-resolving here rather than only in EquipWeapon is what keeps a server-side
                // weapon swap — a respawn with a different loadout, a pickup — from leaving this
                // side predicting with the previous gun's numbers.
                _weapon = WeaponCatalog.For(WeaponId);
                _runtime = WeaponRuntimeState.Loaded(in _weapon);
                _serverRuntime = _runtime;
                _weaponCommands.Clear();

                // And the edge is re-armed for the same reason EquipWeapon re-arms it — this is
                // the OTHER way a weapon changes, a server-side swap this client never asked
                // for (a respawn with a different loadout, a pickup). Re-arming in only one of
                // the two places would fix the swap the player drove and leave the one the
                // server drove with a dead trigger.
                _trigger.ReArm();
            }

            // Taken verbatim, unconditionally, with no equivalent of AmmoResyncThreshold — and
            // that asymmetry with the clip two lines below is deliberate. The clip needs a
            // threshold because PredictFire moves it BETWEEN snapshots, so the snapshot's higher
            // count is normally just stale by the one or two shots in flight, and handing it back
            // every frame is the 30, 29, 30, 29 flicker ReconcileAmmo documents. Nothing predicts
            // a reserve: the only thing that spends one is a reload the SERVER accepted, and the
            // snapshot that carries that reload carries the new reserve with it. There is no
            // in-flight local change here for a threshold to protect, so a threshold could only
            // ever delay the correct number.
            SpareAmmo = SpareAmmo.Decode(entry.SpareAmmoEncoded);

            // Taken verbatim and BEFORE ReconcileAmmo runs, which is the whole point of it: this
            // is the one number on this object that no local prediction has ever touched. Reading
            // it after the reconcile, or reading the reconciled field instead, would fold the
            // threshold's sticky bias back in and leave nothing on the client able to say what
            // the server's clip actually is.
            ServerAmmoInClip = entry.AmmoInClip;
            HasServerAmmo = true;

            bool serverWasReloading = ServerSaysReloading;
            ServerSaysReloading = (entry.WeaponStateFlags & WeaponStateFlags.Reloading) != 0;
            ServerSaysReleasePending =
                (entry.WeaponStateFlags & WeaponStateFlags.PendingRelease) != 0;

            if (_weapon.HasDelayedRelease)
            {
                _serverRuntime = WeaponRuntimeState.Loaded(in _weapon);
                _serverRuntime.AmmoInClip = entry.AmmoInClip;
                _serverRuntime.Reloading = ServerSaysReloading;
                _serverRuntime.PendingRelease = ServerSaysReleasePending;
                _serverRuntime.PendingReleaseTick = ServerSaysReleasePending ? serverTick : 0;
                RebuildThrowablePrediction();
                _reloadPending = false;
                _reloadStartedAt = float.NaN;
                return;
            }

            // A reload the server is still running suspends the anti-flicker rule for the same
            // reason a locally predicted one does: mid-reload, a large predicted/authoritative
            // gap is correct rather than suspicious. This is also what carries a reload the
            // client never asked for — a server-side auto-reload on an empty clip — as one clean
            // jump instead of as a drift correction that climbs SnapshotAmmoCorrections.
            if (ServerSaysReloading) _reloadPending = true;

            // The flag's SET -> CLEAR transition, and nothing else, is "delivered": the server
            // just finished (or refused) the reload this tick, and entry.AmmoInClip is its
            // answer. Routing that through ReconcileAmmo's pending branch is the S4 bug (CMB-19)
            // — a one- or two-round rise sits inside AmmoResyncThreshold, so the reconcile kept
            // the STALE predicted count instead of the delivered clip, and the flagged snapshots
            // along the way (still reloading, ammo frozen at the pre-reload count) got taken
            // verbatim instead, which is the 0 -> 1 jump this fixes. A flag that is still set,
            // or was never set, falls through to the ordinary pending reconcile below.
            bool reloadDelivered = serverWasReloading && !ServerSaysReloading;

            byte reconciled = reloadDelivered
                ? entry.AmmoInClip
                : ReconcileAmmo(_runtime.AmmoInClip, entry.AmmoInClip, _reloadPending);

            if (reconciled != _runtime.AmmoInClip) SnapshotAmmoCorrections++;

            _runtime.AmmoInClip = reconciled;

            // The server's reload is over — finished, or refused and never started — so this
            // snapshot is the answer to it, whichever way it went. The disagreement is resolved
            // the server's way, always: a client that keeps animating a reload the server
            // cancelled is showing a reload that will never deliver a round, and it would keep
            // showing it until its own ReloadSeconds clock ran out.
            //
            // ENDED, not completed: the clip is deliberately not filled here. The snapshot's own
            // ammo count — taken verbatim just above, because _reloadPending was set — already
            // says whether the reload delivered. Filling the clip to ClipSize would overwrite
            // that authoritative answer with a guess in precisely the case where the guess is
            // wrong: a reload the server refused for an empty reserve.
            if (_reloadPending && !ServerSaysReloading)
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
            _serverRuntime = _runtime;
            _weaponCommands.Clear();
            _trigger = EffectiveTrigger.Idle;
            _reloadPending = false;
            _reloadStartedAt = float.NaN;
            _diedAtSeconds = float.NegativeInfinity;
            _deathStamped = false;
            Health = 100;
            IsAlive = true;
            WeaponId = 0;

            // Back to the sentinel, not to Finite(0) — see the property's own remark. A reconnect
            // that reset to zero would render "out of spare rounds" until the first snapshot,
            // which is the one moment a player is most likely to be looking at the HUD.
            SpareAmmo = SpareAmmo.NoResupply;
            ServerSaysReloading = false;
            ServerSaysReleasePending = false;

            // Back to "not measured", not to zero. A reconnect that left the last match's count
            // standing would let a grader read a stale clip as this match's, and zeroing it
            // without clearing the flag would read as an empty magazine — the two states the
            // flag exists to keep apart.
            ServerAmmoInClip = 0;
            HasServerAmmo = false;

            PredictedShots = 0;
            SnapshotAmmoCorrections = 0;
        }

        private void RebuildThrowablePrediction()
        {
            _runtime = _serverRuntime;
            for (int i = 0; i < _weaponCommands.Count; i++)
            {
                PredictedWeaponCommand command = _weaponCommands[i];
                Vec3 aim = command.Aim;
                ThrowableLifecycle.TryBegin(
                    ref _runtime, in _weapon,
                    command.InputTick, command.LocalTick, in aim);
            }
        }

        private static FireRejection MapThrowableRejection(ThrowableRejection rejection)
        {
            switch (rejection)
            {
                case ThrowableRejection.None: return FireRejection.None;
                case ThrowableRejection.Holstered: return FireRejection.Holstered;
                case ThrowableRejection.Reloading: return FireRejection.Reloading;
                case ThrowableRejection.NoAmmo: return FireRejection.NoAmmo;
                default: return FireRejection.OnCooldown;
            }
        }

        /// <summary>
        /// phase-02 trap 4, in one place: the client's predicted ammo wins unless a reload is
        /// in flight or the two have drifted further than <see cref="AmmoResyncThreshold"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Without this, a client that has predicted one shot ahead of the server reads 29
        /// while the snapshot still says 30, takes the snapshot, predicts 29 again on the next
        /// frame, and the HUD reads 30, 29, 30, 29 for as long as the player keeps firing. The
        /// threshold is what distinguishes "one or two shots in flight", which is the normal
        /// operating condition, from "these two numbers are about a different clip", which is
        /// the only case worth a visible correction.
        /// </para>
        /// <para>
        /// <b>While reload-pending, "verbatim" used to mean ANY snapshot, including a stale
        /// one.</b> S4 (CMB-19): a snapshot produced mid-reload, or before the server has even
        /// seen the reload input, still carries the frozen pre-reload count — and a small rise
        /// over the prediction (1 or 2 rounds) is exactly what the pending flag is set to
        /// suspend the anti-flicker rule for, not evidence that the reload delivered. So a
        /// small rise keeps the prediction, same as the non-pending case; only a rise too large
        /// to be that kind of staleness (or a snapshot at or below the prediction) is trusted.
        /// The reload's genuine delivery is <see cref="ApplySnapshot"/>'s own flag-fall bypass,
        /// which does not call this method at all — see its remarks.
        /// </para>
        /// </remarks>
        public static byte ReconcileAmmo(byte predicted, byte fromSnapshot, bool reloadPending)
        {
            if (reloadPending)
            {
                int rise = fromSnapshot - predicted;
                return rise > 0 && rise <= AmmoResyncThreshold ? predicted : fromSnapshot;
            }

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

                // A new life starts under no sprint block, exactly as ClientSession.ResetWeapon
                // clears the server's. Carrying one across a death would refuse the first shot
                // of a life for up to SPRINT_FIRE_BLOCK_SECONDS, from a sprint the previous body
                // was doing — and it would refuse it on THIS side only, which is the shape of
                // disagreement this gate exists to remove.
                //
                // Not cleared by EquipWeapon, deliberately and for the reason EffectiveTrigger's
                // own remark gives: sprinting is a fact about the body, not about the gun in its
                // hands, so a weapon swap mid-sprint must not hand the player a free shot.
                _trigger = EffectiveTrigger.Idle;

                _reloadPending = true;
                _reloadStartedAt = float.NaN;
                OnRespawned?.Invoke();
                return;
            }

            // Death is an authoritative cancellation boundary. Do not keep an unacknowledged
            // local trigger around to replay over a later delta: the server drops the matching
            // pending release in ClientSession.ClearCombatStateOnDeath, and replaying it here
            // would leave the corpse holding a ghost throwable until another weapon field came.
            _weaponCommands.Clear();
            ThrowableLifecycle.Cancel(ref _runtime);
            ThrowableLifecycle.Cancel(ref _serverRuntime);
            ServerSaysReleasePending = false;

            OnDied?.Invoke();
        }
    }
}
