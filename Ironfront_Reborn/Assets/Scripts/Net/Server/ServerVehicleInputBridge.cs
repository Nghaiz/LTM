using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Server;
using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Puts an accepted <c>C_VEHICLE_INPUT</c> where a vehicle can read it: installs a
    /// network input source on the driver's controller for as long as they are driving,
    /// and feeds it the authority's current axes once per tick. V5 task 5.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the wiring nothing did.</b> Before V5,
    /// <c>grep -rn "SetInputSource\|NetInputSource"</c> across the repository returned the
    /// definition, one comment and the test project — no production call site at all.
    /// Nothing noticed, because server movement bypasses the controller entirely
    /// (<c>ServerPlayer</c> drives <c>NetMovementAgent</c> and <c>MovementSimulation</c>). But
    /// all four vehicles PULL through <c>Driver().controller.CarInput()</c> /
    /// <c>HelicopterInput()</c>, so the moment a networked player drives, the vehicle reads
    /// whatever source the controller happens to hold — which on a headless build is a
    /// keyboard that is not there.
    /// </para>
    /// <para>
    /// <b>No new <c>ActorController</c> subclass (V5-D7).</b> Introducing one would trip
    /// <c>Actor.aiControlled</c>, which is frozen in <c>Awake</c> from
    /// <c>controller.GetType() == typeof(AiActorController)</c> and then read by UI, LOD and
    /// weapon culling. Extending the existing <see cref="IInputSource"/> seam does not go near
    /// it, and a test pins that.
    /// </para>
    /// <para>
    /// <b>Both readings of the four axis slots are published, and that is not ambiguity.</b>
    /// <c>C_VEHICLE_INPUT</c> has four generic axis slots whose meaning is per
    /// <c>VehicleKind</c>: a car fills the first two with steer and throttle, a helicopter fills
    /// all four with its control vector. The receiver hands the
    /// source both interpretations because a car never reads the helicopter members and a
    /// helicopter never reads <c>MoveX</c>/<c>MoveZ</c> — so the vehicle picks, and this class
    /// does not have to know the kind or get it wrong.
    /// </para>
    /// <para>
    /// <b>The hold window belongs to <see cref="VehicleInputAuthority"/>, not here</b> (V5-D11).
    /// This pump asks it every tick and centres the source when it says no, so a driver whose
    /// connection stalls coasts to a stop rather than driving into the sea at full throttle.
    /// </para>
    /// </remarks>
    internal sealed class ServerVehicleInputBridge : IVehicleInputHandler
    {
        private readonly VehicleInputAuthority _authority;
        private readonly ServerActorRegistry _actors;
        private readonly Func<uint> _currentTick;

        // V6 task 2. The turret half of C_VEHICLE_INPUT lands here rather than in the per-tick
        // pump because it is a TARGET, not a value to apply: recording it at accept time and
        // walking toward it on the fixed step is what keeps traverse a property of the turret
        // instead of a property of how often the client sends.
        private readonly ServerTurretAuthority _turrets;

        // Allocated once per driver and reused across their seat entries. A player getting in
        // and out of vehicles for a whole match would otherwise leave one dead source per
        // entry for the GC to sweep, on a path that runs at 30 Hz.
        private readonly Dictionary<ushort, IDriverInputSink> _seated =
            new Dictionary<ushort, IDriverInputSink>(16);

        // Iterated in the per-tick pump. Dictionary enumeration allocates an enumerator on the
        // hot path; a parallel list of keys does not.
        private readonly List<ushort> _drivers = new List<ushort>(16);

        internal ServerVehicleInputBridge(
            VehicleInputAuthority authority, ServerActorRegistry actors, Func<uint> currentTick,
            ServerTurretAuthority turrets = null)
        {
            _authority   = authority ?? throw new ArgumentNullException(nameof(authority));
            _actors      = actors ?? throw new ArgumentNullException(nameof(actors));
            _currentTick = currentTick ?? throw new ArgumentNullException(nameof(currentTick));

            // Optional so every V5 test that builds this with three arguments keeps working. A
            // null one means "nobody is tracking turrets", which is what a loop with no
            // mounted-weapon subsystem looks like -- not a silent failure to aim.
            _turrets = turrets;
        }

        /// <summary>The decode-and-authority half, for the overlay and the phase report.</summary>
        internal VehicleInputAuthority Authority => _authority;

        /// <summary>Drivers currently holding a network input source.</summary>
        internal int DriverCount => _drivers.Count;

        /// <summary>
        /// Controllers a seat entry could not reach. Expected to be zero; non-zero means an
        /// actor entered a driver seat with no <c>FpsActorController</c> on it, and that
        /// vehicle will not respond to its driver at all.
        /// </summary>
        internal long UnreachableControllers { get; private set; }

        /// <inheritdoc />
        public void OnVehicleInput(ClientSession session, in ClampedVehicleInput input)
        {
            // Accept, not TryAccept: the driver's axes go through the unchanged V5-D5 rule while
            // a gunner's turret aim is recorded from whatever seat they are in (V6 task 2).
            // TryAccept alone threw a gunner's whole message away as RefusedNotDriver, which is
            // why no turret aim ever reached a server before this phase.
            VehicleInputAuthority.Acceptance acceptance =
                _authority.Accept(session.ActorId, in input, _currentTick());

            if (acceptance == VehicleInputAuthority.Acceptance.Refused) return;
            if (_turrets == null) return;

            if (!_authority.TryGetTurretTarget(
                    session.ActorId, out ushort vehicleId, out byte seatIndex,
                    out float yaw, out float pitch))
                return;

            // Recorded, never applied. ServerTurretAuthority.Step walks toward it at the turret's
            // own slew rate, so a client asking for a 180-degree snap buys one step's arc.
            _turrets.SetTarget(vehicleId, seatIndex, yaw, pitch);
        }

        /// <summary>
        /// Installs or removes the network input source as an actor takes or leaves a driver
        /// seat. Called for every accepted seat decision.
        /// </summary>
        /// <remarks>
        /// Refusals are ignored on purpose: a refused request changed nothing, and acting on it
        /// is how a client ends up predicting a vehicle the server never put it in.
        /// </remarks>
        internal void OnSeatDecision(in SeatDecision decision)
        {
            if (!decision.Accepted) return;

            if (decision.Result == SeatChangeResult.Entered
                && decision.SeatIndex == VehicleInputAuthority.DriverSeatIndex)
            {
                Install(decision.ActorId);
                return;
            }

            if (decision.Result == SeatChangeResult.Left)
            {
                Remove(decision.ActorId);

                // The turret holds its pose but stops chasing a target nobody is asking for. Left
                // standing, a gunner's last request would keep the gun traversing after they got
                // out -- and would resume mid-swing for whoever climbed in next.
                _turrets?.ClearTarget(decision.VehicleId, decision.SeatIndex);
            }
        }

        /// <summary>Removes an actor's source and forgets its axes. Disconnect and death.</summary>
        internal void Forget(ushort actorId)
        {
            // The turret target is dropped BEFORE the record that names its seat, or there is
            // nothing left to say which turret to stop.
            if (_turrets != null
                && _authority.TryGetTurretTarget(
                       actorId, out ushort vehicleId, out byte seatIndex, out _, out _))
                _turrets.ClearTarget(vehicleId, seatIndex);

            Remove(actorId);
            _authority.Forget(actorId);
        }

        /// <summary>Drops every installed source. Round teardown.</summary>
        internal void Reset()
        {
            for (int i = 0; i < _drivers.Count; i++) Restore(_drivers[i]);

            _drivers.Clear();
            _seated.Clear();
            _authority.Reset();
            _turrets?.Reset();
            UnreachableControllers = 0;
        }

        /// <summary>
        /// Publishes each driver's current axes, once per simulated tick, before the vehicles
        /// step.
        /// </summary>
        /// <remarks>
        /// Pushed rather than pulled: a <c>Vehicle</c> reads its driver's controller from
        /// <c>FixedUpdate</c>, which runs at the physics rate and not at the tick rate, so a
        /// source that consulted the authority on each read would answer a different question on
        /// each of the two physics steps inside one tick.
        /// </remarks>
        internal void PumpTick(uint serverTick)
        {
            for (int i = _drivers.Count - 1; i >= 0; i--)
            {
                ushort actorId = _drivers[i];

                if (!_seated.TryGetValue(actorId, out IDriverInputSink sink) || !sink.Exists)
                {
                    // The actor was destroyed under us -- died, disconnected, round reset. Drop
                    // the entry rather than dereferencing a destroyed component every tick.
                    _seated.Remove(actorId);
                    _drivers.RemoveAt(i);
                    continue;
                }

                if (_authority.TryGetCurrent(actorId, serverTick, out ClampedVehicleInput input))
                {
                    // Wire slot order is (throttle, steer, pitchAxis, auxAxis); read as a
                    // helicopter that is (yaw, collective, roll, pitch). Both readings go over,
                    // and the vehicle picks -- see IDriverInputSink.
                    sink.SetAxes(
                        steer: input.Steer,
                        throttle: input.Throttle,
                        heliYaw: input.Throttle,
                        heliCollective: input.Steer,
                        heliRoll: input.PitchAxis,
                        heliPitch: input.AuxAxis);
                }
                else
                {
                    // Nothing received, or the hold window expired. Centring is the whole point
                    // of V5-D11: a stalled driver coasts to a stop instead of holding full
                    // throttle forever.
                    sink.Centre();
                }
            }
        }

        private void Install(ushort actorId)
        {
            if (_seated.ContainsKey(actorId)) return;

            if (!_actors.TryFind(actorId, out NetServerActor actor) || actor == null)
            {
                UnreachableControllers++;
                return;
            }

            IDriverInputSink sink = NetServerBindings.AttachDriverInput(actor.gameObject);
            if (sink == null)
            {
                UnreachableControllers++;
                return;
            }

            _seated[actorId] = sink;
            _drivers.Add(actorId);
        }

        private void Remove(ushort actorId)
        {
            if (!Restore(actorId)) return;

            _seated.Remove(actorId);
            _drivers.Remove(actorId);
        }

        /// <summary>
        /// Puts back whatever input source the controller held before this actor started driving.
        /// </summary>
        /// <remarks>
        /// The previous source rather than unconditionally the null object: on a listen server or
        /// in the Editor the driver may be a local player whose keyboard source is the thing they
        /// walk with, and dropping it on seat exit would leave them unable to move. The sink owns
        /// that memory because the types involved live on the other side of the seam.
        /// </remarks>
        private bool Restore(ushort actorId)
        {
            if (!_seated.TryGetValue(actorId, out IDriverInputSink sink)) return false;

            sink.Detach();
            return true;
        }
    }
}
