using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-35 — the decoders now record WHEN each entry's position arrived, not just what it is.
    /// </summary>
    /// <remarks>
    /// The distinction only exists because a client's decoded world is not a photograph of one
    /// tick. Interest management gives different connections different update rates, so an
    /// entry carried over from a baseline is as old as whenever it last moved — and two clients
    /// holding different values for it are divergent only if these ticks agree.
    /// </remarks>
    public sealed class PositionProvenanceTests
    {
        // ------------------------------------------------------------------ vehicles

        [Fact]
        public void AFullSnapshotStampsEveryEntryWithItsOwnTick()
        {
            var decoder = new VehicleDeltaDecoder();
            var buffer = new byte[VehicleSnapshotMessage.MaxBodySize];

            var world = new VehicleWorldSnapshot { ServerTick = 100 };
            world.Add(Vehicle(1, 0f, 0f, 0f));
            world.Add(Vehicle(2, 5f, 0f, 5f));

            int written = VehicleDeltaEncoder.WriteFull(buffer, world);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            Assert.Equal(100u, decoder.PositionUpdatedAt(0));
            Assert.Equal(100u, decoder.PositionUpdatedAt(1));
        }

        [Fact]
        public void AnEntryWhoseDeltaCarriesNoPositionKeepsTheTickItLastMovedOn()
        {
            // The settling case, in miniature. Vehicle 2 moves at tick 100 and then stops;
            // vehicle 1 keeps moving. At tick 102 the snapshot's own tick is 102 for both, and
            // the truthful answer for vehicle 2 is still 100.
            var decoder = new VehicleDeltaDecoder();
            var buffer = new byte[VehicleSnapshotMessage.MaxBodySize];

            var full = new VehicleWorldSnapshot { ServerTick = 100 };
            full.Add(Vehicle(1, 0f, 0f, 0f));
            full.Add(Vehicle(2, 5f, 0f, 5f));
            int written = VehicleDeltaEncoder.WriteFull(buffer, full);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            // Tick 101: only vehicle 1 carries a position.
            written = VehicleSnapshotMessage.Write(
                buffer,
                new VehicleSnapshotHeader(101, 100, 2),
                new[]
                {
                    Moved(1, 1f, 0f, 0f),
                    Unchanged(2),
                });
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            // Tick 102: same again.
            written = VehicleSnapshotMessage.Write(
                buffer,
                new VehicleSnapshotHeader(102, 101, 2),
                new[]
                {
                    Moved(1, 2f, 0f, 0f),
                    Unchanged(2),
                });
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            Assert.Equal(102u, decoder.Current.ServerTick);
            Assert.Equal(102u, decoder.PositionUpdatedAt(0));
            Assert.Equal(
                100u,
                decoder.PositionUpdatedAt(1));
        }

        [Fact]
        public void AnInheritedTickSurvivesAcrossManyDeltasRatherThanDecayingToTheBaseline()
        {
            // The inherit chain is the part that is easy to get subtly wrong: each tick files
            // its own provenance into history, and the NEXT delta inherits from that filed
            // copy. Get the filing wrong by one tick and a stationary entity's provenance
            // creeps forward one tick at a time, which looks almost right and destroys the
            // divergence/staleness distinction entirely.
            var decoder = new VehicleDeltaDecoder();
            var buffer = new byte[VehicleSnapshotMessage.MaxBodySize];

            var full = new VehicleWorldSnapshot { ServerTick = 200 };
            full.Add(Vehicle(1, 0f, 0f, 0f));
            full.Add(Vehicle(2, 5f, 0f, 5f));
            int written = VehicleDeltaEncoder.WriteFull(buffer, full);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            for (uint tick = 201; tick <= 220; tick++)
            {
                written = VehicleSnapshotMessage.Write(
                    buffer,
                    new VehicleSnapshotHeader(tick, tick - 1, 2),
                    new[]
                    {
                        Moved(1, tick - 200, 0f, 0f),
                        Unchanged(2),
                    });
                Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));
            }

            Assert.Equal(220u, decoder.PositionUpdatedAt(0));
            Assert.Equal(200u, decoder.PositionUpdatedAt(1));
        }

        [Fact]
        public void AVehicleThatArrivesWithNoBaselineIsStampedWithTheTickItArrivedOn()
        {
            var decoder = new VehicleDeltaDecoder();
            var buffer = new byte[VehicleSnapshotMessage.MaxBodySize];

            var full = new VehicleWorldSnapshot { ServerTick = 300 };
            full.Add(Vehicle(1, 0f, 0f, 0f));
            int written = VehicleDeltaEncoder.WriteFull(buffer, full);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            written = VehicleSnapshotMessage.Write(
                buffer,
                new VehicleSnapshotHeader(301, 300, 2),
                new[]
                {
                    Unchanged(1),
                    Vehicle(9, 12f, 0f, 12f),   // never seen before
                });
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            Assert.Equal(300u, decoder.PositionUpdatedAt(0));
            Assert.Equal(301u, decoder.PositionUpdatedAt(1));
        }

        [Fact]
        public void ResetForgetsTheProvenanceAlongWithEverythingElse()
        {
            var decoder = new VehicleDeltaDecoder();
            var buffer = new byte[VehicleSnapshotMessage.MaxBodySize];

            var full = new VehicleWorldSnapshot { ServerTick = 400 };
            full.Add(Vehicle(1, 0f, 0f, 0f));
            int written = VehicleDeltaEncoder.WriteFull(buffer, full);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));
            Assert.Equal(400u, decoder.PositionUpdatedAt(0));

            decoder.Reset();

            Assert.Equal(0u, decoder.PositionUpdatedAt(0));
        }

        // ------------------------------------------------------------------ actors

        [Fact]
        public void TheActorDecoderTracksTheSameThing()
        {
            var decoder = new DeltaDecoder();
            var buffer = new byte[ProtocolConstants.MAX_CHANNEL_PAYLOAD];

            int written = SnapshotMessage.Write(
                buffer,
                new SnapshotHeader(500, 0, 0, 2),
                new[] { Actor(1, 0f, 0f, 0f), Actor(2, 5f, 0f, 5f) });
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            written = SnapshotMessage.Write(
                buffer,
                new SnapshotHeader(501, 0, 500, 2),
                new[]
                {
                    new ActorSnapshotEntry
                    {
                        ActorId = 1,
                        ChangeMask = SnapshotField.Position,
                        PosX = Quantize.PackPos(1f),
                    },
                    new ActorSnapshotEntry { ActorId = 2, ChangeMask = SnapshotField.None },
                });
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            Assert.Equal(501u, decoder.PositionUpdatedAt(0));
            Assert.Equal(500u, decoder.PositionUpdatedAt(1));
        }

        // ------------------------------------------------------------------ helpers

        private static VehicleSnapshotEntry Vehicle(ushort id, float x, float y, float z)
            => new VehicleSnapshotEntry
            {
                VehicleId = id,
                ChangeMask = VehicleField.Full,
                PosX = Quantize.PackPos(x),
                PosY = Quantize.PackPos(y),
                PosZ = Quantize.PackPos(z),
                Rotation = Quantize.PackQuat(0f, 0f, 0f, 1f),
                Health = 255,
            };

        private static VehicleSnapshotEntry Moved(ushort id, float x, float y, float z)
            => new VehicleSnapshotEntry
            {
                VehicleId = id,
                ChangeMask = VehicleField.Position,
                PosX = Quantize.PackPos(x),
                PosY = Quantize.PackPos(y),
                PosZ = Quantize.PackPos(z),
            };

        private static VehicleSnapshotEntry Unchanged(ushort id)
            => new VehicleSnapshotEntry { VehicleId = id, ChangeMask = VehicleField.None };

        private static ActorSnapshotEntry Actor(ushort id, float x, float y, float z)
            => new ActorSnapshotEntry
            {
                ActorId = id,
                ChangeMask = SnapshotField.FullNoSeat,
                PosX = Quantize.PackPos(x),
                PosY = Quantize.PackPos(y),
                PosZ = Quantize.PackPos(z),
                Health = 100,
            };
    }
}
