using System;
using System.Collections.Generic;
using Ironfront.Net.Replication.Match;
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
            NetServerBindings.CapturePoints = new SceneCapturePoints();
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

    /// <summary>Adapts the scene's <c>CapturePoint</c> components to <see cref="ICapturePointDirectory"/>.</summary>
    /// <remarks>
    /// <para>
    /// Phase-V8 tasks 2 and 3. The array is captured once at <see cref="Bind"/> — unlike
    /// <see cref="ActorManagerSpawnPoints"/>, which re-reads per call — because these indices
    /// ARE the wire ids and re-resolving them mid-match would renumber the flags underneath
    /// every connected client. A map change tears the server down and rebuilds it, which is
    /// where the rebind belongs.
    /// </para>
    /// </remarks>
    internal sealed class SceneCapturePoints : ICapturePointDirectory
    {
        private CapturePoint[] _points = Array.Empty<CapturePoint>();

        public int Count => _points.Length;

        public int Bind(Transform[] authored, out bool discovered, out int skipped)
        {
            discovered = false;
            skipped = 0;

            if (authored != null && authored.Length > 0)
            {
                var resolved = new List<CapturePoint>(authored.Length);
                for (int i = 0; i < authored.Length; i++)
                {
                    Transform slot = authored[i];
                    CapturePoint point = slot != null ? slot.GetComponent<CapturePoint>() : null;
                    if (point == null)
                    {
                        skipped++;
                        continue;
                    }

                    resolved.Add(point);
                }

                _points = resolved.ToArray();
                return _points.Length;
            }

            // D7's fallback. Ordered by name, ordinal: FindObjectsOfType makes no ordering
            // promise at all, and an id order that changes between two runs of the same build
            // is a client/server flag mismatch nobody can reproduce.
            CapturePoint[] found = UnityEngine.Object.FindObjectsOfType<CapturePoint>();
            Array.Sort(found, CompareByName);

            _points = found;
            discovered = found.Length > 0;
            return found.Length;
        }

        public CapturePointDefinition GetDefinition(int index)
        {
            CapturePoint point = _points[index];

            // canBeCaptured == false is an HQ: capture speed of zero, so CapturePointState.Tick
            // moves it nowhere while it still counts for spawning, bleed and elimination.
            float speed = point.canBeCaptured ? point.captureSpeed : 0f;

            return new CapturePointDefinition(
                point.transform.position, point.captureRange, speed, point.name);
        }

        public void ApplyAuthoritativeOwner(int index, int spawnPointOwner, float control, bool contested)
        {
            CapturePoint point = _points[index];
            if (point == null) return;

            point.ApplyAuthoritativeOwner(spawnPointOwner, control, contested);
        }

        public bool RefreshPresence(int index, ReadOnlySpan<ActorPresence> actors)
        {
            CapturePoint point = _points[index];
            if (point == null) return false;

            return point.RefreshPresence(actors);
        }

        /// <summary>
        /// Every scene spawn point owned by <paramref name="team"/>, counted exactly the way
        /// <c>ActorManager.HasSpawnPoint</c> counts them (D10) — including uncapturable HQs,
        /// which is what keeps a team with a base alive.
        /// </summary>
        public int CountSpawnPointsOwnedBy(int team)
        {
            ActorManager manager = ActorManager.instance;
            SpawnPoint[] points = manager != null ? manager.spawnPoints : null;
            if (points == null) return 0;

            int count = 0;
            for (int i = 0; i < points.Length; i++)
            {
                SpawnPoint point = points[i];
                if (point != null && point.owner == team) count++;
            }

            return count;
        }

        private static int CompareByName(CapturePoint a, CapturePoint b)
            => string.CompareOrdinal(a != null ? a.name : string.Empty, b != null ? b.name : string.Empty);
    }
}
