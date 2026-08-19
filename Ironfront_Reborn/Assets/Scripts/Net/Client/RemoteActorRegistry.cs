using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Client;
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
            }
        }

        private void OnSpawn(SpawnActorMessage message)
        {
            // The local player is predicted, never interpolated. See the type remarks.
            if (_client != null && message.ActorId == _client.LocalActorId) return;
            if (_live.ContainsKey(message.ActorId)) return;

            Transform t = _pool.Count > 0 ? _pool.Pop() : NewPooled();
            t.gameObject.SetActive(true);
            _live[message.ActorId] = t;

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
