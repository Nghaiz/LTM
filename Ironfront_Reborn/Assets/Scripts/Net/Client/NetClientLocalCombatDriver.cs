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
    /// <b>The death screen is IMGUI and deliberately a stopgap</b>, for the reason
    /// <c>NetClientCombatPresenter.OnGUI</c> gives: a real element belongs on
    /// <c>Ingame UI Container.prefab</c> and phase 2 owns no prefabs or scenes. What is NOT a
    /// stopgap is the state behind it — the countdown, the gate and the request are the shipped
    /// library model, so replacing the drawing does not touch any of it.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class NetClientLocalCombatDriver : MonoBehaviour
    {
        /// <summary>Held to respawn once the clock allows it.</summary>
        [Tooltip("Pressed to respawn once the death countdown has elapsed.")]
        [SerializeField] private KeyCode _respawnKey = KeyCode.Space;

        [Tooltip("Draw the death screen with IMGUI. A stopgap until a HUD element reads the state.")]
        [SerializeField] private bool _drawDeathScreen = true;

        private NetClientBootstrap _client;
        private readonly ClientCombatState _state = new ClientCombatState();

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
            }
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

            _state.EquipWeapon(message.WeaponId);
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
            if (!_state.IsAlive && _state.CanRequestRespawn(Time.time)
                && (Input.GetKeyDown(_respawnKey) || ScriptedRespawnPressed()))
            {
                RequestRespawn();
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

            var writer = new PayloadFrameWriter(_payload, ChannelId.ReliableOrdered);

            if (!writer.WriteMessage(ClientMessageType.SpawnRequest, System.ReadOnlySpan<byte>.Empty)) return;
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
        private void OnDeathMessage(DeathMessage message) => _state.ApplyDeath(in message, Time.time);

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

        private void OnRespawned() => RestoreInput();

        private void RestoreInput()
        {
            if (!_inputSuppressedByDeath) return;
            _inputSuppressedByDeath = false;

            if (!NetClientPresenterGuard.IsLocalActor(_state.LocalActorId)) return;

            ILocalPlayerRig local = NetClientBindings.LocalPlayer;
            if (!local.Exists) return;

            local.EnableInput();
        }

        /// <summary>The death screen. See the class remark for why this is IMGUI.</summary>
        private void OnGUI()
        {
            if (!_drawDeathScreen) return;
            if (_state.IsAlive) return;

            float remaining = _state.SecondsUntilRespawn(Time.time);

            string line = remaining > 0f
                ? "You are dead. Respawn in " + Mathf.CeilToInt(remaining) + "s"
                : "You are dead. Press " + _respawnKey + " to respawn.";

            GUI.Label(new Rect(0f, Screen.height * 0.5f - 12f, Screen.width, 24f), line);
        }
    }
}
