using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// A player whose seat has gone under water is thrown out to swim, and a crew afloat stays
    /// aboard. Owner report 2026-09-29: a car driven into deep water sank with its driver still at
    /// the wheel until the old eight-second drowning killed him.
    /// </summary>
    public sealed class SunkenSeatTests
    {
        private const ushort VehicleId = 9;
        private const ushort ActorId = 241;

        private GameObject _body;
        private GameObject _vehicleObject;
        private bool _registered;
        private float _savedWater;

        [SetUp]
        public void SetUp()
        {
            _savedWater = MovementCore.WaterHeight;
            MovementCore.WaterHeight = float.NegativeInfinity;
        }

        [TearDown]
        public void TearDown()
        {
            MovementCore.WaterHeight = _savedWater;

            ServerVehicleRegistry vehicles = ServerVehicleRegistry.Instance;
            vehicles.Registry.TrySetOccupant(VehicleId, 0, 0);
            if (_registered) vehicles.Unregister(VehicleId);

            if (_body != null)
            {
                ServerActorRegistry.Instance.Unregister(_body.GetComponent<NetServerActor>());
                Object.DestroyImmediate(_body);
            }
            if (_vehicleObject != null) Object.DestroyImmediate(_vehicleObject);
        }

        [Test]
        public void APlayerWhoseSeatHasGoneUnderWaterIsThrownOut()
        {
            FakeVehicle vehicle = SeatAPlayer(out ServerPlayer player, out float seatY);

            // Deep enough that a body standing on the seat would be swimming.
            MovementCore.WaterHeight = seatY + MovementCore.StandHeight * 0.5f + MovementCore.SwimSampleAbove + 1f;
            player.Tick(1f / 30f);

            Assert.AreSame(_body, vehicle.LeftBy, "the player stayed in a seat under water");
            Assert.IsFalse(ServerVehicleRegistry.Instance.Registry.TryFindSeatOf(ActorId, out _, out _));
        }

        [Test]
        public void ACrewAfloatStaysAboard()
        {
            FakeVehicle vehicle = SeatAPlayer(out ServerPlayer player, out float seatY);

            // A boat's seat rides over its waterline.
            MovementCore.WaterHeight = seatY - 0.5f;
            player.Tick(1f / 30f);

            Assert.IsNull(vehicle.LeftBy, "a player was thrown out of a boat afloat");
            Assert.IsTrue(ServerVehicleRegistry.Instance.Registry.TryFindSeatOf(ActorId, out _, out _));
        }

        private FakeVehicle SeatAPlayer(out ServerPlayer player, out float seatY)
        {
            _body = new GameObject("seated player");
            _body.AddComponent<CharacterController>();
            var body = _body.AddComponent<NetServerActor>();
            body.ActorId = ActorId;
            NetMovementAgent agent = body.AttachMovementAgent();
            _body.transform.position = new Vector3(0f, MovementCore.HeightFor(crouching: false) * 0.5f, 0f);
            agent.Teleport(_body.transform.position);
            ServerActorRegistry.Instance.Register(body);
            seatY = _body.transform.position.y;

            var vehicle = new FakeVehicle();
            _vehicleObject = new GameObject("sinking vehicle");
            ServerVehicleRegistry vehicles = ServerVehicleRegistry.Instance;
            _registered = vehicles.Register(VehicleId, _vehicleObject, vehicle);
            Assume.That(_registered, "vehicle id 9 is taken in this Editor's live registry");
            Assert.IsTrue(vehicles.Registry.TrySetOccupant(VehicleId, 0, ActorId), "could not seat the player");

            // Seated with no water about, the way every ride starts.
            player = new ServerPlayer(connectionId: 3, actorId: ActorId) { Actor = body };
            player.Tick(1f / 30f);
            Assert.IsNull(vehicle.LeftBy);
            return vehicle;
        }

        /// <summary>
        /// A vehicle whose seat exit is recorded, and publishes the empty seat the way
        /// <c>Vehicle.OccupantLeft</c> does.
        /// </summary>
        private sealed class FakeVehicle : IGameplayVehicleSource
        {
            public GameObject LeftBy;

            public bool Exists => true;
            public byte NetworkTypeId => VehicleIds.JEEP;
            public int SeatCount => 2;
            public float Health => 100f;
            public float MaxHealth => 100f;
            public float BurnTimeSeconds => 0f;
            public bool CrashSkipsBurn => false;
            public bool IsBurning => false;
            public bool IsDead => false;
            public int OwnerTeam => 0;

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
            public bool IsAirborne => false;
            public void SetHealthAuthoritative(float value) { }
            public void Kill() { }
            public Vector3 GetSeatPosition(int seatIndex) => Vector3.zero;
            public bool TryEnterSeat(GameObject actor, int seatIndex) => false;

            public bool TryLeaveSeat(GameObject actor)
            {
                LeftBy = actor;
                ServerVehicleRegistry.Instance.Registry.TrySetOccupant(VehicleId, 0, 0);
                return true;
            }
        }
    }
}
