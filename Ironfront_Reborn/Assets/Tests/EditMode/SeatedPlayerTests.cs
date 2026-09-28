using System.Reflection;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Vehicles;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// A player in a vehicle is sent, hit and fires from where the seat carries them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Found auditing bug 5 of the 2026-09-28 playtest.</b> Nothing wrote a seated player's
    /// movement agent: its state kept the spot the player boarded from, and that state is both
    /// what the snapshot sends and what the hitbox history records. Measured 141 m from the body
    /// once the vehicle had carried it off -- so a player in a moving jeep was drawn, and could only
    /// be hit, back where they climbed in.
    /// </para>
    /// <para>
    /// And a seated player's shots left from a standing eye on the seat's root, 0.22 m below and
    /// 0.2 m behind the seat's camera.
    /// </para>
    /// </remarks>
    public sealed class SeatedPlayerTests
    {
        private const ushort VehicleId = 7;
        private const ushort ActorId = 240;

        private GameObject _body;
        private bool _added;
        private bool _occupied;

        [TearDown]
        public void TearDown()
        {
            VehicleRegistry registry = ServerVehicleRegistry.Instance.Registry;
            if (_occupied) registry.TrySetOccupant(VehicleId, 0, 0);
            if (_added) registry.Remove(VehicleId);

            if (_body != null)
            {
                ServerActorRegistry.Instance.Unregister(_body.GetComponent<NetServerActor>());
                Object.DestroyImmediate(_body);
            }
        }

        [Test]
        public void ThePositionSentForASeatedPlayerRidesTheSeat()
        {
            (NetServerActor body, ServerPlayer player) = BoardAtTheOrigin();

            Carry(body, player, new Vector3(100f, 50f, 100f), yawDegrees: 0f);

            // The capsule centre a standing body on the seat would have: what every player
            // position is sent as, so a client lowering it by half a capsule draws the seat.
            Vec3 sent = body.Movement.State.Position;
            Assert.AreEqual(100f, sent.X, 1e-3f, "the sent position stayed where the player boarded");
            Assert.AreEqual(50f + MovementCore.HeightFor(crouching: false) * 0.5f, sent.Y, 1e-3f);
            Assert.AreEqual(100f, sent.Z, 1e-3f);
        }

        [Test]
        public void ASeatedPlayerIsHitWhereTheSeatCarriesThem()
        {
            (NetServerActor body, ServerPlayer player) = BoardAtTheOrigin();

            Carry(body, player, new Vector3(100f, 50f, 100f), yawDegrees: 0f);

            // Seated, in the chair pose every client draws: head 0.95 m over the seat, 0.155 m
            // ahead of it.
            Aabb head = body.CaptureHitboxes().Head;
            Assert.AreEqual(50f + HitboxSet.HumanoidSeatedHeadCenterHeight, head.Center.Y, 0.01f,
                "a seated player's head box is not on the seat the vehicle carried them to");
            Assert.AreEqual(100f, head.Center.X, 0.02f);
            Assert.AreEqual(100.155f, head.Center.Z, 0.02f);
        }

        [Test]
        public void ASeatedPlayerFiresFromTheSeatsCameraAndTurnsWithTheVehicle()
        {
            (NetServerActor body, ServerPlayer player) = BoardAtTheOrigin();

            // The vehicle turned to face +X: the seat's forward is now world +X.
            Carry(body, player, new Vector3(100f, 50f, 100f), yawDegrees: 90f);

            Vec3? eye = SeatedEye(ActorId);
            Assert.IsTrue(eye.HasValue, "a seated player has no seated eye");
            Assert.AreEqual(100f + ProtocolConstants.SEATED_EYE_FORWARD, eye.Value.X, 1e-3f);
            Assert.AreEqual(50f + ProtocolConstants.SEATED_EYE_HEIGHT, eye.Value.Y, 1e-3f);
            Assert.AreEqual(100f, eye.Value.Z, 1e-3f);
        }

        [Test]
        public void APlayerOnFootHasNoSeatedEye()
        {
            BoardAtTheOrigin();

            ServerVehicleRegistry.Instance.Registry.TrySetOccupant(VehicleId, 0, 0);
            _occupied = false;

            Assert.IsFalse(SeatedEye(ActorId).HasValue);
        }

        // ------------------------------------------------------------------ helpers

        private (NetServerActor, ServerPlayer) BoardAtTheOrigin()
        {
            _body = new GameObject("seated player");
            _body.AddComponent<CharacterController>();
            var body = _body.AddComponent<NetServerActor>();
            body.ActorId = ActorId;
            NetMovementAgent agent = body.AttachMovementAgent();

            // Standing at the origin, where the player climbs in.
            _body.transform.position = new Vector3(0f, MovementCore.HeightFor(crouching: false) * 0.5f, 0f);
            agent.Teleport(_body.transform.position);
            ServerActorRegistry.Instance.Register(body);

            VehicleRegistry registry = ServerVehicleRegistry.Instance.Registry;
            var state = new VehicleState { VehicleId = VehicleId, SeatCount = 2, Health = 100f, MaxHealth = 100f };
            _added = registry.Add(in state, new StillPose());
            Assume.That(_added, "vehicle id 7 is taken in this Editor's live registry");
            _occupied = registry.TrySetOccupant(VehicleId, 0, ActorId);
            Assert.IsTrue(_occupied, "could not seat the player");

            var player = new ServerPlayer(connectionId: 3, actorId: ActorId) { Actor = body };
            player.Tick(1f / 30f);
            return (body, player);
        }

        /// <summary>The body sits on the seat's own transform, so moving it is the vehicle moving.</summary>
        private static void Carry(NetServerActor body, ServerPlayer player, Vector3 seat, float yawDegrees)
        {
            body.transform.SetPositionAndRotation(seat, Quaternion.Euler(0f, yawDegrees, 0f));
            player.Tick(1f / 30f);
        }

        private static Vec3? SeatedEye(ushort actorId)
        {
            MethodInfo method = typeof(ServerTickLoop).GetMethod(
                "SeatedEye", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(method, "ServerTickLoop.SeatedEye is gone");
            return (Vec3?)method.Invoke(null, new object[] { actorId });
        }

        private sealed class StillPose : IVehiclePoseSource
        {
            public void ReadPose(
                out Vec3 position, out float rotationX, out float rotationY, out float rotationZ,
                out float rotationW, out Vec3 linearVelocity, out Vec3 angularVelocity)
            {
                position = Vec3.Zero;
                rotationX = 0f; rotationY = 0f; rotationZ = 0f; rotationW = 1f;
                linearVelocity = Vec3.Zero;
                angularVelocity = Vec3.Zero;
            }

            public float TurretYaw => 0f;
            public float TurretPitch => 0f;
            public void ReadSubtypeTail(out byte subtypeA, out byte subtypeB) { subtypeA = 0; subtypeB = 0; }
            public bool IsInWater => false;
            public bool IsAirborne => false;
        }
    }
}
