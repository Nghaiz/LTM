using System;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// <see cref="VehicleDeltaEncoder"/> / <see cref="VehicleDeltaDecoder"/>, phase-V3 task 4.
    /// </summary>
    /// <remarks>
    /// The behaviours copied from the actor path are re-tested rather than assumed, because
    /// they are copied by hand: comparing quantized values rather than floats, giving a
    /// not-in-baseline entity the full mask, and verifying the stored tick before trusting a
    /// ring slot. Each of those fails silently on the vehicle path exactly as it would on the
    /// actor one.
    /// </remarks>
    public class VehicleSnapshotAndDeltaTests
    {
        [Fact]
        public void AStationaryVehicleProducesAFourByteEntry()
        {
            VehicleSnapshotEntry entry = Vehicle(1, 100f, 0f, 100f);

            Assert.Equal(
                VehicleField.None,
                VehicleDeltaEncoder.ComputeChangeMask(in entry, in entry));

            Assert.Equal(4, VehicleSnapshotMessage.EntrySize(VehicleField.None));
        }

        [Fact]
        public void PhysicsJitterBelowTheQuantizationStepDoesNotSetTheChangeBit()
        {
            // The trap the actor encoder documents: comparing raw floats sets Position on every
            // entity every tick, the delta carries every field, bandwidth matches a full
            // snapshot — and every functional test still passes. A vehicle idling on a slope is
            // the case that produces it.
            VehicleSnapshotEntry settled  = Vehicle(1, 100f, 0f, 100f);
            VehicleSnapshotEntry jittered = Vehicle(1, 100.0001f, 0.0001f, 100.0001f);

            Assert.Equal(
                VehicleField.None,
                VehicleDeltaEncoder.ComputeChangeMask(in settled, in jittered));
        }

        [Fact]
        public void AVehicleThatOnlyRotatesProducesAnEightByteEntry()
        {
            VehicleSnapshotEntry before = Vehicle(1, 100f, 0f, 100f);
            VehicleSnapshotEntry after  = before;
            after.Rotation = Quantize.PackQuat(0f, 0.7071f, 0f, 0.7071f);

            VehicleField mask = VehicleDeltaEncoder.ComputeChangeMask(in before, in after);

            Assert.Equal(VehicleField.Rotation, mask);
            Assert.Equal(4 + 4, VehicleSnapshotMessage.EntrySize(mask));
        }

        [Fact]
        public void EveryFieldIsDiffedIndividually()
        {
            VehicleSnapshotEntry baseline = Vehicle(1, 0f, 0f, 0f);

            AssertOnly(baseline, e => e.PosX = 500,                    VehicleField.Position);
            AssertOnly(baseline, e => e.Rotation = 0x1234u,            VehicleField.Rotation);
            AssertOnly(baseline, e => e.VelY = 900,                    VehicleField.LinearVelocity);
            AssertOnly(baseline, e => e.AngVelZ = 7,                   VehicleField.AngularVelocity);
            AssertOnly(baseline, e => e.Health = 42,                   VehicleField.Health);
            AssertOnly(baseline, e => e.Flags = VehicleStateFlags.Dead, VehicleField.Flags);
            AssertOnly(baseline, e => e.TurretPitch = -20,             VehicleField.Turret);
            AssertOnly(baseline, e => e.SubtypeB = 0x99,               VehicleField.Subtype);
        }

        [Fact]
        public void AVehicleMissingFromTheBaselineGetsTheFullMask()
        {
            var encoder = new VehicleDeltaEncoder();
            var buffer = new byte[VehicleSnapshotMessage.MaxBodySize];

            // Tick 1, no baseline yet -> full snapshot, and the client acks it.
            var first = new VehicleWorldSnapshot { ServerTick = 1 };
            first.Add(Vehicle(1, 0f, 0f, 0f));
            Assert.True(encoder.Write(buffer, first) > 0);
            encoder.OnClientAck(1);

            // Tick 2 adds a vehicle the client has never seen. A changed-fields-only mask would
            // leave the rest of that entry as garbage on the client.
            var second = new VehicleWorldSnapshot { ServerTick = 2 };
            second.Add(Vehicle(1, 0f, 0f, 0f));
            second.Add(Vehicle(2, 50f, 0f, 50f));

            int written = encoder.Write(buffer, second);
            Assert.True(written > 0);

            var parsed = new VehicleSnapshotEntry[ProtocolConstants.MAX_VEHICLES];
            Assert.True(VehicleSnapshotMessage.TryParse(
                buffer.AsSpan(0, written), parsed, out VehicleSnapshotHeader header, out int count));

            Assert.Equal(1u, header.BaselineTick);
            Assert.Equal(2, count);
            Assert.Equal(VehicleField.None, parsed[0].ChangeMask);   // unchanged
            Assert.Equal(VehicleField.Full, parsed[1].ChangeMask);   // brand new
            Assert.Equal(30, VehicleSnapshotMessage.EntrySize(parsed[1].ChangeMask));
        }

        [Fact]
        public void ADecodedDeltaOverABaselineEqualsTheOriginal()
        {
            var encoder = new VehicleDeltaEncoder();
            var decoder = new VehicleDeltaDecoder();
            var buffer = new byte[VehicleSnapshotMessage.MaxBodySize];

            var first = new VehicleWorldSnapshot { ServerTick = 1 };
            first.Add(Vehicle(1, 10f, 0f, 10f));
            first.Add(Vehicle(2, -10f, 0f, -10f));

            int written = encoder.Write(buffer, first);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));
            encoder.OnClientAck(1);

            // Tick 2: vehicle 1 moved and took damage, vehicle 2 did not move at all.
            var second = new VehicleWorldSnapshot { ServerTick = 2 };
            VehicleSnapshotEntry moved = Vehicle(1, 25f, 1f, 10f);
            moved.Health = 120;
            moved.Flags  = VehicleStateFlags.Burning;
            second.Add(moved);
            second.Add(Vehicle(2, -10f, 0f, -10f));

            written = encoder.Write(buffer, second);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            Assert.Equal(2, decoder.Current.VehicleCount);
            Assert.True(decoder.Current.TryFind(1, out VehicleSnapshotEntry one));
            Assert.True(decoder.Current.TryFind(2, out VehicleSnapshotEntry two));

            Assert.Equal(moved.PosX, one.PosX);
            Assert.Equal(moved.PosY, one.PosY);
            Assert.Equal(moved.PosZ, one.PosZ);
            Assert.Equal(120, one.Health);
            Assert.Equal(VehicleStateFlags.Burning, one.Flags);

            // Vehicle 2 carried nothing on the wire, so every field of it is inherited. This is
            // the trap-5 case: a decoder that builds fresh entries would put it at the origin.
            Assert.Equal(Quantize.PackPos(-10f), two.PosX);
            Assert.Equal(Quantize.PackPos(-10f), two.PosZ);
        }

        [Fact]
        public void ADeltaAgainstAnUnknownBaselineIsCountedNotThrown()
        {
            var decoder = new VehicleDeltaDecoder();

            var entries = new[]
            {
                new VehicleSnapshotEntry { VehicleId = 1, ChangeMask = VehicleField.Position },
            };
            var header = new VehicleSnapshotHeader(50, 49, 1);

            var buffer = new byte[64];
            int written = VehicleSnapshotMessage.Write(buffer, in header, entries);

            Assert.Equal(
                SnapshotReadResult.UnknownBaseline,
                decoder.Read(buffer.AsSpan(0, written)));
            Assert.Equal(1, decoder.UnknownBaselineCount);
        }

        [Fact]
        public void AnOlderSnapshotIsRefusedAsStale()
        {
            var decoder = new VehicleDeltaDecoder();
            var buffer = new byte[64];

            var newer = new VehicleWorldSnapshot { ServerTick = 10 };
            newer.Add(Vehicle(1, 0f, 0f, 0f));
            int written = VehicleDeltaEncoder.WriteFull(buffer, newer);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            var older = new VehicleWorldSnapshot { ServerTick = 9 };
            older.Add(Vehicle(1, 0f, 0f, 0f));
            written = VehicleDeltaEncoder.WriteFull(buffer, older);

            Assert.Equal(SnapshotReadResult.Stale, decoder.Read(buffer.AsSpan(0, written)));
            Assert.Equal(1, decoder.StaleCount);
        }

        [Fact]
        public void ARingSlotHoldingADifferentTickIsNotTrustedAsABaseline()
        {
            // Ticks 1 and 33 share a slot at BaselineHistory = 32. Acking 1 and then asking for
            // a delta at 33 must fall back to a full snapshot rather than delta against
            // whatever tick 33 overwrote the slot with.
            var encoder = new VehicleDeltaEncoder();
            var buffer = new byte[VehicleSnapshotMessage.MaxBodySize];

            var world = new VehicleWorldSnapshot();
            world.Add(Vehicle(1, 0f, 0f, 0f));

            for (uint tick = 1; tick <= 33; tick++)
            {
                world.ServerTick = tick;
                Assert.True(encoder.Write(buffer, world) > 0);
            }

            // The client only ever acked tick 1, which is 32 ticks behind and out of range.
            encoder.OnClientAck(1);

            world.ServerTick = 34;
            int written = encoder.Write(buffer, world);

            var parsed = new VehicleSnapshotEntry[ProtocolConstants.MAX_VEHICLES];
            Assert.True(VehicleSnapshotMessage.TryParse(
                buffer.AsSpan(0, written), parsed, out VehicleSnapshotHeader header, out _));

            Assert.True(header.IsFullSnapshot);
        }

        [Fact]
        public void AnAckIsNeverWalkedBackwards()
        {
            var encoder = new VehicleDeltaEncoder();

            encoder.OnClientAck(100);
            encoder.OnClientAck(99);
            Assert.Equal(100u, encoder.AckedBaselineTick);

            encoder.OnClientAck(101);
            Assert.Equal(101u, encoder.AckedBaselineTick);
        }

        [Fact]
        public void ResetForcesTheNextSnapshotFull()
        {
            var encoder = new VehicleDeltaEncoder();
            var buffer = new byte[VehicleSnapshotMessage.MaxBodySize];

            var world = new VehicleWorldSnapshot { ServerTick = 1 };
            world.Add(Vehicle(1, 0f, 0f, 0f));
            encoder.Write(buffer, world);
            encoder.OnClientAck(1);

            encoder.Reset();
            Assert.Equal(0u, encoder.AckedBaselineTick);

            world.ServerTick = 2;
            int written = encoder.Write(buffer, world);

            var parsed = new VehicleSnapshotEntry[ProtocolConstants.MAX_VEHICLES];
            Assert.True(VehicleSnapshotMessage.TryParse(
                buffer.AsSpan(0, written), parsed, out VehicleSnapshotHeader header, out _));
            Assert.True(header.IsFullSnapshot);
        }

        [Fact]
        public void AVehicleAbsentFromASnapshotIsDroppedFromTheWorld()
        {
            // The despawn path: the vehicle list is authoritative about who exists.
            var encoder = new VehicleDeltaEncoder();
            var decoder = new VehicleDeltaDecoder();
            var buffer = new byte[VehicleSnapshotMessage.MaxBodySize];

            var first = new VehicleWorldSnapshot { ServerTick = 1 };
            first.Add(Vehicle(1, 0f, 0f, 0f));
            first.Add(Vehicle(2, 5f, 0f, 5f));
            int written = encoder.Write(buffer, first);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));
            encoder.OnClientAck(1);

            var second = new VehicleWorldSnapshot { ServerTick = 2 };
            second.Add(Vehicle(1, 0f, 0f, 0f));

            written = encoder.Write(buffer, second);
            Assert.Equal(SnapshotReadResult.Applied, decoder.Read(buffer.AsSpan(0, written)));

            Assert.Equal(1, decoder.Current.VehicleCount);
            Assert.False(decoder.Current.TryFind(2, out _));
        }

        [Fact]
        public void TheWorldSnapshotRefusesToOverflow()
        {
            var world = new VehicleWorldSnapshot();

            for (int i = 0; i < ProtocolConstants.MAX_VEHICLES; i++)
                Assert.True(world.Add(Vehicle((ushort)(i + 1), 0f, 0f, 0f)));

            // One past the cap returns false rather than throwing: a full map is a normal
            // operating condition.
            Assert.False(world.Add(Vehicle(99, 0f, 0f, 0f)));
            Assert.Equal(ProtocolConstants.MAX_VEHICLES, world.VehicleCount);
        }

        // ------------------------------------------------------------------ helpers

        private static void AssertOnly(
            VehicleSnapshotEntry baseline,
            Action<Mutator> mutate,
            VehicleField expected)
        {
            VehicleSnapshotEntry changed = baseline;
            var mutator = new Mutator();
            mutate(mutator);
            mutator.ApplyTo(ref changed);

            Assert.Equal(
                expected,
                VehicleDeltaEncoder.ComputeChangeMask(in baseline, in changed));
        }

        /// <summary>
        /// Records one field assignment so the per-field diff assertions read as one line each.
        /// A lambda taking <c>ref</c> is not expressible, and a full entry per case would bury
        /// the one field that matters.
        /// </summary>
        private sealed class Mutator
        {
            private short? _posX;
            private uint? _rotation;
            private short? _velY;
            private sbyte? _angVelZ;
            private byte? _health;
            private VehicleStateFlags? _flags;
            private sbyte? _turretPitch;
            private byte? _subtypeB;

            public short PosX { set => _posX = value; }
            public uint Rotation { set => _rotation = value; }
            public short VelY { set => _velY = value; }
            public sbyte AngVelZ { set => _angVelZ = value; }
            public byte Health { set => _health = value; }
            public VehicleStateFlags Flags { set => _flags = value; }
            public sbyte TurretPitch { set => _turretPitch = value; }
            public byte SubtypeB { set => _subtypeB = value; }

            public void ApplyTo(ref VehicleSnapshotEntry entry)
            {
                if (_posX.HasValue)        entry.PosX        = _posX.Value;
                if (_rotation.HasValue)    entry.Rotation    = _rotation.Value;
                if (_velY.HasValue)        entry.VelY        = _velY.Value;
                if (_angVelZ.HasValue)     entry.AngVelZ     = _angVelZ.Value;
                if (_health.HasValue)      entry.Health      = _health.Value;
                if (_flags.HasValue)       entry.Flags       = _flags.Value;
                if (_turretPitch.HasValue) entry.TurretPitch = _turretPitch.Value;
                if (_subtypeB.HasValue)    entry.SubtypeB    = _subtypeB.Value;
            }
        }

        /// <summary>
        /// A quantized vehicle entry, built the way a real capture would build it — through
        /// <see cref="Quantize"/>, so the change detection under test is comparing the same
        /// integers the wire would carry.
        /// </summary>
        private static VehicleSnapshotEntry Vehicle(ushort id, float x, float y, float z)
            => new VehicleSnapshotEntry
            {
                VehicleId  = id,
                ChangeMask = VehicleField.Full,
                PosX = Quantize.PackPos(x),
                PosY = Quantize.PackPos(y),
                PosZ = Quantize.PackPos(z),
                Rotation = Quantize.PackQuat(0f, 0f, 0f, 1f),
                Health = 255,
            };
    }
}
