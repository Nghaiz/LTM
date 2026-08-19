using System;
using System.Collections.Generic;
using System.Text;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Vehicles;

namespace Ironfront.Net.Replication.Server
{
    /// <summary>
    /// What a server holds that a match reset is supposed to have emptied. Phase-03 trap 1.
    /// </summary>
    public readonly struct ServerStateSnapshot
    {
        public readonly int ActorIdsInUse;
        public readonly int ActorIdsFree;
        public readonly int ActorIdsQuarantined;
        public readonly int HitboxHistoryActors;
        public readonly int InterestPairs;
        public readonly int SpawnAckPairs;
        public readonly int Sessions;

        /// <summary>Vehicle ids held by a live vehicle. V4, design § 8 criterion 13.</summary>
        public readonly int VehicleIdsInUse;

        /// <summary>Vehicle ids cooling down. Non-zero mid-round is legitimate.</summary>
        public readonly int VehicleIdsQuarantined;

        /// <summary>(viewer, vehicle) rate-table pairs. The trap-2 leak, one dictionary over.</summary>
        public readonly int VehicleInterestPairs;

        /// <summary>Vehicles still registered. Zero after a world reset.</summary>
        public readonly int VehiclesRegistered;

        /// <summary>
        /// Mounted weapons still tracked. Zero after a world reset. V6, criterion 13.
        /// </summary>
        /// <remarks>
        /// A new id space keyed on <c>(vehicleId, seatIndex)</c>, so it leaks the same way the
        /// pair tables above do — silently, on the second and third round of a server nobody is
        /// watching. It joins the audit for exactly that reason.
        /// </remarks>
        public readonly int MountedWeaponsTracked;

        /// <summary>Turrets still tracked. Zero after a world reset. V6, criterion 13.</summary>
        public readonly int TurretsTracked;

        /// <summary>
        /// Projectile ids still held — by a projectile in flight, a deployable on the ground, or
        /// an engine-simulated grenade. Zero after a world reset. V7, criterion 7.
        /// </summary>
        /// <remarks>
        /// The projectile id space is the third to join this audit, and it leaks differently
        /// from the other two: an id is released by the projectile that holds it, so a prefab
        /// destroyed by a path that skips its own teardown keeps the id forever. Five
        /// back-to-back matches is what surfaces that; a single-match smoke test never would.
        /// </remarks>
        public readonly int ProjectileIdsInUse;

        public ServerStateSnapshot(
            int actorIdsInUse, int actorIdsFree, int actorIdsQuarantined,
            int hitboxHistoryActors, int interestPairs, int spawnAckPairs, int sessions,
            int vehicleIdsInUse = 0, int vehicleIdsQuarantined = 0,
            int vehicleInterestPairs = 0, int vehiclesRegistered = 0,
            int mountedWeaponsTracked = 0, int turretsTracked = 0,
            int projectileIdsInUse = 0)
        {
            MountedWeaponsTracked = mountedWeaponsTracked;
            TurretsTracked        = turretsTracked;
            ProjectileIdsInUse    = projectileIdsInUse;

            ActorIdsInUse       = actorIdsInUse;
            ActorIdsFree        = actorIdsFree;
            ActorIdsQuarantined = actorIdsQuarantined;
            HitboxHistoryActors = hitboxHistoryActors;
            InterestPairs       = interestPairs;
            SpawnAckPairs       = spawnAckPairs;
            Sessions            = sessions;

            VehicleIdsInUse       = vehicleIdsInUse;
            VehicleIdsQuarantined = vehicleIdsQuarantined;
            VehicleInterestPairs  = vehicleInterestPairs;
            VehiclesRegistered    = vehiclesRegistered;
        }

        /// <summary>
        /// True when nothing from the previous round is still held.
        /// </summary>
        /// <remarks>
        /// Quarantined ids are deliberately NOT required to be zero here — a reset calls
        /// <see cref="ActorIdPool.ResetAll"/>, which empties the quarantine, but a server
        /// audited mid-round legitimately has ids cooling. What must be zero is anything
        /// keyed on an actor that no longer exists.
        /// </remarks>
        public bool IsClean =>
            IsCleanOfActorState
            && Sessions == 0;

        /// <summary>
        /// Everything <see cref="IsClean"/> checks except the session count.
        /// </summary>
        /// <remarks>
        /// This is the right question after a MATCH RESET, which deliberately keeps its
        /// sessions — a reset is not a disconnect, and the players are still standing there
        /// waiting for the next round. Asking <see cref="IsClean"/> there reported a leak on
        /// every round transition with anyone connected, so the one log line that would have
        /// announced a real trap-1 leak had been crying wolf since the day it was written.
        /// <see cref="IsClean"/> remains the right question at shutdown, when the sessions
        /// really should all be gone.
        /// </remarks>
        public bool IsCleanOfActorState =>
            ActorIdsInUse == 0
            && HitboxHistoryActors == 0
            && InterestPairs == 0
            && SpawnAckPairs == 0
            && IsCleanOfVehicleState;

        /// <summary>
        /// The vehicle half of the same question. V4, design § 8 criterion 13.
        /// </summary>
        /// <remarks>
        /// <b>Quarantined vehicle ids are NOT required to be zero here</b>, for the reason the
        /// actor pool's are not: a reset returns every id at once, but a server audited
        /// mid-round legitimately has ids cooling. What must be zero is anything keyed on a
        /// vehicle that no longer exists — the pair table above all, because that is the
        /// dictionary that grows for the life of the process if <c>Forget</c> is not called on
        /// despawn.
        /// </remarks>
        public bool IsCleanOfVehicleState =>
            VehicleIdsInUse == 0
            && VehicleInterestPairs == 0
            && VehiclesRegistered == 0
            && MountedWeaponsTracked == 0
            && TurretsTracked == 0
            && ProjectileIdsInUse == 0;

        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append("ids in-use=").Append(ActorIdsInUse)
              .Append(" free=").Append(ActorIdsFree)
              .Append(" quarantined=").Append(ActorIdsQuarantined)
              .Append(" | hitboxHistory=").Append(HitboxHistoryActors)
              .Append(" interestPairs=").Append(InterestPairs)
              .Append(" spawnAckPairs=").Append(SpawnAckPairs)
              .Append(" sessions=").Append(Sessions)
              .Append(" | vehicles=").Append(VehiclesRegistered)
              .Append(" vehicleIds in-use=").Append(VehicleIdsInUse)
              .Append(" quarantined=").Append(VehicleIdsQuarantined)
              .Append(" vehicleInterestPairs=").Append(VehicleInterestPairs)
              .Append(" | mountedWeapons=").Append(MountedWeaponsTracked)
              .Append(" turrets=").Append(TurretsTracked)
              .Append(" | projectileIds=").Append(ProjectileIdsInUse);
            return sb.ToString();
        }
    }

    /// <summary>
    /// The engine-free half of the phase-03 sketch's <c>AssertCleanState()</c>: it collects
    /// the counts and answers whether they are clean, and leaves the assertion to the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this is not a <c>Debug.Assert</c>.</b> The sketch's version fires only in a
    /// development build and only on the machine running it, which is exactly the case where
    /// somebody is watching. The leak it is looking for shows up on the second and third round
    /// of a server that has been up for an hour with nobody attached. Returning a value means
    /// the load test can assert on it, the tick loop can log it, and neither has to be a
    /// development build.
    /// </para>
    /// <para>
    /// The reset itself lives here too, for the reason the audit exists: a cleanup that is
    /// written once next to the check for it cannot drift from that check, whereas a cleanup
    /// spread across five call sites in a MonoBehaviour will.
    /// </para>
    /// </remarks>
    public sealed class ServerStateAudit
    {
        private readonly ActorIdPool _ids;
        private readonly HitboxHistory _hitboxHistory;
        private readonly InterestManager _interest;
        private readonly SpawnAckTracker _spawnAcks;
        private readonly Func<int> _sessionCount;

        // Optional, because the audit predates vehicles by five phases and the phase-03 load
        // tests construct it with four arguments. A null one reports zeros, which reads as
        // clean — correct for a server that has no vehicle subsystem at all, and the reason
        // ServerTickLoop passes them rather than leaving the defaults.
        private readonly VehicleIdPool? _vehicleIds;
        private readonly VehicleInterestTracker? _vehicleInterest;
        private readonly VehicleRegistry? _vehicles;

        // Optional for the same reason the three above are: V6 postdates every load test that
        // constructs this with four arguments, and a null one reporting zero reads as clean --
        // correct for a server with no mounted-weapon subsystem at all.
        private readonly MountedWeaponRegistry? _mountedWeapons;
        private readonly Projectiles.ProjectileIdPool? _projectileIds;
        private readonly ServerTurretAuthority? _turrets;

        public ServerStateAudit(
            ActorIdPool ids,
            HitboxHistory hitboxHistory,
            InterestManager interest,
            SpawnAckTracker spawnAcks,
            Func<int>? sessionCount = null,
            VehicleIdPool? vehicleIds = null,
            VehicleInterestTracker? vehicleInterest = null,
            VehicleRegistry? vehicles = null,
            MountedWeaponRegistry? mountedWeapons = null,
            ServerTurretAuthority? turrets = null,
            Projectiles.ProjectileIdPool? projectileIds = null)
        {
            _projectileIds   = projectileIds;
            _vehicleIds      = vehicleIds;
            _vehicleInterest = vehicleInterest;
            _vehicles        = vehicles;
            _mountedWeapons  = mountedWeapons;
            _turrets         = turrets;

            _ids           = ids ?? throw new ArgumentNullException(nameof(ids));
            _hitboxHistory = hitboxHistory ?? throw new ArgumentNullException(nameof(hitboxHistory));
            _interest      = interest ?? throw new ArgumentNullException(nameof(interest));
            _spawnAcks     = spawnAcks ?? throw new ArgumentNullException(nameof(spawnAcks));
            _sessionCount  = sessionCount ?? (() => 0);
        }

        /// <summary>Reads the current counts. Cheap — every source keeps its own count.</summary>
        public ServerStateSnapshot Capture()
            => new ServerStateSnapshot(
                _ids.InUseCount,
                _ids.FreeCount,
                _ids.QuarantinedCount,
                _hitboxHistory.TrackedActorCount,
                _interest.TrackedPairCount,
                _spawnAcks.TrackedPairCount,
                _sessionCount(),
                _vehicleIds?.InUseCount ?? 0,
                _vehicleIds?.QuarantinedCount ?? 0,
                _vehicleInterest?.TrackedPairCount ?? 0,
                _vehicles?.LiveCount ?? 0,
                _mountedWeapons?.TrackedCount ?? 0,
                _turrets?.TrackedCount ?? 0,
                _projectileIds?.InUseCount ?? 0);

        /// <summary>
        /// Empties every per-actor and per-pair table. The host still has to despawn the actors
        /// themselves — this drops what the netcode remembers ABOUT them.
        /// </summary>
        /// <param name="retainedActorIds">
        /// Ids belonging to actors that survive the reset, and so must stay marked in-use in the
        /// id pool. Null means "the whole world is going away", which is what a lobby-driven
        /// round teardown does. A scene whose actors outlive the round — the shipping Dustbowl
        /// map, where 41 bots persist across the match cycle — MUST pass them, or the pool
        /// re-offers ids those actors still hold and <c>ActorIdsInUse</c> reads 0 while 41 are
        /// in use. See <see cref="ActorIdPool.ResetAll(IEnumerable{ushort})"/> for the round-9
        /// measurement behind this.
        /// </param>
        public void ResetForNewMatch(IEnumerable<ushort>? retainedActorIds = null)
        {
            _hitboxHistory.Clear();
            _interest.Reset();
            _spawnAcks.Clear();
            _ids.ResetAll(retainedActorIds);

            // Vehicles have no "retained" case. Actors survive a round on the shipping Dustbowl
            // map — 41 bots persist across the match cycle — but every vehicle is destroyed at
            // the boundary and the client tears its whole vehicle table down with the match
            // phase, so there is nothing left for a stale packet to be applied to.
            _vehicles?.Clear();
            _vehicleInterest?.Reset();
            _vehicleIds?.ReleaseAll();

            // Both are keyed on a vehicle that has just been destroyed, so neither has a
            // "retained" case either. Cleared here rather than from the tick loop for the reason
            // this whole class exists: a cleanup written next to the check for it cannot drift
            // from that check, and a cleanup spread across five MonoBehaviour call sites will.
            _mountedWeapons?.Reset();
            _turrets?.Reset();
        }
    }
}
