using System;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// protocol-spec.md section 14, checklist item 10:
    /// "A delta snapshot with changeMask = 0b00000011 contains only pos + rot".
    /// </summary>
    public class SnapshotTests
    {
        // serverTick 100, lastProcessedInputTick 95, baselineTick 98, 1 actor.
        // Actor 7, changeMask 0b00000011: position (0, 1600, -1600), rotation (0x8000, 45).
        private const string DeltaHex =
            "64 00 00 00 5F 00 00 00 62 00 00 00 01 " +
            "07 00 03 00 00 40 06 C0 F9 00 80 2D";

        [Fact]
        public void HeaderIs13Bytes()
        {
            Assert.Equal(13, SnapshotHeader.Size);
            Assert.Equal(4 + 4 + 4 + 1, SnapshotHeader.Size);
        }

        /// <summary>
        /// The per-actor byte budgets the spec's bandwidth arithmetic depends on
        /// (section 4.3). If these drift, the ~7 KB/s downstream target drifts with them.
        /// </summary>
        [Fact]
        public void EntrySizes_MatchTheSpecBudget()
        {
            // "Full (every field)": 2 + 1 + 6 + 3 + 3 + 1 + 1 + 2 + 1 = 20
            Assert.Equal(20, SnapshotMessage.EntrySize(SnapshotField.FullNoSeat));

            // The seated case, which is what InterestManager now projects against. 3 bytes
            // wider, and the 8 bytes of shedding headroom that costs are the reason it is
            // pinned here rather than left to a bandwidth regression to discover.
            Assert.Equal(23, SnapshotMessage.EntrySize(SnapshotField.Full));

            // "Typical delta (pos + rot only)": 2 + 1 + 6 + 3 = 12
            Assert.Equal(12, SnapshotMessage.EntrySize(
                SnapshotField.Position | SnapshotField.Rotation));

            // "Delta for a stationary actor": 2 + 1 = 3
            Assert.Equal(3, SnapshotMessage.EntrySize(SnapshotField.None));
        }

        [Fact]
        public void ChangeMaskBits_MatchSpecSection43()
        {
            Assert.Equal(1 << 0, (byte)SnapshotField.Position);
            Assert.Equal(1 << 1, (byte)SnapshotField.Rotation);
            Assert.Equal(1 << 2, (byte)SnapshotField.Velocity);
            Assert.Equal(1 << 3, (byte)SnapshotField.StateFlags);
            Assert.Equal(1 << 4, (byte)SnapshotField.Health);
            Assert.Equal(1 << 5, (byte)SnapshotField.Weapon);
            Assert.Equal(1 << 6, (byte)SnapshotField.Team);
            Assert.Equal(1 << 7, (byte)SnapshotField.SeatInfo);

            // All 8 bits, now that the seat field is populated rather than reserved.
            Assert.Equal(0xFF, (byte)SnapshotField.Full);
            Assert.Equal(0x7F, (byte)SnapshotField.FullNoSeat);
        }

        [Fact]
        public void StateFlagBits_MatchSpecSection43()
        {
            Assert.Equal(1 << 0, (byte)ActorStateFlags.IsAlive);
            Assert.Equal(1 << 1, (byte)ActorStateFlags.IsCrouching);
            Assert.Equal(1 << 2, (byte)ActorStateFlags.IsProne);
            Assert.Equal(1 << 3, (byte)ActorStateFlags.IsSprinting);
            Assert.Equal(1 << 4, (byte)ActorStateFlags.IsAiming);
            Assert.Equal(1 << 5, (byte)ActorStateFlags.IsInWater);
            Assert.Equal(1 << 6, (byte)ActorStateFlags.IsRagdoll);
            Assert.Equal(1 << 7, (byte)ActorStateFlags.IsSeated);
        }

        [Fact]
        public void DeltaWithPositionAndRotationOnly_SerializesToTheExpectedBytes()
        {
            var entry = new ActorSnapshotEntry
            {
                ActorId    = 7,
                ChangeMask = SnapshotField.Position | SnapshotField.Rotation,
                PosX = 0, PosY = 1600, PosZ = -1600,
                Yaw = 0x8000, Pitch = 45,

                // These are deliberately set but NOT flagged in the mask. They must not
                // reach the wire — that is exactly what "contains only pos + rot" means.
                Health     = 99,
                WeaponId   = 42,
                Team       = 3,
                StateFlags = ActorStateFlags.IsAlive | ActorStateFlags.IsSprinting,
                VelX = 10, VelY = 20, VelZ = 30,
            };

            var header = new SnapshotHeader(
                serverTick: 100, lastProcessedInputTick: 95, baselineTick: 98, actorCount: 1);

            var entries = new[] { entry };
            Span<byte> buffer = stackalloc byte[SnapshotMessage.SizeFor(entries)];
            int written = SnapshotMessage.Write(buffer, header, entries);

            // 13 header + 12 entry = 25. If any unflagged field leaked in, this grows.
            Assert.Equal(25, written);
            Assert.Equal(DeltaHex, Hex.ToHex(buffer));
        }

        [Fact]
        public void DeltaWithPositionAndRotationOnly_ParsesBackWithOnlyThoseFields()
        {
            var entries = new ActorSnapshotEntry[ProtocolConstants.MAX_ACTORS];

            Assert.True(SnapshotMessage.TryParse(
                Hex.FromHex(DeltaHex), entries, out SnapshotHeader header, out int count));

            Assert.Equal(100u, header.ServerTick);
            Assert.Equal(95u, header.LastProcessedInputTick);
            Assert.Equal(98u, header.BaselineTick);
            Assert.False(header.IsFullSnapshot);
            Assert.Equal(1, count);

            ActorSnapshotEntry e = entries[0];
            Assert.Equal(7, e.ActorId);
            Assert.True(e.Has(SnapshotField.Position));
            Assert.True(e.Has(SnapshotField.Rotation));
            Assert.False(e.Has(SnapshotField.Velocity));
            Assert.False(e.Has(SnapshotField.Health));
            Assert.False(e.Has(SnapshotField.Weapon));
            Assert.False(e.Has(SnapshotField.Team));
            Assert.False(e.Has(SnapshotField.StateFlags));

            Assert.Equal(0, e.PosX);
            Assert.Equal(1600, e.PosY);
            Assert.Equal(-1600, e.PosZ);
            Assert.Equal(0x8000, e.Yaw);
            Assert.Equal(45, e.Pitch);

            // Absent fields stay at their default — the caller reads them from the
            // baseline snapshot, which is what makes delta encoding work.
            Assert.Equal(0, e.Health);
            Assert.Equal(0, e.WeaponId);
            Assert.Equal(0, e.Team);
            Assert.Equal(ActorStateFlags.None, e.StateFlags);
        }

        [Fact]
        public void BaselineTickZero_MeansFullSnapshot()
        {
            var header = new SnapshotHeader(500, 480, 0, 0);
            Assert.True(header.IsFullSnapshot);

            var delta = new SnapshotHeader(500, 480, 490, 0);
            Assert.False(delta.IsFullSnapshot);
        }

        [Fact]
        public void FullEntry_RoundTripsEveryField()
        {
            var entry = new ActorSnapshotEntry
            {
                ActorId    = 63,
                ChangeMask = SnapshotField.FullNoSeat,
                PosX = -32768, PosY = 0, PosZ = 32767,
                Yaw = 65535, Pitch = -127,
                VelX = 127, VelY = -128, VelZ = 0,
                StateFlags = ActorStateFlags.IsAlive | ActorStateFlags.IsAiming,
                Health = 100,
                WeaponId = 200, AmmoInClip = 30,
                Team = 1,
            };

            var entries = new[] { entry };
            var header = new SnapshotHeader(1, 1, 0, 1);

            Span<byte> buffer = stackalloc byte[SnapshotMessage.SizeFor(entries)];
            Assert.Equal(13 + 20, SnapshotMessage.Write(buffer, header, entries));

            var parsed = new ActorSnapshotEntry[1];
            Assert.True(SnapshotMessage.TryParse(buffer, parsed, out _, out int count));
            Assert.Equal(1, count);

            ActorSnapshotEntry p = parsed[0];
            Assert.Equal(entry.ActorId, p.ActorId);
            Assert.Equal(entry.ChangeMask, p.ChangeMask);
            Assert.Equal(entry.PosX, p.PosX);
            Assert.Equal(entry.PosY, p.PosY);
            Assert.Equal(entry.PosZ, p.PosZ);
            Assert.Equal(entry.Yaw, p.Yaw);
            Assert.Equal(entry.Pitch, p.Pitch);
            Assert.Equal(entry.VelX, p.VelX);
            Assert.Equal(entry.VelY, p.VelY);
            Assert.Equal(entry.VelZ, p.VelZ);
            Assert.Equal(entry.StateFlags, p.StateFlags);
            Assert.Equal(entry.Health, p.Health);
            Assert.Equal(entry.WeaponId, p.WeaponId);
            Assert.Equal(entry.AmmoInClip, p.AmmoInClip);
            Assert.Equal(entry.Team, p.Team);
        }

        [Fact]
        public void StationaryActor_CostsThreeBytes()
        {
            var entries = new[]
            {
                new ActorSnapshotEntry { ActorId = 12, ChangeMask = SnapshotField.None },
            };
            var header = new SnapshotHeader(10, 9, 8, 1);

            Span<byte> buffer = stackalloc byte[SnapshotMessage.SizeFor(entries)];
            Assert.Equal(13 + 3, SnapshotMessage.Write(buffer, header, entries));
            Assert.Equal("0A 00 00 00 09 00 00 00 08 00 00 00 01 0C 00 00", Hex.ToHex(buffer));
        }

        [Fact]
        public void Write_RejectsAnActorCountThatDisagreesWithTheEntries()
        {
            var entries = new[]
            {
                new ActorSnapshotEntry { ActorId = 1, ChangeMask = SnapshotField.None },
            };
            // Header claims 2 actors, one entry supplied.
            var header = new SnapshotHeader(1, 1, 0, 2);

            Span<byte> buffer = stackalloc byte[64];
            Assert.Equal(-1, SnapshotMessage.Write(buffer, header, entries));
        }

        [Fact]
        public void TryParse_RejectsATruncatedEntry()
        {
            byte[] truncated = Hex.FromHex(DeltaHex).AsSpan(0, 20).ToArray();
            var entries = new ActorSnapshotEntry[ProtocolConstants.MAX_ACTORS];

            Assert.False(SnapshotMessage.TryParse(truncated, entries, out _, out _));
        }

        [Fact]
        public void TryParse_RejectsAnUndersizedEntryBuffer()
        {
            var tooSmall = new ActorSnapshotEntry[0];
            Assert.False(SnapshotMessage.TryParse(Hex.FromHex(DeltaHex), tooSmall, out _, out _));
        }
    }
}
