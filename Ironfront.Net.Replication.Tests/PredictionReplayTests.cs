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

            var authoritative = MoveState.AtRest(new Vec3(0f, 10f, 3f), grounded: false);
            var predicted = MoveState.AtRest(new Vec3(0f, 10f, 9f), grounded: false);

            // Since X-41 the comparison is against the position recorded FOR the acknowledged
            // tick, so this fixture has to say where the client thought it was then. It thought
            // Z = 9 and the server says 3: a genuine misprediction, which is what this test is
            // about replaying correctly.
            for (uint tick = 1; tick <= 5; tick++)
                reconciler.Record(tick, Forward, predicted.Position);

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

            // Tick 0 is acknowledged and was never recorded, so since X-41 the
            // comparison falls back to the current position -- the resynchronise
            // neighbourhood. These recorded positions are therefore never read, and
            // Vec3.Zero says so rather than implying a fixture that matters.
            for (uint tick = 1; tick <= 3; tick++)
                reconciler.Record(tick, Forward, Vec3.Zero);

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

            // Tick 0 is acknowledged and was never recorded, so since X-41 the
            // comparison falls back to the current position -- the resynchronise
            // neighbourhood. These recorded positions are therefore never read, and
            // Vec3.Zero says so rather than implying a fixture that matters.
            for (uint tick = 1; tick <= 4; tick++)
                reconciler.Record(tick, Forward, Vec3.Zero);

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

            var authoritative = MoveState.AtRest(new Vec3(0f, 0f, 12f));
            var predicted = MoveState.AtRest(new Vec3(0f, 0f, 0f));

            for (uint tick = 1; tick <= 3; tick++)
                reconciler.Record(tick, Forward, predicted.Position);

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
                // STEP then RECORD, since X-41: the history holds the position an input LEFT the
                // client at, which is what the server's answer for that tick is compared with.
                // Recording the pre-step position would offset every comparison by one tick's
                // motion -- at a sprint, most of the tolerance.
                client.Position += MovementCore.Step(ref client, Forward, Dt);
                reconciler.Record(tick, Forward, client.Position);

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

        [Theory]
        [InlineData(4)]
        [InlineData(10)]
        [InlineData(25)]
        public void TheCorrectionCounterMeasuresMispredictionRatherThanLag(int lagTicks)
        {
            // INVERTED from the X-41 pin, not re-pinned (O-D6, pinned-baseline-test-companion.md).
            //
            // WHAT THIS USED TO ASSERT, and why the change of direction is the news: this test was
            // `TheCorrectionCounterMeasuresLagNotMisprediction`, and it pinned **16 corrections on
            // a client that mispredicted nothing**. Reconcile compared the client's CURRENT
            // position against an authoritative state for a tick `lag` in the past, so a client
            // predicting perfectly was compared against a position it had legitimately left; once
            // lag x speed passed PositionToleranceMetres -- 2.1 ticks at a walk, 1.2 at a sprint
            // -- every snapshot returned Corrected while the replay moved the client by nothing.
            //
            // The honest number is now ZERO, and it is zero at every lag rather than at the one
            // the old pin happened to use -- which is why this is a Theory. 25 ticks is 0.83 s of
            // walking, nearly three metres of legitimate lead over the server.
            //
            // DO NOT RE-PIN THIS TO A NON-ZERO COUNT. A rise here is a real regression in the
            // comparison, not a new baseline: it means the reconciler is once again measuring how
            // far behind the server is and reporting it as a mispredict.
            var reconciler = new PredictionReconciler();
            var client = MoveState.AtRest(Vec3.Zero, grounded: false);
            var server = MoveState.AtRest(Vec3.Zero, grounded: false);

            for (uint tick = 1; tick <= 60; tick++)
            {
                client.Position += MovementCore.Step(ref client, Forward, Dt);
                reconciler.Record(tick, Forward, client.Position);

                if (tick <= lagTicks) continue;

                server.Position += MovementCore.Step(ref server, Forward, Dt);
                MoveState authority = server;
                reconciler.Reconcile(ref client, in authority, tick - (uint)lagTicks, Dt);
            }

            Assert.Equal(0, reconciler.CorrectionCount);

            // And the premise is still live: at this lag the OLD comparison would have fired on
            // every snapshot, so a zero here is the fix working rather than the test having
            // stopped exercising anything.
            Assert.True(
                lagTicks * MovementCore.WalkSpeed * Dt > PredictionReconciler.PositionToleranceMetres,
                $"a lag of {lagTicks} ticks no longer exceeds the tolerance, so this case would "
                + "read zero even under the X-41 defect. Raise it rather than deleting it.");
        }

        [Fact]
        public void ARealMispredictionIsStillCorrected()
        {
            // The companion direction, and the one that stops the fix from being "never correct
            // anything". The server refuses the client's position at the acknowledged tick -- a
            // wall it ran through, a speed it was clamped to -- and the correction still lands
            // the client where its own replay says it should be.
            var reconciler = new PredictionReconciler();
            var client = MoveState.AtRest(Vec3.Zero, grounded: false);

            for (uint tick = 1; tick <= 10; tick++)
            {
                client.Position += MovementCore.Step(ref client, Forward, Dt);
                reconciler.Record(tick, Forward, client.Position);
            }

            // Authority for tick 6 is a metre back from where the client recorded itself.
            MoveState recordedAtSix = MoveState.AtRest(Vec3.Zero, grounded: false);
            for (int i = 0; i < 6; i++)
                recordedAtSix.Position += MovementCore.Step(ref recordedAtSix, Forward, Dt);

            MoveState authority = recordedAtSix;
            authority.Position = new Vec3(
                authority.Position.X, authority.Position.Y, authority.Position.Z - 1f);

            ReconcileResult result = reconciler.Reconcile(ref client, in authority, 6, Dt);

            Assert.Equal(ReconcileResult.Corrected, result);
            Assert.Equal(1, reconciler.CorrectionCount);
            Assert.Equal(4, reconciler.ReplayedInputCount);

            // Authority, plus the four inputs the server had not consumed.
            Assert.Equal(
                authority.Position.Z + 4f * MovementCore.WalkSpeed * Dt, client.Position.Z, 4);
        }
    }
}
