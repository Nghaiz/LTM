using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// Capture-point names on the map and in the top-right corner (owner request 2026-10-09,
    /// Forest Lake, Dustbowl and Island): readable, and covering as little of the map as possible.
    /// </summary>
    public sealed class CapturePointLabelRulesTests
    {
        [Theory]
        [InlineData("Lumber Camp", "LUMBER CAMP")]
        [InlineData("  Ford ", "FORD")]
        [InlineData("lakeshore", "LAKESHORE")]
        public void ANamedPointReadsInCapitals(string authored, string expected)
        {
            Assert.Equal(expected, CapturePointLabelRules.Wording(authored));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ABlankPointHasNoLabel(string? authored)
        {
            // A point authored without a name (a new map's, say) must draw no empty label.
            Assert.Null(CapturePointLabelRules.Wording(authored));
        }

        [Fact]
        public void TheFontIsAShareOfTheMapBetweenTheClamps()
        {
            Assert.Equal(14, CapturePointLabelRules.FontPixels(1000f));
            Assert.Equal(CapturePointLabelRules.MinFontPixels, CapturePointLabelRules.FontPixels(300f));
            Assert.Equal(CapturePointLabelRules.MaxFontPixels, CapturePointLabelRules.FontPixels(4000f));
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-5f)]
        [InlineData(float.NaN)]
        public void AMapWithNoWidthStillGetsAReadableFont(float width)
        {
            Assert.Equal(CapturePointLabelRules.MinFontPixels, CapturePointLabelRules.FontPixels(width));
        }

        [Fact]
        public void ALabelTooSmallToReadOrBigEnoughToCoverTheGroundIsRuledOut()
        {
            Assert.True(CapturePointLabelRules.MinFontPixels >= 10, "below 10 px the name stops being readable");
            Assert.True(CapturePointLabelRules.MaxFontPixels <= 18, "past 18 px a name buries the ground round its base");
            Assert.True(CapturePointLabelRules.MinFontPixels < CapturePointLabelRules.MaxFontPixels);
        }

        [Fact]
        public void ALabelGoesUnderItsFlagWhenThereIsRoom()
        {
            // Flag centre 100 px up, flag 40 px: its bottom edge is at 80, room for a 16 px label.
            Assert.True(CapturePointLabelRules.FitsBelow(100f, 40f, 16f));
        }

        [Fact]
        public void ABaseOnTheBottomEdgeHasItsNameAbove()
        {
            // Flag centre 20 px up: under it the label would leave the map.
            Assert.False(CapturePointLabelRules.FitsBelow(20f, 40f, 16f));
        }

        [Fact]
        public void TheLabelNeverTouchesItsFlag()
        {
            Assert.True(CapturePointLabelRules.GapPixels > 0f);
            float exactlyEnough = 20f + CapturePointLabelRules.GapPixels + 16f;
            Assert.True(CapturePointLabelRules.FitsBelow(exactlyEnough, 40f, 16f));
            Assert.False(CapturePointLabelRules.FitsBelow(exactlyEnough - 0.5f, 40f, 16f));
        }

        [Theory]
        [InlineData(500f, 40f, 1000f, 500f)] // the middle of the map: centred on its flag
        [InlineData(10f, 40f, 1000f, 40f)]   // the left edge: moved in, not cut off
        [InlineData(990f, 40f, 1000f, 960f)] // the right edge
        [InlineData(30f, 60f, 100f, 50f)]    // wider than the map: centred on it
        public void ALabelStaysWhollyOnTheMap(float centre, float halfWidth, float extent, float expected)
        {
            Assert.Equal(expected, CapturePointLabelRules.ClampCentre(centre, halfWidth, extent), 3);
        }
    }
}
