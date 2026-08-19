using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The bodies a dedicated server hands to joining connections — one per transport slot,
    /// created at server start. Phase-3A.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The defect this closes.</b> <c>ServerActorRegistry.TryClaimPlayerSlot</c> walks the
    /// registry for a body with <c>AvailableForPlayers</c> set. Exactly one such body existed in
    /// the entire project — <c>Player Fps Actor</c>, the local player's own avatar, instantiated
    /// by <c>GameManager</c> for a process that has a screen. A dedicated server therefore had
    /// zero claimable bodies of its own and answered connection one, never mind connection two,
    /// with <c>DisconnectReason.ServerFull</c>. Meanwhile the startup log advertised
    /// <c>Config.MaxConnections</c> slots. Sixteen transport slots, one player slot, nothing
    /// comparing them.
    /// </para>
    /// <para>
    /// <b>Eager, not lazy.</b> Every body exists from server start. A pool that grew on demand
    /// would have no count to compare against <c>Config.MaxConnections</c> until it was full,
    /// which is exactly the moment the comparison stops catching anything — and the comparison
    /// is the whole point. The cost is <c>MaxConnections</c> idle bodies on a process that
    /// renders nothing.
    /// </para>
    /// <para>
    /// <b>It never short-spawns.</b> A pool that quietly created fewer bodies than it was asked
    /// for would reproduce the original defect one layer up: a number in a log that no longer
    /// describes the world. If the request does not fit under
    /// <see cref="ProtocolConstants.MAX_ACTORS"/>, or the factory fails part-way, nothing is
    /// left behind and the failure is reported with both numbers in it.
    /// </para>
    /// <para>
    /// <b>The factory is a delegate, not a prefab.</b> Building a body means calling
    /// <c>Actor.SetTeam</c>, which lives in <c>Assembly-CSharp</c> and cannot be named here —
    /// see <c>NetServerBindings.PlayerBodyFactory</c>. It is also what lets the EditMode suite
    /// drive this type with no prefab, no scene and no game.
    /// </para>
    /// </remarks>
    public sealed class ServerPlayerSlotPool
    {
        private readonly List<NetServerActor> _bodies = new List<NetServerActor>(ProtocolConstants.MAX_PLAYERS);

        /// <summary>Bodies this pool created and still owns.</summary>
        public int SlotCount => _bodies.Count;

        /// <summary>True once <see cref="Fill"/> has succeeded and before <see cref="Clear"/>.</summary>
        public bool IsFilled => _bodies.Count > 0;

        /// <summary>
        /// Creates <paramref name="slotCount"/> claimable bodies, or none at all.
        /// </summary>
        /// <param name="slotCount">
        /// How many connections this server admits — <c>Config.MaxConnections</c>, the same
        /// field the transport is started with. One source, so the two cannot drift apart.
        /// </param>
        /// <param name="bodyFactory">
        /// Builds one body for the given team, or returns <see langword="null"/> when it cannot.
        /// Teams alternate 0, 1, 0, 1 across the pool.
        /// </param>
        /// <param name="registry">
        /// Where the bodies register themselves, consulted for the headroom check. Defaults to
        /// <see cref="ServerActorRegistry.Instance"/>.
        /// </param>
        /// <returns>True when every requested body was created.</returns>
        public bool Fill(int slotCount, Func<byte, NetServerActor> bodyFactory, ServerActorRegistry registry = null)
        {
            registry = registry ?? ServerActorRegistry.Instance;

            if (IsFilled)
            {
                Debug.LogError(
                    $"[net] player slot pool already holds {_bodies.Count} bodies; refusing to "
                    + "fill twice. Something is starting the server more than once.");
                return false;
            }

            if (slotCount <= 0)
            {
                Debug.LogError(
                    $"[net] refusing to build {slotCount} player slots. A server that admits "
                    + "nobody is a configuration mistake, not a valid deployment.");
                return false;
            }

            if (bodyFactory == null)
            {
                Debug.LogError(
                    "[net] no player-body factory registered, so this server has no claimable "
                    + "bodies and will refuse every connection with ServerFull. "
                    + "NetServerBindings.PlayerBodyFactory is installed by IronfrontNetBindings "
                    + "at BeforeSceneLoad; a scene with no ActorManager has nothing to build "
                    + "bodies from.");
                return false;
            }

            // Headroom, before anything is created. The registry is shared with the map's bots,
            // and the snapshot's actorCount is a u8 with the id quarantine holding the spare
            // slots -- Register() would start refusing part-way through the loop, leaving a pool
            // smaller than the number the startup log is about to print.
            int alreadyRegistered = registry.Count;
            if (alreadyRegistered + slotCount > ProtocolConstants.MAX_ACTORS)
            {
                Debug.LogError(
                    $"[net] {slotCount} player slots will not fit: {alreadyRegistered} actors "
                    + $"are already registered and MAX_ACTORS is {ProtocolConstants.MAX_ACTORS}. "
                    + "Lower MaxConnections or the bot count. No slots were created — the server "
                    + "will refuse every connection rather than admit a number nobody chose.");
                return false;
            }

            for (int i = 0; i < slotCount; i++)
            {
                var team = (byte)(i % 2);
                NetServerActor body = bodyFactory(team);

                if (body == null)
                {
                    Debug.LogError(
                        $"[net] player-body factory returned nothing for slot {i} of "
                        + $"{slotCount}. Rolling back the {_bodies.Count} already created; a "
                        + "half-filled pool is the drift this pool exists to prevent.");
                    Clear();
                    return false;
                }

                body.Team = team;
                body.MarkAvailableForPlayers();
                _bodies.Add(body);
            }

            return true;
        }

        /// <summary>Destroys every body this pool created and forgets them.</summary>
        /// <remarks>
        /// Destroying unregisters each body through <c>NetServerActor.OnDisable</c>, which is
        /// what returns its actor id to the quarantine pool. Dropping the list without
        /// destroying would leave <see cref="ProtocolConstants.MAX_ACTORS"/> worth of claimable
        /// bodies standing in a stopped server's scene.
        /// </remarks>
        public void Clear()
        {
            for (int i = 0; i < _bodies.Count; i++)
            {
                NetServerActor body = _bodies[i];
                if (body == null) continue;

                // DestroyImmediate outside play mode: an EditMode test has no frame boundary for
                // a deferred destroy to land on, so OnDisable -- the thing that unregisters --
                // would never run and the next test would inherit this one's actors.
                if (Application.isPlaying) UnityEngine.Object.Destroy(body.gameObject);
                else UnityEngine.Object.DestroyImmediate(body.gameObject);
            }

            _bodies.Clear();
        }
    }
}
