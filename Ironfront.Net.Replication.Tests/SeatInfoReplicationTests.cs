using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// <see cref="SnapshotField.SeatInfo"/> end to end, phase-V3 task 6.
    /// </summary>
    /// <remarks>
    /// The field was half-built for two milestones: the decoder applied it and the codec read
    /// and wrote it, but neither producer ever set it. These tests pin both halves, and the
    /// shedding budget the third byte costs.
    /// </remarks>
    public class SeatInfoReplicationTests
    {
        [Fact]
        public void CaptureSetsTheSeatBitOnlyForASeatedActor()
        {
            ActorSnapshotEntry onFoot = Capture(vehicleId: 0, seatIndex: 0);
            ActorSnapshotEntry seated = Capture(vehicleId: 7, seatIndex: 2);

            Assert.Equal(SnapshotField.FullNoSeat, onFoot.ChangeMask);
            Assert.False(onFoot.Has(SnapshotField.SeatInfo));

            Assert.Equal(SnapshotField.Full, seated.ChangeMask);
            Assert.True(seated.Has(SnapshotField.SeatInfo));
            Assert.Equal(7, seated.VehicleId);
            Assert.Equal(2, seated.SeatIndex);
        }

        [Fact]
        public void AFullSnapshotCarriesSeatStateForItsPassengers()
        {
            // The join case. WriteFull used to force FullNoSeat on every entry, so a client that
            // joined mid-round saw every passenger standing in the road until they happened to
            // change seats.
            var world = new WorldSnapshot { ServerTick = 10 };
            world.Add(Capture(vehicleId: 0, seatIndex: 0, actorId: 1));
            world.Add(Capture(vehicleId: 7, seatIndex: 1, actorId: 2));

            var buffer = new byte[512];
            int written = SnapshotBuilder.WriteFull(buffer, world, lastProcessedInputTick: 9);
            Assert.Equal(SnapshotHeader.Size + 20 + 23, written);

            var decoder = new DeltaDecoder();
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            Assert.True(decoder.Current.TryFind(2, out ActorSnapshotEntry passenger));
            Assert.Equal(7, passenger.VehicleId);
            Assert.Equal(1, passenger.SeatIndex);
        }

        [Fact]
        public void EnteringASeatProducesADeltaCarryingSeatInfo()
        {
            ActorSnapshotEntry onFoot = Capture(vehicleId: 0, seatIndex: 0);
            ActorSnapshotEntry seated = Capture(vehicleId: 7, seatIndex: 2);

            SnapshotField mask = DeltaEncoder.ComputeChangeMask(in onFoot, in seated);

            Assert.True((mask & SnapshotField.SeatInfo) != 0);
        }

        [Fact]
        public void LeavingASeatProducesADeltaWithSeatInfoAndVehicleIdZero()
        {
            // This is the entire reason vehicleId 0 is a reserved sentinel: a field sent only on
            // change has no other way to say "no longer seated".
            ActorSnapshotEntry seated = Capture(vehicleId: 7, seatIndex: 2);
            ActorSnapshotEntry onFoot = Capture(vehicleId: 0, seatIndex: 0);

            SnapshotField mask = DeltaEncoder.ComputeChangeMask(in seated, in onFoot);

            Assert.True((mask & SnapshotField.SeatInfo) != 0);
            Assert.Equal(0, onFoot.VehicleId);
        }

        [Fact]
        public void AnUnchangedSeatProducesNoSeatInfoBit()
        {
            ActorSnapshotEntry before = Capture(vehicleId: 7, seatIndex: 2);
            ActorSnapshotEntry after  = Capture(vehicleId: 7, seatIndex: 2);

            SnapshotField mask = DeltaEncoder.ComputeChangeMask(in before, in after);

            Assert.Equal(SnapshotField.None, mask);
        }

        [Fact]
        public void ChangingSeatWithinTheSameVehicleStillSetsTheBit()
        {
            // Driver to gunner. The vehicleId is unchanged, so diffing on it alone would miss
            // this entirely and the client would draw the player in the wrong seat.
            ActorSnapshotEntry driver = Capture(vehicleId: 7, seatIndex: 0);
            ActorSnapshotEntry gunner = Capture(vehicleId: 7, seatIndex: 1);

            SnapshotField mask = DeltaEncoder.ComputeChangeMask(in driver, in gunner);

            Assert.True((mask & SnapshotField.SeatInfo) != 0);
        }

        [Fact]
        public void ASeatChangeSurvivesAFullEncodeDecodeRound()
        {
            var encoder = new DeltaEncoder();
            var decoder = new DeltaDecoder();
            var buffer = new byte[1024];

            var world = new WorldSnapshot { ServerTick = 1 };
            world.Add(Capture(vehicleId: 0, seatIndex: 0, actorId: 3));

            int written = encoder.Write(buffer, world, lastProcessedInputTick: 1);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));
            encoder.OnClientAck(1);

            // The actor gets in.
            world.ServerTick = 2;
            world.Actors[0] = Capture(vehicleId: 7, seatIndex: 1, actorId: 3);

            written = encoder.Write(buffer, world, lastProcessedInputTick: 2);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            Assert.True(decoder.Current.TryFind(3, out ActorSnapshotEntry seated));
            Assert.Equal(7, seated.VehicleId);
            Assert.Equal(1, seated.SeatIndex);

            // And out again.
            world.ServerTick = 3;
            world.Actors[0] = Capture(vehicleId: 0, seatIndex: 0, actorId: 3);
            encoder.OnClientAck(2);

            written = encoder.Write(buffer, world, lastProcessedInputTick: 3);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            Assert.True(decoder.Current.TryFind(3, out ActorSnapshotEntry back));
            Assert.Equal(0, back.VehicleId);
        }

        [Fact]
        public void ANewSeatedActorArrivesWithItsSeatState()
        {
            // The interest-set path rather than the join path: an actor entering the viewer's
            // interest set is not in the baseline, so it gets a full mask — which has to include
            // the seat bit when the actor is in a vehicle.
            var encoder = new DeltaEncoder();
            var buffer = new byte[1024];

            var first = new WorldSnapshot { ServerTick = 1 };
            first.Add(Capture(vehicleId: 0, seatIndex: 0, actorId: 1));
            Assert.True(encoder.Write(buffer, first, 1) > 0);
            encoder.OnClientAck(1);

            var second = new WorldSnapshot { ServerTick = 2 };
            second.Add(Capture(vehicleId: 0, seatIndex: 0, actorId: 1));
            second.Add(Capture(vehicleId: 7, seatIndex: 3, actorId: 2));

            int written = encoder.Write(buffer, second, 2);

            var parsed = new ActorSnapshotEntry[ProtocolConstants.MAX_ACTORS];
            Assert.True(SnapshotMessage.TryParse(
                buffer.AsSpan(0, written), parsed, out _, out int count));

            Assert.Equal(2, count);
            Assert.Equal(SnapshotField.Full, parsed[1].ChangeMask);
            Assert.Equal(7, parsed[1].VehicleId);
            Assert.Equal(3, parsed[1].SeatIndex);
        }

        // --------------------------------------------------------- the shedding budget

        [Fact]
        public void TheProjectedEntryWidthIsTwentyThree()
        {
            // Pinned as a number rather than as a formula, so a change to either constant is a
            // red test rather than a bandwidth regression nobody attributes.
            Assert.Equal(23, InterestManager.MaxEntrySize);
            Assert.Equal(23, SnapshotMessage.EntrySize(SnapshotField.Full));
        }

        [Fact]
        public void TheAdmittedActorCeilingIsFiftyWithVehiclesAbsent()
        {
            // (MaxSnapshotBodySize - SnapshotHeader.Size) / MaxEntrySize
            //   = (1178 - 13) / 23 = 50.65 -> 50.
            // It was 58 while MaxEntrySize was 20. The 48-actor case the game ships still never
            // sheds; the margin above it is what task 6 spent, and this is where that shows up.
            const int budget = ServerPayloadWriter.MaxSnapshotBodySize;

            int ceiling = (budget - SnapshotHeader.Size) / InterestManager.MaxEntrySize;
            Assert.Equal(50, ceiling);

            var interest = new InterestManager();
            var session = new ClientSession(connectionId: 1, actorId: 1);
            var view = new WorldSnapshot();

            var world = new WorldSnapshot { ServerTick = 1 };
            for (int i = 0; i < 64; i++)
                world.Add(Capture(0, 0, (ushort)(i + 1), new Vec3(i * 0.5f, 0f, 3f)));

            interest.BeginSnapshot();
            interest.BuildView(session, world, 1u, view, null, budget);

            Assert.Equal(ceiling, view.ActorCount);
        }

        [Fact]
        public void TheActorFloorWithAFullVehicleBodyIsTwentyNine()
        {
            // The co-residency worst case: the bounded vehicle body takes its 489 bytes first
            // and the elastic actor body gets what is left. 29 actors is still ~45% above the
            // ~20 an interest-managed viewer typically sees.
            const int budget =
                ServerPayloadWriter.MaxSnapshotBodySize - VehicleSnapshotMessage.MaxBodySize;

            Assert.Equal(689, budget);
            Assert.Equal(29, (budget - SnapshotHeader.Size) / InterestManager.MaxEntrySize);
        }

        // ------------------------------------------------------------------ helpers

        private static ActorSnapshotEntry Capture(
            ushort vehicleId, byte seatIndex, ushort actorId = 1, Vec3 position = default)
            => SnapshotBuilder.Capture(
                actorId,
                position,
                yawDegrees: 0f,
                pitchDegrees: 0f,
                velocity: Vec3.Zero,
                stateFlags: ActorStateFlags.IsAlive,
                health: 100f,
                weaponId: 1,
                ammoInClip: 30,
                team: 0,
                vehicleId: vehicleId,
                seatIndex: seatIndex);
    }
}
