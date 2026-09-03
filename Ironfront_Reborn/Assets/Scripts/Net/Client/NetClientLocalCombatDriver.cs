using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// The one production owner of <c>ClientCombatState</c>: the local player's health, death,
    /// respawn clock and the request that ends it. debt-closure phase 2 task 2b, ledger C-2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing constructed this type before.</b> <c>ClientCombatState</c> shipped in phase-02
    /// with health, ammo prediction, an <c>OnDied</c> event and a respawn gate sharing the
    /// server's own constant — and every <c>new ClientCombatState</c> in the repository was in a
    /// test. The visible consequence was specific: a dead local player was felled by
    /// <c>NetClientCombatPresenter.KnockOverLocalActor</c> and then had no driver at all. Input
    /// stayed live on a corpse, no screen said the player was dead, and no respawn was ever
    /// requested — the client sent exactly two message types and <c>C_SPAWN_REQUEST</c> was not
    /// one of them, so <c>ServerMessageRouter.SpawnRequestsReceived</c> could only ever read
    /// zero.
    /// </para>
    /// <para>
    /// <b>State, not cosmetics — which is why it is not folded into the combat presenter.</b>
    /// That presenter is cosmetics-only by construction and a CI gate (G2) enforces it: it may
    /// not name <c>SpawnProjectile</c> or <c>ApplyRecoil</c>, and its whole remit is other
    /// players' bodies. This drives THIS player's controller and sends a message. They are
    /// different jobs on the same object.
    /// </para>
    /// <para>
    /// <b>The death screen was IMGUI and is now the HUD's deploy screen</b> (P17 3.2). The state
    /// behind it never was a stopgap — the countdown, the gate and the request are the shipped
    /// library model — so replacing the drawing touched none of it. What DID change is who
    /// decides the screen is up: it is driven from <c>ClientCombatState.IsAlive</c> once a frame
    /// rather than from <see cref="OnDied"/>, so a respawn this client did not request closes it
    /// too (P17 criterion 5), and a death whose S_DEATH lands after the snapshot's IsAlive bit
    /// still names its killer when the message arrives.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class NetClientLocalCombatDriver : MonoBehaviour
    {
        /// <summary>Held to respawn once the clock allows it.</summary>
        [Tooltip("Pressed to respawn once the death countdown has elapsed.")]
        [SerializeField] private KeyCode _respawnKey = KeyCode.Space;

        [Tooltip("Raise the deploy screen on death. Off leaves the respawn key working.")]
        [SerializeField] private bool _drawDeathScreen = true;

        private NetClientBootstrap _client;
        private readonly ClientCombatState _state = new ClientCombatState();

        /// <summary>
        /// The killfeed's name table, for the one string the deploy screen needs.
        /// </summary>
        /// <remarks>
        /// <b>Borrowed rather than duplicated.</b> S_PLAYER_LIST has one consumer by design —
        /// <c>NetClientCombatPresenter</c> owns the table, and the wiring gate's exemption for
        /// that event retires on THAT subscription — so a second table here would be a second
        /// thing to keep in step with the wire. Both components sit on the object carrying
        /// <c>NetClientBootstrap</c>: this one is added there by
        /// <c>NetClientBootstrap.EnsureLocalCombatDriver</c>, and the asset gate's A1 check
        /// requires the presenter there. Absent is survivable — the screen then names the
        /// killer by actor id, which is what the killfeed rendered before names existed.
        /// </remarks>
        private NetClientCombatPresenter _names;

        /// <summary>Who killed this client last, for the deploy screen. P17 3.2.</summary>
        private ushort _lastKillerActorId;
        private bool _lastKillerWasEnvironment;
        private int _lastKillerTeam = TeamId.None;

        /// <summary>Whether the deploy screen is raised, so show and hide each fire once.</summary>
        private bool _deployShown;

        /// <summary>
        /// True until this connection's own first successful <see cref="RequestRespawn"/>.
        /// </summary>
        /// <remarks>
        /// <c>ClientCombatState.CanRequestRespawn</c> requires <c>_deathStamped</c>, which the
        /// very first snapshot's alive=false transition DOES set (<c>SetAlive</c>'s own remark),
        /// so a fresh join would otherwise sit behind the same
        /// <c>RESPAWN_SECONDS</c> cooldown a real death earns -- server-side nothing gates this
        /// connection's first request at all (<c>ServerPlayer.AwaitingFirstDeploy</c>), so the
        /// client must not invent a wait the server never asked for. Mirrors that field's name
        /// and its one-shot shape deliberately; the two are independent flags on independent
        /// processes, checked at different moments, and are not meant to be unified.
        /// </remarks>
        private bool _awaitingFirstDeploy = true;

        /// <summary>Whether a deploy request may be sent right now. See <see cref="_awaitingFirstDeploy"/>.</summary>
        private bool CanDeployNow(float nowSeconds)
            => _awaitingFirstDeploy || _state.CanRequestRespawn(nowSeconds);

        /// <summary>
        /// Set when a death names a killer the screen has not shown yet.
        /// </summary>
        /// <remarks>
        /// The snapshot's IsAlive bit and S_DEATH are produced on the same tick and either can
        /// arrive first (<c>ClientCombatState.ApplyDeath</c>'s own remark). Snapshot-first raises
        /// the screen with no killer named; this is what makes the message that follows rewrite
        /// the line rather than leaving it blank for the whole countdown.
        /// </remarks>
        private bool _killerLabelStale;

        /// <summary>
        /// The local player's combat state. The one production instance in the build.
        /// </summary>
        public ClientCombatState State => _state;

        /// <summary>Whether input was taken away by a death this driver saw.</summary>
        /// <remarks>
        /// Tracked rather than inferred from <c>IsAlive</c>, so this never re-enables input that
        /// something else disabled — a seat, a cutscene, the loadout screen. It gives back only
        /// what it took.
        /// </remarks>
        private bool _inputSuppressedByDeath;

        /// <summary>
        /// <see cref="_inputSuppressedByDeath"/>, read-only. Ledger <b>X-29</b>, check 13.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Surfaced because nothing else in the artifact answers the question.</b> Check 13
        /// reads "death → input disable → respawn screen" and was graded on two of its three
        /// terms. <c>combat.driverEnabled</c> records whether THIS COMPONENT is running, and it
        /// must keep running to accept a respawn request, so its staying <c>true</c> after a
        /// death is correct rather than an answer. <c>FpsActorController.IsInputEnabled</c> —
        /// the accessor X-29 reached for first — is pinned <c>false</c> for the whole life of a
        /// lane-B client, because <c>Start</c> disables it and the only caller that re-enables
        /// is <c>SpawnAt</c>, the gameplay spawn a networked body deliberately never runs. It is
        /// therefore constant, and a constant cannot distinguish alive from dead.
        /// </para>
        /// <para>
        /// <b>A getter, and nothing more.</b> No setter, no side effect, no harness type named
        /// from this file. The suppression is still owned entirely by <see cref="OnDied"/>,
        /// <see cref="OnRespawned"/> and <see cref="RestoreInput"/>; this only lets a recorder
        /// read what they decided, which is the whole difference between a check that grades
        /// the game and one that grades a proxy for it.
        /// </para>
        /// </remarks>
        public bool IsInputSuppressedByDeath => _inputSuppressedByDeath;

        /// <summary>Reused for C_SPAWN_REQUEST. Sized like every other client send buffer.</summary>
        private readonly byte[] _payload = new byte[ProtocolConstants.MAX_PAYLOAD];

        private void Awake()
        {
            if (!NetClientPresenterGuard.IsPresentable)
            {
                enabled = false;
                return;
            }

            if (!NetClientPresenterGuard.TryResolveClient(
                    nameof(NetClientLocalCombatDriver), out _client))
            {
                enabled = false;
                return;
            }

            _names = GetComponent<NetClientCombatPresenter>();
        }

        private void OnEnable()
        {
            if (_client == null) return;

            _state.OnDied += OnDied;
            _state.OnRespawned += OnRespawned;

            _client.Router.OnSpawnActor += OnSpawnActor;
            _client.Router.OnDeath += OnDeathMessage;
            _client.Router.OnSnapshotApplied += OnSnapshotApplied;
        }

        private void OnDisable()
        {
            if (_client == null) return;

            _state.OnDied -= OnDied;
            _state.OnRespawned -= OnRespawned;

            _client.Router.OnSpawnActor -= OnSpawnActor;
            _client.Router.OnDeath -= OnDeathMessage;
            _client.Router.OnSnapshotApplied -= OnSnapshotApplied;

            // Give input back on the way out, or a disconnect while dead leaves the player
            // holding a controller that never comes alive again.
            RestoreInput();
            _state.Reset();

            // A disconnect while dead otherwise leaves the deploy screen up with nothing left
            // running to take it down -- the mirror of the input the line above gives back.
            if (_deployShown)
            {
                _deployShown = false;
                NetClientBindings.MatchHud?.HideDeploy();
            }
        }

        /// <summary>
        /// Ledger row <b>X-11</b>: the local player learns which weapon it is holding.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>SpawnActorMessage</c> has carried <c>WeaponId</c> since the freeze and
        /// <c>ClientCombatState.EquipWeapon</c> has existed to consume it. Nothing connected
        /// them: a repository-wide grep for <c>EquipWeapon</c> on 2026-08-21 returned twenty
        /// call sites and every one was in <c>ClientCombatTests</c>, all green -- the textbook
        /// shape of a green that proves nothing, because each of them calls it first.
        /// </para>
        /// <para>
        /// <b>The loop that kept it invisible.</b> The other way a client could learn its weapon
        /// is a snapshot delta, and <c>DeltaEncoder</c> masks <c>SnapshotField.Weapon</c> only
        /// when the weapon or the ammo count changes -- and the client's own firing is what
        /// would change the ammo. No weapon, cannot fire, ammo never moves, field never sent, no
        /// weapon.
        /// </para>
        /// <para>
        /// <b>Remote spawns are ignored here.</b> A remote body's weapon is a rendering concern
        /// and belongs to <c>RemoteActorRegistry</c>; this state is the LOCAL player's ammo model
        /// and adopting somebody else's weapon into it would show their clip on this HUD.
        /// </para>
        /// </remarks>
        private void OnSpawnActor(SpawnActorMessage message)
        {
            if (!message.IsLocalPlayer) return;

            // Kept in step HERE as well as in Update, because this message is the moment the id
            // becomes known and the guard below reads it. Update copies it once per frame, so at
            // a router callback it can still be the unassigned zero -- and every IsLocalActor
            // guard fed from it would then reject the one message that establishes identity. The
            // Update comment already calls this "keeping it in step"; this is the same line at
            // the only other moment it can change.
            _state.LocalActorId = _client.LocalActorId;

            _state.EquipWeapon(message.WeaponId);

            // Ledger X-11/X-48, and this is the correction to X-48's own comment below: a JOIN
            // no longer places the body (ServerTickLoop.OnClientConnected), so S_SPAWN_ACTOR now
            // reaches every client on interest ALONE, before any deploy has happened -- it is
            // "you now know this actor exists," not "you are deployed." message.Health is the
            // tell: the server parks a not-yet-deployed body at Health 0, so 0 here means stay on
            // the deploy screen and let OnRespawned (below, off the snapshot's IsAlive bit) call
            // EnterDeployedView the moment the server actually confirms a spawn. Non-zero means
            // this announce named an ALREADY-alive body -- a reconnect mid-life is the only
            // production case -- and that still deploys immediately, because no further
            // confirmation is coming for a life already in progress.
            if (message.Health > 0) EnterDeployedView();
        }
        private void Update()
        {
            if (_client == null) return;

            // The id arrives with S_SPAWN_ACTOR, which lands after this component wakes. Keeping
            // it in step here rather than once at Awake is what makes a reconnect work: zero
            // matches nothing, so a death that arrives before the id is known is correctly read
            // as somebody else's rather than as this player's.
            _state.LocalActorId = _client.LocalActorId;

            // ClientCombatState has no clock of its own, by the same decision KillfeedModel made.
            _state.Tick(Time.time);

            ApplyLocalTeam();

            SyncMatchHud();

            // X-16: PredictFire had zero production callers, so predictedShots was 0 at every
            // lane-B checkpoint while the server emptied a magazine — the field could not be
            // read as evidence about anything.
            //
            // Called every frame the trigger is down rather than on a rising edge, and that is
            // correct rather than lazy: PredictFire runs ServerFireResolver.CheckCanFire first,
            // so the cooldown, the empty clip, the reload and the death all reject the extra
            // calls. An edge detector here would additionally have to model auto-fire, which is
            // the same predicate written a second time and free to disagree with the server's.
            //
            // Safe by construction: PredictFire never raycasts, never applies damage and never
            // moves health. It stamps a local cooldown and decrements a predicted clip that the
            // next snapshot reconciles.
            if (_state.IsAlive && FirePressed()) _state.PredictFire(Time.time);

            // Two ways in, and the keyboard is still first so a human press costs no lookup.
            //
            // Defect 4 of the phase-3D report: this line was Input.GetKeyDown alone, so check 13
            // could reach the death and the death screen and could NOT reach the respawn -- no
            // scripted client, no controller and no rebind had any path to C_SPAWN_REQUEST.
            // IInputSource.RespawnPressed is that path. It is local-only and never packed: a
            // respawn is its own reliable message, not a bit in C_INPUT.
            //
            // Short-circuit order matters for the scripted source, whose RespawnPressed consumes
            // its edge when read. A real key press leaves that edge unconsumed, which is the
            // harmless direction: the scripted press then fires on the next frame instead.
            //
            // The Deploy button is the third way in and is read LAST, for ScriptedRespawnPressed's
            // reason: it consumes its edge, and a key press that has already satisfied this
            // condition leaves the button's press unconsumed for the next frame -- which is the
            // harmless direction. The button is only interactable while CanRequestRespawn is
            // true (see TickDeploy), so it cannot post an edge the gate would refuse.
            if (!_state.IsAlive && CanDeployNow(Time.time)
                && (Input.GetKeyDown(_respawnKey) || ScriptedRespawnPressed() || DeployPressed()))
            {
                RequestRespawn();
                _awaitingFirstDeploy = false;
            }
        }

        /// <summary>
        /// The local player's input source, if there is a local player at all.
        /// </summary>
        /// <remarks>
        /// Resolved per frame rather than cached: this component lives on the NetClient object
        /// and the body is spawned, killed and respawned independently of it, so a cached
        /// reference would go stale exactly at a death -- the one moment this method matters.
        /// </remarks>
        private static bool ScriptedRespawnPressed()
        {
            IInputSource input = NetClientBindings.LocalPlayer.InputSource;
            return input != null && input.RespawnPressed;
        }

        /// <summary>
        /// Puts the local body on the team the server says it is on. P12 <b>D-1</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The value already arrived; nothing handed it to the body.</b>
        /// <see cref="NetClientPresenterGuard.TryResolveLocalTeam"/> has read
        /// <c>ActorSnapshotEntry.Team</c> for the local actor since V10, and had exactly one
        /// consumer — the minimap's spawn buttons. Meanwhile the body kept the team its prefab
        /// authored, so a team-1 player rendered blue, every <c>actor.team == playerTeam</c> test
        /// in <c>Assembly-CSharp</c> answered for the wrong side, and no error was ever raised.
        /// </para>
        /// <para>
        /// <b>Polled, not event-driven, and that IS the design.</b> The body and the first
        /// snapshot arrive in either order — the rig spawns from <c>GameManager.StartGame</c>
        /// while the team comes off the wire — so an apply hung on either single event fires when
        /// the other half may not exist. A poll applies on whichever comes second without having
        /// to know which one that was.
        /// </para>
        /// <para>
        /// <b>Idempotent by comparison, not by luck.</b> <c>Actor.SetTeam</c> writes
        /// <c>material.color</c> on two skinned renderers, which instantiates a material the
        /// first time; calling it every frame would be a per-frame write for a value that
        /// changes at most once a life. The equality test is what makes a per-frame poll free.
        /// </para>
        /// <para>
        /// <b>Networked only.</b> Offline there is no snapshot and the resolver is unregistered,
        /// so this would be a no-op anyway — the guard is here so that reads as a decision rather
        /// than an accident, matching the three <c>NetContext.IsOffline</c> gates in
        /// <c>CapturePoint</c>, <c>MinimapUi</c> and <c>Projectile</c>. Offline's own answer is
        /// set in <c>FpsActorController.Awake</c>.
        /// </para>
        /// </remarks>
        /// <remarks>
        /// <b>Reads the team through <c>NetPresenterGate</c>, not through
        /// <c>NetClientPresenterGuard</c> directly</b>, even though the guard is in this same
        /// assembly. The gate reads the resolver the guard registers at <c>BeforeSceneLoad</c>,
        /// so production resolves the identical value — and a test can register its own resolver
        /// and observe this method without a bootstrap, a router or a decoded snapshot. A rule
        /// that can only be observed by standing up a whole client is a rule with no detector.
        /// </remarks>
        internal static void ApplyLocalTeam()
        {
            if (NetContext.IsOffline) return;

            ILocalPlayerRig rig = NetClientBindings.LocalPlayer;
            if (!rig.Exists) return;

            // A team of TeamId.None is "not known yet", not an answer. Writing it would swap one
            // wrong team for another and would then compare unequal forever, re-writing the
            // material every frame.
            if (!NetPresenterGate.TryResolveLocalTeam(out byte team)) return;
            if (team == TeamId.None) return;

            if (rig.Team == team) return;

            rig.SetTeam(team);
        }

        /// <summary>
        /// Whether the local player's trigger is down this frame. Ledger <b>X-16</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Reads <c>IInputSource.Buttons</c> rather than <c>Input.GetMouseButton</c>, and for the
        /// same reason the respawn path reads <c>RespawnPressed</c> (defect 4 of the phase-3D
        /// report): a keyboard-only check leaves every scripted client, controller and rebind
        /// with no path at all, so a recorded programme could fire on the server and never
        /// predict on the client. <c>Buttons</c> is the packed wire word both sources fill.
        /// </para>
        /// <para>
        /// Resolved per frame rather than cached, exactly as
        /// <see cref="ScriptedRespawnPressed"/> is: the body is spawned, killed and respawned
        /// independently of this component, so a cached reference goes stale at a death.
        /// </para>
        /// <para>
        /// <b>What this still does NOT deliver.</b> <c>PredictFire</c>'s own remark names the
        /// effects a caller plays on <c>FireRejection.None</c> — muzzle flash, recoil, a cosmetic
        /// tracer and an ammo readout. Nothing in the build reads
        /// <c>ClientCombatState.AmmoInClip</c> or <c>PredictedShots</c> except
        /// <c>LaneBCheckpointRecorder</c>, so what this call buys today is that the recorder's
        /// <c>predictedShots</c> stops being a constant zero and becomes evidence. The
        /// presentation half needs a presenter that does not exist yet (ledger X-16).
        /// </para>
        /// </remarks>
        private static bool FirePressed()
        {
            IInputSource input = NetClientBindings.LocalPlayer.InputSource;
            if (input == null) return false;

            return (input.Buttons & (ushort)InputButtons.Fire) != 0;
        }

        /// <summary>
        /// Sends C_SPAWN_REQUEST. The body carries no fields (protocol-spec § 4.1).
        /// </summary>
        /// <remarks>
        /// <b>Reliable, on channel 2.</b> A dropped respawn request is a player standing at a
        /// death screen that never clears, with nothing to re-send it — the client has no retry
        /// and the server has no way to know one was wanted. <c>ServerRespawnGate</c> refuses an
        /// early request as a normal outcome rather than as corruption, so a request that races
        /// the clock by a frame costs nothing.
        /// </remarks>
        private void RequestRespawn()
        {
            if (_client == null || !_client.IsConnected) return;

            // V8, ledger X-11: the body used to be empty. It now carries the loadout THIS
            // client is about to render -- read from the same LoadoutUi selection GetLoadout()
            // already draws from offline -- so the server arms the identical weapons rather
            // than its own draw. Deliberately NOT populating a spawn point choice: the
            // minimap-driven selection is not yet wired across the network, so this sends
            // SpawnRequestMessage.NoSpawnPointPreference (the constructor's own default) and the
            // server keeps choosing at random among eligible points, exactly as before.
            NetClientBindings.LocalPlayer.GetChosenLoadout(
                out byte primary, out byte secondary, out byte gear1, out byte gear2, out byte gear3);

            var spawnRequest = new SpawnRequestMessage(primary, secondary, gear1, gear2, gear3);
            System.Span<byte> body = stackalloc byte[SpawnRequestMessage.Size];
            if (spawnRequest.Write(body) < 0) return;

            var writer = new PayloadFrameWriter(_payload, ChannelId.ReliableOrdered);

            if (!writer.WriteMessage(ClientMessageType.SpawnRequest, body)) return;
            if (!writer.TryFinish(out int total)) return;

            _client.Send(
                ChannelId.ReliableOrdered, new System.ReadOnlySpan<byte>(_payload, 0, total),
                reliable: true);
        }

        /// <summary>
        /// Folds the local actor's snapshot entry in: health, alive/dead and the ammo count.
        /// </summary>
        /// <remarks>
        /// Reads the decoder the same way <c>ClientPredictionStage</c> does. A snapshot that does
        /// not mention this actor is a normal outcome — the delta encoder masks on change — so a
        /// miss returns rather than logging.
        /// </remarks>
        private void OnSnapshotApplied(uint serverTick, uint lastProcessedInputTick)
        {
            if (_client == null) return;

            ushort localActor = _client.LocalActorId;
            if (localActor == 0) return;

            if (!_client.Router.Decoder.Current.TryFind(localActor, out ActorSnapshotEntry entry)) return;

            _state.ApplySnapshot(in entry, Time.time);
        }

        /// <summary>
        /// One S_DEATH. <c>ApplyDeath</c> filters by actor id, so everyone else's is ignored.
        /// </summary>
        /// <remarks>
        /// The event is a broadcast because the killfeed is global — wiring it straight here
        /// without that filter would kill the local player on every death in the match, which is
        /// the failure <c>ClientCombatState.LocalActorId</c>'s remark describes.
        /// </remarks>
        private void OnDeathMessage(DeathMessage message)
        {
            if (!_state.ApplyDeath(in message, Time.time)) return;

            // Recorded here rather than read in OnDied, because OnDied is raised from INSIDE
            // ApplyDeath -- the killer would not be stored yet. The deploy screen is raised from
            // Update off the alive flag, so this lands before the frame that renders it, and a
            // snapshot-first death gets its killer named on the frame S_DEATH arrives.
            _lastKillerActorId = message.KillerActorId;
            _lastKillerWasEnvironment = message.KilledByEnvironment;
            _lastKillerTeam = message.KilledByEnvironment
                ? TeamId.None
                : (NetClientPresenterGuard.TryResolveActorTeam(message.KillerActorId, out byte team)
                    ? team
                    : TeamId.None);

            _killerLabelStale = true;
        }

        /// <summary>
        /// Takes input away from the corpse.
        /// </summary>
        /// <remarks>
        /// <b>Input only; the body is somebody else's job.</b>
        /// <c>NetClientCombatPresenter.KnockOverLocalActor</c> already fells it from the same
        /// message, behind <c>Actor.KnockOver</c>'s own re-entrancy guard. Felling it here too
        /// would be a second writer for one event.
        /// </remarks>
        private void OnDied()
        {
            if (!NetClientPresenterGuard.IsLocalActor(_state.LocalActorId)) return;

            ILocalPlayerRig local = NetClientBindings.LocalPlayer;
            if (!local.Exists) return;

            local.DisableInput();
            _inputSuppressedByDeath = true;
        }

        /// <summary>
        /// A respawn is a deploy, so it runs the whole presentation switch rather than input
        /// alone. Ledger <b>X-48</b>.
        /// </summary>
        /// <remarks>
        /// <b>Deliberately not <see cref="RestoreInput"/>.</b> That method gives back only what
        /// a death took, which is right for the disconnect path it also serves and wrong here:
        /// offline, a respawn goes through <c>Actor.SpawnAt</c>, which turns the backdrop off and
        /// enables input unconditionally because the player is being placed into the world. The
        /// suppression flag is cleared alongside so the <c>OnDisable</c> path does not later
        /// re-enable input a seat or a loadout screen has legitimately taken.
        /// </remarks>
        private void OnRespawned()
        {
            _inputSuppressedByDeath = false;
            EnterDeployedView();
        }

        /// <summary>
        /// Switches this client out of the pre-deploy menu view.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Guarded exactly as <see cref="RestoreInput"/> is. <c>IsLocalActor</c> reads the live
        /// <c>NetClientBootstrap.LocalActorId</c> and compares it against the argument, so the
        /// staleness that matters is in what we PASS: <c>_state.LocalActorId</c> is copied once
        /// per frame in <c>Update</c>, and at a router callback it can still be the unassigned
        /// zero. <c>OnSpawnActor</c> therefore refreshes it before calling here — the guard is
        /// kept rather than dropped, because gate rule G4 is right that a per-actor path reaching
        /// the local rig unguarded is how one player's event writes another player's camera.
        /// </para>
        /// </remarks>
        private void EnterDeployedView()
        {
            if (!NetClientPresenterGuard.IsLocalActor(_state.LocalActorId)) return;

            ILocalPlayerRig local = NetClientBindings.LocalPlayer;
            if (!local.Exists) return;

            local.EnterDeployedView();
        }

        private void RestoreInput()
        {
            if (!_inputSuppressedByDeath) return;
            _inputSuppressedByDeath = false;

            if (!NetClientPresenterGuard.IsLocalActor(_state.LocalActorId)) return;

            ILocalPlayerRig local = NetClientBindings.LocalPlayer;
            if (!local.Exists) return;

            local.EnableInput();
        }

        /// <summary>
        /// Drives the in-match readout: the local team, and the deploy screen. P17 3.1 and 3.2.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The team is resolved through <c>NetPresenterGate</c>, which is where P12 reads
        /// it.</b> Not a second resolution path: it is the same method against the same
        /// registered resolver, called once more per frame. § 3.1 makes that mandatory, and the
        /// reason is what this element is FOR — it exists to make a wrong team visible, and an
        /// element that answers the question by its own route can be wrong on its own.
        /// </para>
        /// <para>
        /// <b>Pushed every frame rather than on a change.</b> The HUD compares before it writes
        /// (<c>MatchHud.SetLocalTeam</c>), so the cost here is one snapshot lookup, and keeping
        /// the change detection on the far side is what lets a HUD that registers late get the
        /// team on its first frame instead of waiting for the next one to differ.
        /// </para>
        /// <para>
        /// <b>Visibility is <c>IsAlive</c>, never the button.</b> Criterion 5 is a respawn the
        /// player did not ask for — a server force-respawn, a match reset — and a screen closed
        /// by its own Deploy control survives exactly that and blocks the player. Driving both
        /// edges off the same flag the input suppression uses is what makes the two agree.
        /// </para>
        /// </remarks>
        private void SyncMatchHud()
        {
            IMatchHud hud = NetClientBindings.MatchHud;
            if (hud == null) return;

            hud.SetLocalTeam(
                NetPresenterGate.TryResolveLocalTeam(out byte team) ? team : TeamId.None);

            if (!_drawDeathScreen) return;

            if (_state.IsAlive)
            {
                if (!_deployShown) return;

                _deployShown = false;
                hud.HideDeploy();
                return;
            }

            if (!_deployShown || _killerLabelStale)
            {
                _deployShown = true;
                _killerLabelStale = false;
                hud.ShowDeploy(KillerLabel(), _lastKillerTeam);
            }

            hud.TickDeploy(
                _state.SecondsUntilRespawn(Time.time), CanDeployNow(Time.time));
        }

        /// <summary>
        /// What the deploy screen calls whoever killed this client.
        /// </summary>
        /// <remarks>
        /// The fallback is the killfeed's, verbatim: an id when no S_PLAYER_LIST has named that
        /// actor. Manufacturing something friendlier would make a genuinely missing name
        /// indistinguishable from a real one, which is the reason <c>PlayerNameTable</c> returns
        /// null and leaves the wording to its caller.
        /// </remarks>
        private string KillerLabel()
        {
            if (_lastKillerWasEnvironment) return "The world";

            string fallback = "actor " + _lastKillerActorId;

            return _names != null ? _names.Names.NameOr(_lastKillerActorId, fallback) : fallback;
        }

        /// <summary>Whether the HUD's Deploy control was pressed, clearing the edge.</summary>
        private static bool DeployPressed()
        {
            IMatchHud hud = NetClientBindings.MatchHud;
            return hud != null && hud.ConsumeDeployPressed();
        }
    }
}
