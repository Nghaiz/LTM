using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Server;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Every <see cref="NetServerActor"/> currently in the scene, and the source of the actor
    /// set a snapshot is built from.
    /// </summary>
    /// <remarks>
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

        /// <summary>
        /// Raised with an actor's id once it has left the world, whatever removed it.
        /// </summary>
        /// <remarks>
        /// The per-pair tables keyed on (viewer, target) are only cleaned up on player
        /// disconnect, which covers players and nothing else. A bot that is disabled or pooled
        /// unregisters itself here and used to leave its hitbox ring, interest rows and
        /// spawn-ack rows behind — the exact trap-2 leak that machinery exists to prevent, and
        /// enough to have the state audit report the world as unclean at the end of a round.
        /// </remarks>
        public event Action<ushort> ActorUnregistered;

        public void Unregister(NetServerActor actor)
        {
            if (actor == null) return;

            ushort actorId = actor.ActorId;

            actor.Release();
            _actors.Remove(actor);

            // Back to the pool, which quarantines it rather than handing it straight out again.
            if (_idPool != null) _idPool.Release(actorId, NowSeconds());

            ActorUnregistered?.Invoke(actorId);
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

        /// <summary>
        /// Points auto-id allocation at the match's <see cref="ActorIdPool"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The pool was built for phase-02 trap 2 — an id must cool in quarantine before it can
        /// be handed out again, or a reused id inherits the previous incarnation's spawn rows
        /// and the gate reports "already announced" for an actor no client has been told about.
        /// It was constructed, wired into the audit, and then never called: ids actually came
        /// from the private counter below, which has no quarantine and never resets. Two
        /// allocators for one id space, and the audit's ActorIdsInUse was structurally always
        /// zero, so it could not have detected anything.
        /// </para>
        /// <para>
        /// A registry with no pool (a bare test scene, anything without a MatchController)
        /// keeps the counter, so this is additive.
        /// </para>
        /// </remarks>
        public void UseIdPool(ActorIdPool pool) => _idPool = pool;

        private ActorIdPool _idPool;

        private ushort NextAutoId()
        {
            if (_idPool != null && _idPool.TryAcquire(NowSeconds(), out ushort pooled)) return pooled;

            while (TryFind(_nextAutoId, out NetServerActor _)) _nextAutoId++;
            return _nextAutoId++;
        }

        private static float NowSeconds() => (float)Time.realtimeSinceStartupAsDouble;

        /// <summary>
        /// Drops every actor and every id-allocation decision, in place.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called at <c>SubsystemRegistration</c> so a play session started with domain reload
        /// disabled does not inherit the previous session's actors.
        /// </para>
        /// <para>
        /// <b>It clears the instance rather than nulling the reference, and that distinction is
        /// the whole point.</b> A MonoBehaviour's field initializers and constructor run during
        /// scene deserialisation, which is <i>before</i> <c>SubsystemRegistration</c>. Anything
        /// that captured <c>Instance</c> that early held a reference this method used to throw
        /// away: actors then registered from <c>OnEnable</c> into a second, freshly-created
        /// registry, and the early capture kept scanning an empty one forever. The client track reproduced
        /// exactly that 3x from cold starts in round 9 -- <c>ServerTickLoop</c>'s constructor
        /// handed <c>ServerCombatBridge</c> and <c>ServerActorDamageSink</c> a registry with 0
        /// actors while 41 registered elsewhere, so <c>BuildTargets</c> handed
        /// <c>LagCompensator</c> an empty candidate span and no bullet in the game could hit
        /// anything (ShotsResolved 507, ShotsHit 0, over ~31 000 ticks).
        /// </para>
        /// <para>
        /// Fixing it at the capture site would have fixed those two call sites. Clearing in
        /// place fixes the shape: reference identity is now stable for the process lifetime, so
        /// no future early capture can split the registry again. There is nothing to gain from
        /// a fresh object -- nothing compares registry instances, and every field it owns is
        /// reset below.
        /// </para>
        /// </remarks>
        /// <para>
        /// Private on purpose. Mid-session this would drop live <see cref="ActorUnregistered"/>
        /// subscribers and orphan every registered actor; the only caller that should ever
        /// exist is the reset below, which runs before any scene object awakes.
        /// </para>
        private void Clear()
        {
            _actors.Clear();
            _nextAutoId = 1;
            _idPool = null;
            ActorUnregistered = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => _instance?.Clear();
    }
}
