using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Projectiles;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// Turns <c>S_PROJECTILE_SPAWN</c> into a projectile this client can watch. Phase-V7 task 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>V7-D5: projectiles replicate by parameter, not by state.</b> One message carries
    /// <c>(origin, velocity, spawnTick)</c> and the client simulates the flight from there. The
    /// message is not a position update and there is no per-tick projectile entry in the
    /// snapshot; a bullet costs one 20-byte event for its whole life.
    /// </para>
    /// <para>
    /// <b>The flight is fast-forwarded to now, not started from the origin.</b> The launch spent
    /// the one-way latency getting here, so instantiating at <c>origin</c> would put every
    /// tracer visibly behind where it actually is —
    /// <see cref="ClientProjectileTracker.Apply"/> advances it by the elapsed ticks first.
    /// </para>
    /// <para>
    /// <b>A repeat of a live id re-seats, it does not spawn a second projectile</b> (V7-D6 for a
    /// guided missile at 5 Hz, V7-D8 for a tumbling deployable at 10 Hz). Without that, a
    /// Javelin would be a new missile every 200 ms and the sky would fill with them.
    /// </para>
    /// <para>
    /// <b>Nothing here does damage.</b> Every projectile this file instantiates has
    /// <c>source</c> left null and its damage path disabled by
    /// <c>Projectile.Hit</c>'s <c>NetContext.IsClient</c> branch — V7-D3 puts damage entirely on
    /// the server, computed from the server's own distance accumulator. That is also why this
    /// presenter never reads <c>Projectile.Damage()</c>.
    /// </para>
    /// <para>
    /// <b>No handler throws (V10 D22).</b> <c>ClientMessageRouter.Route</c> counts malformed
    /// input rather than throwing, and an exception raised from a subscriber would propagate
    /// straight into the transport pump.
    /// </para>
    /// </remarks>
    [DefaultExecutionOrder(-50)]
    [DisallowMultipleComponent]
    public sealed class NetClientProjectilePresenter : MonoBehaviour
    {
        [Tooltip("Indexed by (byte)ProjectileKind: Shell=0, Rocket=1, GuidedMissile=2, "
                 + "Grenade=3, AmmoBag=4, Medipack=5, Bullet=6. An empty slot draws nothing and "
                 + "must not throw. Client-track item.")]
        [SerializeField] private GameObject[] _prefabsByKind;

        private NetClientBootstrap _client;
        private ClientProjectileTracker _tracker;

        private readonly Dictionary<ushort, Projectile> _spawned =
            new Dictionary<ushort, Projectile>();

        // Reused every frame. Sized to the id pool so a mass expiry cannot overflow it and leave
        // a projectile alive on screen with nothing left to expire it.
        private readonly ushort[] _expiredBuffer = new ushort[ProjectileIdPool.DefaultCapacity];

        /// <summary>Projectiles this client is currently drawing.</summary>
        public int ActiveCount => _spawned.Count;

        /// <summary>
        /// Messages naming a kind with no prefab authored. Non-zero means a client-track gap,
        /// not a protocol fault — counted rather than logged per message, because a missing
        /// bullet prefab would log at the rate of every trigger finger in the match.
        /// </summary>
        public long UnrenderableKinds { get; private set; }

        private void Awake()
        {
            if (!NetClientPresenterGuard.IsPresentable)
            {
                enabled = false;
                return;
            }

            if (!NetClientPresenterGuard.TryResolveClient(
                    nameof(NetClientProjectilePresenter), out _client))
            {
                enabled = false;
                return;
            }

            _tracker = new ClientProjectileTracker(ProjectileCatalogBuilder.FromPrefabs(_prefabsByKind));
        }

        private void OnEnable()
        {
            if (_client == null) return;

            _client.Router.OnProjectileSpawn += OnProjectileSpawn;
        }

        private void OnDisable()
        {
            if (_client == null) return;

            _client.Router.OnProjectileSpawn -= OnProjectileSpawn;
        }

        private void Update()
        {
            if (_tracker == null) return;

            int expired = _tracker.Tick(Time.deltaTime, _expiredBuffer);
            for (int i = 0; i < expired; i++) Despawn(_expiredBuffer[i]);
        }

        /// <summary>
        /// Drops a projectile the server has ended. Called when its detonation arrives as
        /// <c>S_EXPLOSION</c>, so a grenade does not keep rolling after its own blast.
        /// </summary>
        public void Retire(ushort projectileId)
        {
            _tracker?.Remove(projectileId);
            Despawn(projectileId);
        }

        private void OnProjectileSpawn(ProjectileSpawnMessage message)
        {
            if (_tracker == null) return;

            ProjectileApplyResult result = _tracker.Apply(in message, NetContext.CurrentTick);

            if (result.Action == ProjectileApplyAction.Ignore)
            {
                Despawn(result.ProjectileId);
                return;
            }

            if (result.Action == ProjectileApplyAction.ReSeat
                && _spawned.TryGetValue(result.ProjectileId, out Projectile live)
                && live != null)
            {
                live.transform.SetPositionAndRotation(
                    ToUnity(result.Position), RotationFor(result.Velocity));

                // THE VELOCITY IS THE POINT OF THE CORRECTION, not the pose. A guided missile
                // re-parameterizes at 5 Hz precisely because its heading changes; snapping the
                // transform while leaving the projectile coasting on its launch vector would
                // make it jump every 200 ms and fly the wrong way in between -- V7-D6 corrected
                // in appearance only.
                live.ApplyNetVelocity(ToUnity(result.Velocity));
                return;
            }

            GameObject prefab = PrefabFor(message.Kind);
            if (prefab == null)
            {
                UnrenderableKinds++;
                return;
            }

            GameObject instance = Object.Instantiate(
                prefab, ToUnity(result.Position), RotationFor(result.Velocity));

            var projectile = instance.GetComponent<Projectile>();
            if (projectile != null)
            {
                // source stays null on purpose: it is the field Weapon.SpawnProjectile sets to
                // make a projectile do real damage, and a cosmetic instance must never carry it.
                projectile.netProjectileId = result.ProjectileId;

                // A grenade's fuse counts from the launch tick, so both sides detonate on the
                // same integer rather than on whichever frame each side's own float crossed.
                //
                // The subtraction is clamped because these are unsigned: early in a match
                // CurrentTick can be smaller than the catch-up, and an underflow would wrap to
                // roughly four billion and hand the grenade a fuse that never fires. A clamp
                // costs one comparison and the worst case is a grenade that detonates slightly
                // early on a client during the first two seconds of a round.
                if (projectile is GrenadeProjectile grenade)
                {
                    uint now = NetContext.CurrentTick;
                    var caughtUp = (uint)result.FastForwardedTicks;
                    grenade.ArmFuse(now >= caughtUp ? now - caughtUp : 0u);
                }

                _spawned[result.ProjectileId] = projectile;
            }
        }

        private GameObject PrefabFor(ProjectileKind kind)
        {
            var index = (int)kind;
            if (_prefabsByKind == null || index < 0 || index >= _prefabsByKind.Length) return null;

            return _prefabsByKind[index];
        }

        private void Despawn(ushort projectileId)
        {
            if (!_spawned.TryGetValue(projectileId, out Projectile projectile)) return;

            _spawned.Remove(projectileId);
            if (projectile != null) Object.Destroy(projectile.gameObject);
        }

        private static Vector3 ToUnity(in Ironfront.Net.Replication.Movement.Vec3 v)
            => new Vector3(v.X, v.Y, v.Z);

        /// <summary>
        /// Facing for a projectile travelling along <paramref name="velocity"/>. A zero vector
        /// would make <c>LookRotation</c> log an error every frame, so it falls back to identity.
        /// </summary>
        private static Quaternion RotationFor(in Ironfront.Net.Replication.Movement.Vec3 velocity)
        {
            Vector3 forward = ToUnity(velocity);
            return forward.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(forward)
                : Quaternion.identity;
        }
    }
}
