using System;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.Net.Protocol.Tests.Conformance
{
    /// <summary><c>C_SHOT_REPORT</c> (0x29, 14.0.6): the rounds of one pull the shooter saw strike a body.</summary>
    public sealed class ShotReportMessageTests
    {
        private static ShotReportHit Hit(ushort target, HitboxType box, byte pellet, float x, float y, float z,
            float travelled, uint renderTick)
            => new ShotReportHit(target, box, pellet,
                ShotReportHit.PackMillimetres(x), ShotReportHit.PackMillimetres(y), ShotReportHit.PackMillimetres(z),
                ShotReportHit.PackDecimetres(travelled), renderTick);

        [Fact]
        public void ARoundTripKeepsEveryFieldToTheMillimetre()
        {
            var hits = new[]
            {
                Hit(17, HitboxType.Head, 0, 1234.567f, 81.25f, -300.001f, 412.3f, 98765),
                Hit(42, HitboxType.Body, 13, 10f, 20f, 30f, 6553.4f, 4000000000u),
            };
            var buffer = new byte[ShotReportMessage.SizeFor(hits.Length)];

            int written = ShotReportMessage.Write(buffer, 123456u, WeaponIds.SL_DEFENDER, hits);
            Assert.Equal(ShotReportMessage.SizeFor(2), written);

            var read = new ShotReportHit[ShotReportMessage.MaxHits];
            Assert.True(ShotReportMessage.TryParse(buffer, read, out uint tick, out byte weapon, out int count));
            Assert.Equal(123456u, tick);
            Assert.Equal(WeaponIds.SL_DEFENDER, weapon);
            Assert.Equal(2, count);

            Assert.Equal(17, read[0].TargetActorId);
            Assert.Equal(HitboxType.Head, read[0].HitboxType);
            Assert.Equal(1234.567f, read[0].PointX, 3);
            Assert.Equal(81.25f, read[0].PointY, 3);
            Assert.Equal(-300.001f, read[0].PointZ, 3);
            Assert.Equal(412.3f, read[0].TravelledMetres, 1);
            Assert.Equal(98765u, read[0].RenderTick);
            Assert.Equal(13, read[1].Pellet);
            Assert.Equal(4000000000u, read[1].RenderTick);
        }

        [Fact]
        public void TwentyPelletsFitOneChannelPayload()
        {
            Assert.True(ShotReportMessage.SizeFor(ShotReportMessage.MaxHits) <= ProtocolConstants.MAX_CHANNEL_PAYLOAD);
        }

        [Fact]
        public void AnEmptyOrOverfullReportIsNotWritten()
        {
            var buffer = new byte[1024];
            Assert.Equal(-1, ShotReportMessage.Write(buffer, 1, WeaponIds.RK44, ReadOnlySpan<ShotReportHit>.Empty));
            Assert.Equal(-1, ShotReportMessage.Write(buffer, 1, WeaponIds.RK44, new ShotReportHit[ShotReportMessage.MaxHits + 1]));
        }

        [Fact]
        public void ABodyOfTheWrongLengthOrAnUnknownHitboxIsRefused()
        {
            var hits = new[] { Hit(5, HitboxType.Limb, 0, 1f, 2f, 3f, 4f, 5) };
            var buffer = new byte[ShotReportMessage.SizeFor(1)];
            ShotReportMessage.Write(buffer, 9, WeaponIds.RK44, hits);
            var read = new ShotReportHit[ShotReportMessage.MaxHits];

            Assert.True(ShotReportMessage.TryParse(buffer, read, out _, out _, out _));
            Assert.False(ShotReportMessage.TryParse(buffer.AsSpan(0, buffer.Length - 1), read, out _, out _, out _));

            var longer = new byte[buffer.Length + 1];
            buffer.CopyTo(longer, 0);
            Assert.False(ShotReportMessage.TryParse(longer, read, out _, out _, out _));

            buffer[ShotReportMessage.HeaderSize + 2] = 3;   // a hitbox the wire does not define
            Assert.False(ShotReportMessage.TryParse(buffer, read, out _, out _, out _));
        }

        [Fact]
        public void MetresClampInsteadOfWrapping()
        {
            Assert.Equal(int.MaxValue, ShotReportHit.PackMillimetres(1e12f));
            Assert.Equal(0, ShotReportHit.PackMillimetres(float.NaN));
            Assert.Equal(ushort.MaxValue, ShotReportHit.PackDecimetres(1e9f));
            Assert.Equal(0, ShotReportHit.PackDecimetres(-5f));
        }
    }
}
