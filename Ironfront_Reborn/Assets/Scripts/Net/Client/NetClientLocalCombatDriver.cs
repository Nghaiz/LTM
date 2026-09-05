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

        /// <summary>
        /// <c>Time.time</c> of the last spawn request that actually went out, or 0 if none has.
        /// The zero is what keeps <see cref="ResendDeployUntilPlaced"/> from asking before the
        /// player ever did.
        /// </summary>
        private float _lastDeployRequestAt;

        /// <summary>
        /// How long to wait for the server to answer a first deploy before asking again.
        /// </summary>
        /// <remarks>
        /// A placement is announced within an RTT, and lane-B's loopback RTT is single-digit
        /// milliseconds, so one second is roughly two orders of magnitude of headroom: it will
        /// not race a healthy answer, and it still recovers a dropped request inside the time a
        /// player spends reading the loadout screen. Shorter would narrow the window in which a
        /// re-send can cross a placement in flight, but that window is already made harmless by
        /// the server's aliveness guard, so there is nothing to buy by making this tighter.
        /// </remarks>
        private const float DeployResendSeconds = 1f;

        /// <summary>
        /// How long a first deploy waits for the player to press something before asking anyway.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Because the press is not guaranteed to be reachable.</b> The first spawn's only
        /// button is the loadout screen's Deploy (<see cref="LoadoutDeployPressed"/>), and that
        /// screen is opened by <c>GameManager.StartGame</c>'s <c>Invoke("OpenPlayerLoadout", 1f)</c>
        /// through <c>FpsActorController.OpenLoadoutWhileDead</c> — which returns early when
        /// <c>deployedView</c> is set, and closes without posting the edge when the player toggles
        /// it shut with the "Loadout" axis. Either route left the client with no path to
        /// <c>C_SPAWN_REQUEST</c> for the rest of the match, standing in a body the server had
        /// never placed. A deploy the player cannot ask for is not a design decision, so this asks
        /// on their behalf.
        /// </para>
        /// <para>
        /// Five seconds, and measured from the last frame the loadout screen was up rather than
        /// from the connect: the screen opens a second after the level does, and this must not
        /// fire in the gap before it appears, nor while the player is still choosing weapons —
        /// the request carries that choice (X-11). While it IS up, the grace is pushed forward
        /// every frame, so the fallback only ever runs when there is no button on screen to press.
        /// </para>
        /// </remarks>
        private const float DeployFallbackGraceSeconds = 5f;

        /// <summary>
        /// Earliest <c>Time.time</c> at which the unattended first deploy may be sent, or a
        /// negative value before the first frame has set it. See
        /// <see cref="DeployFallbackGraceSeconds"/>.
        /// </summary>
        private float _deployFallbackNotBefore = -1f;

        /// <summary>Whether a deploy request may be sent right now. See <see cref="_awaitingFirstDeploy"/>.</summary>
        private bool CanDeployNow(float nowSeconds)
            => _awaitingFirstDeploy || _state.CanRequestRespawn(nowSeconds);

        /// <summary>
        /// Whether this client still owes a deploy: it has never placed a body, or it is dead.
        /// </summary>
        /// <remarks>
        /// <b>Awaiting the first deploy is not being alive, whatever the predicted state says.</b>
        /// Both gates below used to read <c>!_state.IsAlive</c> alone, and that deadlocked every
        /// join from the moment a join stopped placing the body (ledger X-86, first bad commit
        /// b482d4c, pinned by bisect against 9c8d461). The server parks the claimed body DEAD and
        /// waits for C_SPAWN_REQUEST -- <c>Actor.Awake</c> writes <c>dead = true</c> and nothing
        /// clears it until a placement -- but it parks it WITHOUT a death, so no S_ACTOR_DEATH is
        /// ever sent and <c>ClientCombatState</c> keeps its opening `alive, health 100`. Measured
        /// on the driver at `spawned`: <c>combat.alive true</c>, <c>health 100</c>,
        /// <c>deathObserved false</c>, while the server held the same body parked. So the deploy
        /// screen hid itself, RequestRespawn's own gate refused, the request was never sent, and
        /// the body sat where Instantiate left it -- near the world origin, falling, which is the
        /// 963 m reading in artifacts/lane-b/b7-regrade-02 against 10.08 m one commit earlier.
        /// <para>
        /// <b>The parked body's HEALTH is 100, not 0</b>, and reading it as 0 is what broke the
        /// deploy path a second time -- see <see cref="OnSpawnActor"/>. <c>Actor.health</c> is a
        /// field initializer; <c>SpawnAt</c> is what writes it, and a parked body never runs
        /// <c>SpawnAt</c>. Only the <c>dead</c> flag distinguishes a parked slot from a live one.
        /// </para>
        /// <para>
        /// Deliberately NOT fixed by making the server send a death on join: nobody died, a
        /// synthetic one would reach the killfeed, the scoreboard and every death presenter, and
        /// <c>deathObserved</c> is a graded lane-B signal. The client already tracks the state
        /// this needs.
        /// </para>
        /// </remarks>
        private bool OwesDeploy => _awaitingFirstDeploy || !_state.IsAlive;

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

        /// <summary>Scratch for the deploy request's body, reused rather than stack-allocated.</summary>
        /// <remarks>
        /// A field and not a <c>stackalloc</c>, and the reason is a compiler rule rather than a
        /// preference: a stack-allocated span's ref-safe-to-escape scope is narrower than the
        /// <c>PayloadFrameWriter</c> ref struct it would be handed to, so passing one is CS8350
        /// and CS8352. <c>BaselineAckPolicy</c> and <c>ClientPredictionStage</c> both already
        /// solve it this way -- a heap array wrapped in a <c>ReadOnlySpan</c> at the call site --
        /// and this follows them rather than inventing a third shape.
        /// </remarks>
        private readonly byte[] _spawnRequestBody = new byte[SpawnRequestMessage.Size];

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

            // Ledger X-11/X-48/X-86. A JOIN no longer places the body
            // (ServerTickLoop.OnClientConnected), so S_SPAWN_ACTOR now reaches every client on
            // interest ALONE, before any deploy has happened -- it is "you now know this actor
            // exists," not "you are deployed."
            //
            // WHAT USED TO BE HERE, AND WHY IT WAS WRONG. This method read `message.Health > 0`
            // as "an already-alive body, so deploy immediately", on the stated belief that "the
            // server parks a not-yet-deployed body at Health 0". It does not, and never did. A
            // parked slot is Instantiate'd from actorPrefab, so Actor.health keeps its field
            // initializer of 100f; the only thing marking it unspawned is Actor.Awake's
            // `dead = true`, because `health = 100f; dead = false;` is written by SpawnAt, which
            // a parked body deliberately never runs. NetServerActor.Health is a pass-through to
            // that same field (D9), so entry.Health is 100 for EVERY parked slot and this branch
            // fired on EVERY join.
            //
            // The cost, measured on tmp/playtest (2026-09-04, one server + two clients): zero
            // "placed at spawn point" lines in 1312 server log lines, i.e. not one body was ever
            // placed. EnterDeployedView sets FpsActorController.deployedView, OpenLoadoutWhileDead
            // returns early on it, and the loadout screen is the ONLY first-spawn Deploy button
            // (LoadoutDeployPressed) -- so it never opened, no press was ever detected, no
            // C_SPAWN_REQUEST was ever framed, and ResendDeployUntilPlaced stayed asleep behind
            // its own `_lastDeployRequestAt <= 0` guard. The body stayed where
            // GameManager.StartGame put it, (0, 1000, 0), falling a kilometre onto the corner of
            // the heightmap: the "map loaded wrong, I fall off the edge" report.
            //
            // The alive/dead bit is not in S_SPAWN_ACTOR at all -- SpawnFlags carries IsBot and
            // IsLocalPlayer and nothing else -- so this message CANNOT answer the question it was
            // being asked. The snapshot can, and does: AdoptAlreadyAliveBody covers the
            // reconnect-mid-life case off the StateFlags bit, and OnRespawned covers the ordinary
            // dead->alive transition. Both are the server's own view of a body it has placed.
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
            // _client.IsConnected is part of the GATE, not just of RequestRespawn's own guard,
            // and the ordering is the whole point: this file's own remark below records that
            // ScriptedRespawnPressed CONSUMES its edge when read. A press read while the
            // connection is still coming up is therefore spent forever -- the programme declares
            // one edge per step and there is no second one -- so the client never deploys even
            // though the grant survived. Observed as the failure MOVING between clients across
            // two runs (observer-b at 968.53 in fix-verify-02, observer-a at 950.03 in
            // fix-verify-03) while the other two grounded, which is the signature of a race
            // rather than of a per-client defect. Ledger X-86.
            if (OwesDeploy && CanDeployNow(Time.time)
                && _client != null && _client.IsConnected
                && (Input.GetKeyDown(_respawnKey) || ScriptedRespawnPressed()
                    || DeployPressed() || LoadoutDeployPressed()))
            {
                // The grant is NOT retired here. A sent request is not a placed body: the server
                // drops the request outright when the connection has no ServerPlayer yet
                // (`ISpawnRequestHandler.OnSpawnRequested`'s first line returns on a
                // `TryGetValue` miss), and retiring on the send spent the only grant on a
                // message nobody acted on. Measured as 2 of 3 and 1 of 3 clients left at the
                // prefab park across repeat runs. The grant is retired where the server ANSWERS
                // -- OnRespawned, off the snapshot's own IsAlive bit -- and until then the block
                // below re-sends. Ledger X-86.
                if (RequestRespawn()) _lastDeployRequestAt = Time.time;
            }

            TrackDeployFallbackGrace();
            ResendDeployUntilPlaced();
        }

        /// <summary>
        /// Holds the unattended first deploy back while a Deploy button is on screen — or while
        /// there is nothing to send it to. See <see cref="DeployFallbackGraceSeconds"/>.
        /// </summary>
        private void TrackDeployFallbackGrace()
        {
            if (!_awaitingFirstDeploy) return;

            bool nothingToPressYet = _client == null || !_client.IsConnected
                                     || NetClientBindings.LocalPlayer.IsLoadoutOpen;

            // The `< 0f` arm is the first frame: an unset grace must not read as an elapsed one,
            // or a client that connects before the loadout screen opens would deploy on frame one
            // with the default loadout and never show the player the screen at all.
            if (nothingToPressYet || _deployFallbackNotBefore < 0f)
                _deployFallbackNotBefore = Time.time + DeployFallbackGraceSeconds;
        }

        /// <summary>
        /// Re-sends the first deploy request, once a second, until the server answers it.
        /// Ledger <b>X-86</b>'s residual.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why a re-send and not a better first send.</b> The request is dropped by the
        /// server, not lost by the client: `OnSpawnRequested` returns immediately when
        /// `_byConnection` has no `ServerPlayer` for the session yet, because there is no body to
        /// place. Nothing on the client can make that arrive earlier — the only repair available
        /// to the sender is to ask again.
        /// </para>
        /// <para>
        /// <b>Why a duplicate is safe.</b> The server's own `AwaitingFirstDeploy` flag makes the
        /// FIRST accepted request the placement and sends every later one to
        /// `ServerCombatBridge.TryRespawn` — which, as of this change, refuses a body that is
        /// alive. So a re-send that crosses the placement in flight is declined rather than
        /// teleporting a player who has just deployed. That guard is the half of this fix that
        /// makes the other half safe, and neither should land alone.
        /// </para>
        /// <para>
        /// <b>Stopping condition is the server's answer, not a count.</b> `_awaitingFirstDeploy`
        /// is cleared in <see cref="OnRespawned"/>, which fires off the snapshot's IsAlive bit —
        /// the client observing its own body PLACED. A retry cap would restore exactly the
        /// failure this closes, so there is none; the cost of the unbounded case is one 8-byte
        /// message a second against a server that is not placing anybody, which is a condition
        /// worth being noisy about rather than silent.
        /// </para>
        /// <para>
        /// <b>It also sends the FIRST request when no press is coming.</b> This method used to
        /// return while `_lastDeployRequestAt` was 0, on the reading that asking before the player
        /// did would be presumptuous. The 2026-09-04 playtest is what that reading cost: the press
        /// it waited for can only come from the loadout screen, the screen did not open, and so
        /// the re-sender — the whole repair — was unreachable for the entire match. It now asks
        /// unprompted once <see cref="DeployFallbackGraceSeconds"/> has elapsed with no Deploy
        /// button on screen, which is the only state in which there is nothing to be presumptuous
        /// about.
        /// </para>
        /// </remarks>
        private void ResendDeployUntilPlaced()
        {
            if (!_awaitingFirstDeploy) return;
            if (_client == null || !_client.IsConnected) return;

            // Nobody has pressed anything. This used to `return` here, which made the whole
            // re-sender dead code on the run that mattered: the press it waited for came from a
            // loadout screen that never opened, so `_lastDeployRequestAt` stayed 0 for the whole
            // match and the server logged zero placements. The grace above is what makes asking
            // unprompted safe — it only elapses when no Deploy button is on screen.
            if (_lastDeployRequestAt <= 0f)
            {
                if (_deployFallbackNotBefore < 0f || Time.time < _deployFallbackNotBefore) return;

                if (RequestRespawn()) _lastDeployRequestAt = Time.time;
                return;
            }

            if (Time.time - _lastDeployRequestAt < DeployResendSeconds) return;

            if (RequestRespawn()) _lastDeployRequestAt = Time.time;
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
        /// <returns>True when the request was framed and handed to the transport.</returns>
        private bool RequestRespawn()
        {
            if (_client == null || !_client.IsConnected) return false;

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
            if (spawnRequest.Write(_spawnRequestBody) < 0) return false;

            var writer = new PayloadFrameWriter(_payload, ChannelId.ReliableOrdered);

            if (!writer.WriteMessage(
                    ClientMessageType.SpawnRequest,
                    new System.ReadOnlySpan<byte>(_spawnRequestBody))) return false;
            if (!writer.TryFinish(out int total)) return false;

            _client.Send(
                ChannelId.ReliableOrdered, new System.ReadOnlySpan<byte>(_payload, 0, total),
                reliable: true);

            // The client logged NOTHING about deploy, and that is why the 2026-09-04 playtest had
            // to be solved by reading source instead of logs: three log files, 18,929 lines, and
            // no way to tell a request that was never sent from one the server never answered.
            // Rate is bounded by the caller — one press, then at most one per DeployResendSeconds
            // until the body is placed — so this is not a per-frame line.
            Debug.Log(
                $"[net] deploy requested for actor {_state.LocalActorId} "
                + $"(first deploy: {_awaitingFirstDeploy}, loadout {primary}/{secondary}/"
                + $"{gear1}/{gear2}/{gear3})");

            return true;
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

            AdoptAlreadyAliveBody(in entry);
        }

        /// <summary>
        /// Retires the first-deploy grant when the server's own snapshot says this body is
        /// ALREADY alive — the reconnect-mid-life case. Ledger <b>X-86</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the half of <see cref="OnSpawnActor"/>'s old <c>message.Health &gt; 0</c> test
        /// that was worth keeping, moved to the one source that can answer it. A reconnect arrives
        /// mid-life: the snapshot's IsAlive bit is already true when the first one lands, so it
        /// never transitions and <see cref="OnRespawned"/> — which fires on that edge — never
        /// runs. Without this the client would owe a deploy for a body it is already standing in,
        /// and <see cref="ResendDeployUntilPlaced"/> would ask for one every second for the whole
        /// match.
        /// </para>
        /// <para>
        /// <b>Gated on the field being PRESENT, not on the property alone.</b>
        /// <c>ClientCombatState.IsAlive</c> opens at <see langword="true"/> because for most of a
        /// life that is the answer, and <c>DeltaEncoder</c> masks
        /// <c>SnapshotField.StateFlags</c> only when it changes — so reading the property would
        /// adopt an unplaced body off a snapshot that said nothing about aliveness at all, which
        /// is the same shape of mistake as trusting <c>message.Health</c>.
        /// </para>
        /// </remarks>
        private void AdoptAlreadyAliveBody(in ActorSnapshotEntry entry)
        {
            if (!_awaitingFirstDeploy) return;
            if (!entry.Has(SnapshotField.StateFlags)) return;
            if ((entry.StateFlags & ActorStateFlags.IsAlive) == 0) return;

            _awaitingFirstDeploy = false;

            Debug.Log(
                $"[net] deploy adopted for actor {_state.LocalActorId}: the snapshot reports this "
                + "body already alive (reconnect mid-life); no first deploy is owed");

            // Kept in step for the same reason OnSpawnActor does it: EnterDeployedView guards on
            // IsLocalActor(_state.LocalActorId), Update copies that id once per frame, and at a
            // router callback it can still be the unassigned zero -- which would reject the one
            // call that takes the menu down.
            _state.LocalActorId = _client.LocalActorId;

            EnterDeployedView();
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
            // The server ANSWERED. This is the placement signal X-86's residual said the client
            // did not have, and it is trustworthy in the way `combat.alive` is not: it fires on
            // the snapshot's IsAlive going true, so it reports the server's own view of a body it
            // has placed, rather than the client's opening assumption about a body it has not.
            // Retiring the grant here rather than at the send is what stops a dropped request
            // from being the only one.
            bool wasFirstDeploy = _awaitingFirstDeploy;
            _awaitingFirstDeploy = false;

            if (wasFirstDeploy)
            {
                // The counterpart to "deploy requested" in RequestRespawn, and the line whose
                // absence a future playtest should be read for: server-side the same moment logs
                // "[net] actor N (team T) placed at spawn point ...", so the two together say
                // whether a missing body is a request that never left or a placement that never
                // came back.
                Debug.Log(
                    $"[net] deploy granted for actor {_state.LocalActorId}: "
                    + "the server placed this body");
            }

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

            // IsAlive, NOT OwesDeploy. This panel is the DEATH screen -- its title is authored in
            // the scene as "YOU WERE KILLED" and MatchHud writes a killer name under it -- so
            // showing it whenever a deploy is owed showed it on the FIRST spawn too, before any
            // death, with _lastKillerActorId still at its 0 default. Every player's first sight
            // of the game was "YOU WERE KILLED / Killed by actor 0" and nobody had been killed.
            //
            // The deadlock that made OwesDeploy necessary is fixed where it belongs: the SEND
            // gate above still uses OwesDeploy, and the first spawn's request now comes from the
            // loadout screen's own Deploy (LoadoutDeployPressed) -- the screen the player is
            // actually looking at then, and the one that chose the loadout the request carries.
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

        /// <summary>
        /// Whether the LOADOUT screen's Deploy was pressed, clearing the edge. The first spawn's
        /// button, as distinct from the death screen's.
        /// </summary>
        /// <remarks>
        /// The death screen cannot serve the first spawn: that panel is authored with a "YOU WERE
        /// KILLED" title and a killer name, and <see cref="_lastKillerActorId"/> is 0 until a
        /// <c>DeathMessage</c> sets it — so driving it off <see cref="OwesDeploy"/> greeted every
        /// player with "Killed by actor 0" before anything had happened. The screen is back on
        /// <c>IsAlive</c> (see <c>SyncMatchHud</c>) and the first deploy comes from here instead,
        /// which is also the screen where the loadout this request carries was chosen.
        /// </remarks>
        private static bool LoadoutDeployPressed()
        {
            ILocalPlayerRig local = NetClientBindings.LocalPlayer;
            return local.Exists && local.ConsumeDeployIntent();
        }
    }
}
