using System;
using Xunit;

namespace Ironfront.Net.Protocol.Tests
{
    /// <summary>
    /// Pins the v11 meaning of the existing weapon-state byte at the actual snapshot boundary.
    /// The expected bytes are derived directly from protocol-spec.md section 4.3.
    /// </summary>
    public class WeaponStateFlagTests
    {
        private const string ReloadAndPendingHex =
            "07 00 00 00 06 00 00 00 00 00 00 00 01 " +
            "0B 00 20 07 01 01 00 03";

        [Fact]
        public void ReloadAndPendingReleaseShareTheWeaponStateByteOnTheWire()
        {
            var entry = new ActorSnapshotEntry
            {
                ActorId = 11,
                ChangeMask = SnapshotField.Weapon,
                WeaponId = 7,
                AmmoInClip = 1,
                SpareAmmoEncoded = 1,
                WeaponStateFlags = WeaponStateFlags.Reloading | WeaponStateFlags.PendingRelease,
            };

            Span<byte> buffer = stackalloc byte[64];
            var header = new SnapshotHeader(7, 6, 0, 1);
            int written = SnapshotMessage.Write(buffer, in header, new[] { entry });

            Assert.Equal(ReloadAndPendingHex, Hex.ToHex(buffer.Slice(0, written)));

            var parsed = new ActorSnapshotEntry[1];
            Assert.True(SnapshotMessage.TryParse(buffer.Slice(0, written), parsed, out _, out _));
            Assert.Equal(
                WeaponStateFlags.Reloading | WeaponStateFlags.PendingRelease,
                parsed[0].WeaponStateFlags);
        }
    }
}
