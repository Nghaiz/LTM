using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using UnityEngine;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>How one replicated vehicle gets its transform.</summary>
    public enum VehicleClientMode
    {
        /// <summary>
        /// Kinematic, drawn from the snapshot stream at <c>DelayTicks</c> behind newest. Every
        /// vehicle this client is not driving, and — when the fallback is on — the one it is.
        /// </summary>
        Remote = 0,

        /// <summary>
        /// Dynamic, simulated locally against local input, corrected towards each snapshot.
        /// Only ever the vehicle this client is driving, and only when
        /// <c>VehicleReplicationConfig.PredictLocalVehicle</c> is true.
        /// </summary>
        Predicted = 1,
    }

    /// <summary>
    /// One replicated vehicle on a client: the id it was given, the scene object drawing it,
    /// and which of the two modes it is in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two modes, one class — that is what makes V5-D6's fallback a flag rather than a
    /// rewrite.</b> Turning prediction off does not take a different code path; it registers the
    /// driven vehicle in <see cref="VehicleClientMode.Remote"/>, which is the path fifteen other
    /// vehicles are already on and which the whole test suite already covers.
    /// </para>
    /// <para>
    /// <b>A plain class, not a <c>MonoBehaviour</c></b> — the same departure
    /// <c>NetServerVehicle</c> records and for the same reason. A component form would have to
    /// be authored onto every vehicle prefab, and a registry that stays empty until somebody
    /// re-saves fourteen prefabs on two maps fails silently, with nothing anywhere to say so.
    /// <see cref="RemoteVehicleRegistry"/> creates these when the spawn arrives, which is the
    /// one place that already knows both the vehicle and its id.
    /// </para>
    /// <para>
    /// <b>Remote vehicles go kinematic (V5-D3).</b> A replicated vehicle whose <c>Rigidbody</c>
    /// is still dynamic runs local PhysX <i>against</i> the incoming snapshots — the two fight,
    /// and the result is jitter that looks like a network problem and is not.
    /// </para>
    /// </remarks>
    internal sealed class NetClientVehicle
    {
        private readonly IGameplayVehicleBody _vehicle;
        private readonly Rigidbody _rigidbody;

        private VehicleCorrectionStats _stats;
        private bool _hasPose;
        private float _lastCorrectionTime;

        internal NetClientVehicle(ushort vehicleId, VehicleKind kind, IGameplayVehicleBody vehicle)
        {
            VehicleId = vehicleId;
            Kind      = kind;
            _vehicle  = vehicle;
            _rigidbody = vehicle != null ? vehicle.Rigidbody : null;

            SetMode(VehicleClientMode.Remote);
        }

        /// <summary>The server's id for this vehicle.</summary>
        internal ushort VehicleId { get; }

        /// <summary>
        /// The physics family, from <c>S_VEHICLE_SPAWN</c>. Decides how the subtype tail reads.
        /// </summary>
        internal VehicleKind Kind { get; }

        /// <summary>
        /// The scene object, behind the seam. Null on a record whose vehicle never resolved.
        /// </summary>
        /// <remarks>
        /// <b>Renamed from <c>Vehicle</c> in phase C4b</b>, because its type is no longer
        /// <c>Vehicle</c> and <c>vehicle.Vehicle.Transform</c> reads as a typo. Phase C4's § 0
        /// asked for this member to be DECIDED with the vehicle cluster rather than left to the
        /// sealing sub-phase, because <c>Net/Diagnostics</c> reads through it and
        /// <c>internal</c> stops working the moment the two folders become separate assemblies.
        /// It stays <c>internal</c> here; C4c settles the visibility when it knows which assembly
        /// Diagnostics is reading from.
        /// </remarks>
        internal IGameplayVehicleBody Body => _vehicle;

        /// <summary>False once the underlying vehicle has been destroyed.</summary>
        internal bool Exists => _vehicle != null && _vehicle.Exists;

        /// <summary>Remote or Predicted.</summary>
        internal VehicleClientMode Mode { get; private set; }

        /// <summary>How the corrections have been going. Zero snaps is the healthy state.</summary>
        internal VehicleCorrectionStats Stats => _stats;

        /// <summary>
        /// Switches modes, taking the body kinematic or giving it back to PhysX.
        /// </summary>
        internal void SetMode(VehicleClientMode mode)
        {
            Mode = mode;

            if (_vehicle == null) return;

            _vehicle.SetNetworkDriven(mode == VehicleClientMode.Remote);

            if (mode == VehicleClientMode.Predicted)
            {
                // The correction clock restarts with the mode. Carrying the old timestamp across
                // a mode change would produce one enormous dt on the first correction and blend
                // the whole error away instantly -- which looks exactly like a teleport, on the
                // one frame nobody is expecting one.
                _lastCorrectionTime = Time.time;
                _stats.Reset();
            }
        }

        /// <summary>
        /// Writes an interpolated pose straight onto the body. <see cref="VehicleClientMode.Remote"/>
        /// only.
        /// </summary>
        /// <remarks>
        /// <c>Rigidbody.position</c>/<c>.rotation</c> rather than <c>transform</c>: on a
        /// kinematic body the two are equivalent for rendering, but writing through the body
        /// keeps the physics transform and the render transform in step, so anything that
        /// raycasts against this vehicle in the same frame hits it where it is drawn.
        /// </remarks>
        internal void ApplyRemote(in VehiclePose pose)
        {
            if (_vehicle == null) return;

            if (_rigidbody != null)
            {
                _rigidbody.position = new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z);
                _rigidbody.rotation = ToQuaternion(in pose.Rotation);
            }
            else
            {
                _vehicle.Transform.SetPositionAndRotation(
                    new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z),
                    ToQuaternion(in pose.Rotation));
            }

            ApplyAuthoritativeState(in pose);
            _hasPose = true;
        }

        /// <summary>
        /// Nudges the locally simulated vehicle towards the server's pose.
        /// <see cref="VehicleClientMode.Predicted"/> only. V5-D4.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Called on snapshot arrival, not per frame.</b> A correction is only meaningful when
        /// new authority has arrived; re-running it every frame against the same snapshot would
        /// blend towards a target that is not moving and drag the vehicle onto a pose the server
        /// has already left. <c>ClientPredictionStage</c> makes the same choice for actors, for
        /// the same reason.
        /// </para>
        /// <para>
        /// <b><c>dt</c> is real elapsed time between corrections</b>, so the exponential blend is
        /// framerate-independent — see <c>VehicleCorrectionSolver</c>. Using
        /// <c>Time.deltaTime</c> here would make the correction rate depend on the render rate
        /// while the corrections themselves arrive at 20 Hz, which is a slower kind of the same
        /// bug.
        /// </para>
        /// </remarks>
        internal void ApplyCorrection(
            in VehiclePose server, float rttSeconds, in VehicleReplicationConfig config)
        {
            if (_vehicle == null || _rigidbody == null) return;

            float now = Time.time;
            float dt = now - _lastCorrectionTime;
            _lastCorrectionTime = now;

            VehiclePose local = ReadLocalPose(in server);

            CorrectionMode mode = VehicleCorrectionSolver.Solve(
                in local, in server, rttSeconds, dt, in config,
                out VehiclePose corrected, out float positionError, out float angleError);

            _stats.Record(mode, positionError, angleError);

            _rigidbody.position = new Vector3(
                corrected.Position.X, corrected.Position.Y, corrected.Position.Z);
            _rigidbody.rotation = ToQuaternion(in corrected.Rotation);

            if (mode == CorrectionMode.Snap)
            {
                // Only a snap overwrites velocity. A blend that also wrote the server's
                // velocities would replace the local simulation's momentum every 50 ms, which
                // is not prediction -- it is a 20 Hz teleport with a smoothing filter on the
                // position and nothing on the feel.
                _rigidbody.linearVelocity = new Vector3(
                    corrected.LinearVelocity.X, corrected.LinearVelocity.Y, corrected.LinearVelocity.Z);
                _rigidbody.angularVelocity = new Vector3(
                    corrected.AngularVelocity.X, corrected.AngularVelocity.Y, corrected.AngularVelocity.Z);
            }

            ApplyAuthoritativeState(in server);
            _hasPose = true;
        }

        /// <summary>Whether a pose has ever been applied. Until then the scene pose stands.</summary>
        internal bool HasPose => _hasPose;

        /// <summary>
        /// The turret aim from the last applied snapshot, degrees. V6 task 2.
        /// </summary>
        /// <remarks>
        /// <b>Held here rather than written straight onto the turret, because this class does not
        /// know which turret.</b> The vehicle entry carries one turret slot (V6-D3) and the
        /// component that owns it resolves itself through <c>NetTurretAim</c> on its own fixed
        /// step. Storing the pair is also what lets the interpolator's output arrive at whatever
        /// rate it likes without the turret having to be subscribed to anything.
        /// </remarks>
        internal float TurretYaw { get; private set; }

        /// <inheritdoc cref="TurretYaw"/>
        internal float TurretPitch { get; private set; }

        /// <summary>
        /// Health, burning, in-water and the subtype tail: the parts of the snapshot that are
        /// statements about the world rather than about where the vehicle is.
        /// </summary>
        /// <remarks>
        /// Applied in both modes. Prediction is about the transform; a client never predicts its
        /// own health, in exactly the same way infantry prediction does not.
        /// </remarks>
        private void ApplyAuthoritativeState(in VehiclePose pose)
        {
            // Recorded in BOTH modes, like every other authoritative scalar here. A locally
            // predicted vehicle still has a turret somebody else may be aiming: the driver
            // predicts the hull, never the gunner's traverse.
            TurretYaw   = pose.TurretYaw;
            TurretPitch = pose.TurretPitch;

            _vehicle.SetHealthAuthoritative(pose.Health * _vehicle.MaxHealth);

            _vehicle.ApplyReplicatedFlags(
                (pose.Flags & VehicleStateFlags.InWater) != 0,
                (pose.Flags & VehicleStateFlags.Airborne) != 0);

            _vehicle.ApplyReplicatedSubtypeTail(pose.SubtypeA, pose.SubtypeB);
        }

        /// <summary>
        /// The local body's pose, in the shape the solver wants.
        /// </summary>
        /// <remarks>
        /// The authoritative scalars are copied from <paramref name="server"/> rather than read
        /// back off the vehicle: the solver carries them straight through to its output, and
        /// taking them from the local side would have a <c>Blend</c> write the client's own
        /// health back over the server's.
        /// </remarks>
        private VehiclePose ReadLocalPose(in VehiclePose server)
        {
            Vector3 p = _rigidbody.position;
            Quaternion r = _rigidbody.rotation;
            Vector3 v = _rigidbody.linearVelocity;
            Vector3 w = _rigidbody.angularVelocity;

            return new VehiclePose(
                new Vec3(p.x, p.y, p.z),
                new Quat(r.x, r.y, r.z, r.w),
                new Vec3(v.x, v.y, v.z),
                new Vec3(w.x, w.y, w.z),
                server.Health,
                server.Flags,
                server.TurretYaw,
                server.TurretPitch,
                server.SubtypeA,
                server.SubtypeB);
        }

        private static Quaternion ToQuaternion(in Quat q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    }
}
