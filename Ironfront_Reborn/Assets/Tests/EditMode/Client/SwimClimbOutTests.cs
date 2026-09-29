using System.Collections.Generic;
using Ironfront.Net.Replication.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A swimmer climbs out up a bank too steep to walk, through the real capsule and the real
    /// movement agent: live test 2026-09-30, a swimmer pressed against Island's west shore (43 to
    /// 51 degrees under water) until its breath ran out.
    /// </summary>
    public sealed class SwimClimbOutTests
    {
        private const float Water = 100f;
        private const float BeachAboveWater = 0.25f;
        private const float Dt = 1f / 30f;

        private static readonly Vector3 Origin = new Vector3(5000f, 0f, 5000f);
        private static readonly MoveInput Forward =
            new MoveInput(0f, 1f, 0f, jump: false, sprint: false, crouch: false);

        private readonly List<GameObject> _made = new List<GameObject>();
        private float _savedWater;

        [SetUp]
        public void SetUp()
        {
            _savedWater = MovementCore.WaterHeight;
            MovementCore.WaterHeight = Water;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject made in _made) Object.DestroyImmediate(made);
            _made.Clear();
            MovementCore.WaterHeight = _savedWater;
        }

        [TestCase(50f, TestName = "ASwimmerClimbsOutUpABankTooSteepToWalk")]
        [TestCase(90f, TestName = "ASwimmerClimbsOutOntoALowLedge")]
        public void ASwimmerClimbsOut(float bankDegrees)
        {
            // The beach, its edge at z = 2, and under it the bank running down into deep water.
            Box(new Vector3(0f, Water + BeachAboveWater - 0.5f, 22f), new Vector3(10f, 1f, 40f), Quaternion.identity);
            float rad = bankDegrees * Mathf.Deg2Rad;
            var upTheBank = new Vector3(0f, Mathf.Sin(rad), Mathf.Cos(rad));
            var outOfTheBank = new Vector3(0f, Mathf.Cos(rad), -Mathf.Sin(rad));
            var edge = new Vector3(0f, Water + BeachAboveWater, 2f);
            Box(edge - upTheBank * 3f - outOfTheBank * 0.25f, new Vector3(10f, 0.5f, 6f),
                Quaternion.LookRotation(upTheBank, outOfTheBank));

            NetMovementAgent swimmer = Swimmer(new Vector3(0f, Water - MovementCore.SwimFloatDepth, -2f));
            for (int i = 0; i < 30 * 6; i++) swimmer.Tick(in Forward, Dt);

            Vector3 at = swimmer.transform.position - Origin;
            Assert.IsFalse(MovementCore.IsInWater(swimmer.State.Position.Y), $"still in the water at {at}");
            Assert.Greater(at.z, 2.3f, $"never got onto the beach: {at}");
            Assert.AreEqual(Water + BeachAboveWater + MovementCore.StandHeight * 0.5f, at.y, 0.15f,
                $"not standing on the beach: {at}");
        }

        [Test]
        public void ASwimmerCannotClimbACliff()
        {
            // A wall three metres over the water: the climb lifts the feet ClimbOutLip over the
            // surface and no higher.
            Box(new Vector3(0f, Water - 2f, 4f), new Vector3(10f, 10f, 4f), Quaternion.identity);

            NetMovementAgent swimmer = Swimmer(new Vector3(0f, Water - MovementCore.SwimFloatDepth, -1f));
            float highest = float.MinValue;
            for (int i = 0; i < 30 * 6; i++)
            {
                swimmer.Tick(in Forward, Dt);
                highest = Mathf.Max(highest, swimmer.transform.position.y);
            }

            // The lip, plus the capsule's own step of 0.3 m on top of it at the most.
            float highestFeet = highest - Origin.y - MovementCore.StandHeight * 0.5f;
            Assert.Less(highestFeet, Water + MovementCore.ClimbOutLip + 0.35f, "a swimmer climbed a cliff");
            Assert.Less(swimmer.transform.position.z - Origin.z, 2f, "a swimmer went through the cliff");
        }

        private void Box(Vector3 centre, Vector3 size, Quaternion rotation)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _made.Add(box);
            box.layer = 0;
            box.transform.SetPositionAndRotation(Origin + centre, rotation);
            box.transform.localScale = size;
        }

        // The player's capsule as Player Fps Actor.prefab authors it.
        private NetMovementAgent Swimmer(Vector3 centre)
        {
            var body = new GameObject("swimmer");
            _made.Add(body);
            body.transform.position = Origin + centre;

            var capsule = body.AddComponent<CharacterController>();
            capsule.height = MovementCore.StandHeight;
            capsule.radius = 0.3f;
            capsule.slopeLimit = 45f;
            capsule.stepOffset = 0.3f;
            capsule.skinWidth = 0.08f;
            capsule.center = Vector3.zero;

            var agent = body.AddComponent<NetMovementAgent>();
            agent.State = MoveState.AtRest(MovementSimulation.ToCore(body.transform.position), grounded: false);
            Physics.SyncTransforms();
            return agent;
        }
    }
}
