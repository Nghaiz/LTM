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

            _client.Router.OnDeath += OnDeathMessage;
            _client.Router.OnSnapshotApplied += OnSnapshotApplied;
        }

        private void OnDisable()
        {
            if (_client == null) return;

            _state.OnDied -= OnDied;
            _state.OnRespawned -= OnRespawned;

            _client.Router.OnDeath -= OnDeathMessage;
            _client.Router.OnSnapshotApplied -= OnSnapshotApplied;

            // Give input back on the way out, or a disconnect while dead leaves the player
            // holding a controller that never comes alive again.
            RestoreInput();
            _state.Reset();
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

            if (!_state.IsAlive
                && _state.CanRequestRespawn(Time.time)
                && Input.GetKeyDown(_respawnKey))
            {
                RequestRespawn();
            }
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

            FpsActorController local = FpsActorController.instance;
            if (local == null) return;

            local.DisableInput();
            _inputSuppressedByDeath = true;
        }

        private void OnRespawned() => RestoreInput();

        private void RestoreInput()
        {
            if (!_inputSuppressedByDeath) return;
            _inputSuppressedByDeath = false;

            if (!NetClientPresenterGuard.IsLocalActor(_state.LocalActorId)) return;

            FpsActorController local = FpsActorController.instance;
            if (local == null) return;

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
