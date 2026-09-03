using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// An error larger than the replay ring can close must be adopted as a RESYNCHRONISE, so the
    /// caller teleports instead of sweeping a <c>CharacterController</c> through the map.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The failure this pins.</b> <c>Reconcile</c> classified every out-of-tolerance error as
    /// <see cref="ReconcileResult.Corrected"/> whenever a single unacknowledged input existed,
    /// with no bound on magnitude — and <c>NetMovementAgent.ApplyCorrectedState(hardSnap: false)</c>
    /// applies that through <c>CharacterController.Move</c>, a SWEPT motion that collision stops
    /// at the first obstruction. A body that has not been placed yet sits at the player prefab's
    /// authored park near <c>(0, 1000, 0)</c>, roughly 975 m from wherever the server put it, so
    /// the sweep could not arrive, the identical error was recomputed on the next snapshot, and
    /// the body was shoved once per tick for as long as the disagreement lasted. Reported from
    /// play as floating in the air and juddering continuously.
    /// </para>
    /// <para>
    /// <b>Both directions, in one file.</b> A test that only proved the large error resynchronises
    /// would pass on a reconciler that resynchronised for everything and threw the replay away —
    /// which is a different, quieter regression (X-21's, re-introduced). The ordinary-mispredict
    /// test below is the other half and must stay <see cref="ReconcileResult.Corrected"/>.
    /// </para>
    /// </remarks>
    public sealed class PredictionResyncDistanceTests
    {
        private const float Dt = 1f / ProtocolConstants.SIM_TICK_RATE;

        /// <summary>Forward at a walk, no jump, no crouch, no sprint.</summary>
        private static MoveInput Forward => new MoveInput(0f, 1f, 0f, false, false, false);

        [Fact]
        public void AnUnplacedBodyResynchronisesRatherThanReplaying()
        {
            // Observed RED against the pre-fix tree, which answered Corrected here and handed the
            // Unity side a 975 m swept move.
            var reconciler = new PredictionReconciler();

            // The park the player prefab is instantiated at, against a spawn point on the ground.
            var predicted = MoveState.AtRest(new Vec3(0f, 1000f, 0f), grounded: false);
            var authoritative = MoveState.AtRest(new Vec3(1885.33f, 24.89f, 1805.13f), grounded: true);

            // Unacknowledged inputs EXIST -- this is the case the old code sent down the replay
            // path. A client sends input at the tick rate from the moment it connects, so this is
            // the normal state of affairs and not a contrived one.
            for (uint tick = 1; tick <= 5; tick++)
                reconciler.Record(tick, Forward, predicted.Position);

            ReconcileResult result = reconciler.Reconcile(ref predicted, authoritative, 3, Dt);

            Assert.Equal(ReconcileResult.Resynchronised, result);

            // Authority adopted EXACTLY, with no replay laid over it: a replay here would move the
            // body off the only position that is known to be true.
            Assert.Equal(0, reconciler.ReplayedInputCount);
            Assert.Equal(authoritative.Position.X, predicted.Position.X, 4);
            Assert.Equal(authoritative.Position.Y, predicted.Position.Y, 4);
            Assert.Equal(authoritative.Position.Z, predicted.Position.Z, 4);
        }

        [Fact]
        public void AnOrdinaryMispredictStillReplays()
        {
            // The other direction. 6 m is a real disagreement -- far past the 0.25 m tolerance --
            // but well inside what one second of buffered input can legitimately produce, so it
            // is exactly what the replay exists for.
            var reconciler = new PredictionReconciler();

            var authoritative = MoveState.AtRest(new Vec3(0f, 10f, 3f), grounded: false);
            var predicted = MoveState.AtRest(new Vec3(0f, 10f, 9f), grounded: false);

            for (uint tick = 1; tick <= 5; tick++)
                reconciler.Record(tick, Forward, predicted.Position);

            ReconcileResult result = reconciler.Reconcile(ref predicted, authoritative, 3, Dt);

            Assert.Equal(ReconcileResult.Corrected, result);
            Assert.Equal(2, reconciler.ReplayedInputCount);
            Assert.True(predicted.Position.Z > 3f,
                        "the replay was skipped for an error the ring can close: the resync bound "
                        + "is set too tight and is swallowing ordinary corrections");
        }

        [Fact]
        public void TheBoundIsWiderThanTheInputRingCanEverHold()
        {
            // Pins the DERIVATION, not the number. The replay covers Capacity ticks; the widest
            // divergence that window can hold is both simulations running flat out in opposite
            // directions. A bound at or below that would resynchronise errors the replay was
            // built to fix -- which is the regression the test above would then report.
            const float bufferedSeconds = PredictionReconciler.Capacity / (float)ProtocolConstants.SIM_TICK_RATE;
            const float opposedFullSpeed = 2f * MovementCore.RunSpeed * bufferedSeconds;

            Assert.True(PredictionReconciler.ResyncDistanceMetres > opposedFullSpeed,
                        $"resync bound {PredictionReconciler.ResyncDistanceMetres} m is not wider than "
                        + $"the {opposedFullSpeed} m the {bufferedSeconds} s input ring can hold");

            // And it is nowhere near the park distance, which is the case it exists to catch.
            Assert.True(PredictionReconciler.ResyncDistanceMetres < 900f,
                        "resync bound is so wide that an unplaced body at the prefab's park would "
                        + "still take the replay path");
        }
    }
}
