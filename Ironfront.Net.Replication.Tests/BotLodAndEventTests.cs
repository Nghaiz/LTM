using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-02 tasks 5 and 6: bot AI LOD (criterion 8) and the gameplay event channel
    /// assignment plus the spawn-ordering guard (criterion 10 / trap 8).
    /// </summary>
    public sealed class BotLodAndEventTests
    {
        // ------------------------------------------------------------------ criterion 8

        [Fact]
        public void ABotNearAPlayerTicksEverySimulationTick()
        {
            var scheduler = new BotLodScheduler();

            for (uint tick = 0; tick < 30; tick++)
                Assert.True(scheduler.ShouldTick(50, InterestLevel.Near, tick));
        }

        [Fact]
        public void ABotAtMidStillTicksEveryTick()
        {
            // Mid is the threshold: a bot a player can see at 100 m must not visibly stutter.
            var scheduler = new BotLodScheduler();

            for (uint tick = 0; tick < 30; tick++)
                Assert.True(scheduler.ShouldTick(50, InterestLevel.Mid, tick));
        }

        [Fact]
        public void ADistantBotTicksAtSixHertz()
        {
            var scheduler = new BotLodScheduler();
            int ticked = 0;

            for (uint tick = 0; tick < 30; tick++)
                if (scheduler.ShouldTick(50, InterestLevel.Far, tick)) ticked++;

            // 30 simulation ticks at one in five.
            Assert.Equal(6, ticked);
        }

        [Fact]
        public void DistantBotsDoNotAllThinkOnTheSameTick()
        {
            // The sketch's plain `serverTick % 5 == 0` concentrates four ticks of AI into one,
            // which makes the p99 the tick budget is graded on WORSE while the mean improves.
            // Offsetting by actor id spreads them evenly.
            var scheduler = new BotLodScheduler();
            var perTick = new int[BotLodScheduler.FarTickInterval];

            for (uint tick = 0; tick < BotLodScheduler.FarTickInterval; tick++)
                for (ushort bot = 100; bot < 132; bot++)
                    if (scheduler.ShouldTick(bot, InterestLevel.Far, tick))
                        perTick[tick]++;

            foreach (int count in perTick)
                Assert.True(count > 0, "a tick had no bots at all — the phase offset is not spreading");

            int busiest = 0, quietest = int.MaxValue;
            foreach (int count in perTick)
            {
                if (count > busiest) busiest = count;
                if (count < quietest) quietest = count;
            }

            Assert.True(busiest - quietest <= 1,
                $"bot AI is bunching: busiest tick {busiest}, quietest {quietest}");
        }

        [Fact]
        public void LodTickingSkipsAtLeastThirtyPercentOfBotUpdates()
        {
            // Criterion 8, on the scenario the phase document describes: 20 of 32 bots distant.
            var scheduler = new BotLodScheduler();

            for (uint tick = 0; tick < 300; tick++)
            {
                for (ushort bot = 100; bot < 112; bot++)
                    scheduler.ShouldTick(bot, InterestLevel.Near, tick);

                for (ushort bot = 112; bot < 132; bot++)
                    scheduler.ShouldTick(bot, InterestLevel.Far, tick);
            }

            Assert.True(scheduler.SkippedPercent >= 30.0,
                $"expected at least 30% of AI updates skipped, measured {scheduler.SkippedPercent:F1}%");
        }

        [Fact]
        public void ASkippedBotIsHandedTheLongerDeltaTime()
        {
            // A bot ticking at 6 Hz that receives a 30 Hz delta moves at a fifth of its speed —
            // the LOD would show up in gameplay as distant bots crawling.
            const float fixedDelta = 1f / 30f;

            Assert.Equal(fixedDelta, BotLodScheduler.DeltaTimeFor(InterestLevel.Near, fixedDelta), 6);
            Assert.Equal(
                fixedDelta * BotLodScheduler.FarTickInterval,
                BotLodScheduler.DeltaTimeFor(InterestLevel.Far, fixedDelta), 6);
        }

        [Fact]
        public void ACulledBotIsTreatedAsDistantRatherThanFrozen()
        {
            // Culled is below the threshold, so it still ticks at the reduced rate. A frozen bot
            // would be standing exactly where it was when the last player walked away.
            var scheduler = new BotLodScheduler();
            int ticked = 0;

            for (uint tick = 0; tick < 30; tick++)
                if (scheduler.ShouldTick(50, InterestLevel.Culled, tick)) ticked++;

            Assert.Equal(6, ticked);
        }

        [Fact]
        public void ResetStatisticsZeroesTheLodCounters()
        {
            var scheduler = new BotLodScheduler();
            scheduler.ShouldTick(50, InterestLevel.Far, 1);

            scheduler.ResetStatistics();

            Assert.Equal(0, scheduler.TicksGranted);
            Assert.Equal(0, scheduler.TicksSkipped);
            Assert.Equal(0.0, scheduler.SkippedPercent);
        }

        // ------------------------------------------------------------------ trap 8

        [Fact]
        public void ASpawnIsRememberedPerViewer()
        {
            var tracker = new SpawnAckTracker();

            Assert.True(tracker.MarkSpawnSent(1, 50));

            Assert.True(tracker.HasSpawnBeenSent(1, 50));
            Assert.False(tracker.HasSpawnBeenSent(2, 50));
        }

        [Fact]
        public void MarkingTheSameSpawnTwiceReportsTheDuplicate()
        {
            var tracker = new SpawnAckTracker();

            Assert.True(tracker.MarkSpawnSent(1, 50));
            Assert.False(tracker.MarkSpawnSent(1, 50));
            Assert.Equal(1, tracker.TrackedPairCount);
        }

        [Fact]
        public void ForgettingAnActorClearsItAsBothViewerAndTarget()
        {
            // Forgetting only the target role leaves a departed player's rows behind forever;
            // forgetting only the viewer role means a respawned id is never re-announced and
            // stays invisible to everyone who knew its previous incarnation.
            var tracker = new SpawnAckTracker();
            tracker.MarkSpawnSent(1, 50);   // 1 knows about 50
            tracker.MarkSpawnSent(50, 1);   // 50 knows about 1
            tracker.MarkSpawnSent(2, 3);    // unrelated

            tracker.Forget(50);

            Assert.False(tracker.HasSpawnBeenSent(1, 50));
            Assert.False(tracker.HasSpawnBeenSent(50, 1));
            Assert.True(tracker.HasSpawnBeenSent(2, 3));
        }

        [Fact]
        public void ClearDropsEveryPair()
        {
            var tracker = new SpawnAckTracker();
            tracker.MarkSpawnSent(1, 50);

            tracker.Clear();

            Assert.Equal(0, tracker.TrackedPairCount);
        }

        // ------------------------------------------------------------------ task 6 channels

        [Fact]
        public void FactsGoReliableAndCuesGoUnreliable()
        {
            // AD-7. Losing a spawn, despawn, death, hit confirmation or explosion leaves the
            // client permanently wrong; losing a gunshot costs one muzzle flash.
            Assert.Equal(ChannelId.ReliableOrdered, ServerEventWriter.ReliableChannel);
            Assert.Equal(ChannelId.SnapshotSequenced, ServerEventWriter.CosmeticChannel);
        }

        [Fact]
        public void ASpawnFramesOntoTheReliableChannel()
        {
            Span<byte> payload = stackalloc byte[ProtocolConstants.MAX_PAYLOAD];
            var message = new SpawnActorMessage(
                42, team: 1, SpawnFlags.IsBot, 100, 0, 200, 1024, health: 100, weaponId: 3);

            int written = ServerEventWriter.WriteSpawn(payload, in message);

            Assert.True(written > 0);
            AssertChannel(payload.Slice(0, written), ChannelId.ReliableOrdered,
                ServerMessageType.SpawnActor);
        }

        [Fact]
        public void ADespawnFramesOntoTheReliableChannel()
        {
            Span<byte> payload = stackalloc byte[ProtocolConstants.MAX_PAYLOAD];

            int written = ServerEventWriter.WriteDespawn(
                payload, new DespawnActorMessage(42, DespawnReason.Left));

            Assert.True(written > 0);
            AssertChannel(payload.Slice(0, written), ChannelId.ReliableOrdered,
                ServerMessageType.DespawnActor);
        }

        [Fact]
        public void ADeathFramesOntoTheReliableChannel()
        {
            Span<byte> payload = stackalloc byte[ProtocolConstants.MAX_PAYLOAD];

            int written = ServerEventWriter.WriteDeath(
                payload, new DeathMessage(9, 1, CauseOfDeath.Bullet, 10, 20, 30, 1));

            Assert.True(written > 0);
            AssertChannel(payload.Slice(0, written), ChannelId.ReliableOrdered,
                ServerMessageType.Death);
        }

        [Fact]
        public void AHitConfirmFramesOntoTheReliableChannel()
        {
            Span<byte> payload = stackalloc byte[ProtocolConstants.MAX_PAYLOAD];

            int written = ServerEventWriter.WriteHitConfirm(
                payload, new HitConfirmMessage(9, 250, HitboxType.Head, HitFlags.Headshot));

            Assert.True(written > 0);
            AssertChannel(payload.Slice(0, written), ChannelId.ReliableOrdered,
                ServerMessageType.HitConfirm);
        }

        [Fact]
        public void AGunshotFramesOntoTheUnreliableChannel()
        {
            // Retransmitting a gunshot would put the muzzle flash on screen after the moment
            // had passed.
            Span<byte> payload = stackalloc byte[ProtocolConstants.MAX_PAYLOAD];

            int written = ServerEventWriter.WriteWeaponFire(
                payload, new WeaponFireMessage(9, 2, 0, 0, 127));

            Assert.True(written > 0);
            AssertChannel(payload.Slice(0, written), ChannelId.SnapshotSequenced,
                ServerMessageType.WeaponFire);
        }

        [Fact]
        public void AnExplosionFramesOntoTheReliableChannel()
        {
            // Reliable while a gunshot is not: a missed muzzle flash is invisible, a missed
            // explosion is a player dying to nothing.
            Span<byte> payload = stackalloc byte[ProtocolConstants.MAX_PAYLOAD];

            int written = ServerEventWriter.WriteExplosion(
                payload, new ExplosionMessage(9, 100, 0, 200, 8, ExplosionKind.Grenade));

            Assert.True(written > 0);
            AssertChannel(payload.Slice(0, written), ChannelId.ReliableOrdered,
                ServerMessageType.Explosion);
        }

        [Fact]
        public void ATooSmallDestinationIsRefusedRatherThanTruncated()
        {
            Span<byte> tiny = stackalloc byte[4];

            Assert.Equal(-1, ServerEventWriter.WriteDespawn(
                tiny, new DespawnActorMessage(1, DespawnReason.Left)));
        }

        [Fact]
        public void EarshotRadiiMatchTheTaskTable()
        {
            Assert.Equal(100f, ServerEventWriter.WeaponFireAudibleRadius, 3);
            Assert.Equal(200f, ServerEventWriter.ExplosionAudibleRadius, 3);

            // Squared, because that is what the broadcast loop has. Passing a linear distance
            // would compare 150 against 40,000 and report almost everything as audible.
            Assert.True(ServerEventWriter.IsWithinEarshotSquared(
                99f * 99f, ServerEventWriter.WeaponFireAudibleRadius));
            Assert.False(ServerEventWriter.IsWithinEarshotSquared(
                101f * 101f, ServerEventWriter.WeaponFireAudibleRadius));
            Assert.True(ServerEventWriter.IsWithinEarshotSquared(
                150f * 150f, ServerEventWriter.ExplosionAudibleRadius));
        }

        private static void AssertChannel(
            ReadOnlySpan<byte> payload, ChannelId expectedChannel, ServerMessageType expectedType)
        {
            var reader = new PayloadFrameReader(payload);

            Assert.True(reader.IsValid);
            Assert.Equal(expectedChannel, reader.Channel);
            Assert.Equal(1, reader.MessageCount);
            Assert.True(reader.TryReadMessage(out byte msgType, out _));
            Assert.Equal((byte)expectedType, msgType);
        }
    }
}
