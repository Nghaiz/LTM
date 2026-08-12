using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Ironfront.Net.Transport.Simulation;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Dev B phase-00 acceptance criteria 5 (reproducible with the same seed) and 6 (all five
    /// impairments, each verified statistically over 10,000 packets).
    /// </summary>
    public sealed class NetworkSimulatorTests
    {
        private const int SampleSize = 10_000;

        private static NetworkSimulator<int> Build(SimulatorConfig config)
            => new NetworkSimulator<int>(config, new BufferPool(1024, ProtocolConstants.MTU_SAFE));

        /// <summary>Pushes N packets through and reports how many came back out.</summary>
        private static int PushAndDrain(NetworkSimulator<int> simulator, int count, double spacingMs = 1.0)
        {
            var payload = new byte[32];
            int delivered = 0;
            double now = 0;

            for (int i = 0; i < count; i++)
            {
                payload[0] = (byte)i;
                simulator.ShouldSend(payload, 0, now);
                now += spacingMs;
            }

            // Far past the longest possible delay, so nothing is still in flight.
            simulator.Flush(now + 100_000.0, (_, _, _) => delivered++);
            return delivered;
        }

        // ------------------------------------------------------------------ reproducibility

        [Fact]
        public void TheSameSeedProducesTheIdenticalLossSequence()
        {
            // The single most important property. Without it, "it only happens sometimes"
            // netcode bugs can never be reproduced, let alone fixed.
            List<int> first  = RecordDeliveredIds(seed: 12345);
            List<int> second = RecordDeliveredIds(seed: 12345);

            Assert.Equal(first, second);
            Assert.NotEmpty(first);
        }

        [Fact]
        public void ADifferentSeedProducesADifferentSequence()
        {
            List<int> a = RecordDeliveredIds(seed: 12345);
            List<int> b = RecordDeliveredIds(seed: 54321);

            Assert.NotEqual(a, b);
        }

        private static List<int> RecordDeliveredIds(int seed)
        {
            SimulatorConfig config = SimulatorConfig.Typical();
            config.RandomSeed = seed;

            NetworkSimulator<int> simulator = Build(config);
            var payload = new byte[8];
            var delivered = new List<int>();
            double now = 0;

            for (int i = 0; i < 500; i++)
            {
                payload[0] = (byte)(i & 0xFF);
                payload[1] = (byte)(i >> 8);
                simulator.ShouldSend(payload, 0, now);
                now += 5.0;
            }

            simulator.Flush(now + 10_000.0, (buffer, _, _) => delivered.Add(buffer[0] | (buffer[1] << 8)));
            return delivered;
        }

        // ------------------------------------------------------------------ 1. loss

        [Fact]
        public void PacketLossLandsCloseToTheConfiguredRate()
        {
            var config = new SimulatorConfig { Enabled = true, PacketLossPercent = 20f, RandomSeed = 1 };
            NetworkSimulator<int> simulator = Build(config);

            int delivered = PushAndDrain(simulator, SampleSize);
            double lossRate = 1.0 - (double)delivered / SampleSize;

            Assert.InRange(lossRate, 0.18, 0.22);
            Assert.Equal(SampleSize - delivered, simulator.DroppedCount);
        }

        [Fact]
        public void ZeroLossDeliversEverything()
        {
            var config = new SimulatorConfig { Enabled = true, LatencyMs = 10f, RandomSeed = 1 };
            Assert.Equal(SampleSize, PushAndDrain(Build(config), SampleSize));
        }

        // ------------------------------------------------------------------ 2. latency

        [Fact]
        public void LatencyHoldsPacketsForTheConfiguredTime()
        {
            var config = new SimulatorConfig { Enabled = true, LatencyMs = 50f, RandomSeed = 1 };
            NetworkSimulator<int> simulator = Build(config);

            simulator.ShouldSend(new byte[16], 0, 0.0);

            int delivered = 0;
            simulator.Flush(49.0, (_, _, _) => delivered++);
            Assert.Equal(0, delivered);
            Assert.Equal(1, simulator.InFlightCount);

            simulator.Flush(50.0, (_, _, _) => delivered++);
            Assert.Equal(1, delivered);
            Assert.Equal(0, simulator.InFlightCount);
        }

        // ------------------------------------------------------------------ 3. jitter

        [Fact]
        public void JitterSpreadsArrivalsAroundTheBaseLatency()
        {
            var config = new SimulatorConfig
            {
                Enabled = true, LatencyMs = 100f, JitterMs = 40f, RandomSeed = 7,
            };
            NetworkSimulator<int> simulator = Build(config);

            var arrivals = new List<double>();
            var payload = new byte[8];

            for (int i = 0; i < 2000; i++)
            {
                simulator.ShouldSend(payload, 0, 0.0);

                // Step the clock finely so each arrival time is observable.
                for (double t = 55.0; t <= 145.0; t += 1.0)
                {
                    double at = t;
                    simulator.Flush(at, (_, _, _) => arrivals.Add(at));
                    if (simulator.InFlightCount == 0) break;
                }
            }

            Assert.Equal(2000, arrivals.Count);

            double min = double.MaxValue, max = double.MinValue;
            foreach (double a in arrivals)
            {
                if (a < min) min = a;
                if (a > max) max = a;
            }

            // Within 100 +/- 40, and actually using a decent slice of that range.
            Assert.InRange(min, 60.0, 85.0);
            Assert.InRange(max, 115.0, 141.0);
        }

        // ------------------------------------------------------------------ 4. duplication

        [Fact]
        public void DuplicationDeliversSomePacketsTwice()
        {
            var config = new SimulatorConfig
            {
                Enabled = true, LatencyMs = 5f, DuplicatePercent = 10f, RandomSeed = 3,
            };
            NetworkSimulator<int> simulator = Build(config);

            int delivered = PushAndDrain(simulator, SampleSize);
            double duplicateRate = (double)(delivered - SampleSize) / SampleSize;

            Assert.InRange(duplicateRate, 0.08, 0.12);
            Assert.InRange(simulator.DuplicatedCount, 800, 1200);
        }

        // ------------------------------------------------------------------ 5. reordering

        [Fact]
        public void ReorderingActuallyDeliversPacketsOutOfOrder()
        {
            var config = new SimulatorConfig
            {
                Enabled = true, LatencyMs = 50f, ReorderPercent = 20f, RandomSeed = 11,
            };
            NetworkSimulator<int> simulator = Build(config);

            var payload = new byte[8];
            var order = new List<int>();
            double now = 0;

            for (int i = 0; i < 500; i++)
            {
                payload[0] = (byte)(i & 0xFF);
                payload[1] = (byte)(i >> 8);
                simulator.ShouldSend(payload, 0, now);
                now += 10.0;
            }

            simulator.Flush(now + 10_000.0, (buffer, _, _) => order.Add(buffer[0] | (buffer[1] << 8)));

            int inversions = 0;
            for (int i = 1; i < order.Count; i++)
                if (order[i] < order[i - 1]) inversions++;

            Assert.True(inversions > 20, $"only {inversions} inversions — reordering is not working");
        }

        [Fact]
        public void ReorderingEveryPacketReordersNothing()
        {
            // The other half of the same trap, and the one that actually bit while writing
            // these tests: reordering is implemented as an extra delay on the CHOSEN packets,
            // so choosing all of them shifts the entire stream uniformly and preserves the
            // order perfectly. A test that sets ReorderPercent = 100 to "make sure reordering
            // happens" measures the opposite of what it thinks.
            var config = new SimulatorConfig
            {
                Enabled = true, LatencyMs = 50f, ReorderPercent = 100f, RandomSeed = 4,
            };
            NetworkSimulator<int> simulator = Build(config);

            var payload = new byte[8];
            var order = new List<int>();

            for (int i = 0; i < 200; i++)
            {
                payload[0] = (byte)i;
                simulator.ShouldSend(payload, 0, i * 10.0);
            }

            simulator.Flush(10_000.0, (buffer, _, _) => order.Add(buffer[0]));

            for (int i = 1; i < order.Count; i++)
                Assert.True(order[i] > order[i - 1], "a uniform extra delay must not reorder anything");
        }

        [Fact]
        public void ReorderingWithZeroLatencyReordersNothing()
        {
            // The trap called out in the phase-00 doc: reordering works by pushing a packet
            // back by LatencyMs, so at zero latency the extra delay is zero and nothing
            // overtakes. A test that forgets this passes while measuring nothing.
            var config = new SimulatorConfig
            {
                Enabled = true, LatencyMs = 0f, ReorderPercent = 50f, RandomSeed = 11,
            };
            NetworkSimulator<int> simulator = Build(config);

            var payload = new byte[8];
            var order = new List<int>();

            for (int i = 0; i < 200; i++)
            {
                payload[0] = (byte)i;
                simulator.ShouldSend(payload, 0, i * 10.0);
            }

            simulator.Flush(10_000.0, (buffer, _, _) => order.Add(buffer[0]));

            for (int i = 1; i < order.Count; i++)
                Assert.True(order[i] > order[i - 1], "nothing should reorder at zero latency");
        }

        // ------------------------------------------------------------------ plumbing

        [Fact]
        public void ADisabledSimulatorIsAPassThrough()
        {
            NetworkSimulator<int> simulator = Build(SimulatorConfig.Disabled());

            // True means "send it yourself, unchanged" — the simulator took no ownership.
            Assert.True(simulator.ShouldSend(new byte[16], 0, 0.0));
            Assert.Equal(0, simulator.InFlightCount);
        }

        [Fact]
        public void PacketsAreDeliveredToTheDestinationTheyWereSentTo()
        {
            var config = new SimulatorConfig { Enabled = true, LatencyMs = 1f, RandomSeed = 1 };
            NetworkSimulator<int> simulator = Build(config);

            simulator.ShouldSend(new byte[] { 0xAA }, 7, 0.0);
            simulator.ShouldSend(new byte[] { 0xBB }, 9, 0.0);

            var seen = new List<(int destination, byte first)>();
            simulator.Flush(10.0, (buffer, _, destination) => seen.Add((destination, buffer[0])));

            Assert.Contains((7, (byte)0xAA), seen);
            Assert.Contains((9, (byte)0xBB), seen);
        }

        [Fact]
        public void BuffersAreReturnedToThePoolAfterDelivery()
        {
            var pool = new BufferPool(64, ProtocolConstants.MTU_SAFE);
            var config = new SimulatorConfig { Enabled = true, LatencyMs = 5f, RandomSeed = 1 };
            var simulator = new NetworkSimulator<int>(config, pool);

            for (int round = 0; round < 100; round++)
            {
                for (int i = 0; i < 32; i++) simulator.ShouldSend(new byte[64], 0, round * 100.0);
                simulator.Flush(round * 100.0 + 50.0, (_, _, _) => { });
            }

            // 64 pre-allocated buffers, never more than 32 out at once: the pool must never
            // have had to grow.
            Assert.Equal(0, pool.GrewCount);
            Assert.Equal(0, pool.RentedCount);
        }

        [Fact]
        public void ClearReturnsEverythingStillInFlight()
        {
            var pool = new BufferPool(16, ProtocolConstants.MTU_SAFE);
            var config = new SimulatorConfig { Enabled = true, LatencyMs = 1000f, RandomSeed = 1 };
            var simulator = new NetworkSimulator<int>(config, pool);

            for (int i = 0; i < 10; i++) simulator.ShouldSend(new byte[32], 0, 0.0);
            Assert.Equal(10, pool.RentedCount);

            simulator.Clear();

            Assert.Equal(0, simulator.InFlightCount);
            Assert.Equal(0, pool.RentedCount);
        }

        // ------------------------------------------------------------------ presets

        [Theory]
        [InlineData("lan")]
        [InlineData("GOOD")]
        [InlineData("Typical")]
        [InlineData("bad")]
        [InlineData("awful")]
        public void PresetNamesResolveCaseInsensitively(string name)
        {
            Assert.True(SimulatorConfig.FromPresetName(name).Enabled);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("typo")]
        public void AnUnknownPresetIsDisabledRatherThanGuessed(string? name)
        {
            // A typo in IRONFRONT_SIM must not silently impair a production server.
            Assert.False(SimulatorConfig.FromPresetName(name).Enabled);
        }

        [Fact]
        public void TheSeedCanBeOverriddenAlongsideThePreset()
        {
            SimulatorConfig config = SimulatorConfig.FromPresetName("bad", "999");
            Assert.Equal(999, config.RandomSeed);
            Assert.Equal(15f, config.PacketLossPercent);
        }

        [Fact]
        public void PresetsGetProgressivelyWorse()
        {
            Assert.True(SimulatorConfig.Lan().LatencyMs < SimulatorConfig.Good().LatencyMs);
            Assert.True(SimulatorConfig.Good().LatencyMs < SimulatorConfig.Typical().LatencyMs);
            Assert.True(SimulatorConfig.Typical().LatencyMs < SimulatorConfig.Bad().LatencyMs);
            Assert.True(SimulatorConfig.Bad().LatencyMs < SimulatorConfig.Awful().LatencyMs);

            Assert.True(SimulatorConfig.Typical().PacketLossPercent < SimulatorConfig.Bad().PacketLossPercent);
            Assert.True(SimulatorConfig.Bad().PacketLossPercent < SimulatorConfig.Awful().PacketLossPercent);
        }
    }
}
