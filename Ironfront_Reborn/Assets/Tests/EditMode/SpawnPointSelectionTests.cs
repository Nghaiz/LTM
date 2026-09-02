using System;
using NUnit.Framework;
using UnityEngine;
using Ironfront.Net.Unity.Diagnostics;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Pins the respawn point selection that <c>ServerCombatBridge</c> performs.
    /// </summary>
    /// <remarks>
    /// This logic used to sit against <c>ActorManager.instance</c> inside a MonoBehaviour-bound
    /// assembly, which is why nothing tested it: no test assembly could see the type, and
    /// exercising it needed a loaded scene with authored spawn points. Behind
    /// <see cref="ISpawnPointDirectory"/> it is an ordinary function over an array.
    /// </remarks>
    public sealed class SpawnPointSelectionTests
    {
        /// <summary>A directory backed by owner ids; <see langword="null"/> is an empty slot.</summary>
        private sealed class FakeSpawnPoints : ISpawnPointDirectory
        {
            private readonly int?[] _owners;

            internal FakeSpawnPoints(params int?[] owners) => _owners = owners;

            /// <summary>How many times a position was actually asked for.</summary>
            internal int PositionsRequested { get; private set; }

            public int Count => _owners.Length;

            public bool IsEligible(int index, int team)
            {
                int? owner = _owners[index];
                if (owner == null) return false;              // the point == null branch
                return owner.Value < 0 || owner.Value == team; // owner < 0 means any team
            }

            public Vector3 GetSpawnPosition(int index)
            {
                PositionsRequested++;
                return new Vector3(index, 0f, 0f);
            }
        }

        [Test]
        public void AnEmptyDirectoryChoosesNothing()
        {
            Assert.AreEqual(-1, ServerCombatBridge.ChooseSpawnIndex(new FakeSpawnPoints(), 0));
        }

        [Test]
        public void SlotsHoldingNoPointAreSkipped()
        {
            var points = new FakeSpawnPoints(null, null, null);

            Assert.AreEqual(-1, ServerCombatBridge.ChooseSpawnIndex(points, 0));
        }

        [Test]
        public void OnlyPointsThisTeamOwnsAreEverChosen()
        {
            // Owners 0, 1, 1, 0. Team 1 may only ever land on index 1 or 2.
            var points = new FakeSpawnPoints(0, 1, 1, 0);

            for (int attempt = 0; attempt < 200; attempt++)
            {
                int chosen = ServerCombatBridge.ChooseSpawnIndex(points, 1);
                Assert.That(chosen, Is.EqualTo(1).Or.EqualTo(2),
                    $"attempt {attempt} put team 1 on index {chosen}, which another team owns");
            }
        }

        [Test]
        public void APointOwnedByNobodyIsEligibleForEveryTeam()
        {
            var points = new FakeSpawnPoints(-1);

            for (int team = 0; team < 4; team++)
                Assert.AreEqual(0, ServerCombatBridge.ChooseSpawnIndex(points, team));
        }

        [Test]
        public void ATeamWithNoPointOfItsOwnChoosesNothing()
        {
            // Every point belongs to team 0; team 3 has nowhere to go and must stay put rather
            // than spawn on someone else's line.
            var points = new FakeSpawnPoints(0, 0, 0);

            Assert.AreEqual(-1, ServerCombatBridge.ChooseSpawnIndex(points, 3));
        }

        [Test]
        public void SamplingReachesEveryEligiblePointRatherThanAlwaysTheFirst()
        {
            var points = new FakeSpawnPoints(-1, -1, -1);
            var seen = new bool[3];

            for (int attempt = 0; attempt < 300; attempt++)
                seen[ServerCombatBridge.ChooseSpawnIndex(points, 0)] = true;

            Assert.IsTrue(seen[0] && seen[1] && seen[2],
                $"300 draws only ever reached [{seen[0]}, {seen[1]}, {seen[2]}] — the reservoir "
                + "sampling has collapsed onto one point");
        }

        // ---- PinnedSpawnPointDirectory - the X-22 fix -----------------------------------

        /// <summary>
        /// Retired (X-28): a single pinned slot puts every same-team player on the exact same
        /// point, which stacks same-team clients on top of one another and puts them in each
        /// other's fire before any check that names an ENEMY has a chance to matter. Per
        /// <c>pinned-baseline-test-companion.md</c> this is inverted, not re-pinned:
        /// <see cref="APinnedDirectoryRotatesThroughItsSlotsAcrossPlacements"/> below asserts the
        /// rotation a three-element pin now produces instead of the constant a single-element
        /// one used to.
        /// </summary>
        [Test]
        public void APinnedDirectoryRotatesThroughItsSlotsAcrossPlacements()
        {
            // Team 0 rotates 3, 4, 5; team 1 stays on its own single slot, 0. Every point is
            // owner -1 (any team), so nothing here starves.
            var points = new PinnedSpawnPointDirectory(
                new FakeSpawnPoints(-1, -1, -1, -1, -1, -1),
                new[] { new[] { 3, 4, 5 }, new[] { 0 } });

            int[] expected = { 3, 4, 5, 3, 4, 5, 3 };
            for (int placement = 0; placement < expected.Length; placement++)
            {
                int chosen = ServerCombatBridge.ChooseSpawnIndex(points, 0);
                Assert.AreEqual(expected[placement], chosen,
                    $"placement {placement} chose {chosen}, expected {expected[placement]} - "
                    + "the rotation stopped advancing or wrapped wrong");

                // A real placement always asks for the position exactly once
                // (ChoosingAPointDoesNotAskAnyPointForItsPosition, below) - and that call is
                // what advances PinnedSpawnPointDirectory's rotation cursor (X-28). Without it
                // the cursor never moves and every placement would repeat slot 3.
                points.GetSpawnPosition(chosen);
            }

            // Team 1's one-element rotation never had anywhere else to go, and never widened
            // into team 0's slots.
            Assert.AreEqual(0, ServerCombatBridge.ChooseSpawnIndex(points, 1));
        }

        /// <summary>
        /// X-63 extended to a rotation: every slot in every team's list is checked at
        /// construction, not merely the first one drawn — a bad slot buried three deep would
        /// otherwise starve a placement in the middle of a run instead of refusing at the top.
        /// </summary>
        [Test]
        public void ARotationContainingASlotTheOtherTeamCannotUseThrowsAtConstruction()
        {
            // Team 0's rotation is [0, 1, 2]; index 1 belongs to team 1 alone (owner 1), so
            // team 0 would be starved the moment its rotation reaches that slot.
            var points = new FakeSpawnPoints(-1, 1, -1);

            var ex = Assert.Throws<ArgumentException>(() =>
                new PinnedSpawnPointDirectory(points, new[] { new[] { 0, 1, 2 }, new[] { 1 } }));

            StringAssert.Contains("X-63", ex.Message);
            StringAssert.Contains("slot 1", ex.Message);
            StringAssert.Contains("team 0", ex.Message);
        }

        [Test]
        public void APinnedDirectoryNarrowsAndNeverWidens()
        {
            // Pinning index 0, which team 0 owns. Team 1 must still get nothing rather than be
            // handed a point the team rule forbids: the pin removes candidates, it does not
            // grant eligibility.
            var points = new PinnedSpawnPointDirectory(new FakeSpawnPoints(0, -1, -1), 0);

            Assert.AreEqual(0, ServerCombatBridge.ChooseSpawnIndex(points, 0));
            Assert.AreEqual(-1, ServerCombatBridge.ChooseSpawnIndex(points, 1),
                "the pin widened eligibility - a pinned point owned by one team must starve the "
                + "other, loudly, rather than silently admit it");
        }

        [Test]
        public void PinningAnEmptySlotChoosesNothingRatherThanFallingBack()
        {
            // A fallback to sampling here would be the exact failure X-22 describes: a run that
            // quietly stopped being deterministic. -1 trips MoveToSpawnPoint's existing warning.
            var points = new PinnedSpawnPointDirectory(new FakeSpawnPoints(-1, null, -1), 1);

            Assert.AreEqual(-1, ServerCombatBridge.ChooseSpawnIndex(points, 0));
        }

        [Test]
        public void APinnedIndexIsASlotAndNeverTheNoPointSentinel()
        {
            // -1 is what ChooseSpawnIndex RETURNS for "nowhere to go". Accepting it as an input
            // would make a typo read as a deliberate pin onto nothing.
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => new PinnedSpawnPointDirectory(new FakeSpawnPoints(-1), -1));
        }

        [Test]
        public void APinnedDirectoryDelegatesCountAndPosition()
        {
            var inner = new FakeSpawnPoints(-1, -1, -1);
            var points = new PinnedSpawnPointDirectory(inner, 1);

            Assert.AreEqual(3, points.Count, "Count must stay the scene's, not the pin's");
            Assert.AreEqual(1, points.PinnedIndex);
            Assert.AreEqual(new Vector3(1f, 0f, 0f), points.GetSpawnPosition(1));
        }

        [Test]
        public void ChoosingAPointDoesNotAskAnyPointForItsPosition()
        {
            // SpawnPoint.GetSpawnPosition is virtual and subclasses jitter the result, so asking
            // every candidate in order to pick between them would be a different behaviour.
            var points = new FakeSpawnPoints(-1, -1, -1);

            ServerCombatBridge.ChooseSpawnIndex(points, 0);

            Assert.AreEqual(0, points.PositionsRequested);
        }

        // ---- ScriptedAim.SteerToward - the X-66 route-steering arithmetic ----------------

        [Test]
        public void SteeringDueEastWhileFacingNorthIsAPureRightStrafe()
        {
            // Facing yaw 0 is "north" in ScriptedAim's own frame (faces +Z). A corner due east
            // (+X, same Z) is a pure strafe: legs move right, the body keeps facing forward.
            ScriptedAim.SteerToward(
                fromX: 0f, fromZ: 0f, toX: 10f, toZ: 0f, facingYawDegrees: 0f,
                out float moveX, out float moveZ);

            Assert.AreEqual(1f, moveX, 0.001f);
            Assert.AreEqual(0f, moveZ, 0.001f);
        }

        [Test]
        public void SteeringAtACornerTheBodyAlreadyFacesIsPureForward()
        {
            // Facing yaw 90 (east) with a corner due east of the walker: the bearing to the
            // corner and the facing agree, so the quantized direction is pure forward.
            ScriptedAim.SteerToward(
                fromX: 0f, fromZ: 0f, toX: 10f, toZ: 0f, facingYawDegrees: 90f,
                out float moveX, out float moveZ);

            Assert.AreEqual(0f, moveX, 0.001f);
            Assert.AreEqual(1f, moveZ, 0.001f);
        }

        [Test]
        public void SteeringAtTheWalkersOwnPositionHoldsStill()
        {
            ScriptedAim.SteerToward(
                fromX: 5f, fromZ: 5f, toX: 5f, toZ: 5f, facingYawDegrees: 37f,
                out float moveX, out float moveZ);

            Assert.AreEqual(0f, moveX);
            Assert.AreEqual(0f, moveZ);
        }

        [Test]
        public void SteeringOutputIsAlwaysOneOfTheEightQuantizedAxisValues()
        {
            float[] allowed = { -1f, 0f, 1f };

            for (float bearingDeg = 0f; bearingDeg < 360f; bearingDeg += 7f)
            {
                double radians = bearingDeg * Math.PI / 180.0;
                float toX = (float)Math.Sin(radians) * 10f;
                float toZ = (float)Math.Cos(radians) * 10f;

                ScriptedAim.SteerToward(
                    0f, 0f, toX, toZ, facingYawDegrees: 0f, out float moveX, out float moveZ);

                Assert.Contains(moveX, allowed, $"bearing {bearingDeg} produced moveX={moveX}");
                Assert.Contains(moveZ, allowed, $"bearing {bearingDeg} produced moveZ={moveZ}");
            }
        }

        // ---- ScriptedRouteCursor - the X-66 route cursor ----------------------------------

        [Test]
        public void ARouteCursorAdvancesOnceTheWalkerEntersTheCornersRadius()
        {
            var route = new ScriptedRouteCursor(
                xs: new[] { 10f, 10f }, zs: new[] { 0f, 10f }, cornerRadiusMeters: 2f);

            Assert.AreEqual(0, route.CornerIndex);
            Assert.IsTrue(route.Advance(0f, 0f),
                "far from the first corner - the route must not report finished");
            Assert.AreEqual(0, route.CornerIndex,
                "still outside the radius - must not have advanced yet");

            Assert.IsTrue(route.Advance(9f, 0f),
                "inside the first corner's radius - must advance to the second corner");
            Assert.AreEqual(1, route.CornerIndex);
        }

        [Test]
        public void ARouteCursorReportsFinishedOnceTheLastCornerIsPassed()
        {
            var route = new ScriptedRouteCursor(
                xs: new[] { 0f }, zs: new[] { 0f }, cornerRadiusMeters: 1f);

            Assert.IsFalse(route.Advance(0f, 0f), "already inside the only corner's radius");
            Assert.IsTrue(route.Finished);
            Assert.AreEqual(0f, route.CurrentCornerX);
            Assert.AreEqual(0f, route.CurrentCornerZ);
        }

        [Test]
        public void ARouteCursorNeverRegressesEvenIfTheWalkerRetreats()
        {
            var route = new ScriptedRouteCursor(
                xs: new[] { 10f, 20f }, zs: new[] { 0f, 0f }, cornerRadiusMeters: 1f);

            route.Advance(10f, 0f); // consumes the first corner
            Assert.AreEqual(1, route.CornerIndex);

            // Walking back onto the ALREADY-PASSED first corner must not re-trigger it: the
            // cursor is monotonic, exactly like ScriptedInputCursor's own StepIndex.
            route.Advance(10f, 0f);
            Assert.AreEqual(1, route.CornerIndex,
                "the cursor regressed - a corner already passed was re-entered");
        }
    }
}
