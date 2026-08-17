using System;

namespace Ironfront.Net.Replication.Vehicles
{
    /// <summary>
    /// The authoritative aim of one turret. The joint or transform that renders it is an
    /// <b>output</b> of this pair, never the storage for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The shipped turrets have no such field: <c>TankTurret</c> accumulates into
    /// <c>ConfigurableJoint.targetRotation</c> and reads it back out next frame, and
    /// <c>MountedTurret</c> does the same through <c>Transform.localEulerAngles</c>. A value
    /// that lives only inside an engine object cannot be snapshotted, cannot be set by a
    /// server, and round-trips through <c>Quaternion.eulerAngles</c> — which is not injective,
    /// so the value you read back is not always the one you wrote.
    /// </para>
    /// <para>
    /// <c>[Serializable]</c> so Unity can show the pair in the Inspector when a
    /// <c>MonoBehaviour</c> holds one. It carries no <c>UnityEngine</c> dependency of its own.
    /// </para>
    /// </remarks>
    [Serializable]
    public struct TurretAimState
    {
        /// <summary>Traverse, degrees, wrapped to <c>[0, 360)</c> by <see cref="TurretAimCore"/>.</summary>
        public float Yaw;

        /// <summary>
        /// Elevation, degrees, clamped to the turret's limits. Does <b>not</b> wrap — a
        /// wrapped elevation would let a gun depress past its stop and come out the top.
        /// </summary>
        public float Pitch;
    }
}
