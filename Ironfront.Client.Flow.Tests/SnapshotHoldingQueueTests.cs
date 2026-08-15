using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// phase-03 trap 3: what happens to the snapshots that arrive during the 2-5 seconds a
    /// match scene takes to load.
    /// </summary>
    public sealed class SnapshotHoldingQueueTests
    {
        private static (GamePayloadRoute Route, List<byte[]> Seen) Recorder()
        {
            var seen = new List<byte[]>();
            GamePayloadRoute route = payload =>
            {
                seen.Add(payload.ToArray());
                return 1;
            };
            return (route, seen);
        }

        [Fact]
        public void NothingIsHeldUntilHoldingStarts()
        {
            var queue = new SnapshotHoldingQueue();

            Assert.False(queue.IsHolding);
            Assert.False(queue.TryHold(new byte[] { 1 }));
            Assert.Equal(0, queue.Count);
        }

        [Fact]
        public void HeldPayloadsComeBackInArrivalOrder()
        {
            var queue = new SnapshotHoldingQueue();
            (GamePayloadRoute route, List<byte[]> seen) = Recorder();
            queue.Hold();

            for (byte i = 1; i <= 4; i++) queue.TryHold(new[] { i, (byte)(i * 10) });

            Assert.Equal(4, queue.Count);
            Assert.Equal(4, queue.Release(route));

            Assert.Equal(4, seen.Count);
            for (int i = 0; i < 4; i++) Assert.Equal(new[] { (byte)(i + 1), (byte)((i + 1) * 10) }, seen[i]);
            Assert.False(queue.IsHolding);
            Assert.Equal(0, queue.Count);
        }

        [Fact]
        public void PayloadsAreCopiedRatherThanReferenced()
        {
            // ITransportClient.OnMessage hands out a pooled buffer that is returned the moment
            // the handler returns. Keeping the reference reads whatever the pool handed out next.
            var queue = new SnapshotHoldingQueue();
            (GamePayloadRoute route, List<byte[]> seen) = Recorder();
            queue.Hold();

            var pooled = new byte[] { 7, 7, 7 };
            queue.TryHold(pooled);
            pooled[0] = 99;   // the pool reused it

            queue.Release(route);

            Assert.Equal(new byte[] { 7, 7, 7 }, seen[0]);
        }

        [Fact]
        public void ADeltaChainSurvivesTheHold()
        {
            // The reason everything is replayed rather than filtered down to the newest. A real
            // delta names the baseline it was built against; skipping the middle of the run
            // leaves the decoder with a baseline it never saw.
            var queue = new SnapshotHoldingQueue();
            var decoded = new List<uint>();
            GamePayloadRoute route = payload =>
            {
                decoded.Add(BitConverter.ToUInt32(payload.ToArray(), 0));
                return 1;
            };

            queue.Hold();
            for (uint tick = 100; tick < 110; tick++) queue.TryHold(BitConverter.GetBytes(tick));
            queue.Release(route);

            Assert.Equal(10, decoded.Count);
            for (int i = 0; i < decoded.Count; i++) Assert.Equal((uint)(100 + i), decoded[i]);
        }

        [Fact]
        public void OverflowDropsTheOldestAndSaysSo()
        {
            var queue = new SnapshotHoldingQueue(capacity: 3);
            (GamePayloadRoute route, List<byte[]> seen) = Recorder();
            queue.Hold();

            for (byte i = 1; i <= 5; i++) queue.TryHold(new[] { i });

            Assert.Equal(3, queue.Count);
            Assert.Equal(2, queue.DroppedForOverflow);
            Assert.Equal(5, queue.TotalHeld);

            queue.Release(route);

            Assert.Equal(new byte[] { 3 }, seen[0]);
            Assert.Equal(new byte[] { 5 }, seen[2]);
        }

        [Fact]
        public void TheDefaultCapacityCoversTheWorstSceneLoadInThePlan()
        {
            // phase-03 trap 3 puts scene loading at 2-5 seconds; snapshots come at 20 Hz.
            int worstCase = 5 * ProtocolConstants.SNAPSHOT_RATE;

            Assert.True(
                SnapshotHoldingQueue.DefaultCapacity > worstCase,
                $"capacity {SnapshotHoldingQueue.DefaultCapacity} does not cover {worstCase} payloads");
        }

        [Fact]
        public void ClearThrowsAwayWhatWasHeld()
        {
            var queue = new SnapshotHoldingQueue();
            (GamePayloadRoute route, List<byte[]> seen) = Recorder();
            queue.Hold();
            queue.TryHold(new byte[] { 1 });

            queue.Clear();

            Assert.False(queue.IsHolding);
            Assert.Equal(0, queue.Count);
            Assert.Equal(0, queue.Release(route));
            Assert.Empty(seen);
        }

        [Fact]
        public void TheQueueSurvivesThreeMatchesBackToBack()
        {
            // phase-03 criterion 5. The buffers are reused rather than released, so a growing
            // Count across cycles would be the leak that criterion is looking for.
            var queue = new SnapshotHoldingQueue();
            (GamePayloadRoute route, List<byte[]> seen) = Recorder();

            for (int match = 0; match < 3; match++)
            {
                queue.Hold();
                for (byte i = 0; i < 20; i++) queue.TryHold(new[] { (byte)(match * 100 + i) });
                Assert.Equal(20, queue.Release(route));
                Assert.Equal(0, queue.Count);
            }

            Assert.Equal(60, seen.Count);
            Assert.Equal(60, queue.TotalHeld);
            Assert.Equal(0, queue.DroppedForOverflow);
        }

        [Fact]
        public void AShorterPayloadAfterALongerOneDoesNotLeakTheTail()
        {
            // The buffers are reused and are sized to the largest payload that has used the
            // slot, so the length has to be tracked separately from the array.
            var queue = new SnapshotHoldingQueue(capacity: 1);
            (GamePayloadRoute route, List<byte[]> seen) = Recorder();

            queue.Hold();
            queue.TryHold(new byte[] { 1, 2, 3, 4, 5 });
            queue.Release(route);

            queue.Hold();
            queue.TryHold(new byte[] { 9 });
            queue.Release(route);

            Assert.Equal(new byte[] { 9 }, seen[1]);
        }

        [Fact]
        public void ReleasingWithoutARouteIsRejected()
        {
            var queue = new SnapshotHoldingQueue();
            Assert.Throws<ArgumentNullException>(() => queue.Release(null!));
        }

        [Fact]
        public void ACapacityBelowOneIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new SnapshotHoldingQueue(0));
        }
    }
}
