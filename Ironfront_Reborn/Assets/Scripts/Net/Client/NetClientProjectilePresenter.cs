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
                 + "Grenade=3, AmmoBag=4, Medipack=5, Bullet=6, Spearhead=7. An empty slot draws nothing and "
                 + "must not throw. Client-track item.")]
        [SerializeField] private GameObject[] _prefabsByKind;

        private NetClientBootstrap _client;
        private ClientProjectileTracker _tracker;

        private readonly Dictionary<ushort, IProjectileBody> _spawned =
            new Dictionary<ushort, IProjectileBody>();

        /// <summary>
        /// A deployable's drawn motion: from where it was drawn to where the server last put it.
        /// </summary>
        private struct DeployableGlide
        {
            public Vector3 From;
            public Vector3 To;
            public float StartedAt;
        }

        /// <summary>
        /// Deployables this client draws, which it moves itself rather than letting physics do it.
        /// See <see cref="SeatDeployable"/>.
        /// </summary>
        private readonly Dictionary<ushort, DeployableGlide> _deployables =
            new Dictionary<ushort, DeployableGlide>();

        /// <summary>
        /// How long a deployable takes to glide to a new server pose: the interval the server
        /// re-announces a moving one at, so a thrown pack reaches each pose as the next arrives.
        /// </summary>
        private static readonly float DeployableGlideSeconds =
            ServerDeployableAuthority.MovingReAnnounceTicks * ProtocolConstants.MS_PER_TICK / 1000f;

        // Reused by Update: a dictionary cannot be written while it is enumerated, which Mono
        // enforces and modern .NET does not.
        private readonly List<ushort> _deployableIds = new List<ushort>();

        // Reused every frame. Sized to the id pool so a mass expiry cannot overflow it and leave
        // a projectile alive on screen with nothing left to expire it.
        private readonly ushort[] _expiredBuffer = new ushort[ProjectileIdPool.DefaultCapacity];

        /// <summary>Projectiles this client is currently drawing.</summary>
        public int ActiveCount => _spawned.Count;

        /// <summary>Distinct authoritative projectile ids accepted as new spawns.</summary>
        public long ProjectilesSpawned { get; private set; }

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

            // The catalogue is read off the prefabs by the side that can name Projectile and its
            // configuration; this side receives the finished ProjectileCatalog, which is a
            // replication-library type and crosses the seam unwrapped.
            _tracker = new ClientProjectileTracker(
                NetClientBindings.BuildProjectileCatalog(_prefabsByKind));
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

            GlideDeployables(Time.time);
        }

        /// <summary>Moves every drawn deployable along its glide to the last server pose.</summary>
        private void GlideDeployables(float now)
        {
            if (_deployables.Count == 0) return;

            _deployableIds.Clear();
            foreach (KeyValuePair<ushort, DeployableGlide> pair in _deployables) _deployableIds.Add(pair.Key);

            for (int i = 0; i < _deployableIds.Count; i++)
            {
                ushort id = _deployableIds[i];
                if (!_spawned.TryGetValue(id, out IProjectileBody body) || body == null || !body.Exists)
                {
                    _deployables.Remove(id);
                    continue;
                }

                DeployableGlide glide = _deployables[id];
                float t = Mathf.Clamp01((now - glide.StartedAt) / DeployableGlideSeconds);
                body.Transform.position = Vector3.Lerp(glide.From, glide.To, t);
            }
        }

        /// <summary>
        /// Draws a deployable where the server says it is, and nowhere else.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Playtest 2026-09-28, bug 4: a medipack thrown on an upper floor fell through to the
        /// floor below, and standing next to it healed nobody.</b> The client ran the pack's own
        /// rigidbody and teleported it to every announcement, and each announcement was
        /// fast-forwarded along the launch arc, gravity included. That could put the pack inside
        /// the floor, and physics then dropped it a storey while the server's pack, the one that
        /// heals, stayed upstairs. Measured in the farmhouse on Island (2026-09-29): at 6-9 ticks
        /// of catch-up the client's copy fell through in five throws of eight, the server's in
        /// none.
        /// </para>
        /// <para>
        /// So the client's copy is kinematic and never simulates: it glides from where it is drawn
        /// to each pose the server announces, over the interval the server announces a moving
        /// pack at, and the server now also announces the pose it comes to rest in. Every point it
        /// is drawn at lies between two poses the server's physics actually reached.
        /// </para>
        /// </remarks>
        private void SeatDeployable(ushort id, IProjectileBody body, Vector3 target, bool spawning)
        {
            if (spawning)
            {
                Rigidbody rigidbody = body.GameObject.GetComponent<Rigidbody>();
                if (rigidbody != null)
                {
                    rigidbody.linearVelocity = Vector3.zero;
                    rigidbody.angularVelocity = Vector3.zero;
                    rigidbody.isKinematic = true;
                }

                // Upright on its throw's heading: it no longer tumbles, and a box left pitched
                // along its launch arc would be drawn half inside the floor it rests on.
                body.Transform.SetPositionAndRotation(
                    target, Quaternion.Euler(0f, body.Transform.eulerAngles.y, 0f));
            }

            _deployables[id] = new DeployableGlide
            {
                From = body.Transform.position,
                To = target,
                StartedAt = Time.time,
            };
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

            if (IsGrenade(message.Kind))
            {
                Debug.Log($"[net] grenade {message.ProjectileId} from actor "
                          + $"{message.OwnerActorId}: {result.Action}, age "
                          + $"{result.FastForwardedTicks} ticks, remaining "
                          + $"{result.RemainingLifetimeSeconds:F2}s, position "
                          + $"{result.Position.X:F2},{result.Position.Y:F2},{result.Position.Z:F2}");
            }

            if (result.Action == ProjectileApplyAction.Ignore)
            {
                Despawn(result.ProjectileId);
                return;
            }

            if (result.Action == ProjectileApplyAction.Spawn) ProjectilesSpawned++;

            if (result.Action == ProjectileApplyAction.ReSeat
                && _spawned.TryGetValue(result.ProjectileId, out IProjectileBody live)
                && live != null && live.Exists
                && DeployableKinds.Is(message.Kind))
            {
                SeatDeployable(result.ProjectileId, live, ToUnity(result.Position), spawning: false);
                return;
            }

            if (result.Action == ProjectileApplyAction.ReSeat
                && _spawned.TryGetValue(result.ProjectileId, out live)
                && live != null && live.Exists)
            {
                live.Transform.SetPositionAndRotation(
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
                if (IsGrenade(message.Kind))
                    Debug.LogError("[net] grenade spawn has no client prefab; the explosion can "
                                   + "arrive but the thrown grenade cannot be drawn.");
                return;
            }

            GameObject instance = Object.Instantiate(
                prefab, ToUnity(result.Position), RotationFor(result.Velocity));

            IProjectileBody projectile = NetClientBindings.ResolveProjectileBody(instance);
            if (projectile != null)
            {
                // source stays null on purpose: it is the field Weapon.SpawnProjectile sets to
                // make a projectile do real damage, and a cosmetic instance must never carry it.
                // Since C4b the seam simply does not expose it, so that is structural rather
                // than a comment asking nicely.
                projectile.SetNetProjectileId(result.ProjectileId);

                if (DeployableKinds.Is(message.Kind))
                {
                    SeatDeployable(result.ProjectileId, projectile, ToUnity(result.Position), spawning: true);
                }
                else
                {
                    // Instantiate happens before Projectile.Start in this frame. Apply the actual
                    // authoritative velocity now as well as on later re-seats so rigidbody-backed
                    // grenades do not sit invisibly at the spawn point for their first rendered
                    // step.
                    projectile.ApplyNetVelocity(ToUnity(result.Velocity));
                }

                // A grenade's fuse counts from the launch tick, so both sides detonate on the
                // same integer rather than on whichever frame each side's own float crossed.
                //
                // The subtraction is clamped because these are unsigned: early in a match
                // CurrentTick can be smaller than the catch-up, and an underflow would wrap to
                // roughly four billion and hand the grenade a fuse that never fires. A clamp
                // costs one comparison and the worst case is a grenade that detonates slightly
                // early on a client during the first two seconds of a round.
                // "Does this thing have a fuse", asked of the projectile itself. It replaces a
                // `projectile is GrenadeProjectile` type test this assembly may no longer write,
                // and is the better question: a second fused type would have needed a second
                // branch here and now needs none.
                {
                    uint now = NetContext.CurrentTick;
                    var caughtUp = (uint)result.FastForwardedTicks;
                    projectile.TryArmFuse(now >= caughtUp ? now - caughtUp : 0u);
                }

                _spawned[result.ProjectileId] = projectile;
            }
            else if (IsGrenade(message.Kind))
            {
                Debug.LogError($"[net] grenade prefab '{prefab.name}' has no projectile body; "
                               + "the instantiated mesh cannot follow authoritative flight.");
            }
        }

        private GameObject PrefabFor(ProjectileKind kind)
        {
            var index = (int)kind;
            if (_prefabsByKind == null || index < 0 || index >= _prefabsByKind.Length) return null;

            return _prefabsByKind[index];
        }

        private static bool IsGrenade(ProjectileKind kind)
            => kind == ProjectileKind.Grenade || kind == ProjectileKind.Spearhead;

        /// <summary>
        /// Drops a projectile and lets whatever it has to say be heard first.
        /// </summary>
        /// <remarks>
        /// <b>Two things end a client's grenade on the same tick, and only one of them makes a
        /// sound.</b> The grenade counts its own fuse down from the launch tick and calls
        /// <c>Explode</c>, which plays the report; this presenter counts the same three seconds
        /// from the spawn message and destroys the object. Which runs first is frame order, and
        /// <c>[trace-grenade]</c> showed the instance dying at 3.1 s rather than at the ten
        /// seconds <c>Explode</c> would have held it — so on a client <c>Explode</c> was not
        /// running at all. The blast survived that because it is drawn from <c>S_EXPLOSION</c> by
        /// <c>NetClientExplosionPresenter</c>, a separate path that always runs; the report had no
        /// such second home, so a client heard nothing.
        /// </remarks>
        private void Despawn(ushort projectileId)
        {
            _deployables.Remove(projectileId);

            if (!_spawned.TryGetValue(projectileId, out IProjectileBody projectile)) return;

            _spawned.Remove(projectileId);
            if (projectile == null || !projectile.Exists) return;

            GameObject instance = projectile.GameObject;

            AudioSource report = instance.GetComponent<AudioSource>();
            if (report != null && report.clip != null)
            {
                if (!report.isPlaying) report.Play();

                // The source dies with the GameObject, and Destroy takes effect at the end of the
                // frame -- destroying here would cut the report off before a single sample is
                // heard, which is exactly what an earlier version of this fix did. Holding the
                // object for the clip's own length is what makes the sound audible at all; the
                // renderers are already off, so nothing is drawn while it plays.
                Object.Destroy(instance, report.clip.length);
                return;
            }

            Object.Destroy(instance);
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
