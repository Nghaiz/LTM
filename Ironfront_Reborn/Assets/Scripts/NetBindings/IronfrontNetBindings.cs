using Ironfront.Net.Unity.Server;
using UnityEngine;

namespace Ironfront.Net.Unity.Bindings
{
    /// <summary>
    /// Implements the seams <c>Ironfront.Net.Unity.Server</c> declares, in terms of the original
    /// game's own types, and installs them at startup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This file deliberately lives OUTSIDE any assembly definition, so it compiles into
    /// <c>Assembly-CSharp</c> alongside <c>Actor</c>, <c>Weapon</c>, <c>ActorManager</c> and
    /// <c>SpawnPoint</c>. That is the only assembly that can see both halves: predefined
    /// assemblies are compiled last and automatically reference every asmdef, while no asmdef
    /// can reference back into them.
    /// </para>
    /// <para>
    /// Moving it into an asmdef, or adding an <c>.asmdef</c> anywhere above it, breaks the
    /// build — the game types stop resolving and nothing registers.
    /// </para>
    /// </remarks>
    internal static class IronfrontNetBindings
    {
        /// <summary>
        /// Runs before the first scene's objects exist, so no <c>NetServerActor.Awake</c> can
        /// resolve its source before the resolver is in place. Re-runs on every entry into play
        /// mode, which is what keeps this correct when domain reload is disabled.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            NetServerBindings.ActorSourceResolver = ResolveActorSource;
            NetServerBindings.SpawnPoints = new ActorManagerSpawnPoints();
        }

        /// <summary>
        /// The <c>GetComponent&lt;Actor&gt;()</c> that <c>NetServerActor.Awake</c> used to do
        /// itself, now performed here. Null for a replicated object that is not an actor — a
        /// prop, a bare test rig — which is the case the caller's fallback fields exist for.
        /// </summary>
        private static IGameplayActorSource ResolveActorSource(GameObject gameObject)
        {
            if (gameObject == null) return null;

            Actor actor = gameObject.GetComponent<Actor>();
            return actor != null ? new ActorGameplaySource(actor) : null;
        }
    }

    /// <summary>Adapts one <c>Actor</c> to <see cref="IGameplayActorSource"/>.</summary>
    internal sealed class ActorGameplaySource : IGameplayActorSource
    {
        private readonly Actor _actor;

        internal ActorGameplaySource(Actor actor) => _actor = actor;

        /// <summary>
        /// The <c>UnityEngine.Object</c> null check, kept on this side of the seam where it
        /// still means "the native half is alive".
        /// </summary>
        public bool Exists => _actor != null;

        public float Health
        {
            get => _actor.health;
            set => _actor.health = value;
        }

        public bool IsDead
        {
            get => _actor.dead;
            set => _actor.dead = value;
        }

        /// <summary>
        /// The stagger half of <c>Actor.Damage</c>, without its health or death half.
        /// </summary>
        /// <remarks>
        /// The clamp and the knock-over threshold are copied from <c>Actor.Damage</c> deliberately
        /// -- they are the game's rules, and this is the game's side of the seam. A seated actor
        /// in an enclosed vehicle does not stagger, for the same reason it does not there.
        /// </remarks>
        public void ApplyBalanceDamage(float balanceDamage)
        {
            if (_actor == null || _actor.dead) return;
            if (_actor.IsSeated() && _actor.seat.enclosed) return;

            _actor.balance = Mathf.Max(_actor.balance - balanceDamage, -100f);

            if (_actor.balance < 0f) _actor.KnockOver(Vector3.up * 100f);
        }

        /// <summary>
        /// <c>Actor.SpawnWeapon</c> stamps <c>WeaponManager.NetworkIdOf(entry)</c> onto
        /// <c>Weapon.NetworkId</c> at spawn, and <c>activeWeapon</c> is whichever one is
        /// unholstered. Holstered-everything reports false, not zero.
        /// </summary>
        public bool TryGetActiveWeaponNetworkId(out byte networkId)
        {
            Weapon weapon = _actor.activeWeapon;
            if (weapon == null)
            {
                networkId = 0;
                return false;
            }

            networkId = weapon.NetworkId;
            return true;
        }
    }

    /// <summary>Adapts <c>ActorManager.spawnPoints</c> to <see cref="ISpawnPointDirectory"/>.</summary>
    /// <remarks>
    /// <c>ActorManager.instance</c> is read per call rather than captured, because the array is
    /// rebuilt by <c>FindObjectsOfType&lt;SpawnPoint&gt;</c> on scene load and a captured
    /// reference would go stale across a map change — which is exactly the moment respawning
    /// matters.
    /// </remarks>
    internal sealed class ActorManagerSpawnPoints : ISpawnPointDirectory
    {
        public int Count
        {
            get
            {
                SpawnPoint[] points = Points();
                return points != null ? points.Length : 0;
            }
        }

        public bool IsEligible(int index, int team)
        {
            SpawnPoint point = At(index);
            if (point == null) return false;

            // owner < 0 means "any team", which is how SpawnPoint.owner already defines it.
            return point.owner < 0 || point.owner == team;
        }

        public Vector3 GetSpawnPosition(int index) => At(index).GetSpawnPosition();

        private static SpawnPoint[] Points()
        {
            ActorManager manager = ActorManager.instance;
            return manager != null ? manager.spawnPoints : null;
        }

        private static SpawnPoint At(int index)
        {
            SpawnPoint[] points = Points();
            if (points == null || index < 0 || index >= points.Length) return null;
            return points[index];
        }
    }
}
