using Ironfront.Net.Replication.Vehicles;

namespace Ironfront.Net.Unity.Server
{
    /// <summary>
    /// The server's answer to "where should this turret be pointing": whatever
    /// <see cref="ServerTurretAuthority"/> holds. V6 task 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It carries no logic and that is the requirement, not an accident.</b> The clamp, the
    /// slew rate, the wrap and the pitch stops are all in <c>TurretAimCore</c>, which
    /// <c>dotnet test</c> grades without opening Unity. What is left here is an array read behind
    /// an interface the other assembly can name.
    /// </para>
    /// <para>
    /// <b>Registration is lazy, from the first resolve.</b> The alternative was a
    /// <c>MonoBehaviour</c> on every turret prefab registering in <c>OnEnable</c> — which the
    /// phase plan itself lists as Editor work handed to the client track, so it would ship an
    /// authority whose table stayed empty until fourteen prefabs were re-saved. A turret that
    /// asks for its aim is a turret that exists; that is the registration signal.
    /// </para>
    /// </remarks>
    internal sealed class ServerTurretDirectory : ITurretAimDirectory
    {
        private readonly ServerTurretAuthority _authority;

        internal ServerTurretDirectory(ServerTurretAuthority authority)
        {
            _authority = authority;
        }

        /// <inheritdoc />
        public bool TryResolve(
            ushort vehicleId, byte seatIndex, bool locallyOccupied,
            out TurretAimSource source, out float yawDegrees, out float pitchDegrees)
        {
            source       = TurretAimSource.ServerTarget;
            yawDegrees   = 0f;
            pitchDegrees = 0f;

            if (_authority == null) return false;

            if (!_authority.TryGetAim(vehicleId, seatIndex, out TurretAimState aim)) return false;

            yawDegrees   = aim.Yaw;
            pitchDegrees = aim.Pitch;
            return true;
        }

        /// <inheritdoc />
        public void Declare(
            ushort vehicleId, byte seatIndex, in TurretAimLimits limits,
            float seedYaw, float seedPitch)
        {
            // Register is idempotent on the aim and refreshes the limits, so a turret declaring
            // itself on every fixed step costs an array index and never snaps the gun back to its
            // prefab rest pose.
            _authority?.Register(vehicleId, seatIndex, in limits, seedYaw, seedPitch);
        }
    }
}
