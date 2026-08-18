using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The face <c>Assembly-CSharp</c>'s <c>Vehicle</c> calls into for its role guards. V4
    /// tasks 5 and 6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Static, for <see cref="NetVehicleLifecycle"/>'s reason.</b> A <c>Vehicle</c> is an
    /// authored prefab instantiated by a spawner; a serialized reference per vehicle would be a
    /// per-prefab manual step that gets forgotten on the one prefab nobody re-opened, and the
    /// symptom would be a vehicle whose damage is applied twice with nothing in the log.
    /// </para>
    /// <para>
    /// <b>Uninstalled is the normal state.</b> A client and an offline build never install a
    /// sink, so every method here reports "not handled" and <c>Vehicle</c> runs exactly the code
    /// it shipped with. That is what acceptance criterion 12 asks for, and it is why the guards
    /// are written as <c>if (handled) return;</c> rather than as a role switch: there is one
    /// branch and it is false unless a server put something behind it.
    /// </para>
    /// <para>
    /// <b>Cleared at subsystem registration</b>, because with domain reload disabled a static
    /// field survives leaving play mode and the second run would route damage into the first
    /// run's registry.
    /// </para>
    /// </remarks>
    public static class NetVehicleAuthority
    {
        private static IVehicleDamageSink _damageSink;
        private static ServerVehicleRegistry _vehicles;
        private static ServerActorRegistry _actors;

        /// <summary>True when a server is authoritative over vehicle health and seat claims.</summary>
        public static bool IsInstalled => _damageSink != null && _vehicles != null;

        /// <summary>
        /// True when the server decides WHEN a burning vehicle dies, and the scene must not.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>V4-D11 says the server owns when the burn ends, and without this guard it did
        /// not.</b> <c>Vehicle.FixedUpdate</c> counts <c>burnTime</c> down on the wall clock at
        /// the physics rate and calls <c>Die()</c>, which reaches <c>VehicleSpawner.VehicleDied</c>
        /// and announces <c>S_VEHICLE_DESPAWN</c>. <c>VehicleBurnClock</c> counts the SAME burn in
        /// ticks. Two authorities over one event, deduplicated only by the id pool's
        /// <c>IsInUse</c> check — so whichever fires first wins, they disagree by up to a snapshot
        /// interval, and the wall-clock one usually wins because it runs at 60 Hz against the
        /// tick clock's 20. The tick-counted clock the phase exists to install was decorative on
        /// the death path.
        /// </para>
        /// <para>
        /// A separate name from <see cref="IsInstalled"/> even though the value is the same,
        /// because the two gameplay call sites in <c>Vehicle</c> are asking a different question
        /// — "may I kill this?" — and a bare <c>IsInstalled</c> there would read as an
        /// implementation detail rather than a rule.
        /// </para>
        /// </remarks>
        public static bool ServerOwnsVehicleDeath => IsInstalled;

        /// <summary>Installs the server's sinks. Called from <c>ServerTickLoop.Bind</c>.</summary>
        public static void Install(
            IVehicleDamageSink damageSink,
            ServerVehicleRegistry vehicles,
            ServerActorRegistry actors)
        {
            _damageSink = damageSink;
            _vehicles   = vehicles;
            _actors     = actors;
        }

        /// <summary>Restores the uninstalled state. Called from <c>ServerTickLoop.Unbind</c>.</summary>
        public static void Uninstall()
        {
            _damageSink = null;
            _vehicles   = null;
            _actors     = null;
        }

        /// <summary>
        /// Routes a vehicle's damage through the authoritative sink.
        /// </summary>
        /// <returns>
        /// True when the server handled it, in which case the caller must NOT subtract health
        /// itself. False offline, on a client, and for a vehicle that was never replicated —
        /// every one of which is a case where the shipped path is the correct one.
        /// </returns>
        public static bool TryApplyDamage(GameObject vehicle, float amount, int attackerActorId)
        {
            if (!IsInstalled) return false;

            ushort vehicleId = _vehicles.NetworkIdOf(vehicle);
            if (vehicleId == 0) return false;

            ushort attacker = attackerActorId > 0 && attackerActorId <= ushort.MaxValue
                ? (ushort)attackerActorId
                : (ushort)0;

            _damageSink.ApplyDamage(vehicleId, amount, attacker);
            return true;
        }

        /// <summary>
        /// Routes a repair through the authoritative health record.
        /// </summary>
        /// <returns>
        /// True when the server handled it, in which case the caller must NOT add health itself.
        /// </returns>
        /// <remarks>
        /// <b><c>Damage</c> got a role guard and <c>Repair</c> did not, and the asymmetry was the
        /// worst defect in this phase.</b> The scene's health rose while the authoritative record
        /// did not, so the snapshot shipped a stale byte and the next hit subtracted from a stale
        /// value — one more shot killed a fully repaired vehicle. Anything that writes
        /// <c>Vehicle.health</c> has to come through here.
        /// </remarks>
        public static bool TryApplyRepair(GameObject vehicle, float amount)
        {
            if (!IsInstalled) return false;

            ushort vehicleId = _vehicles.NetworkIdOf(vehicle);
            if (vehicleId == 0) return false;

            _damageSink.ApplyRepair(vehicleId, amount);
            return true;
        }

        /// <summary>
        /// Tells the burn clock a repair has put the fire out.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="TryApplyRepair"/> because the SCENE decides when a burn stops
        /// — <c>Vehicle.Repair</c> needs three of them (<c>stopBurningRepairs</c>) — and that is a
        /// gameplay rule, not a netcode one. Without this call the tick-counted clock kept its
        /// countdown armed and despawned a repaired, drivable vehicle on schedule.
        /// </remarks>
        public static void ExtinguishBurn(GameObject vehicle)
        {
            if (!IsInstalled) return;

            ushort vehicleId = _vehicles.NetworkIdOf(vehicle);
            if (vehicleId == 0) return;

            (_damageSink as ServerVehicleDamageSink)?.ExtinguishBurn(vehicleId);
        }

        /// <summary>
        /// True when this build must suppress a vehicle's local health and death logic because
        /// the server owns them.
        /// </summary>
        /// <remarks>
        /// A client, and only a client. It is asked separately from
        /// <see cref="TryApplyDamage"/> because the two answers differ on the one build that
        /// matters: a client has no sink to route to and must still not run the health ladder,
        /// where an offline build has no sink and must.
        /// </remarks>
        public static bool IsClientSuppressed => NetContext.IsClient;

        // ------------------------------------------------------------------- seat claims

        /// <summary>
        /// Claims a bot could not record against the claims table — an unresolvable bot, or a
        /// vehicle whose every seat was already spoken for.
        /// </summary>
        /// <remarks>
        /// Expected to be near zero, and non-zero is worth looking at rather than absorbing: it
        /// means the AI asked for a seat on a vehicle that had none, or asked on behalf of an
        /// actor with no <c>NetServerActor</c>. Counted here because the alternative — silently
        /// falling back to the offline counter — is what makes the derived count wrong.
        /// </remarks>
        public static long UnrecordedClaims { get; private set; }

        /// <summary>
        /// Reserves a seat for a bot by identity. V4-D10.
        /// </summary>
        /// <returns>
        /// <b>Whether this vehicle's claims are the server's to account for</b> — NOT whether a
        /// claim was recorded. The two are different questions and conflating them is a real bug:
        /// the caller reads <c>false</c> as "fall back to <c>seatsClaimedByBots</c>", and on a
        /// replicated vehicle that field is not the source of truth. <c>Vehicle.ClaimedSeatCount</c>
        /// reads the claims TABLE there, so an increment into the counter is invisible — and the
        /// case where it matters most is a vehicle whose seats are all claimed, which is exactly
        /// when the count must say "full" and would instead keep reporting room.
        /// </returns>
        public static bool TryClaimSeat(GameObject vehicle, GameObject botActor)
        {
            if (!IsInstalled) return false;

            ushort vehicleId = _vehicles.NetworkIdOf(vehicle);
            if (vehicleId == 0) return false;   // genuinely not replicated; the counter is right

            // From here the answer is true whatever happens below: this vehicle IS ours.
            if (!TryResolveBot(botActor, out ushort botId)
                || !_vehicles.Registry.TryGetState(vehicleId, out VehicleState state))
            {
                UnrecordedClaims++;
                return true;
            }

            // The shipped ClaimSeat() reserves "a seat", not a particular one, and the AI picks
            // which on arrival. What matters for the count is that the claim NAMES the bot, so
            // the first free index is taken and two bots occupy two slots rather than one.
            for (byte seat = 0; seat < state.SeatCount; seat++)
            {
                ushort held = _vehicles.Claims.ClaimantOf(vehicleId, seat);
                if (held != 0 && held != botId) continue;

                _vehicles.Claims.TryClaim(vehicleId, seat, botId, Time.time);
                return true;
            }

            // Every seat spoken for. Nothing to record, and nothing to fall back to.
            UnrecordedClaims++;
            return true;
        }

        /// <summary>Releases every claim this bot holds on this vehicle.</summary>
        /// <returns>
        /// Whether this vehicle's claims are the server's to account for. See
        /// <see cref="TryClaimSeat"/> — releasing has the mirror hazard, where a fall-through
        /// would DECREMENT a counter nothing reads and leave the table holding a claim forever.
        /// </returns>
        public static bool TryDropSeatClaim(GameObject vehicle, GameObject botActor)
        {
            if (!IsInstalled) return false;

            ushort vehicleId = _vehicles.NetworkIdOf(vehicle);
            if (vehicleId == 0) return false;

            if (!TryResolveBot(botActor, out ushort botId)
                || !_vehicles.Registry.TryGetState(vehicleId, out VehicleState state))
                return true;

            for (byte seat = 0; seat < state.SeatCount; seat++)
                if (_vehicles.Claims.Release(vehicleId, seat, botId)) break;

            return true;
        }

        /// <summary>
        /// Live claims on a vehicle, or -1 when this build is not replicating.
        /// </summary>
        /// <remarks>
        /// <b>-1 rather than 0 for "no answer".</b> Zero is a legitimate count and the caller
        /// falls back to its own field on -1; conflating the two would report every vehicle on a
        /// client as having no claims, which is the AI reading a number it was never given.
        /// </remarks>
        public static int ClaimCount(GameObject vehicle)
        {
            if (!IsInstalled) return -1;

            ushort vehicleId = _vehicles.NetworkIdOf(vehicle);
            return vehicleId == 0 ? -1 : _vehicles.Claims.ClaimCount(vehicleId);
        }

        /// <summary>Drops claims whose per-claim deadline has passed.</summary>
        /// <remarks>
        /// Per-claim, not per-vehicle. The shipped <c>drainClaimAction</c> takes one claim off an
        /// anonymous pile every ten seconds, which is why two bots claiming and one dying leaves
        /// the count permanently wrong (V4-D10).
        /// </remarks>
        public static void ReleaseExpiredClaims()
        {
            if (!IsInstalled) return;

            _vehicles.Claims.ReleaseExpired(Time.time);
        }

        private static bool TryResolveBot(GameObject botActor, out ushort botId)
        {
            botId = 0;

            if (_actors == null || botActor == null) return false;

            NetServerActor actor = botActor.GetComponent<NetServerActor>();
            if (actor == null || actor.ActorId == 0) return false;

            botId = actor.ActorId;
            return true;
        }
    }
}
