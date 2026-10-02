using UnityEngine;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Whether a vehicle's wheel colliders are simulated: always for a vehicle this process moves
    /// with physics, and for one the network moves only near the camera.
    /// </summary>
    /// <remarks>
    /// A network-driven vehicle is kinematic, so its wheels move nothing; PhysX ran their suspension
    /// raycasts every step only so the wheel meshes sat on the ground. In a 100-bot Forest Lake match
    /// that was <c>PxVehicles.simulate</c> at about 0.44 ms a step for 40-48 wheels (development
    /// profile, 2026-10-02), most of them on vehicles too far away for a wheel's travel to show.
    /// </remarks>
    public static class RemoteWheelSimulation
    {
        /// <summary>How near the camera a network-driven vehicle's wheels are simulated.</summary>
        public const float WithinMetres = 150f;

        /// <summary>Whether wheels at <paramref name="position"/> are simulated.</summary>
        public static bool IsSimulated(bool networkDriven, Vector3 position, Camera viewer)
        {
            if (!networkDriven) return true;
            if (viewer == null) return false;
            return (position - viewer.transform.position).sqrMagnitude <= WithinMetres * WithinMetres;
        }
    }
}
