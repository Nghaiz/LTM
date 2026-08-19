using System;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// <c>S_VEHICLE_SNAPSHOT</c> (0x4C) sizing and codec. protocol-spec.md section 4.10.
    /// </summary>
    /// <remarks>
    /// The size assertions are written against the section 4.10 field table row by row rather
    /// than against the total. Asserting <c>EntrySize(Full) == 30</c> alone would pass with two
    /// fields mis-sized in opposite directions, which is exactly the shape a hand-written codec
    /// gets wrong.
    /// </remarks>
    public class VehicleSnapshotTests
    {
        [Fact]
        public void EachFieldCostsWhatSection410Says()
        {
            const int header = VehicleSnapshotMessage.EntryHeaderSize;

            Assert.Equal(4, header);
            Assert.Equal(header,     VehicleSnapshotMessage.EntrySize(VehicleField.None));
            Assert.Equal(header + 6, VehicleSnapshotMessage.EntrySize(VehicleField.Position));
            Assert.Equal(header + 4, VehicleSnapshotMessage.EntrySize(VehicleField.Rotation));
            Assert.Equal(header + 6, VehicleSnapshotMessage.EntrySize(VehicleField.LinearVelocity));
            Assert.Equal(header + 3, VehicleSnapshotMessage.EntrySize(VehicleField.AngularVelocity));
            Assert.Equal(header + 1, VehicleSnapshotMessage.EntrySize(VehicleField.Health));
            Assert.Equal(header + 1, VehicleSnapshotMessage.EntrySize(VehicleField.Flags));
            Assert.Equal(header + 3, VehicleSnapshotMessage.EntrySize(VehicleField.Turret));
            Assert.Equal(header + 2, VehicleSnapshotMessage.EntrySize(VehicleField.Subtype));
        }

        [Fact]
        public void AFullEntryIsThirtyBytesAndAStationaryOneIsFour()
        {
            Assert.Equal(30, VehicleSnapshotMessage.EntrySize(VehicleField.Full));
            Assert.Equal(30, VehicleSnapshotMessage.FullEntrySize);
            Assert.Equal(4,  VehicleSnapshotMessage.EntrySize(VehicleField.None));
            Assert.Equal(9,  VehicleSnapshotHeader.Size);
        }

        [Fact]
        public void TheChangeMaskBitsMatchSection410()
        {
            Assert.Equal(1 << 0, (ushort)VehicleField.Position);
            Assert.Equal(1 << 1, (ushort)VehicleField.Rotation);
            Assert.Equal(1 << 2, (ushort)VehicleField.LinearVelocity);
            Assert.Equal(1 << 3, (ushort)VehicleField.AngularVelocity);
            Assert.Equal(1 << 4, (ushort)VehicleField.Health);
            Assert.Equal(1 << 5, (ushort)VehicleField.Flags);
            Assert.Equal(1 << 6, (ushort)VehicleField.Turret);
            Assert.Equal(1 << 7, (ushort)VehicleField.Subtype);

            // 8 of 16 used. The spare half is the point: a ninth vehicle field is an additive
            // change, not the mask widening that SnapshotField would need.
            Assert.Equal(0x00FF, (ushort)VehicleField.Full);
        }

        [Fact]
        public void TheVehicleStateFlagBitsMatchSection410()
        {
            Assert.Equal(1 << 0, (byte)VehicleStateFlags.Dead);
            Assert.Equal(1 << 1, (byte)VehicleStateFlags.Burning);
            Assert.Equal(1 << 2, (byte)VehicleStateFlags.InWater);
            Assert.Equal(1 << 3, (byte)VehicleStateFlags.Airborne);
        }

        [Fact]
        public void TheWorstCaseBodyIsFourHundredAndEightyNineAndFitsOneDatagram()
        {
            Assert.Equal(16, ProtocolConstants.MAX_VEHICLES);
            Assert.Equal(489, VehicleSnapshotMessage.MaxBodySize);

            // The co-residency claim, as arithmetic rather than as prose: the bounded vehicle
            // body leaves this much of the snapshot budget for the elastic actor body.
            const int snapshotBudget = 1178;   // ServerPayloadWriter.MaxSnapshotBodySize
            Assert.Equal(689, snapshotBudget - VehicleSnapshotMessage.MaxBodySize);
            Assert.True(VehicleSnapshotMessage.MaxBodySize
                        < ProtocolConstants.MAX_CHANNEL_PAYLOAD);
        }

        [Fact]
        public void SixteenFullEntriesEncodeToExactlyMaxBodySize()
        {
            var entries = new VehicleSnapshotEntry[ProtocolConstants.MAX_VEHICLES];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i].VehicleId  = (ushort)(i + 1);
                entries[i].ChangeMask = VehicleField.Full;
            }

            var header = new VehicleSnapshotHeader(100, 0, (byte)entries.Length);

            Assert.Equal(
                VehicleSnapshotMessage.MaxBodySize,
                VehicleSnapshotMessage.SizeFor(entries));

            var buffer = new byte[VehicleSnapshotMessage.MaxBodySize];
            Assert.Equal(
                VehicleSnapshotMessage.MaxBodySize,
                VehicleSnapshotMessage.Write(buffer, in header, entries));
        }

        [Fact]
        public void AMaskWithOnlyPositionCostsTenBytesAndTouchesNothingElse()
        {
            var entry = new VehicleSnapshotEntry
            {
                VehicleId  = 3,
                ChangeMask = VehicleField.Position,
                PosX = 100, PosY = -200, PosZ = 300,

                // Set, but not flagged. A codec that writes by field rather than by mask would
                // smuggle these onto the wire and the size assertion would catch it.
                Rotation = 0xDEADBEEF, Health = 99, TurretYaw = 1234,
            };

            Assert.Equal(10, VehicleSnapshotMessage.EntrySize(entry.ChangeMask));

            var buffer = new byte[64];
            var header = new VehicleSnapshotHeader(7, 6, 1);
            int written = VehicleSnapshotMessage.Write(
                buffer, in header, new[] { entry });

            Assert.Equal(VehicleSnapshotHeader.Size + 10, written);

            var parsed = new VehicleSnapshotEntry[1];
            Assert.True(VehicleSnapshotMessage.TryParse(
                buffer.AsSpan(0, written), parsed,
                out VehicleSnapshotHeader parsedHeader, out int count));

            Assert.Equal(1, count);
            Assert.Equal(7u, parsedHeader.ServerTick);
            Assert.False(parsedHeader.IsFullSnapshot);

            Assert.Equal(100, parsed[0].PosX);
            Assert.Equal(-200, parsed[0].PosY);
            Assert.Equal(300, parsed[0].PosZ);

            // Everything unflagged came back at its default, which is what tells the decoder to
            // inherit it from the baseline.
            Assert.Equal(0u, parsed[0].Rotation);
            Assert.Equal(0, parsed[0].Health);
            Assert.Equal(0, parsed[0].TurretYaw);
        }

        [Fact]
        public void AFullEntryRoundTripsFieldForField()
        {
            var entry = new VehicleSnapshotEntry
            {
                VehicleId   = 9,
                ChangeMask  = VehicleField.Full,
                PosX = -1000, PosY = 2000, PosZ = 32767,
                Rotation    = 0xC3FFFC00u,
                VelX = 500, VelY = -500, VelZ = 0,
                AngVelX = 12, AngVelY = -12, AngVelZ = 127,
                Health      = 254,
                Flags       = VehicleStateFlags.Burning | VehicleStateFlags.Airborne,
                TurretYaw   = 40000,
                TurretPitch = -100,
                SubtypeA    = 0xAB,
                SubtypeB    = 0xCD,
            };

            var buffer = new byte[64];
            var header = new VehicleSnapshotHeader(500, 499, 1);
            int written = VehicleSnapshotMessage.Write(buffer, in header, new[] { entry });
            Assert.Equal(VehicleSnapshotHeader.Size + 30, written);

            var parsed = new VehicleSnapshotEntry[1];
            Assert.True(VehicleSnapshotMessage.TryParse(
                buffer.AsSpan(0, written), parsed, out _, out int count));
            Assert.Equal(1, count);

            Assert.Equal(entry.VehicleId, parsed[0].VehicleId);
            Assert.Equal(entry.ChangeMask, parsed[0].ChangeMask);
            Assert.Equal(entry.PosX, parsed[0].PosX);
            Assert.Equal(entry.PosY, parsed[0].PosY);
            Assert.Equal(entry.PosZ, parsed[0].PosZ);
            Assert.Equal(entry.Rotation, parsed[0].Rotation);
            Assert.Equal(entry.VelX, parsed[0].VelX);
            Assert.Equal(entry.VelY, parsed[0].VelY);
            Assert.Equal(entry.VelZ, parsed[0].VelZ);
            Assert.Equal(entry.AngVelX, parsed[0].AngVelX);
            Assert.Equal(entry.AngVelY, parsed[0].AngVelY);
            Assert.Equal(entry.AngVelZ, parsed[0].AngVelZ);
            Assert.Equal(entry.Health, parsed[0].Health);
            Assert.Equal(entry.Flags, parsed[0].Flags);
            Assert.Equal(entry.TurretYaw, parsed[0].TurretYaw);
            Assert.Equal(entry.TurretPitch, parsed[0].TurretPitch);
            Assert.Equal(entry.SubtypeA, parsed[0].SubtypeA);
            Assert.Equal(entry.SubtypeB, parsed[0].SubtypeB);
        }

        [Fact]
        public void TryParseRefusesAVehicleCountBeyondTheCallersSpan()
        {
            // The hostile case: a count field the sender inflated. Refused before any field is
            // read, so it cannot walk off the end of the array one entry at a time.
            var buffer = new byte[VehicleSnapshotHeader.Size];
            var header = new VehicleSnapshotHeader(1, 0, 8);
            new SpanWriterProbe(buffer).WriteHeader(in header);

            var tooSmall = new VehicleSnapshotEntry[4];
            Assert.False(VehicleSnapshotMessage.TryParse(buffer, tooSmall, out _, out int count));
            Assert.Equal(0, count);
        }

        [Fact]
        public void TryParseRefusesATruncatedBody()
        {
            var entry = new VehicleSnapshotEntry
            {
                VehicleId = 1, ChangeMask = VehicleField.Full,
            };

            var buffer = new byte[64];
            var header = new VehicleSnapshotHeader(1, 0, 1);
            int written = VehicleSnapshotMessage.Write(buffer, in header, new[] { entry });

            var parsed = new VehicleSnapshotEntry[1];
            Assert.False(VehicleSnapshotMessage.TryParse(
                buffer.AsSpan(0, written - 1), parsed, out VehicleSnapshotHeader got, out int count));

            Assert.Equal(0, count);
            Assert.Equal(default, got.ServerTick);
        }

        [Fact]
        public void WriteRejectsAHeaderCountThatDisagreesWithTheEntries()
        {
            var entries = new VehicleSnapshotEntry[2];
            var header = new VehicleSnapshotHeader(1, 0, 3);
            Assert.Equal(-1, VehicleSnapshotMessage.Write(new byte[128], in header, entries));
        }

        /// <summary>
        /// Writes just a header, so the count-overflow test can build a body no encoder would
        /// produce without reaching into the codec.
        /// </summary>
        private readonly struct SpanWriterProbe
        {
            private readonly byte[] _buffer;
            public SpanWriterProbe(byte[] buffer) => _buffer = buffer;

            public void WriteHeader(in VehicleSnapshotHeader header)
            {
                Endian.WriteU32LE(_buffer, 0, header.ServerTick);
                Endian.WriteU32LE(_buffer, 4, header.BaselineTick);
                _buffer[8] = header.VehicleCount;
            }
        }
    }
}
