using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The client half of replication: snapshot interpolation, prediction reconciliation, and
    /// inbound message routing. M1 criterion 7 — two clients seeing each other move smoothly at
    /// 100 ms RTT and 5% loss — is graded on what these three produce, and none of it can be
    /// measured in the Editor without first being correct here.
    /// </summary>
    public sealed class ClientReplicationTests
    {
        private const float Dt = 1f / ProtocolConstants.SIM_TICK_RATE;

        private static WorldSnapshot SnapshotAt(uint tick, ushort actorId, float x, float yawDeg = 0f)
        {
            var world = new WorldSnapshot { ServerTick = tick };
            world.Add(new ActorSnapshotEntry
            {
                ActorId = actorId,
                PosX = Quantize.PackPos(x),
                PosY = Quantize.PackPos(0f),
                PosZ = Quantize.PackPos(0f),
                Yaw = Quantize.PackYaw(yawDeg),
            });
            return world;
        }

        // ------------------------------------------------------- SnapshotInterpolator

        [Fact]
        public void OneSnapshotIsNotEnoughToInterpolate()
        {
            var buffer = new SnapshotInterpolator();
            buffer.Push(SnapshotAt(10, 1, 0f));

            InterpolationResult result = buffer.TrySample(10, out _, out _, out double alpha);

            Assert.Equal(InterpolationResult.Starved, result);
            Assert.Equal(0.0, alpha);
        }

        [Fact]
        public void ASnapshotOlderThanTheNewestIsRejected()
        {
            var buffer = new SnapshotInterpolator();
            buffer.Push(SnapshotAt(10, 1, 0f));

            Assert.False(buffer.Push(SnapshotAt(9, 1, 5f)));
            Assert.Equal(1, buffer.Count);
            Assert.Equal(1, buffer.OutOfOrderCount);
        }

        [Fact]
        public void TheMidpointBetweenTwoSnapshotsIsHalfway()
        {
            var buffer = new SnapshotInterpolator();
            buffer.Push(SnapshotAt(10, 7, 0f));
            buffer.Push(SnapshotAt(11, 7, 2f));

            InterpolationResult result = buffer.TrySample(
                10.5, out WorldSnapshot? from, out WorldSnapshot? to, out double alpha);

            Assert.Equal(InterpolationResult.Interpolated, result);
            Assert.Equal(0.5, alpha, 5);

            Assert.True(SnapshotInterpolator.TryLerpPosition(from, to, alpha, 7, out Vec3 p));
            Assert.Equal(1f, p.X, 1);
        }

        /// <summary>
        /// The guard on dividing by a hardcoded 1. A dropped snapshot leaves a two-tick gap, and
        /// an actor that covers it in one tick and then waits is the exact stutter interpolation
        /// exists to remove — which at criterion 7's 5% loss happens roughly once a second.
        /// </summary>
        [Fact]
        public void AGapLeftByALostSnapshotIsSpreadAcrossItsFullSpan()
        {
            var buffer = new SnapshotInterpolator();
            buffer.Push(SnapshotAt(10, 7, 0f));
            buffer.Push(SnapshotAt(12, 7, 4f));   // tick 11 never arrived

            buffer.TrySample(11, out WorldSnapshot? from, out WorldSnapshot? to, out double alpha);

            Assert.Equal(0.5, alpha, 5);
            Assert.True(SnapshotInterpolator.TryLerpPosition(from, to, alpha, 7, out Vec3 p));
            Assert.Equal(2f, p.X, 1);
        }

        [Fact]
        public void PastTheNewestSnapshotItStallsRatherThanExtrapolating()
        {
            var buffer = new SnapshotInterpolator();
            buffer.Push(SnapshotAt(10, 7, 0f));
            buffer.Push(SnapshotAt(11, 7, 2f));

            InterpolationResult result = buffer.TrySample(
                12.5, out WorldSnapshot? from, out WorldSnapshot? to, out double alpha);

            Assert.Equal(InterpolationResult.Stalled, result);
            Assert.Equal(0.0, alpha);

            // Held at the newest pose, not projected past it.
            Assert.True(SnapshotInterpolator.TryLerpPosition(from, to, alpha, 7, out Vec3 p));
            Assert.Equal(2f, p.X, 1);
        }

        [Fact]
        public void BeforeTheOldestSnapshotItSnapsBackRatherThanRunningBackwards()
        {
            var buffer = new SnapshotInterpolator();
            buffer.Push(SnapshotAt(10, 7, 0f));
            buffer.Push(SnapshotAt(11, 7, 2f));

            Assert.Equal(InterpolationResult.TooOld, buffer.TrySample(5, out _, out _, out _));
        }

        /// <summary>
        /// DeltaDecoder mutates and reuses ONE WorldSnapshot. Storing the reference would leave
        /// every ring slot pointing at the newest state — interpolation silently becomes a no-op
        /// and every actor teleports, with nothing in a debugger to explain why.
        /// </summary>
        [Fact]
        public void ThePushedSnapshotIsCopiedNotAliased()
        {
            var buffer = new SnapshotInterpolator();
            WorldSnapshot reused = SnapshotAt(10, 7, 0f);
            buffer.Push(reused);

            // Same instance, mutated the way the decoder mutates it.
            reused.Clear();
            reused.ServerTick = 11;
            reused.Add(new ActorSnapshotEntry { ActorId = 7, PosX = Quantize.PackPos(2f) });
            buffer.Push(reused);

            buffer.TrySample(10.5, out WorldSnapshot? from, out WorldSnapshot? to, out double alpha);

            Assert.True(SnapshotInterpolator.TryLerpPosition(from, to, alpha, 7, out Vec3 p));
            Assert.Equal(1f, p.X, 1);   // 0 -> 2 halfway. Aliased, both ends would read 2.
        }

        /// <summary>
        /// A plain lerp from 350 to 10 spins the actor 340 degrees the wrong way. That is not an
        /// edge case; it is any actor facing roughly north.
        /// </summary>
        [Fact]
        public void YawTakesTheShortWayRoundTheWrap()
        {
            var buffer = new SnapshotInterpolator();
            buffer.Push(SnapshotAt(10, 7, 0f, 350f));
            buffer.Push(SnapshotAt(11, 7, 0f, 10f));

            buffer.TrySample(10.5, out WorldSnapshot? from, out WorldSnapshot? to, out double alpha);

            Assert.True(SnapshotInterpolator.TryLerpYaw(from, to, alpha, 7, out float yaw));
            Assert.Equal(0f, yaw, 1);
        }

        [Fact]
        public void TheRenderTickTrailsTheNewestSnapshotByTheDelay()
        {
            var buffer = new SnapshotInterpolator();
            buffer.Push(SnapshotAt(100, 7, 0f));

            Assert.Equal(100 - SnapshotInterpolator.DelayTicks + 0.25,
                         buffer.RenderTick(0.25), 5);
        }

        [Fact]
        public void AnActorMissingFromEitherEndIsNotInterpolated()
        {
            var buffer = new SnapshotInterpolator();
            buffer.Push(SnapshotAt(10, 7, 0f));
            buffer.Push(SnapshotAt(11, 7, 2f));

            buffer.TrySample(10.5, out WorldSnapshot? from, out WorldSnapshot? to, out double alpha);

            Assert.False(SnapshotInterpolator.TryLerpPosition(from, to, alpha, 999, out _));
        }

        // ------------------------------------------------------- PredictionReconciler

        [Fact]
        public void AgreementWithinToleranceLeavesThePredictionAlone()
        {
            var reconciler = new PredictionReconciler();
            var predicted = MoveState.AtRest(new Vec3(5f, 0f, 0f));
            var authoritative = MoveState.AtRest(new Vec3(5.05f, 0f, 0f));

            ReconcileResult result = reconciler.Reconcile(ref predicted, authoritative, 10, Dt);

            Assert.Equal(ReconcileResult.Agreed, result);
            Assert.Equal(5f, predicted.Position.X, 3);
            Assert.Equal(0, reconciler.CorrectionCount);
        }

        /// <summary>
        /// The whole point of reconciliation: after a correction the client must land where the
        /// server WILL be once it has consumed the inputs it has not seen yet — not where the
        /// server was half a round trip ago.
        /// </summary>
        /// <remarks>
        /// <b>This test was green while the replay never moved the position at all</b> — ledger
        /// row X-21, 2,208 corrections in a 136 s run that never converged. It built
        /// <c>expected</c> by calling <c>MovementCore.Step</c> and reading
        /// <c>expected.Position</c>, and <c>Step</c> deliberately does not write that field, so
        /// both sides sat on the authoritative 3.000 m and compared equal. The expectation is now
        /// accumulated from the returned deltas the way a caller must; the position assertion is
        /// stated independently of the constants in <c>PredictionReplayTests</c>.
        /// </remarks>
        [Fact]
        public void ACorrectionReplaysTheUnacknowledgedInputsOverAuthority()
        {
            var reconciler = new PredictionReconciler();
            var input = new MoveInput(0f, 1f, 0f, false, false, false);

            // The server has consumed inputs 1-3 and disagrees by well over the tolerance.
            var authoritative = MoveState.AtRest(new Vec3(0f, 0f, 3f));
            var predicted = MoveState.AtRest(new Vec3(0f, 0f, 9f));

            // Since X-41 the comparison is at the ACKNOWLEDGED tick, so the fixture states where
            // the client believed it was then: Z = 9 against the server's 3.
            for (uint tick = 1; tick <= 5; tick++)
                reconciler.Record(tick, input, predicted.Position);

            // What the server itself will reach once it consumes ticks 4 and 5 -- with the motion
            // WRITTEN BACK, which is the half this test used to drop on the floor.
            var expected = authoritative;
            expected.Position += MovementCore.Step(ref expected, in input, Dt);
            expected.Position += MovementCore.Step(ref expected, in input, Dt);

            ReconcileResult result = reconciler.Reconcile(ref predicted, authoritative, 3, Dt);

            Assert.Equal(ReconcileResult.Corrected, result);
            Assert.Equal(expected.Position.Z, predicted.Position.Z, 4);
            Assert.Equal(2, reconciler.ReplayedInputCount);
            Assert.Equal(1, reconciler.CorrectionCount);
        }

        [Fact]
        public void AnAcknowledgementOlderThanTheLastOneIsIgnored()
        {
            var reconciler = new PredictionReconciler();
            var predicted = MoveState.AtRest(new Vec3(0f, 0f, 9f));
            var authoritative = MoveState.AtRest(new Vec3(0f, 0f, 0f));

            reconciler.Reconcile(ref predicted, authoritative, 10, Dt);
            Vec3 after = predicted.Position;

            Assert.Equal(ReconcileResult.Stale,
                         reconciler.Reconcile(ref predicted, authoritative, 7, Dt));
            Assert.Equal(after.Z, predicted.Position.Z, 5);
        }

        [Fact]
        public void AnAcknowledgementOlderThanTheInputBufferResynchronises()
        {
            var reconciler = new PredictionReconciler();
            var input = new MoveInput(0f, 1f, 0f, false, false, false);

            // Fill past capacity so tick 1 has been evicted.
            for (uint tick = 1; tick <= PredictionReconciler.Capacity + 10; tick++)
                reconciler.Record(tick, input, Vec3.Zero);

            var authoritative = MoveState.AtRest(new Vec3(0f, 0f, 42f));
            var predicted = MoveState.AtRest(new Vec3(0f, 0f, 0f));

            ReconcileResult result = reconciler.Reconcile(ref predicted, authoritative, 1, Dt);

            Assert.Equal(ReconcileResult.Resynchronised, result);
            Assert.Equal(42f, predicted.Position.Z, 3);
            Assert.Equal(1, reconciler.ResyncCount);
        }

        /// <summary>
        /// Positions travel quantised to 1 cm, so a tolerance at or below the quantisation step
        /// would fire on rounding alone — a correction every tick, forever.
        /// </summary>
        [Fact]
        public void TheToleranceSitsAboveTheWireQuantisationStep()
        {
            float step = Quantize.UnpackPos(1) - Quantize.UnpackPos(0);

            // The step is 6.25 cm, not the 1 cm it is easy to assume. Pinned here so a change to
            // POS_RANGE cannot silently push the tolerance under the noise floor.
            Assert.Equal(0.0625f, step, 4);

            // Worst-case rounding error is half a step per axis, in 3D.
            float worstCaseRounding = (float)Math.Sqrt(3.0) * step * 0.5f;
            Assert.True(PredictionReconciler.PositionToleranceMetres > worstCaseRounding * 2f,
                        $"tolerance {PredictionReconciler.PositionToleranceMetres} is not clear of "
                        + $"the {worstCaseRounding} m quantisation noise floor");
        }

        // ------------------------------------------------------- ClientMessageRouter

        [Fact]
        public void AMalformedPayloadIsCountedRatherThanThrown()
        {
            var router = new ClientMessageRouter();

            Assert.Equal(0, router.Route(new byte[] { 0xFF }));
            Assert.Equal(1, router.MalformedMessages);
        }

        [Fact]
        public void AMessageTypeThisBuildDoesNotKnowIsCountedAndSkipped()
        {
            var payload = new byte[64];
            var writer = new PayloadFrameWriter(payload, ChannelId.Unreliable);
            Assert.True(writer.WriteMessage(0x7F, new byte[] { 1, 2, 3 }));
            Assert.True(writer.TryFinish(out int length));

            var router = new ClientMessageRouter();

            Assert.Equal(0, router.Route(payload.AsSpan(0, length)));
            Assert.Equal(1, router.UnknownMessages);
            Assert.Equal(0, router.MalformedMessages);
        }

        [Fact]
        public void AnAppliedSnapshotReachesTheInterpolatorAndRaisesItsEvent()
        {
            var encoder = new DeltaEncoder();
            var world = SnapshotAt(41, 3, 1.5f);

            var payload = new byte[ProtocolConstants.MAX_PAYLOAD];
            var scratch = new byte[ServerPayloadWriter.MaxSnapshotBodySize];
            int length = ServerPayloadWriter.WriteSnapshot(payload, scratch, encoder, world, 37);
            Assert.True(length > 0);

            var router = new ClientMessageRouter();
            uint seenTick = 0, seenInputTick = 0;
            router.OnSnapshotApplied += (t, i) => { seenTick = t; seenInputTick = i; };

            Assert.Equal(1, router.Route(payload.AsSpan(0, length)));

            Assert.Equal(1, router.SnapshotsApplied);
            Assert.Equal(41u, seenTick);
            Assert.Equal(37u, seenInputTick);
            Assert.Equal(1, router.Interpolator.Count);
            Assert.Equal(41u, router.Interpolator.NewestTick);
        }

        [Fact]
        public void ResetClearsEveryCounterAndBuffer()
        {
            var router = new ClientMessageRouter();
            router.Route(new byte[] { 0xFF });
            Assert.Equal(1, router.MalformedMessages);

            router.Reset();

            Assert.Equal(0, router.MalformedMessages);
            Assert.Equal(0, router.Interpolator.Count);
        }
    }
}
