using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Vehicles;
using UnityEngine;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// Adapts one <see cref="IGameplayVehicleSource"/> to the engine-free
    /// <see cref="IVehiclePoseSource"/>. This is the whole PhysX seam, and it holds no
    /// decisions at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A plain class, not a <c>MonoBehaviour</c> — a deliberate departure from the phase
    /// plan.</b> The plan had this component attached to every vehicle prefab, registering in
    /// <c>OnEnable</c>. But "<c>NetServerVehicle</c> attached to every vehicle prefab, and its
    /// <c>.meta</c>" is listed in the plan's own § 4 as prefab authoring handed to the client
    /// track — so a component form would ship a registry that stays empty until somebody
    /// re-saves fourteen prefabs on two maps, with no error anywhere to say so. Registration is
    /// instead driven from <c>NetVehicleLifecycle.ReportSpawned</c>, which is the one place that
    /// already knows both the vehicle and the id it was given.
    /// </para>
    /// <para>
    /// <b>Nothing here decides anything.</b> Everything above this class — id allocation,
    /// interest classification, rate limiting, seat arbitration, the burn state machine — is
    /// engine-free and runs under <c>dotnet test</c> against a fake implementation of the same
    /// interface. What is left is reading a body and calling a method, which is what design
    /// § 3.2 says cannot be ported.
    /// </para>
    /// </remarks>
    internal sealed class NetServerVehicle : IVehiclePoseSource
    {
        private readonly IGameplayVehicleSource _source;
        private readonly ServerTurretAuthority _turrets;

        internal NetServerVehicle(
            ushort vehicleId, IGameplayVehicleSource source,
            ServerTurretAuthority turrets = null)
        {
            VehicleId = vehicleId;
            _source   = source;

            // Optional so every pre-V6 construction keeps working. A null one reports a zero
            // turret pose, which is exactly what this class shipped with for two phases.
            _turrets  = turrets;
        }

        /// <summary>The id the lifecycle sink assigned at spawn.</summary>
        internal ushort VehicleId { get; }

        /// <summary>The game-side vehicle, for the seat bridge and the damage sink.</summary>
        internal IGameplayVehicleSource Source => _source;

        /// <summary>False once the underlying <c>Vehicle</c> has been destroyed.</summary>
        internal bool Exists => _source != null && _source.Exists;

        /// <summary>
        /// The turret pose the snapshot entry carries. V6 task 2.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Read from the authority, never from the scene.</b> The turret's transform is an
        /// OUTPUT of <see cref="ServerTurretAuthority"/>'s pose — the components write the joint
        /// target from it and nothing reads an angle back (V0's invariant, V6-D5-local). Reading
        /// the scene here would replicate a value PhysX produced, which is the one thing the
        /// design says cannot be replicated; it would also round-trip through
        /// <c>Quaternion.eulerAngles</c>, which is not injective.
        /// </para>
        /// <para>
        /// <b>The vehicle's FIRST tracked turret</b>, per V6-D3: the entry has one turret slot and
        /// it belongs to the first mounted-weapon seat in <c>Vehicle.seats</c> order. A second
        /// turret is drawn from its occupant's already-replicated actor rotation and holds its
        /// last pose when vacant — a cosmetic-only error, because no shot ever reads a remote
        /// client's transform.
        /// </para>
        /// <para>
        /// Zero for a vehicle with no turret at all, which is honest rather than a fallback: an
        /// untracked vehicle's turret slot has no value to report and the entry masks the field
        /// out on change detection anyway.
        /// </para>
        /// </remarks>
        public float TurretYaw =>
            _turrets != null && _turrets.TryGetPrimaryAim(VehicleId, out TurretAimState aim)
                ? aim.Yaw
                : 0f;

        /// <inheritdoc cref="TurretYaw"/>
        public float TurretPitch =>
            _turrets != null && _turrets.TryGetPrimaryAim(VehicleId, out TurretAimState aim)
                ? aim.Pitch
                : 0f;

        public bool IsInWater => Exists && _source.IsInWater;

        public bool IsAirborne => Exists && _source.IsAirborne;

        /// <inheritdoc />
        public void ReadPose(
            out Vec3 position,
            out float rotationX, out float rotationY, out float rotationZ, out float rotationW,
            out Vec3 linearVelocity,
            out Vec3 angularVelocity)
        {
            if (!Exists)
            {
                // A vehicle destroyed between registration and capture. Reporting the identity
                // pose is honest — it is what the entry would carry anyway — and the despawn
                // that removes it from the registry is already in flight on channel 2.
                position        = default;
                rotationX       = 0f;
                rotationY       = 0f;
                rotationZ       = 0f;
                rotationW       = 1f;
                linearVelocity  = default;
                angularVelocity = default;
                return;
            }

            _source.ReadPose(
                out Vector3 p, out Quaternion r, out Vector3 v, out Vector3 w);

            position        = new Vec3(p.x, p.y, p.z);
            rotationX       = r.x;
            rotationY       = r.y;
            rotationZ       = r.z;
            rotationW       = r.w;
            linearVelocity  = new Vec3(v.x, v.y, v.z);
            angularVelocity = new Vec3(w.x, w.y, w.z);
        }

        /// <inheritdoc />
        public void ReadSubtypeTail(out byte subtypeA, out byte subtypeB)
        {
            if (!Exists)
            {
                subtypeA = 0;
                subtypeB = 0;
                return;
            }

            _source.ReadSubtypeTail(out subtypeA, out subtypeB);
        }
    }
}
