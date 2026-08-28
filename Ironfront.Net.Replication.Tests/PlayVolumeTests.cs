using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.World;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The play volume, and the wire behaviour that makes leaving it silent. Ledger <b>E-6</b>.
    /// </summary>
    /// <remarks>
    /// The first test here is about <c>Quantize</c> rather than <c>PlayVolume</c> on purpose. It
    /// pins the mechanism the rest of this file exists to defend against — every other assertion
    /// is only worth making because that one is true, and if the quantizer ever started throwing
    /// or wrapping instead of clamping, the response chosen for E-6 would be answering a
    /// question nobody was asking any more.
    /// </remarks>
    public sealed class PlayVolumeTests
    {
        /// <summary>Dustbowl's authored box: 1700 x 700 x 1600 m, centred near the origin.</summary>
        private static PlayVolume Dustbowl() =>
            new PlayVolume(
                new Vec3(-70.778564f, 207.57117f, -88.625244f),
                new Vec3(1700f, 700f, 1600f));

        /// <summary>
        /// Past the wire's range the snapshot pins the body and says nothing. This is E-6.
        /// </summary>
        [Fact]
        public void PositionQuantizationClampsSilentlyBeyondTheWireRange()
        {
            short atEdge = Quantize.PackPos(Quantize.POS_MAX);
            short wayPast = Quantize.PackPos(Quantize.POS_MAX + 500f);

            // No exception, no sentinel — the same code for two positions 500 m apart.
            Assert.Equal(atEdge, wayPast);

            float decoded = Quantize.UnpackPos(wayPast);
            Assert.True(
                Math.Abs(decoded - Quantize.POS_MAX) < 0.1f,
                $"a body 500 m past the range decoded to {decoded}, not the boundary.");
        }

        [Fact]
        public void ContainsAcceptsTheBoundaryAndRejectsEachAxisIndependently()
        {
            PlayVolume volume = Dustbowl();

            Assert.True(volume.Contains(volume.Min));
            Assert.True(volume.Contains(volume.Max));

            // One axis out at a time, so a check that ORs its axes cannot pass this.
            Assert.False(volume.Contains(new Vec3(volume.Max.X + 1f, 0f, 0f)));
            Assert.False(volume.Contains(new Vec3(0f, volume.Max.Y + 1f, 0f)));
            Assert.False(volume.Contains(new Vec3(0f, 0f, volume.Max.Z + 1f)));
            Assert.False(volume.Contains(new Vec3(volume.Min.X - 1f, 0f, 0f)));
            Assert.False(volume.Contains(new Vec3(0f, volume.Min.Y - 1f, 0f)));
            Assert.False(volume.Contains(new Vec3(0f, 0f, volume.Min.Z - 1f)));
        }

        /// <summary>
        /// The size/extents confusion, pinned. Half of 1700 is 850, not 1700.
        /// </summary>
        /// <remarks>
        /// <c>LevelBounds.SetupBounds</c> passes <c>transform.localScale</c> to
        /// <c>Bounds(center, size)</c>, so this constructor must read its argument as a full
        /// size. Reading it as extents doubles the box, which looks right in the Editor and lets
        /// bodies out at twice the authored distance with every test still green.
        /// </remarks>
        [Fact]
        public void TheConstructorReadsAFullSizeNotExtents()
        {
            var volume = new PlayVolume(new Vec3(0f, 0f, 0f), new Vec3(1700f, 700f, 1600f));

            Assert.Equal(-850f, volume.Min.X, 3);
            Assert.Equal(850f, volume.Max.X, 3);
            Assert.Equal(-350f, volume.Min.Y, 3);
            Assert.Equal(800f, volume.Max.Z, 3);
        }

        [Fact]
        public void TryClampReportsFalseAndChangesNothingInside()
        {
            PlayVolume volume = Dustbowl();
            var inside = new Vec3(0f, 200f, 0f);

            Assert.False(volume.TryClamp(in inside, out Vec3 clamped));
            Assert.Equal(inside, clamped);
        }

        /// <summary>
        /// The clamp pulls back only the axes that left, and reports that it happened.
        /// </summary>
        /// <remarks>
        /// The bool is the load-bearing half. A clamp that returned only a position would turn a
        /// crossing into a value and lose the event, which is precisely how this stayed
        /// invisible: the quantizer has been clamping all along.
        /// </remarks>
        [Fact]
        public void TryClampPullsBackOnlyTheAxesThatLeft()
        {
            PlayVolume volume = Dustbowl();
            var outside = new Vec3(volume.Max.X + 1200f, 300f, 0f);

            Assert.True(volume.TryClamp(in outside, out Vec3 clamped));
            Assert.Equal(volume.Max.X, clamped.X, 3);
            Assert.Equal(300f, clamped.Y, 3);   // untouched
            Assert.Equal(0f, clamped.Z, 3);     // untouched
            Assert.True(volume.Contains(in clamped));
        }

        /// <summary>
        /// Dustbowl's authored box fits the wire, so clamping to it also keeps bodies encodable.
        /// </summary>
        /// <remarks>
        /// This is the assertion that stops the E-6 response from being decorative. Clamping to
        /// an authored box only prevents the silent divergence if the box is itself inside
        /// ±2048 m; widen it past that and every other test in this file still passes while the
        /// original bug returns.
        /// </remarks>
        [Fact]
        public void DustbowlsAuthoredVolumeFitsOnTheWire()
        {
            Assert.True(Dustbowl().FitsOnTheWire);

            Assert.True(Dustbowl().Max.X < Quantize.POS_MAX);
            Assert.True(Dustbowl().Min.X > Quantize.POS_MIN);
        }

        [Fact]
        public void AVolumeWiderThanTheWireIsReportedAsSuch()
        {
            // Centred on the WINDOW, not on the origin. The window stopped being symmetric in
            // 4.0.0 (X-53: -1024 .. 3072, same 4096 m width, moved to where playable content
            // is), and this fixture used to assume those were the same point -- so a volume of
            // exactly the right width centred on zero read as fitting only by coincidence.
            float centre = (Quantize.POS_MIN + Quantize.POS_MAX) / 2f;
            var origin = new Vec3(centre, centre, centre);

            var tooWide = new PlayVolume(
                origin, new Vec3(Quantize.POS_RANGE + 2f, 100f, 100f));

            Assert.False(tooWide.FitsOnTheWire);

            // Exactly the range still fits — the quantizer maps POS_MAX to the top code.
            var exact = new PlayVolume(
                origin, new Vec3(Quantize.POS_RANGE, Quantize.POS_RANGE, Quantize.POS_RANGE));
            Assert.True(exact.FitsOnTheWire);

            // And width alone is not the test: the same box slid off the window does not fit.
            var slid = new PlayVolume(
                new Vec3(centre - 1f, centre, centre),
                new Vec3(Quantize.POS_RANGE, Quantize.POS_RANGE, Quantize.POS_RANGE));
            Assert.False(slid.FitsOnTheWire);
        }

        [Fact]
        public void ANegativeDimensionIsRejectedRatherThanInverted()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new PlayVolume(new Vec3(0f, 0f, 0f), new Vec3(-1f, 10f, 10f)));
        }
    }
}
