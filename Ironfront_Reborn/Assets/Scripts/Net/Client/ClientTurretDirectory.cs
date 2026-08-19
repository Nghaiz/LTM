namespace Ironfront.Net.Unity.Client
{
    /// <summary>
    /// The client's answer to "where should this turret be pointing": its own aim when the local
    /// player is the gunner, and the decoded snapshot pose otherwise. V6 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The local gunner keeps integrating locally, and that is not a hole in the authority.</b>
    /// Zero-latency traverse is the difference between a turret that feels attached to the mouse
    /// and one that does not, and it costs nothing here because the replicated quantity is a joint
    /// TARGET — a PhysX input rather than a PhysX output (V6-D5-local). Both sides run the same
    /// <c>TurretAimCore</c> over the same clamped demand toward the same requested pose, so the
    /// steady-state disagreement is one quantization step and no correction channel is needed.
    /// What a client never gets to decide is where a shell goes: V7's <c>S_PROJECTILE_SPAWN</c>
    /// carries a server-computed origin.
    /// </para>
    /// <para>
    /// <b>A remote turret never runs the policy.</b> It is drawing a result somebody else decided,
    /// so it takes the pose outright — running the policy there would integrate a second time from
    /// an input it does not have.
    /// </para>
    /// </remarks>
    internal sealed class ClientTurretDirectory : ITurretAimDirectory
    {
        private readonly RemoteVehicleRegistry _registry;

        internal ClientTurretDirectory(RemoteVehicleRegistry registry)
        {
            _registry = registry;
        }

        /// <inheritdoc />
        public bool TryResolve(
            ushort vehicleId, byte seatIndex, bool locallyOccupied,
            out TurretAimSource source, out float yawDegrees, out float pitchDegrees)
        {
            source       = TurretAimSource.Local;
            yawDegrees   = 0f;
            pitchDegrees = 0f;

            // The local gunner aims for themselves. Answering false would say the same thing, but
            // saying it explicitly is what stops a later edit from "fixing" the fall-through into
            // a remote pose and pinning the local player's own turret to a 20 Hz stream.
            if (locallyOccupied) return true;

            if (_registry == null) return false;
            if (!_registry.TryGetTurretPose(vehicleId, out float yaw, out float pitch)) return false;

            source       = TurretAimSource.RemotePose;
            yawDegrees   = yaw;
            pitchDegrees = pitch;
            return true;
        }

        /// <summary>
        /// A no-op. A client keeps no turret table — it has the decoded pose, which is the whole
        /// of what it needs, and a second local one would be a copy that can disagree with it.
        /// </summary>
        public void Declare(
            ushort vehicleId, byte seatIndex,
            in Ironfront.Net.Replication.Vehicles.TurretAimLimits limits,
            float seedYaw, float seedPitch)
        {
        }
    }
}
