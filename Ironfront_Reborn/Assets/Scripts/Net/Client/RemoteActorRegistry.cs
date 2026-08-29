using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Unity;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Spawns, despawns and draws every actor this client does not control, positioning them
    /// between snapshots so they move smoothly rather than teleporting 30 times a second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// At execution order -50: after <c>NetClientBootstrap</c> has pumped the transport at
    /// -1000, so the snapshot drawn this frame is the newest that arrived, and before anything
    /// at the default order reads a transform.
    /// </para>
    /// <para>
    /// <b>The local player is skipped.</b> It is driven by prediction and reconciliation, not by
    /// interpolation — drawing it from the snapshot buffer would put it two ticks in the past
    /// and reintroduce exactly the input lag prediction exists to remove. The one actor whose
    /// position the player can feel is the one this must not touch.
    /// </para>
    /// <para>
    /// <b>Despawn returns to a pool rather than destroying.</b> Interest management means actors
    /// cross the boundary constantly at 48 actors, and <c>Instantiate</c>/<c>Destroy</c> on every
    /// crossing is both a hitch and a steady allocation — against M1 criterion 9, which asks for
    /// none per tick.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class RemoteActorRegistry : MonoBehaviour
    {
        [Tooltip("Instantiated for each actor the server spawns into view.")]
        [SerializeField] private GameObject _remoteActorPrefab;

        [Tooltip("Pre-warmed pool size. 48 actors is the M1 target world.")]
        [SerializeField] private int _prewarm = ProtocolConstants.MAX_PLAYERS;

        private NetClientBootstrap _client;

        private readonly Dictionary<ushort, Transform> _live =
            new Dictionary<ushort, Transform>(ProtocolConstants.MAX_ACTORS);

        private readonly Stack<Transform> _pool = new Stack<Transform>();

        // Resolved once per spawn, never per snapshot. GetComponent at 30 Hz x 48 actors is the
        // allocation-free-but-slow trap: it costs nothing the profiler flags as garbage and
        // shows up as a flat frame-time tax instead.
        private readonly Dictionary<ushort, RemoteActorView> _views =
            new Dictionary<ushort, RemoteActorView>(ProtocolConstants.MAX_ACTORS);

        /// <summary>Actors currently drawn.</summary>
        public int LiveCount => _live.Count;

        /// <summary>Actors held in the pool, ready to reuse.</summary>
        public int PooledCount => _pool.Count;

        /// <summary>
        /// Resolves a network actor id to the transform drawing it, if this client is drawing
        /// one. phase-V10 task 1.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The local player is never here.</b> It is excluded from <c>_live</c> on purpose —
        /// it is predicted, not interpolated — so a caller asking about its own actor gets a
        /// miss. Check <c>NetClientPresenterGuard.IsLocalActor</c> first, always.
        /// </para>
        /// <para>
        /// <b>A miss is a normal outcome, not an error.</b> Interest management means an actor
        /// that died outside this client's view was never spawned here at all. Do not log one.
        /// </para>
        /// <para>
        /// <b>Do not cache the result across a despawn.</b> Transforms return to a pool and are
        /// handed to whichever actor spawns next, so a held reference silently starts pointing
        /// at a different player.
        /// </para>
        /// <para>
        /// Named for symmetry with <c>ServerActorRegistry.TryFind</c>, so both sides of the wire
        /// read alike.
        /// </para>
        /// </remarks>
        public bool TryFind(ushort actorId, out Transform t) => _live.TryGetValue(actorId, out t);

        /// <summary>
        /// Resolves a network actor id to the <see cref=RemoteActorView/> presenting it. Same
        /// three caveats as <see cref=TryFind/>.
        /// </summary>
        /// <remarks>
        /// The view is resolved once, on spawn, into the pooled entry. A
        /// <c>GetComponent</c> per snapshot would be 48 lookups at 30 Hz for a value that
        /// cannot change while the transform is live.
        /// </remarks>
        public bool TryFindView(ushort actorId, out RemoteActorView view)
        {
            if (_views.TryGetValue(actorId, out view)) return view != null;

            view = null;
            return false;
        }

        private void Awake()
        {
            _client = NetClientBootstrap.Current;

            if (_remoteActorPrefab == null)
            {
                Debug.LogError("[net] RemoteActorRegistry has no prefab. Remote actors will not be drawn.");
                enabled = false;
                return;
            }

            for (int i = 0; i < _prewarm; i++) _pool.Push(NewPooled());
        }

        private void OnEnable()
        {
            if (_client == null) return;
            _client.Router.OnSpawnActor += OnSpawn;
            _client.Router.OnDespawnActor += OnDespawn;
        }

        private void OnDisable()
        {
            if (_client == null) return;
            _client.Router.OnSpawnActor -= OnSpawn;
            _client.Router.OnDespawnActor -= OnDespawn;
        }

        private void Update()
        {
            if (_client == null || _live.Count == 0) return;

            SnapshotInterpolator buffer = _client.Router.Interpolator;

            // Alpha comes from the prediction clock so motion is smooth above 30 fps. Without
            // it the render tick advances in whole steps and the interpolation is quantised to
            // the very tick rate it exists to hide.
            double renderTick = buffer.RenderTick(NetPredictionClock.Current?.Alpha ?? 0f);

            if (buffer.TrySample(renderTick, out WorldSnapshot from, out WorldSnapshot to, out double alpha)
                == InterpolationResult.Starved)
            {
                return;
            }

            foreach (KeyValuePair<ushort, Transform> pair in _live)
            {
                if (SnapshotInterpolator.TryLerpPosition(from, to, alpha, pair.Key, out Vec3 p))
                    pair.Value.position = new Vector3(p.X, p.Y, p.Z);

                if (SnapshotInterpolator.TryLerpYaw(from, to, alpha, pair.Key, out float yaw))
                    pair.Value.rotation = Quaternion.Euler(0f, yaw, 0f);

                // Everything past position and yaw -- pitch, stance, aim, ragdoll, weapon, team
                // -- was decoded and discarded until phase-V10. It is read from `to` rather than
                // interpolated: these are discrete states, and lerping a crouch is meaningless.
                if (!_views.TryGetValue(pair.Key, out RemoteActorView view) || view == null) continue;
                if (to.TryFind(pair.Key, out ActorSnapshotEntry entry)) view.Apply(in entry);

                // P3 task 3.4. Team arrives with the snapshot, not with the spawn, so the
                // colour is written every frame rather than once. SetMarker is idempotent by
                // subject -- a repeat recolours in place -- so this costs one dictionary hit
                // and one Color assignment per live actor and never stacks a second icon.
                NetClientBindings.Minimap?.SetBodyMarker(
                    pair.Value, CapturePointOwnership.ToSpawnPointOwner(view.Team));
            }
        }

        private void OnSpawn(SpawnActorMessage message)
        {
            // The local player is predicted, never interpolated. See the type remarks.
            if (_client != null && message.ActorId == _client.LocalActorId) return;
            if (_live.ContainsKey(message.ActorId)) return;

            Transform t = _pool.Count > 0 ? _pool.Pop() : NewPooled();

            // PLACED from the spawn message, not left wherever the pool parked it. X-17.
            //
            // This looks like a redundant write -- the Update loop below positions everything
            // every frame -- and it is not, for two reasons that only bite together.
            //
            // TryLerpPosition needs the actor in BOTH interpolation endpoints, and interest
            // culling REMOVES a distant actor from the accumulated world (DeltaDecoder.Current),
            // so an actor past InterestManager.CullRadius is in neither. Meanwhile
            // AnnounceNewActors announces EVERY actor to every client regardless of interest.
            // Together those describe an actor that is spawned and never replicated, and for
            // that actor this message carries the only position it will ever be given.
            //
            // Without this the proxy renders at the pool's parking spot and stays there. Measured
            // 2026-08-22 (artifacts/lane-b/x17-measure-01): a client 2570 m from its target drew
            // it at (0, 2000, 0) at every one of seven checkpoints, while the snapshot -- whenever
            // it did arrive -- carried (1088.11, 103.41, 954.30), the victim's exact position to
            // the centimetre. Nothing was wrong with the wire, the interest manager or the
            // decoder. The scripted aim solver reported `resolved: true` and fired 240 rounds
            // into open sky, and a human's crosshair would have done the same.
            t.position = new Vector3(
                Quantize.UnpackPos(message.PosX),
                Quantize.UnpackPos(message.PosY),
                Quantize.UnpackPos(message.PosZ));
            t.rotation = Quaternion.Euler(0f, Quantize.UnpackYaw(message.Yaw), 0f);

            t.gameObject.SetActive(true);
            _live[message.ActorId] = t;

            // P3 task 3.4. The icon is bound HERE, at the neutral colour, rather than waiting
            // for the first snapshot to reveal the team: SpawnActorMessage does not carry a
            // team, and an actor past InterestManager.CullRadius may never appear in a snapshot
            // at all (see the placement comment above). Waiting would leave exactly those
            // actors -- the far ones, the ones a minimap is FOR -- with no icon. The colour is
            // corrected in Update the moment a snapshot names the team.
            //
            // Ledger A-2 is not touched: nothing here registers a proxy with ActorManager, so
            // ActorManager.Player still resolves to the local body. That is the whole reason
            // this goes through MinimapUi.SetMarker (Transform-keyed) and not AddActorBlip.
            NetClientBindings.Minimap?.SetBodyMarker(t, -1);

            RemoteActorView view = t.GetComponent<RemoteActorView>();
            if (view != null)
            {
                view.Bind(message.ActorId);
                _views[message.ActorId] = view;
            }
            else
            {
                _views.Remove(message.ActorId);
                NetClientPresenterGuard.WarnOnce(
                    "no-remote-actor-view",
                    "[net] the remote actor prefab carries no RemoteActorView, so remote players "
                    + "will slide at a fixed pose: no stance, no aim, no weapon, no ragdoll. This "
                    + "is client-track item E1 -- add the component to the prefab.");
            }
        }

        private void OnDespawn(DespawnActorMessage message)
        {
            if (!_live.TryGetValue(message.ActorId, out Transform t)) return;

            _live.Remove(message.ActorId);
            _views.Remove(message.ActorId);

            // BEFORE the transform goes back to the pool. The marker is keyed by that
            // transform, and the pool hands the same one to the NEXT actor -- so a marker left
            // behind is not merely stale, it is an icon wearing the previous occupant's team
            // that SetMarker would then recolour instead of replacing.
            NetClientBindings.Minimap?.RemoveMarker(t);

            t.gameObject.SetActive(false);
            _pool.Push(t);
        }

        private Transform NewPooled()
        {
            GameObject go = Instantiate(_remoteActorPrefab, transform);
            go.SetActive(false);
            return go.transform;
        }
    }
}
