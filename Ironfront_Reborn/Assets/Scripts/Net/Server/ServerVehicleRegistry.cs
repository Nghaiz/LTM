using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The Unity companion to the engine-free <see cref="VehicleRegistry"/>: it holds the
    /// game-side vehicle references that library cannot, and owns the bot seat claims. V4
    /// tasks 2 and 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors <see cref="ServerActorRegistry"/>'s shape — a lazily created singleton, cleared
    /// at subsystem registration so a static field does not survive leaving play mode with
    /// domain reload disabled.
    /// </para>
    /// <para>
    /// <b>Every decision is one layer down.</b> This class stores, forwards and subscribes; the
    /// registry, the arbiter, the burn clock and the claims table are all engine-free and all
    /// tested in CI. That split is what V4-D15 draws at <i>decides</i> versus <i>applies</i>.
    /// </para>
    /// </remarks>
    public sealed class ServerVehicleRegistry
    {
        private static ServerVehicleRegistry _instance;

        private readonly VehicleRegistry _registry = new VehicleRegistry();
        private readonly BotSeatClaims _claims = new BotSeatClaims();

        private readonly Dictionary<ushort, NetServerVehicle> _vehicles =
            new Dictionary<ushort, NetServerVehicle>(ProtocolConstants.MAX_VEHICLES);

        // GameObject -> id, so Vehicle.cs's role guards can find their own network id without
        // Assembly-CSharp ever holding one. A Vehicle has no id field and giving it one would
        // put a wire concern on an authored prefab.
        private readonly Dictionary<GameObject, ushort> _byGameObject =
            new Dictionary<GameObject, ushort>(ProtocolConstants.MAX_VEHICLES);

        private ServerActorRegistry _actorRegistrySubscribedTo;

        public static ServerVehicleRegistry Instance
            => _instance ?? (_instance = new ServerVehicleRegistry());

        /// <summary>The authoritative record, for the tick loop's capture and the arbiter.</summary>
        public VehicleRegistry Registry => _registry;

        /// <summary>The identity-bearing replacement for <c>Vehicle.seatsClaimedByBots</c>.</summary>
        public BotSeatClaims Claims => _claims;

        /// <summary>Vehicles registered right now.</summary>
        public int Count => _registry.LiveCount;

        /// <summary>
        /// Registers a vehicle under the id <c>ServerVehicleLifecycleSink</c> just assigned it.
        /// </summary>
        /// <remarks>
        /// <b>Id 0 is refused, not tolerated.</b> It means the spawn was never replicated — an
        /// unauthored prefab or an exhausted pool — and registering it would put a vehicle in the
        /// capture buffer that no client has been told exists, which is trap 8 one entity type
        /// over.
        /// </remarks>
        /// <returns>False when the id is 0, out of range, or already registered.</returns>
        public bool Register(
            ushort vehicleId, GameObject owner, IGameplayVehicleSource source)
        {
            if (vehicleId == 0 || owner == null || source == null || !source.Exists) return false;
            if (_vehicles.ContainsKey(vehicleId)) return false;

            if (!VehicleIds.TryGetKind(source.NetworkTypeId, out VehicleKind kind))
                return false;

            var pose = new NetServerVehicle(vehicleId, source);

            VehicleState state = VehicleState.Spawned(
                vehicleId,
                spawnerId: 0,
                kind,
                (byte)Mathf.Clamp(source.SeatCount, 0, VehicleState.MaxSeats),
                // maxHealth, not the live health: the vehicle was instantiated this frame, so
                // the two are equal — but reading the live one would make a re-registration
                // after damage silently reset the ceiling rather than the value.
                source.MaxHealth,
                (byte)Mathf.Clamp(source.OwnerTeam, 0, byte.MaxValue));

            if (!_registry.Add(in state, pose)) return false;

            _vehicles[vehicleId] = pose;
            _byGameObject[owner]  = vehicleId;
            return true;
        }

        /// <summary>Removes a vehicle. Its id is quarantined by the lifecycle sink, not here.</summary>
        public bool Unregister(ushort vehicleId)
        {
            if (!_vehicles.Remove(vehicleId)) return false;

            // Scanned rather than kept in a reverse map: a destroyed GameObject is not a usable
            // dictionary key any more, and a second index that can hold a dead one is exactly
            // the stale-reverse-index failure VehicleRegistry.TryFindSeatOf refuses to build.
            //
            // The key is found first and removed after the loop. Removing inside it is legal on
            // modern .NET and is NOT on the Mono runtime Unity ships, where it invalidates the
            // enumerator — and this runs at most sixteen times per despawn, so there is nothing
            // to win by relying on the difference.
            GameObject doomed = null;
            foreach (KeyValuePair<GameObject, ushort> pair in _byGameObject)
            {
                if (pair.Value != vehicleId) continue;
                doomed = pair.Key;
                break;
            }

            if (doomed != null) _byGameObject.Remove(doomed);

            _claims.ReleaseVehicle(vehicleId);
            _registry.Remove(vehicleId);
            return true;
        }

        /// <summary>
        /// The network id of a vehicle GameObject, or 0 when it is not replicated.
        /// </summary>
        /// <remarks>
        /// 0 is a real answer, not a failure: offline, on a client, and for a vehicle whose
        /// prefab is unauthored or whose spawn found the id pool empty. Every caller reads it as
        /// "there is no network here" and falls through to the shipped behaviour.
        /// </remarks>
        public ushort NetworkIdOf(GameObject vehicle)
            => vehicle != null && _byGameObject.TryGetValue(vehicle, out ushort id) ? id : (ushort)0;

        /// <summary>Finds the game-side vehicle behind an id.</summary>
        public bool TryFind(ushort vehicleId, out IGameplayVehicleSource source)
        {
            if (_vehicles.TryGetValue(vehicleId, out NetServerVehicle pose) && pose.Exists)
            {
                source = pose.Source;
                return true;
            }

            source = null;
            return false;
        }

        /// <summary>
        /// Captures every live vehicle into the wire buffer. Called once per snapshot from
        /// <c>ServerTickLoop.RunSnapshotStage</c>.
        /// </summary>
        public void CaptureInto(VehicleWorldSnapshot world, uint serverTick)
            => _registry.CaptureInto(world, serverTick);

        /// <summary>
        /// Subscribes to actor despawns so a dying bot gives its seat claims back immediately.
        /// </summary>
        /// <remarks>
        /// <b>This is the half of V4-D10 that fixes the bug.</b> The shipped counter is drained
        /// by a 10-second whole-vehicle timer that names nobody, so two bots claiming and one
        /// dying leaves the count permanently wrong. <c>ActorUnregistered</c> already fires;
        /// nothing was listening.
        /// </remarks>
        public void SubscribeTo(ServerActorRegistry actors)
        {
            if (actors == null || ReferenceEquals(actors, _actorRegistrySubscribedTo)) return;

            Unsubscribe();
            actors.ActorUnregistered += OnActorUnregistered;
            _actorRegistrySubscribedTo = actors;
        }

        /// <summary>Drops the subscription. Called from <c>ServerTickLoop.Unbind</c>.</summary>
        public void Unsubscribe()
        {
            if (_actorRegistrySubscribedTo == null) return;

            _actorRegistrySubscribedTo.ActorUnregistered -= OnActorUnregistered;
            _actorRegistrySubscribedTo = null;
        }

        /// <summary>Forgets every vehicle and every claim. For a round boundary.</summary>
        public void Clear()
        {
            _vehicles.Clear();
            _byGameObject.Clear();
            _claims.Clear();
            _registry.Clear();
        }

        private void OnActorUnregistered(ushort actorId)
        {
            _claims.Release(actorId);

            // An actor that vanishes while seated leaves its seat booked forever otherwise, and
            // the arbiter would refuse every later request for that seat on RejectedOccupied
            // naming an actor that no longer exists.
            if (_registry.TryFindSeatOf(actorId, out ushort vehicleId, out byte seatIndex))
                _registry.TrySetOccupant(vehicleId, seatIndex, 0);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad()
        {
            _instance?.Unsubscribe();
            _instance?.Clear();
        }
    }
}
