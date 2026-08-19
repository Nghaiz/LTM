using Ironfront.Net.Replication.Vehicles;

namespace Ironfront.Net.Unity
{
    /// <summary>Where a turret gets its aim from this frame. V6 task 2.</summary>
    public enum TurretAimSource : byte
    {
        /// <summary>
        /// The turret integrates its own input, exactly as it does offline. The local gunner's
        /// path — zero latency, and it converges with the server by construction because both
        /// sides run the same policy over the same clamped demand (V6-D5-local).
        /// </summary>
        Local = 0,

        /// <summary>
        /// The occupant's requested pose, to be walked toward at the turret's own slew rate
        /// through <see cref="TurretAimCore.StepToward"/>. The server's path.
        /// </summary>
        ServerTarget = 1,

        /// <summary>
        /// The decoded pose from the vehicle snapshot entry, applied outright. A remote client's
        /// path — it never runs the policy, because it is drawing a result rather than deciding
        /// one.
        /// </summary>
        RemotePose = 2,
    }

    /// <summary>
    /// Supplies a turret's aim for the current role. Implemented once per side.
    /// </summary>
    /// <remarks>
    /// <b>Keyed by ids rather than by component</b>, because this assembly cannot name
    /// <c>Seat</c>, <c>Vehicle</c> or <c>MountedWeapon</c> — they compile into
    /// <c>Assembly-CSharp</c>, a predefined assembly no <c>.asmdef</c> may reference. The turret
    /// resolves its own <c>(vehicleId, seatIndex)</c>, where every type it needs is in scope, and
    /// asks here with two numbers.
    /// </remarks>
    public interface ITurretAimDirectory
    {
        /// <summary>
        /// Answers where the turret at <paramref name="seatIndex"/> on
        /// <paramref name="vehicleId"/> should be pointing.
        /// </summary>
        /// <returns>
        /// False when this side has nothing to say about that turret, which the caller reads as
        /// <see cref="TurretAimSource.Local"/> — an unregistered vehicle behaves exactly as it
        /// does offline rather than freezing.
        /// </returns>
        bool TryResolve(
            ushort vehicleId, byte seatIndex, bool locallyOccupied,
            out TurretAimSource source, out float yawDegrees, out float pitchDegrees);

        /// <summary>
        /// Announces that a turret exists on this seat, with these limits and this rest pose.
        /// </summary>
        /// <remarks>
        /// <b>Idempotent, and the pose is a SEED rather than an assignment.</b> A turret declares
        /// itself on every fixed step it is occupied — cheaper than a lifecycle it would have to
        /// keep in sync — so a re-declaration that overwrote the aim would pin every turret to its
        /// prefab rest pose forever.
        /// </remarks>
        void Declare(
            ushort vehicleId, byte seatIndex, in TurretAimLimits limits,
            float seedYaw, float seedPitch);
    }

    /// <summary>
    /// The one place a turret asks whether its aim belongs to it this frame. V6 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A static resolver, not a component on every turret prefab.</b> The phase plan had a
    /// <c>NetTurret</c> <c>MonoBehaviour</c> attached to each one — and the plan's own § 7 hands
    /// that attachment to the client track as Editor work, which means it would ship a seam that
    /// stays inert until fourteen prefabs are re-saved, with nothing anywhere reporting the gap.
    /// <c>NetServerVehicle</c> declined the same trap for the same reason. Every id this needs is
    /// reachable at runtime from <c>MountedWeapon.user.seat</c>, so no authoring is required.
    /// </para>
    /// <para>
    /// <b>Offline is a hard <c>false</c> before the directory is even consulted (V6-D9).</b>
    /// Single-player turret behaviour is unchanged byte for byte, and a test pins it.
    /// </para>
    /// </remarks>
    public static class NetTurretAim
    {
        /// <summary>
        /// The active side's implementation, installed by whichever bootstrap is running. Null
        /// means "nobody is replicating turrets", which reads as local aim.
        /// </summary>
        public static ITurretAimDirectory Directory { get; set; }

        /// <summary>
        /// Resolves a vehicle <c>GameObject</c> to the id the replication layer gave it, or 0.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A seam rather than a field on <c>Vehicle</c>, and that is an SSOT decision.</b> Both
        /// sides already hold the mapping — <c>ServerVehicleRegistry.NetworkIdOf</c> one way,
        /// <c>RemoteVehicleRegistry.NetworkIdOf</c> the other — and stamping a copy onto the
        /// component would make a third, which is the one that goes stale when a vehicle is
        /// despawned and its GameObject lives another frame.
        /// </para>
        /// <para>
        /// <b>0 is a real answer.</b> Offline, and for a vehicle that was never replicated — an
        /// unauthored prefab, or a spawn that found the id pool empty. Every caller reads it as
        /// "there is no network here" and runs the shipped path.
        /// </para>
        /// </remarks>
        public static System.Func<UnityEngine.GameObject, ushort> VehicleIdResolver { get; set; }

        /// <inheritdoc cref="VehicleIdResolver"/>
        public static ushort VehicleIdOf(UnityEngine.GameObject vehicle)
        {
            if (vehicle == null) return 0;

            System.Func<UnityEngine.GameObject, ushort> resolver = VehicleIdResolver;
            return resolver != null ? resolver(vehicle) : (ushort)0;
        }

        /// <summary>The local gunner's current aim, republished every fixed step while seated.</summary>
        private static TurretAimState _localAim;

        private static bool _hasLocalAim;

        /// <summary>
        /// Publishes the aim the local player's turret just integrated, so
        /// <c>ClientVehicleStage</c> can put it in the next <c>C_VEHICLE_INPUT</c>.
        /// </summary>
        /// <remarks>
        /// <b>One slot, because there is one local player and they occupy one seat.</b> A table
        /// keyed by turret would be a table with one row in it and a lifetime problem: a turret
        /// destroyed with its vehicle would leave a stale entry that the input stage would keep
        /// sending. <see cref="ClearLocal"/> on seat exit is the whole cleanup.
        /// </remarks>
        public static void PublishLocal(float yawDegrees, float pitchDegrees)
        {
            _localAim.Yaw   = yawDegrees;
            _localAim.Pitch = pitchDegrees;
            _hasLocalAim    = true;
        }

        /// <summary>Forgets the local aim. Seat exit, death, and role teardown.</summary>
        public static void ClearLocal()
        {
            _localAim    = default;
            _hasLocalAim = false;
        }

        /// <summary>
        /// The aim the local player's turret last integrated, for the outbound input message.
        /// </summary>
        public static bool TryGetLocal(out float yawDegrees, out float pitchDegrees)
        {
            yawDegrees   = _localAim.Yaw;
            pitchDegrees = _localAim.Pitch;
            return _hasLocalAim;
        }

        /// <inheritdoc cref="ITurretAimDirectory.Declare"/>
        public static void Declare(
            ushort vehicleId, byte seatIndex, in TurretAimLimits limits,
            float seedYaw, float seedPitch)
        {
            if (NetContext.IsOffline) return;
            if (vehicleId == 0) return;

            Directory?.Declare(vehicleId, seatIndex, in limits, seedYaw, seedPitch);
        }

        /// <inheritdoc cref="ITurretAimDirectory.TryResolve"/>
        public static bool TryResolve(
            ushort vehicleId, byte seatIndex, bool locallyOccupied,
            out TurretAimSource source, out float yawDegrees, out float pitchDegrees)
        {
            source       = TurretAimSource.Local;
            yawDegrees   = 0f;
            pitchDegrees = 0f;

            if (NetContext.IsOffline) return false;

            ITurretAimDirectory directory = Directory;
            if (directory == null) return false;
            if (vehicleId == 0) return false;

            return directory.TryResolve(
                vehicleId, seatIndex, locallyOccupied,
                out source, out yawDegrees, out pitchDegrees);
        }

        /// <summary>Drops the directory and the local aim. Called on role teardown.</summary>
        public static void Clear()
        {
            Directory         = null;
            VehicleIdResolver = null;
            ClearLocal();
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(
            UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetOnLoad() => Clear();
    }
}
