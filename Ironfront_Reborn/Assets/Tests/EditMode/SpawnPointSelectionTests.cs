using System;
using NUnit.Framework;
using UnityEngine;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
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
        /// <summary>
        /// A directory backed by owner ids; <see langword="null"/> is an empty slot and
        /// <c>-1</c> is a <b>wildcard</b> slot that every team may use.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The wildcard is this fake's own convenience and NOT the scene's rule.</b>
        /// <c>ActorManagerSpawnPoints.IsEligible</c> is <c>point.owner == team</c> — a real
        /// spawn point is a <c>CapturePoint</c> and <c>owner == -1</c> means neutral, not
        /// "anyone" (see that method's remarks and <see cref="CapturePointOwners"/> below, which
        /// models the real rule). The wildcard survives here because the
        /// <see cref="PinnedSpawnPointDirectory"/> tests further down are about rotation and
        /// refusal rather than ownership, and spelling out a team per slot in each of them would
        /// bury what they actually assert under bookkeeping.
        /// </para>
        /// </remarks>
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
                return owner.Value < 0 || owner.Value == team; // test-only wildcard, see remarks
            }

            public Vector3 GetSpawnPosition(int index)
            {
                PositionsRequested++;
                return new Vector3(index, 0f, 0f);
            }
        }

        /// <summary>
        /// A directory with the shipping rule — <c>owner == team</c>, exactly what
        /// <c>ActorManagerSpawnPoints</c> asks of a real <c>CapturePoint</c>.
        /// </summary>
        /// <remarks>
        /// Kept separate from <see cref="FakeSpawnPoints"/> so the two things being pinned stay
        /// apart: that reservoir sampling honours whatever a directory says, and that the
        /// directory a live scene installs says <i>neutral ground belongs to nobody</i>.
        /// </remarks>
        private sealed class CapturePointOwners : ISpawnPointDirectory
        {
            private readonly int[] _owners;

            internal CapturePointOwners(params int[] owners) => _owners = owners;

            public int Count => _owners.Length;

            public bool IsEligible(int index, int team) => _owners[index] == team;

            public Vector3 GetSpawnPosition(int index) => new Vector3(index, 0f, 0f);
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

        /// <summary>
        /// The sampler asks the directory and does not second-guess it: a slot the directory
        /// calls eligible for every team is chosen for every team.
        /// </summary>
        /// <remarks>
        /// This test used to be named <c>APointOwnedByNobodyIsEligibleForEveryTeam</c> and was
        /// read as a statement about the map. It never was one — it exercises
        /// <see cref="FakeSpawnPoints"/>' test-only wildcard. On a real map an unowned point is a
        /// NEUTRAL capture point and belongs to nobody;
        /// <see cref="ANeutralPointBelongsToNobodyAndIsChosenByNobody"/> pins that.
        /// </remarks>
        [Test]
        public void AWildcardSlotIsChosenForEveryTeam()
        {
            var points = new FakeSpawnPoints(-1);

            for (int team = 0; team < 4; team++)
                Assert.AreEqual(0, ServerCombatBridge.ChooseSpawnIndex(points, team));
        }

        /// <summary>
        /// The shipping rule: a neutral capture point is nobody's spawn. Placing a deploying
        /// player on one is what emptied the map on 2026-09-04 — alone on a contested flag,
        /// every bot of their own team 500 m away at the base and therefore culled out of the
        /// snapshot (X-17), and the flag itself authored out on the heightmap rim.
        /// </summary>
        [Test]
        public void ANeutralPointBelongsToNobodyAndIsChosenByNobody()
        {
            // Dustbowl's authored owners: Oasis 0, Fortress 1, and four neutral flags.
            var points = new CapturePointOwners(0, -1, -1, 1, -1, -1);

            for (int draw = 0; draw < 200; draw++)
            {
                Assert.AreEqual(0, ServerCombatBridge.ChooseSpawnIndex(points, 0),
                    "team 0 was placed somewhere other than the one point it owns");
                Assert.AreEqual(3, ServerCombatBridge.ChooseSpawnIndex(points, 1),
                    "team 1 was placed somewhere other than the one point it owns");
            }
        }

        /// <summary>
        /// The fallback in <c>MoveToSpawnPoint</c>: a team that has lost every flag asks on
        /// behalf of owner -1, and that finds the neutral points and only those.
        /// </summary>
        /// <remarks>
        /// Standing on contested ground beats the alternative, which is not "spawn later" but
        /// staying at the prefab origin, alive, falling, until the wire-volume guard kills the
        /// body for leaving the world.
        /// </remarks>
        [Test]
        public void ATeamThatHasLostEveryFlagFallsBackOntoNeutralGround()
        {
            var points = new CapturePointOwners(0, -1, 0, -1);
            var seenNeutral = new bool[4];

            Assert.AreEqual(-1, ServerCombatBridge.ChooseSpawnIndex(points, 1),
                "team 1 owns nothing here and must draw the no-point sentinel");

            for (int draw = 0; draw < 300; draw++)
            {
                int chosen = ServerCombatBridge.ChooseSpawnIndex(points, -1);
                Assert.That(chosen, Is.EqualTo(1).Or.EqualTo(3),
                    $"draw {draw} fell back onto index {chosen}, which a team owns");
                seenNeutral[chosen] = true;
            }

            Assert.IsTrue(seenNeutral[1] && seenNeutral[3],
                "the fallback collapsed onto a single neutral point");
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

        // ---- StandingBodyPosition - the capsule lift a spawn placement owes the ground --------

        /// <summary>
        /// A ground position is lifted to the capsule's centre, and only vertically.
        /// </summary>
        /// <remarks>
        /// The numbers are the real ones: <c>artifacts/lane-b/predict-01</c> placed actor 33 at
        /// spawn point 3, ground <c>(2085.34, 8.82, 1139.82)</c>.
        /// </remarks>
        [Test]
        public void AGroundPositionIsLiftedToTheCapsuleCentre()
        {
            var ground = new Vector3(2085.34f, 8.82f, 1139.82f);

            Vector3 body = ServerCombatBridge.StandingBodyPosition(ground);

            Assert.AreEqual(ground.x, body.x, 0.0001f, "the lift moved the body horizontally");
            Assert.AreEqual(ground.z, body.z, 0.0001f, "the lift moved the body horizontally");
            Assert.AreEqual(ground.y + MovementCore.StandHeight * 0.5f, body.y, 0.0001f);
        }

        /// <summary>
        /// The lift is derived from the stance height, not written down twice.
        /// </summary>
        /// <remarks>
        /// <c>NetMovementAgent.ApplyStanceHeight</c> assigns the capsule
        /// <c>MovementCore.HeightFor(IsCrouching)</c>. If this lift stopped tracking that function
        /// the placement would be measured against a capsule the agent never builds — which is the
        /// original defect with an extra step, not a fix.
        /// </remarks>
        [Test]
        public void TheLiftIsHalfTheStanceHeightTheAgentWillBuild()
        {
            Assert.AreEqual(
                MovementCore.HeightFor(crouching: false) * 0.5f,
                ServerCombatBridge.StandingLiftMetres,
                0.0001f);

            Assert.AreEqual(0.9f, ServerCombatBridge.StandingLiftMetres, 0.0001f,
                "the player prefab's CharacterController is height 1.8 with center.y = 0, so the "
                + "capsule runs 0.9 m below the transform");
        }

        /// <summary>
        /// The lift collapses the measured disagreement to inside the reconciler's tolerance.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the test that would go red on the pre-fix line, and the only one here that
        /// pins the CONSEQUENCE rather than the arithmetic. Unplifted, the server held actor 33 at
        /// <c>y = 8.82</c> while the client's <c>CharacterController</c> de-penetrated the buried
        /// capsule to <c>y = 9.81</c> — ground + 0.9 + the 0.08 skin width — and
        /// <see cref="PredictionReconciler.PositionToleranceMetres"/> is 0.25, so every single
        /// snapshot was a correction: 197 of them in ten seconds, with <c>err = 0.98 m</c>
        /// unchanged throughout (<c>artifacts/lane-b/predict-01</c>, <c>[predict]</c> lines).
        /// </para>
        /// <para>
        /// Asserted against the tolerance rather than against 0.08 m: what matters is that the
        /// residual is something the reconciler calls agreement, not that it is any particular
        /// number. The skin width is Unity's and is not ours to pin.
        /// </para>
        /// </remarks>
        [Test]
        public void ThePlacedBodyIsWhereCollisionWillLeaveItRatherThanBuriedUnderIt()
        {
            const float measuredGroundY = 8.82f;
            const float measuredSettledY = 9.81f;   // where the client's controller ended up

            float placedY = ServerCombatBridge
                .StandingBodyPosition(new Vector3(2085.34f, measuredGroundY, 1139.82f)).y;

            float residual = Mathf.Abs(measuredSettledY - placedY);

            Assert.Less(residual, PredictionReconciler.PositionToleranceMetres,
                $"a body placed at y={placedY:F3} still sits {residual:F3} m from where "
                + $"collision leaves it ({measuredSettledY:F3}), which is outside the "
                + $"{PredictionReconciler.PositionToleranceMetres} m tolerance -- so every "
                + "snapshot is a correction and the body judders for the whole match");

            Assert.Greater(residual, 0f,
                "the skin width is real: a residual of exactly zero means this test is asserting "
                + "its own arithmetic rather than the measurement");
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
            // Pinning index 0 for team 0, which owns it, and index 2 (owner -1) for team 1.
            // The pin REMOVES candidates; it never grants eligibility, so team 1 must land on
            // its own pinned slot and never on the one team 0 owns.
            var points = new PinnedSpawnPointDirectory(
                new FakeSpawnPoints(0, -1, -1), new[] { 0, 2 });

            Assert.AreEqual(0, ServerCombatBridge.ChooseSpawnIndex(points, 0));
            Assert.AreEqual(2, ServerCombatBridge.ChooseSpawnIndex(points, 1),
                "the pin narrowed team 1 to a slot it cannot use");

            for (int draw = 0; draw < 50; draw++)
            {
                Assert.AreNotEqual(0, ServerCombatBridge.ChooseSpawnIndex(points, 1),
                    "the pin widened eligibility - team 1 was handed a point team 0 owns");
            }
        }

        /// <summary>
        /// A pin that starves a team is refused when it is set, not discovered mid-run.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This assertion used to read the other way: it constructed the starving pin and
        /// checked that the starved team drew -1 at RUNTIME, "loudly". X-63 changed the
        /// decision — the refusal moved to construction, so a bad pin fails at the top of the
        /// run rather than ninety seconds in, once the operator can still fix the flag. The
        /// test was never updated and had been failing on develop ever since, which is why it
        /// is rewritten here rather than re-pinned.
        /// </para>
        /// </remarks>
        [Test]
        public void APinThatStarvesATeamIsRefusedAtConstruction()
        {
            ArgumentException thrown = Assert.Throws<ArgumentException>(
                () => new PinnedSpawnPointDirectory(new FakeSpawnPoints(0, -1, -1), 0));

            // Loudly still means loudly: the message has to name the team that would starve.
            StringAssert.Contains("team 1", thrown.Message);
        }

        [Test]
        public void PinningAnEmptySlotChoosesNothingRatherThanFallingBack()
        {
            // A fallback to sampling would be the exact failure X-22 describes: a run that
            // quietly stopped being deterministic. Pinning a slot with no point behind it is
            // refused where the operator can still act on it (X-63) -- and what matters is
            // that it never silently widens back to a draw.
            ArgumentException thrown = Assert.Throws<ArgumentException>(
                () => new PinnedSpawnPointDirectory(new FakeSpawnPoints(-1, null, -1), 1));

            StringAssert.Contains("1", thrown.Message);

            // And the narrowing itself still holds for a slot that DOES exist: pinning slot 0
            // gives slot 0 and nothing else, rather than falling back to sampling 0 or 2.
            var pinned = new PinnedSpawnPointDirectory(new FakeSpawnPoints(-1, null, -1), 0);

            for (int draw = 0; draw < 50; draw++)
                Assert.AreEqual(0, ServerCombatBridge.ChooseSpawnIndex(pinned, 0));
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
