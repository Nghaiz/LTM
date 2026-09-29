using System.Collections.Generic;
using Ironfront.Net.Replication.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Cover judged against the enemy a bot faces, with real colliders (phase P28, part 2): the
    /// owner asked for bots that hide behind props, and a point only counts if it actually stands
    /// between the bot and the shooter.
    /// </summary>
    /// <remarks>
    /// Every body is built far from the open scene's contents and well above any map's water, so
    /// nothing the Editor happens to have loaded can stand in a ray's way.
    /// </remarks>
    public sealed class CoverProbeTests
    {
        private static readonly Vector3 Origin = new Vector3(5000f, 1000f, 5000f);

        /// <summary>The enemy's eye: 40 m ahead along +z, at head height.</summary>
        private static readonly Vector3 Threat = Origin + new Vector3(0f, 1.6f, 40f);

        private readonly List<GameObject> _made = new List<GameObject>();
        private float _waterHeight;

        [SetUp]
        public void SetUp()
        {
            _waterHeight = MovementCore.WaterHeight;
            MovementCore.WaterHeight = float.NegativeInfinity;
            // The floor everything stands on.
            Box(Origin + new Vector3(0f, -0.5f, 0f), new Vector3(200f, 1f, 200f));
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject made in _made)
                if (made != null) Object.DestroyImmediate(made);
            _made.Clear();
            MovementCore.WaterHeight = _waterHeight;
            Physics.SyncTransforms();
        }

        private GameObject Box(Vector3 centre, Vector3 size, int layer = 0)
        {
            GameObject box = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _made.Add(box);
            box.layer = layer;
            box.transform.position = centre;
            box.transform.localScale = size;
            Physics.SyncTransforms();
            return box;
        }

        /// <summary>A wall across the line of fire, its near face half a metre in front of the spot.</summary>
        private GameObject WallAhead(Vector3 spot, float height, float left, float right, int layer = 0)
        {
            float width = right - left;
            var centre = new Vector3(spot.x + (left + right) * 0.5f, spot.y + height * 0.5f, spot.z + 0.6f);
            return Box(centre, new Vector3(width, height, 0.2f), layer);
        }

        // ------------------------------------------------------------------ Judge

        [Test]
        public void OpenGround_HidesNothing()
            => Assert.AreEqual(CoverFit.None, CoverProbe.Judge(Origin, Threat));

        [Test]
        public void ALowWallBetweenTheSpotAndTheShooter_HidesACrouchedBot_AndLetsItShootOverTheTop()
        {
            WallAhead(Origin, 1.1f, -2f, 2f);
            Assert.AreEqual(CoverFit.OverTop, CoverProbe.Judge(Origin, Threat));
        }

        [Test]
        public void ATallWallWithItsLeftEndBesideTheSpot_IsShotRoundOnTheLeft()
        {
            // Facing +z the right hand is +x: the wall runs off to the right and stops just short of
            // the spot's left, so only a lean to the left clears it.
            WallAhead(Origin, 2.5f, -0.1f, 3f);
            Assert.AreEqual(CoverFit.LeanLeft, CoverProbe.Judge(Origin, Threat));
        }

        [Test]
        public void ATallWallWithItsRightEndBesideTheSpot_IsShotRoundOnTheRight()
        {
            WallAhead(Origin, 2.5f, -3f, 0.1f);
            Assert.AreEqual(CoverFit.LeanRight, CoverProbe.Judge(Origin, Threat));
        }

        [Test]
        public void ATallWideWall_IsSomewhereToHide_NotToShootFrom()
        {
            WallAhead(Origin, 2.5f, -3f, 3f);
            Assert.AreEqual(CoverFit.Hidden, CoverProbe.Judge(Origin, Threat));
        }

        [Test]
        public void AWallBehindTheSpot_DoesNotHideItFromTheFront()
        {
            Box(Origin + new Vector3(0f, 1.25f, -0.6f), new Vector3(6f, 2.5f, 0.2f));
            Assert.AreEqual(CoverFit.None, CoverProbe.Judge(Origin, Threat));
        }

        [Test]
        public void AWallTooFarAhead_IsNotCoverToHug()
        {
            Box(Origin + new Vector3(0f, 0.55f, CoverProbe.NearBlock + 1f), new Vector3(6f, 1.1f, 0.2f));
            Assert.AreEqual(CoverFit.None, CoverProbe.Judge(Origin, Threat));
        }

        [Test]
        public void AWallFacingTheOtherWay_DoesNotHideTheSpotFromAShooterOnTheFlank()
        {
            WallAhead(Origin, 1.1f, -2f, 2f);
            Vector3 flankShooter = Origin + new Vector3(40f, 1.6f, 0f);
            Assert.AreEqual(CoverFit.None, CoverProbe.Judge(Origin, flankShooter),
                "the facing test the original used would have taken this point for a shooter at +x");
        }

        [Test]
        public void AParkedVehicle_IsCover()
        {
            WallAhead(Origin, 1.1f, -2f, 2f, layer: 12);
            Assert.AreEqual(CoverFit.OverTop, CoverProbe.Judge(Origin, Threat));
        }

        [Test]
        public void AVehicleDrivingPast_IsNotCover()
        {
            GameObject car = WallAhead(Origin, 1.1f, -2f, 2f, layer: 12);
            Rigidbody body = car.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.linearVelocity = new Vector3(8f, 0f, 0f);
            Assert.AreEqual(CoverFit.None, CoverProbe.Judge(Origin, Threat));
        }

        // ------------------------------------------------------------------ Choose

        [Test]
        public void TheSpotBehindTheRock_BeatsANearerOneInTheOpen()
        {
            Vector3 open = Origin + new Vector3(2f, 0f, 0f);
            Vector3 behindRock = Origin + new Vector3(-6f, 0f, 0f);
            WallAhead(behindRock, 1.1f, -1f, 1f);

            var spots = new[] { open, behindRock };
            int chosen = CoverProbe.Choose(spots, spots.Length, Origin, Threat, false, out CoverFit fit);

            Assert.AreEqual(1, chosen);
            Assert.AreEqual(CoverFit.OverTop, fit);
        }

        [Test]
        public void NothingThatHides_ChoosesNothing()
        {
            var spots = new[] { Origin + new Vector3(2f, 0f, 0f), Origin + new Vector3(-3f, 0f, 1f) };
            Assert.AreEqual(-1, CoverProbe.Choose(spots, spots.Length, Origin, Threat, false, out CoverFit fit));
            Assert.AreEqual(CoverFit.None, fit);
        }

        [Test]
        public void OfTwoGoodSpots_AFightingBotTakesTheNearer()
        {
            Vector3 near = Origin + new Vector3(3f, 0f, 0f);
            Vector3 far = Origin + new Vector3(-9f, 0f, 0f);
            WallAhead(near, 1.1f, -1f, 1f);
            WallAhead(far, 1.1f, -1f, 1f);

            var spots = new[] { far, near };
            Assert.AreEqual(1, CoverProbe.Choose(spots, spots.Length, Origin, Threat, false, out _));
        }

        [Test]
        public void AFightingBot_TakesASpotItCanShootFrom_OverANearerHidingPlace()
        {
            Vector3 hiding = Origin + new Vector3(2f, 0f, 0f);
            Vector3 firing = Origin + new Vector3(-6f, 0f, 0f);
            WallAhead(hiding, 2.5f, -3f, 3f);
            WallAhead(firing, 1.1f, -1f, 1f);

            var spots = new[] { hiding, firing };
            Assert.AreEqual(1, CoverProbe.Choose(spots, spots.Length, Origin, Threat, false, out CoverFit fit));
            Assert.AreEqual(CoverFit.OverTop, fit);
        }

        [Test]
        public void AFallingBackBot_TakesTheSpotAwayFromTheEnemy()
        {
            Vector3 forward = Origin + new Vector3(0f, 0f, 5f);
            Vector3 back = Origin + new Vector3(0f, 0f, -5f);
            WallAhead(forward, 1.1f, -1f, 1f);
            WallAhead(back, 1.1f, -1f, 1f);

            var spots = new[] { forward, back };
            Assert.AreEqual(1, CoverProbe.Choose(spots, spots.Length, Origin, Threat, true, out _));
            Assert.AreEqual(0, CoverProbe.Choose(spots, spots.Length, Origin, Threat, false, out _),
                "a fighting bot gives ground only for a clearly better spot; these two are equal");
        }

        // ------------------------------------------------------------------ behind a vehicle

        [Test]
        public void TheSpotBehindABox_IsOnTheGround_OnTheFarSide_AndHidesTheBot()
        {
            GameObject tank = Box(Origin + new Vector3(0f, 1f, 0f), new Vector3(3f, 2f, 4f), layer: 12);
            Bounds bounds = tank.GetComponent<Collider>().bounds;

            Assert.IsTrue(CoverProbe.SpotBehind(bounds, Threat, out Vector3 spot));
            Assert.Less(spot.z, bounds.min.z, "the spot is not on the far side of the box from the shooter");
            Assert.AreEqual(Origin.y, spot.y, 0.01f, "the spot is not on the ground");
            Assert.AreNotEqual(CoverFit.None, CoverProbe.Judge(spot, Threat));
        }

        [Test]
        public void GroundUnderWater_IsNoSpot()
        {
            MovementCore.WaterHeight = Origin.y + 0.5f;
            Assert.IsFalse(CoverProbe.Ground(Origin + Vector3.up, Origin.y, out _));
        }

        [Test]
        public void WhereARockStands_IsNoSpot()
        {
            Box(Origin + new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 2f));
            Assert.IsFalse(CoverProbe.Ground(Origin, Origin.y, out _),
                "the rock's top is not ground beside it: a bot sent there climbs or stops");
        }

        [Test]
        public void HardAgainstAWall_ThereIsNoRoomToStand()
        {
            // The ray down misses the wall a hand's breadth away; a body's width does not.
            Box(Origin + new Vector3(0.6f, 1f, 0f), new Vector3(1f, 2f, 2f));
            Assert.IsFalse(Physics.Raycast(Origin + Vector3.up * 3f, Vector3.down, 2.9f),
                "the fixture no longer reproduces the case: the ray down must miss the wall");
            Assert.IsFalse(CoverProbe.Ground(Origin + Vector3.up, Origin.y, out _));
        }

        [Test]
        public void OpenFloor_IsASpot()
        {
            Assert.IsTrue(CoverProbe.Ground(Origin + Vector3.up, Origin.y, out Vector3 spot));
            Assert.AreEqual(Origin.y, spot.y, 0.01f);
        }
    }
}
