using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A parked remote vehicle is not written back onto itself every frame (phase P32): the write
    /// moves the body, and PhysX re-syncs every collider and wheel of it each step.
    /// </summary>
    public sealed class NetClientVehicleSamePoseTests
    {
        [Test]
        public void TheSamePoseIsUnchanged()
        {
            Quaternion facing = Quaternion.Euler(3f, 120f, -2f);
            Assert.IsTrue(NetClientVehicle.IsSamePose(new Vector3(10f, 2f, 5f), facing, new Vector3(10f, 2f, 5f), facing));
        }

        [Test]
        public void AQuaternionAndItsNegationAreTheSameFacing()
        {
            Quaternion facing = Quaternion.Euler(0f, 45f, 0f);
            var negated = new Quaternion(-facing.x, -facing.y, -facing.z, -facing.w);
            Assert.IsTrue(NetClientVehicle.IsSamePose(Vector3.zero, facing, Vector3.zero, negated));
        }

        [Test]
        public void ACentimetreOrATenthOfADegreeIsAMove()
        {
            Quaternion facing = Quaternion.Euler(0f, 45f, 0f);
            Assert.IsFalse(NetClientVehicle.IsSamePose(Vector3.zero, facing, new Vector3(0.01f, 0f, 0f), facing));
            Assert.IsFalse(NetClientVehicle.IsSamePose(Vector3.zero, facing, Vector3.zero, Quaternion.Euler(0f, 45.1f, 0f)));
        }
    }
}
