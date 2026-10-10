using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Vehicles;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// A vehicle that goes down is reported with its crew still aboard. Achievements v2 asks who sat
    /// in the pilot's seat when a helicopter went down (AIR DEFENSE, DOGFIGHT, IMPOSSIBLE ANGLE).
    /// </summary>
    /// <remarks>
    /// Owner's run of 2026-10-10: "DOGFIGHT did not unlock when I shot an enemy helicopter down from
    /// ours". A helicopter skips the burn, and the clock that kills it outright empties every seat;
    /// the report came after that, so every helicopter went down empty and none of the three could
    /// ever be earned. The tally's own tests passed throughout: they hand it a crew directly.
    /// </remarks>
    public sealed class VehicleDownCrewTests
    {
        private const ushort VehicleId = 11;
        private const ushort Pilot = 201;
        private const ushort Gunner = 202;
        private const ushort Attacker = 203;

        private GameObject _owner;

        [TearDown]
        public void TearDown()
        {
            if (_owner != null) Object.DestroyImmediate(_owner);
        }

        [Test]
        public void AHelicopterShotDownIsReportedWithItsCrewAboard()
        {
            AssertReportedWithCrew(VehicleIds.HELICOPTER, crashSkipsBurn: true);
        }

        [Test]
        public void AVehicleThatBurnsIsReportedWithItsCrewAboard()
        {
            AssertReportedWithCrew(VehicleIds.TANK, crashSkipsBurn: false);
        }

        private void AssertReportedWithCrew(byte vehicleType, bool crashSkipsBurn)
        {
            var vehicles = new ServerVehicleRegistry();
            _owner = new GameObject("downed vehicle");
            Assert.IsTrue(vehicles.Register(VehicleId, _owner, new FakeVehicle(vehicleType, crashSkipsBurn)));
            Assert.IsTrue(vehicles.Registry.TrySetOccupant(VehicleId, 0, Pilot));
            Assert.IsTrue(vehicles.Registry.TrySetOccupant(VehicleId, 1, Gunner));

            var sink = new ServerVehicleDamageSink(vehicles, new VehicleBurnClock(vehicles.Registry), () => 100u);
            int reports = 0;
            ushort credited = 0, pilot = 0, gunner = 0;
            sink.Downed += (id, destroyer) =>
            {
                reports++;
                credited = destroyer;
                pilot = vehicles.Registry.OccupantOf(id, 0);
                gunner = vehicles.Registry.OccupantOf(id, 1);
            };

            sink.ApplyDamage(VehicleId, 500f, Attacker);

            Assert.AreEqual(1, reports, "one report per vehicle going down");
            Assert.AreEqual(Attacker, credited);
            Assert.AreEqual(Pilot, pilot, "the vehicle was reported without its pilot");
            Assert.AreEqual(Gunner, gunner, "the vehicle was reported without its gunner");
        }

        private sealed class FakeVehicle : IGameplayVehicleSource
        {
            private readonly byte _type;
            private readonly bool _crashSkipsBurn;

            public FakeVehicle(byte type, bool crashSkipsBurn)
            {
                _type = type;
                _crashSkipsBurn = crashSkipsBurn;
            }

            public bool Exists => true;
            public byte NetworkTypeId => _type;
            public int SeatCount => 2;
            public float Health => 100f;
            public float MaxHealth => 100f;
            public float BurnTimeSeconds => 4f;
            public bool CrashSkipsBurn => _crashSkipsBurn;
            public bool IsBurning => false;
            public bool IsDead => false;
            public int OwnerTeam => 1;

            public void ReadPose(
                out Vector3 position, out Quaternion rotation,
                out Vector3 linearVelocity, out Vector3 angularVelocity)
            {
                position = Vector3.zero;
                rotation = Quaternion.identity;
                linearVelocity = Vector3.zero;
                angularVelocity = Vector3.zero;
            }

            public float TurretYaw => 0f;
            public float TurretPitch => 0f;
            public void ReadSubtypeTail(out byte subtypeA, out byte subtypeB) { subtypeA = 0; subtypeB = 0; }
            public bool IsInWater => false;
            public bool IsAirborne => true;
            public void SetHealthAuthoritative(float value) { }
            public void Kill() { }
            public Vector3 GetSeatPosition(int seatIndex) => Vector3.zero;
            public bool TryEnterSeat(GameObject actor, int seatIndex) => false;
            public bool TryLeaveSeat(GameObject actor) => false;
        }
    }
}
