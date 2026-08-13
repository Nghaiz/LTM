using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Every <see cref="NetServerActor"/> currently in the scene, and the source of the actor
    /// set a snapshot is built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OWNER: Dev C.
    /// </para>
    /// <para>
    /// A registry rather than a scene scan: <c>FindObjectsOfType</c> allocates an array on
    /// every call, which at 20 Hz is a garbage source in the one loop that must not have one
    /// (M1 criterion 9). Actors add themselves in <c>OnEnable</c> and remove themselves in
    /// <c>OnDisable</c>, so the list is correct across additive scene loads and pooled spawns
    /// without anything having to look for them.
    /// </para>
    /// <para>
    /// It is a static instance rather than a component because actors register from
    /// <c>OnEnable</c>, which can run before any bootstrap in an additively loaded scene.
    /// Depending on component ordering there would mean a silently unregistered actor — one
    /// that exists on the server and never appears on any client.
    /// </para>
    /// </remarks>
    public sealed class ServerActorRegistry
    {
        private static ServerActorRegistry _instance;

        private readonly List<NetServerActor> _actors =
            new List<NetServerActor>(ProtocolConstants.MAX_ACTORS);

        private ushort _nextAutoId = 1;

        public static ServerActorRegistry Instance => _instance ?? (_instance = new ServerActorRegistry());

        /// <summary>Registered actors, in registration order.</summary>
        public IReadOnlyList<NetServerActor> Actors => _actors;

        public int Count => _actors.Count;

        /// <summary>
        /// Adds an actor, assigning an id when it does not have one.
        /// </summary>
        /// <remarks>
        /// Id 0 means "unassigned", which is the value every prefab has until somebody sets it
        /// in the inspector. Auto-assigning is what stops a scene full of default-valued
        /// prefabs from producing sixty-four actors that all claim to be actor 0 — a state in
        /// which the delta encoder's baseline lookup matches the wrong actor and clients see
        /// one player teleporting between every spawn point.
        /// </remarks>
        public void Register(NetServerActor actor)
        {
            if (actor == null || _actors.Contains(actor)) return;

            if (actor.ActorId == 0) actor.ActorId = NextAutoId();
            else if (TryFind(actor.ActorId, out NetServerActor existing) && existing != actor)
            {
                Debug.LogError(
                    $"[net] actor id {actor.ActorId} on '{actor.name}' is already taken by "
                    + $"'{existing.name}'. Reassigning; fix the duplicate in the scene.");
                actor.ActorId = NextAutoId();
            }

            if (_actors.Count >= ProtocolConstants.MAX_ACTORS)
            {
                Debug.LogError(
                    $"[net] refusing to register '{actor.name}': already at MAX_ACTORS "
                    + $"({ProtocolConstants.MAX_ACTORS}). The snapshot actorCount is a u8 and "
                    + "the id quarantine needs the spare slots.");
                return;
            }

            _actors.Add(actor);
        }

        public void Unregister(NetServerActor actor)
        {
            if (actor == null) return;

            actor.Release();
            _actors.Remove(actor);
        }

        public bool TryFind(ushort actorId, out NetServerActor actor)
        {
            for (int i = 0; i < _actors.Count; i++)
            {
                if (_actors[i] == null || _actors[i].ActorId != actorId) continue;

                actor = _actors[i];
                return true;
            }

            actor = null;
            return false;
        }

        /// <summary>Hands an unclaimed player slot to a joining connection.</summary>
        public bool TryClaimPlayerSlot(out NetServerActor actor)
        {
            for (int i = 0; i < _actors.Count; i++)
            {
                NetServerActor candidate = _actors[i];
                if (candidate == null || !candidate.AvailableForPlayers || candidate.IsClaimed)
                    continue;

                candidate.Claim();
                actor = candidate;
                return true;
            }

            actor = null;
            return false;
        }

        public void ReleaseSlot(NetServerActor actor)
        {
            if (actor != null) actor.Release();
        }

        /// <summary>
        /// Rebuilds <paramref name="world"/> from the live actor set. Allocation-free: the
        /// snapshot's array is fixed-capacity and <see cref="WorldSnapshot.Clear"/> only moves
        /// the fence.
        /// </summary>
        public void CaptureInto(WorldSnapshot world)
        {
            if (world == null) return;

            uint tick = world.ServerTick;
            world.Clear();
            world.ServerTick = tick;

            for (int i = 0; i < _actors.Count; i++)
            {
                NetServerActor actor = _actors[i];
                if (actor == null || !actor.isActiveAndEnabled) continue;

                if (!world.Add(actor.Capture())) return; // full; the guard in Register makes this unreachable
            }
        }

        private ushort NextAutoId()
        {
            while (TryFind(_nextAutoId, out NetServerActor _)) _nextAutoId++;
            return _nextAutoId++;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => _instance = null;
    }
}
