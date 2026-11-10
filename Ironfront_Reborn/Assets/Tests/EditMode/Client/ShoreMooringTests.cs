using System;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// A boat moored by a shore is brought in to the water's edge, and a player reaches its seats
    /// from wherever round the hull they swim up to it. Owner report 2026-11-10: Forest Lake's boat
    /// always lay out in the lake, and sometimes could not be boarded while swimming.
    /// </summary>
    public sealed class ShoreMooringTests
    {
        /// <summary>The hull fits wherever the water under it is deep enough: west of <paramref name="edgeX"/>.</summary>
        private static Func<float, float, bool> FloatsWestOf(float edgeX) => (x, z) => x < edgeX;

        [Test]
        public void TheHullIsBroughtInToTheLastPlaceItFloats()
        {
            Assert.IsTrue(ShoreMooring.TrySlide(0f, 0f, 1f, 0f, 15f, FloatsWestOf(9.2f), out float x, out float z));

            Assert.AreEqual(9f, x, 1e-4f, "one step short of where it would ground");
            Assert.AreEqual(0f, z, 1e-4f, "along the line to the shore, nowhere else");
        }

        /// <summary>
        /// A bar of shallows between the boat and a deeper channel by the beach: the boat stops at
        /// the bar rather than jumping it, because a hull slid across a bar would be dragged over it.
        /// </summary>
        [Test]
        public void TheSlideStopsAtTheFirstShallows()
        {
            Func<float, float, bool> bar = (x, z) => x < 4f || (x > 6f && x < 12f);

            Assert.IsTrue(ShoreMooring.TrySlide(0f, 0f, 1f, 0f, 15f, bar, out float x, out _));
            Assert.AreEqual(3.5f, x, 1e-4f);
        }

        [Test]
        public void TheSlideGoesNoFurtherThanAsked()
        {
            Assert.IsTrue(ShoreMooring.TrySlide(0f, 0f, 0f, -1f, 2f, (x, z) => true, out float x, out float z));

            Assert.AreEqual(0f, x, 1e-4f);
            Assert.AreEqual(-2f, z, 1e-4f, "the full two metres, and not a step past them");
        }

        [Test]
        public void AHullThatDoesNotFitWhereItStartsIsNotMoored()
        {
            Assert.IsFalse(ShoreMooring.TrySlide(10f, 0f, 1f, 0f, 15f, FloatsWestOf(9f), out float x, out _));
            Assert.AreEqual(10f, x, "left where it was asked about");
        }

        // ===== Seat reach =====

        /// <summary>The RHIB's seats in its own space (rhib.prefab): driver aft, bow gun forward.</summary>
        private static readonly Vector3[] RhibSeats =
        {
            new Vector3(0.54f, 0.21f, -1.09f),  // driver
            new Vector3(-0.54f, 0.21f, -1.09f),
            new Vector3(0.54f, 0.21f, 0.44f),
            new Vector3(-0.54f, 0.21f, 0.44f),
            new Vector3(0f, 0.16f, 1.93f),      // bow gun
        };

        /// <summary>
        /// The defect's shape: a swimmer 1.5 m off the bow -- capsule centre
        /// <c>MovementCore.SwimFloatDepth</c> under the surface, the hull riding 0.28 m over it -- is
        /// out of reach of the driver's seat and near the bow gun. The reach is to the nearest seat,
        /// so the boat is his.
        /// </summary>
        [Test]
        public void ASwimmerOffTheBowIsInReachOfTheBoat()
        {
            const float hullOverSurface = 0.28f;
            const float bowTip = 3.81f;
            var swimmer = new Vector3(
                0f, -hullOverSurface - Ironfront.Net.Replication.Movement.MovementCore.SwimFloatDepth, bowTip + 1.5f);

            float toDriver = (RhibSeats[0] - swimmer).magnitude;
            Assert.Greater(toDriver, Ironfront.Net.Replication.Vehicles.SeatArbiter.MaxSeatReachMetres,
                "Setup: the driver's seat must be out of reach from the bow, or this proves nothing.");

            float nearest = Mathf.Sqrt(SeatReach.NearestSquared(swimmer, RhibSeats.Length, i => RhibSeats[i]));
            Assert.LessOrEqual(nearest, Ironfront.Net.Replication.Vehicles.SeatArbiter.MaxSeatReachMetres,
                "A swimmer touching the bow is refused the boat.");
        }

        [Test]
        public void ASeatThatIsNotThereIsNotTheNearest()
        {
            Vector3 missing = Vector3.positiveInfinity;
            var seats = new[] { missing, new Vector3(3f, 0f, 0f), new Vector3(float.NaN, 0f, 0f) };

            Assert.AreEqual(9f, SeatReach.NearestSquared(Vector3.zero, seats.Length, i => seats[i]), 1e-4f);
        }

        [Test]
        public void AVehicleWithNoSeatToLocateIsOutOfReach()
        {
            Assert.IsTrue(float.IsPositiveInfinity(SeatReach.NearestSquared(Vector3.zero, 0, i => Vector3.zero)));
            Assert.IsTrue(float.IsPositiveInfinity(SeatReach.NearestSquared(Vector3.zero, 2, null)));
            Assert.IsTrue(float.IsPositiveInfinity(
                SeatReach.NearestSquared(Vector3.zero, 2, i => Vector3.positiveInfinity)));
        }
    }
}
