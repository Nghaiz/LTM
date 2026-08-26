using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Ledger row <b>X-21</b> — the replay that advanced velocity and stance and never the
    /// position.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this file exists when <c>ClientReplicationTests</c> already had a replay test.</b>
    /// It did, and it was green on the broken code:
    /// <c>ACorrectionReplaysTheUnacknowledgedInputsOverAuthority</c> built its expected position
    /// by calling <c>MovementCore.Step</c> — the same method the reconciler called, and the one
    /// that deliberately does not write <c>MoveState.Position</c>. Both sides therefore stayed at
    /// the authoritative position and compared equal, for 2,208 corrections in one measured run.
    /// A test that derives its expectation from the code under test can only ever agree with it.
    /// </para>
    /// <para>
    /// Every expected value below is computed from the movement CONSTANTS instead — walk speed,
    /// tick interval, stick-to-ground force — so the assertion has a second source and the fault
    /// has somewhere to show.
    /// </para>
    /// <para>
    /// <b>And it is a position test, not a correction-count test.</b> Asserting
    /// <c>CorrectionCount == 0</c> would have passed on the post-X-19 idle path, where prediction
    /// had nothing to do because every body was pinned inside the driver's hold distance — the
    /// exact reading B-8 was warned not to accept (<c>green-that-proves-nothing.md</c>).
    /// </para>
    /// </remarks>
    public sealed class PredictionReplayTests
    {
        private const float Dt = 1f / Protocol.ProtocolConstants.SIM_TICK_RATE;

        /// <summary>Forward at a walk, no jump, no crouch, no sprint.</summary>
        private static MoveInput Forward => new MoveInput(0f, 1f, 0f, false, false, false);

        [Fact]
        public void ReplayAdvancesThePositionByEveryUnacknowledgedInput()
        {
            // X-21, stated in one assertion: after replaying N unacknowledged inputs over a
            // corrected base, the predicted position is the base ADVANCED BY THOSE N INPUTS.
            //
            // Observed RED against the pre-fix tree, where the replay left the position at the
            // authoritative 3.000 m and the client lost every metre of unacknowledged motion.
            var reconciler = new PredictionReconciler();

            for (uint tick = 1; tick <= 5; tick++) reconciler.Record(tick, Forward);

            var authoritative = MoveState.AtRest(new Vec3(0f, 10f, 3f), grounded: false);
            var predicted = MoveState.AtRest(new Vec3(0f, 10f, 9f), grounded: false);

            // Airborne, so the vertical channel is pure gravity and the arithmetic below is the
            // simulation's own definition rather than an approximation of a collision.
            ReconcileResult result = reconciler.Reconcile(ref predicted, authoritative, 3, Dt);

            Assert.Equal(ReconcileResult.Corrected, result);
            Assert.Equal(2, reconciler.ReplayedInputCount);

            // Two ticks of walking, from the constants: 3.5 m/s at 1/30 s each.
            const float perTick = MovementCore.WalkSpeed * Dt;
            Assert.Equal(3f + 2f * perTick, predicted.Position.Z, 4);

            // And it genuinely moved off authority, which is the whole of the row.
            Assert.True(predicted.Position.Z > 3f,
                        $"replay left the position on the server's stale value ({predicted.Position.Z:F4} m): "
                        + "MovementCore.Step's return value is being discarded (ledger X-21)");
        }

        [Fact]
        public void AirborneReplayIntegratesGravityIntoThePosition()
        {
            // The vertical half of the same fault. Velocity was already being replayed correctly;
            // it was the position that never received it, so a client corrected mid-fall resumed
            // from the server's stale altitude with the fall it had already predicted discarded.
            var reconciler = new PredictionReconciler();

            for (uint tick = 1; tick <= 3; tick++) reconciler.Record(tick, Forward);

            var authoritative = new MoveState
            {
                Position = new Vec3(0f, 50f, 0f),
                Velocity = new Vec3(0f, 0f, 0f),
                IsGrounded = false,
            };
            var predicted = MoveState.AtRest(new Vec3(0f, 40f, 0f), grounded: false);

            reconciler.Reconcile(ref predicted, authoritative, 0, Dt);

            Assert.Equal(3, reconciler.ReplayedInputCount);

            // v(n) = n * g * dt, and the position takes v AFTER the step, so the drop over three
            // ticks is (1 + 2 + 3) * g * dt^2.
            const float expectedDrop = 6f * MovementCore.Gravity * Dt * Dt;
            Assert.Equal(50f + expectedDrop, predicted.Position.Y, 4);
            Assert.True(predicted.Position.Y < 50f, "an airborne replay did not fall");
        }

        [Fact]
        public void AGroundedReplayCarriesTheStickToGroundForceAndTheCorrectionAbsorbsIt()
        {
            // Stated rather than discovered later. `MovementCore.Step` asks for
            // -StickToGroundForce every grounded tick, and on both the server and an ordinary
            // predicted tick the collision system refuses it — that is what the force is FOR. The
            // replay has no collision system, so an N-input replay asks to descend N * 0.333 m.
            //
            // This is not a silent fallback: the client applies a correction through
            // `NetMovementAgent.ApplyCorrectedState`, whose non-resync path moves the body with
            // `CharacterMove` and writes back where collision actually left it — a grounded body
            // does not sink. Pinned here so the number is on the record and a reader who finds it
            // in a log knows it is expected and knows what absorbs it.
            var reconciler = new PredictionReconciler();

            for (uint tick = 1; tick <= 4; tick++) reconciler.Record(tick, Forward);

            var authoritative = MoveState.AtRest(new Vec3(0f, 5f, 0f));           // grounded
            var predicted = MoveState.AtRest(new Vec3(0f, 5f, 4f));

            reconciler.Reconcile(ref predicted, authoritative, 0, Dt);

            Assert.Equal(4, reconciler.ReplayedInputCount);
            Assert.Equal(5f - 4f * MovementCore.StickToGroundForce * Dt, predicted.Position.Y, 4);

            // The horizontal channel — the one that matters — is exact.
            Assert.Equal(4f * MovementCore.WalkSpeed * Dt, predicted.Position.Z, 4);
        }

        [Fact]
        public void AReplayOfNothingLeavesThePositionOnAuthority()
        {
            // The boundary the fix must not overshoot: every held input acknowledged, positions
            // disagreeing anyway. There is no unacknowledged motion to add, so authority IS the
            // answer, and adding a step here would leave the client permanently ahead of it.
            var reconciler = new PredictionReconciler();

            for (uint tick = 1; tick <= 3; tick++) reconciler.Record(tick, Forward);

            var authoritative = MoveState.AtRest(new Vec3(0f, 0f, 12f));
            var predicted = MoveState.AtRest(new Vec3(0f, 0f, 0f));

            ReconcileResult result = reconciler.Reconcile(ref predicted, authoritative, 3, Dt);

            Assert.Equal(ReconcileResult.Corrected, result);
            Assert.Equal(0, reconciler.ReplayedInputCount);
            Assert.Equal(12f, predicted.Position.Z, 4);
        }

        [Fact]
        public void ACorrectedClientIsNotMOVEDByTheCorrection()
        {
            // What X-21 cost in the field: `corrections: 2208` in a 136 s run that never
            // converged, with pendingInputs pinned at Capacity. But the counter is the wrong
            // instrument for it -- see `TheCorrectionCounterMeasuresLagNotMisprediction` below --
            // so this asserts the thing a player would actually feel: with client and server
            // running the same MovementCore over the same inputs, a correction must land the
            // client exactly where it had already predicted itself to be. Zero DISPLACEMENT is
            // convergence; a correction count is not.
            //
            // Observed RED against the pre-fix tree, where each correction dragged the client
            // back onto the server's stale position -- a 0.47 m snap, every snapshot, forever.
            var reconciler = new PredictionReconciler();
            var client = MoveState.AtRest(Vec3.Zero, grounded: false);
            var server = MoveState.AtRest(Vec3.Zero, grounded: false);

            const int lagTicks = 4;
            float worstSnap = 0f;

            for (uint tick = 1; tick <= 60; tick++)
            {
                reconciler.Record(tick, Forward);
                client.Position += MovementCore.Step(ref client, Forward, Dt);

                if (tick <= lagTicks) continue;

                // The server consumes one input per tick, `lagTicks` behind the client.
                server.Position += MovementCore.Step(ref server, Forward, Dt);

                Vec3 before = client.Position;
                MoveState authority = server;
                reconciler.Reconcile(ref client, in authority, tick - lagTicks, Dt);

                float snap = (client.Position - before).Magnitude;
                if (snap > worstSnap) worstSnap = snap;
            }

            Assert.True(worstSnap < 1e-4f,
                        $"a correction moved a correctly-predicting client by {worstSnap:F4} m. "
                        + "The replay is not reproducing the client's own simulation (ledger X-21)");

            // And it is genuinely ahead of authority by the unacknowledged inputs, rather than
            // sitting on it -- which is what "the client lost the motion" would look like.
            Assert.Equal(lagTicks * MovementCore.WalkSpeed * Dt, client.Position.Z - server.Position.Z, 4);
        }

        [Fact]
        public void TheCorrectionCounterMeasuresLagNotMisprediction()
        {
            // Filed as ledger row X-41, and pinned here so the number in an artifact is not read
            // as a mispredict rate.
            //
            // `Reconcile` compares the client's CURRENT position against an authoritative state
            // for a tick `lag` in the past, so a perfectly-predicting client is compared against
            // a position it has legitimately left. Once lag x speed exceeds
            // PositionToleranceMetres -- 0.25 m, which is 2.1 ticks at a walk and 1.2 at a
            // sprint -- every snapshot reports `Corrected` even though the replay then moves the
            // client by nothing at all (the test above). So `corrections: N` counts snapshots
            // taken at more than 0.25 m of lag; it does not count mispredictions.
            //
            // Left as a finding rather than fixed here: comparing at the acknowledged tick
            // instead needs a position history beside the input ring, which is a change to what
            // this class stores, and X-21 is about the replay.
            Assert.True(
                4 * MovementCore.WalkSpeed * Dt > PredictionReconciler.PositionToleranceMetres,
                "the premise of X-41 no longer holds; re-read the row before trusting this test");

            var reconciler = new PredictionReconciler();
            var client = MoveState.AtRest(Vec3.Zero, grounded: false);
            var server = MoveState.AtRest(Vec3.Zero, grounded: false);

            for (uint tick = 1; tick <= 20; tick++)
            {
                reconciler.Record(tick, Forward);
                client.Position += MovementCore.Step(ref client, Forward, Dt);

                if (tick <= 4) continue;

                server.Position += MovementCore.Step(ref server, Forward, Dt);
                MoveState authority = server;
                reconciler.Reconcile(ref client, in authority, tick - 4, Dt);
            }

            // Every snapshot after the lag window, on a client that mispredicted nothing.
            Assert.Equal(16, reconciler.CorrectionCount);
        }
    }
}
