using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Ironfront.Net.Replication.Vehicles;
using Ironfront.Net.Unity.Diagnostics;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// orphan-closure O2 — the <c>approachVehicle</c> verb. Ledger <b>X-44</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What the row was.</b> R2 gave a scripted client a way to ASK for a seat, and asking is
    /// not reaching: <c>ClientSeatRequester.TryFindNearestSeat</c> only sees seats within
    /// <see cref="SeatArbiter.MaxSeatReachMetres"/> of where the player is already standing, and
    /// the programme vocabulary had no verb that walks to one — <c>approach</c> resolves a
    /// player DISPLAY NAME, and a vehicle has none. So a driver programme's first step only
    /// worked if a vehicle happened to be parked next to the pinned spawn point.
    /// </para>
    /// <para>
    /// <b>What is testable here and what is not.</b> Only <c>ScriptedAim.cs</c> and
    /// <c>ScriptedInputProgramme.cs</c> reach <c>dotnet test</c>, through the
    /// <c>&lt;Compile Include&gt;</c> links — the solver, the input source, the harness and the
    /// recorder all name <c>UnityEngine</c>. So the arithmetic and the programme model are
    /// graded behaviourally, and the three Unity halves are pinned as source text, which is the
    /// same split <see cref="ScriptedAimTests"/> already uses.
    /// </para>
    /// </remarks>
    public sealed class ApproachVehicleTests
    {
        // ------------------------------------------------ the nearest-within scan

        [Fact]
        public void TheNearestCandidateInsideTheRadiusWins()
        {
            var xs = new[] { 30f, 5f, 12f };
            var zs = new[] { 0f, 0f, 0f };

            Assert.Equal(1, ScriptedAim.NearestIndexWithin(0f, 0f, xs, zs, 3, 120f));
        }

        [Fact]
        public void NothingInsideTheRadiusIsAMissRatherThanTheLeastBadCandidate()
        {
            // The failure this forbids is the one that reads as success: returning the nearest
            // of a set that is entirely out of range would send the client walking at a vehicle
            // 400 m away for the whole step, and the artifact would show a resolved target.
            var xs = new[] { 400f, 900f };
            var zs = new[] { 0f, 0f };

            Assert.Equal(-1, ScriptedAim.NearestIndexWithin(0f, 0f, xs, zs, 2, 120f));
        }

        [Fact]
        public void AnEmptyOrUnusableCandidateSetIsAMiss()
        {
            Assert.Equal(-1, ScriptedAim.NearestIndexWithin(0f, 0f, new float[4], new float[4], 0, 120f));
            Assert.Equal(-1, ScriptedAim.NearestIndexWithin(0f, 0f, null!, new float[4], 4, 120f));
            Assert.Equal(-1, ScriptedAim.NearestIndexWithin(0f, 0f, new float[4], null!, 4, 120f));

            // A non-positive radius is a programme that asked for nothing, not one that asked
            // for everything.
            Assert.Equal(-1, ScriptedAim.NearestIndexWithin(0f, 0f, new[] { 1f }, new[] { 0f }, 1, 0f));
        }

        [Fact]
        public void CountBoundsTheScanRatherThanTheArrayLength()
        {
            // The caller reuses its arrays across frames, so Length is CAPACITY and not
            // population. Scanning to Length would let a vehicle that despawned last frame win
            // this frame -- a target that is not there, reported as resolved.
            var xs = new float[8];
            var zs = new float[8];

            xs[0] = 50f;   // live
            xs[1] = 2f;    // stale leftover, nearer

            Assert.Equal(0, ScriptedAim.NearestIndexWithin(0f, 0f, xs, zs, 1, 120f));
        }

        [Fact]
        public void ATieGoesToTheLowerIndexSoTwoRunsOfAProgrammeAgree()
        {
            // Two vehicles equidistant is the ordinary case at a spawn pad. Picking whichever
            // the comparison happened to see first would make one run differ from the next,
            // which is the single property lane B is buying.
            var xs = new[] { 10f, -10f };
            var zs = new[] { 0f, 0f };

            Assert.Equal(0, ScriptedAim.NearestIndexWithin(0f, 0f, xs, zs, 2, 120f));
        }

        [Fact]
        public void TheRadiusBandIsClosedAtTheTopLikeTheHoldBand()
        {
            // ApproachMoveZ stops AT the hold distance rather than inside it. A search band that
            // was open at the top would disagree with it about the same vehicle at the same
            // distance, which is the sort of off-by-one nothing reports.
            var xs = new[] { 120f };
            var zs = new[] { 0f };

            Assert.Equal(0, ScriptedAim.NearestIndexWithin(0f, 0f, xs, zs, 1, 120f));
            Assert.Equal(-1, ScriptedAim.NearestIndexWithin(0f, 0f, xs, zs, 1, 119.9f));
        }

        [Fact]
        public void DistanceIsPlanarSoASlopeDoesNotChangeWhichVehicleIsNearest()
        {
            // The arbiter measures its own distance, and the approach has to agree with it or
            // the client stops where the seat request is refused. Height is not part of either.
            var xs = new[] { 5f };
            var zs = new[] { 0f };

            Assert.Equal(0, ScriptedAim.NearestIndexWithin(0f, 0f, xs, zs, 1, 6f));
            Assert.Equal(5f, ScriptedAim.PlanarDistance(0f, 0f, 5f, 0f), 4);
        }

        // ------------------------------------------------ the hold distance and the arbiter

        [Fact]
        public void AVehicleApproachStopsInsideTheReachTheArbiterMeasures()
        {
            // THE load-bearing test in this file. A step that precedes a seatToggle must stop
            // within SeatArbiter.MaxSeatReachMetres or the request comes back RejectedTooFar --
            // a round trip spent to be told no, and an artifact showing a client standing next
            // to a vehicle it never got into.
            //
            // Asserted against the arbiter's own constant rather than against a number restated
            // here, so moving the reach moves this test with it.
            var step = new ScriptedInputStep();

            Assert.True(
                step.vehicleHoldDistanceMeters < SeatArbiter.MaxSeatReachMetres,
                $"the default vehicle hold distance ({step.vehicleHoldDistanceMeters} m) is not "
                + $"inside SeatArbiter.MaxSeatReachMetres ({SeatArbiter.MaxSeatReachMetres} m), "
                + "so a programme leaving it unset would stop where the seat request is refused.");

            // And it is a SEPARATE field from the player one, which is 8 m and therefore outside
            // that reach. Folding them would make the default silently wrong for one of the two.
            Assert.True(step.holdDistanceMeters > SeatArbiter.MaxSeatReachMetres);
        }

        [Fact]
        public void TheApproachStopsAtTheHoldDistanceAndWalksOutsideIt()
        {
            var step = new ScriptedInputStep();

            Assert.Equal(1f, ScriptedAim.ApproachMoveZ(30f, step.vehicleHoldDistanceMeters));
            Assert.Equal(0f, ScriptedAim.ApproachMoveZ(step.vehicleHoldDistanceMeters, step.vehicleHoldDistanceMeters));
            Assert.Equal(0f, ScriptedAim.ApproachMoveZ(1f, step.vehicleHoldDistanceMeters));
        }

        // ------------------------------------------------ the programme-level conflict

        [Fact]
        public void AStepNamingBothAVehicleAndAPlayerIsRejectedByIndex()
        {
            var programme = new ScriptedInputProgramme
            {
                steps = new[]
                {
                    new ScriptedInputStep { moveZ = 1f },
                    new ScriptedInputStep { approachVehicle = true, aimAtPlayer = "OBS-A" },
                },
            };

            // 1-based, because the number is quoted to a human editing JSON.
            Assert.Equal(2, programme.FindConflictingStep());
        }

        [Fact]
        public void ACleanProgrammeReportsZeroAndEitherVerbAloneIsClean()
        {
            Assert.Equal(0, new ScriptedInputProgramme().FindConflictingStep());

            Assert.Equal(0, new ScriptedInputProgramme
            {
                steps = new[]
                {
                    new ScriptedInputStep { approachVehicle = true },
                    new ScriptedInputStep { approach = true, aimAtPlayer = "OBS-A" },
                    null!,
                },
            }.FindConflictingStep());
        }

        // ------------------------------------------------ the Unity halves, pinned as text

        [Fact]
        public void TheSolverResolvesVehiclesThroughThePoseSeamAndNotByReachingInside()
        {
            // RemoteVehicleRegistry.TryGetPose exists so NetClientVehicle does not have to be
            // public -- its own remark says widening that type would export a collaborator of
            // the vehicle stage as API, and InternalsVisibleTo("Assembly-CSharp") would open
            // every internal to four hundred legacy files. A harness reaching past it would undo
            // phase C4c's seam for a convenience.
            string solver = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Net/Diagnostics/ScriptedTargetSolver.cs");

            Assert.Contains("public Solution SolveNearestVehicle(", solver, StringComparison.Ordinal);
            Assert.Contains("vehicles.TryGetPose(", solver, StringComparison.Ordinal);

            // Matched on the CALL, not on the type name: check-net-layering.ps1 learned this the
            // hard way in phase C2 -- fifteen of its sixteen FpsActorController "references" were
            // doc comments, and a gate that counts prose gets muted by the first person who has
            // to explain the seam. The remark above names NetClientVehicle on purpose.
            Assert.DoesNotMatch(new Regex(@"vehicles\.TryFind\s*\("), solver);
            Assert.DoesNotMatch(new Regex(@"\.Body\.Transform"), solver);

            // The arithmetic stays on the engine-free side. This file's own remark says anything
            // that starts computing in it has left coverage.
            Assert.Contains("ScriptedAim.NearestIndexWithin(", solver, StringComparison.Ordinal);
        }

        [Fact]
        public void ThePlayerSolveAndTheVehicleSolveDoNotShareAMemoKey()
        {
            // Both memoize on Time.frameCount so three callers in one frame get ONE answer. With
            // only the frame as the key, a player solve and a vehicle solve in the same frame
            // return each other's -- the client would face a car and walk at a person.
            string solver = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Net/Diagnostics/ScriptedTargetSolver.cs");

            Assert.Matches(
                new Regex(@"_solvedFrame == Time\.frameCount\s*&&\s*_solvedIsVehicle"), solver);
            Assert.Matches(
                new Regex(@"_solvedFrame == Time\.frameCount\s*\r?\n\s*&&\s*!_solvedIsVehicle"),
                solver);
        }

        [Fact]
        public void TheMovementHalfUsesTheVehicleHoldDistanceForAVehicleStep()
        {
            // Reading step.holdDistanceMeters for a vehicle step would stop the client at 8 m --
            // outside SeatArbiter.MaxSeatReachMetres -- and the seat request would be refused
            // while every artifact showed a resolved target and a client that walked.
            string harness = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Net/Diagnostics/LaneBHarness.cs");

            Assert.Matches(
                new Regex(
                    @"step\.approachVehicle\s*\r?\n?\s*\?\s*step\.vehicleHoldDistanceMeters"
                    + @"\s*\r?\n?\s*:\s*step\.holdDistanceMeters"),
                harness);

            // ... and the verb reaches the movement half at all.
            Assert.Contains("step.approach || step.approachVehicle", harness, StringComparison.Ordinal);

            // The conflicting-verb check is at LOAD time, where a programme defect belongs.
            Assert.Contains("programme.FindConflictingStep()", harness, StringComparison.Ordinal);
        }

        [Fact]
        public void TheRecordNamesTheVehicleItResolvedRatherThanWritingNull()
        {
            // AppendAim wrote `aim: null` whenever no NAME was requested. A vehicle solve
            // requests no name, so without the second half of that question an approach that
            // resolved a vehicle and a step that never ran produce the same artifact -- the
            // exact confusion that block's own remark exists to prevent.
            string recorder = ReadUnitySource(
                "Ironfront_Reborn/Assets/Scripts/Net/Diagnostics/LaneBCheckpointRecorder.cs");

            Assert.Contains("_solver.LastRequestWasVehicle", recorder, StringComparison.Ordinal);
            Assert.Contains("targetVehicleId", recorder, StringComparison.Ordinal);
        }

        // ------------------------------------------------ helpers

        private static string ReadUnitySource(string relativePath)
        {
            string path = Path.Combine(
                RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(path), $"missing Unity source: {path}");
            return File.ReadAllText(path);
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }
    }
}
