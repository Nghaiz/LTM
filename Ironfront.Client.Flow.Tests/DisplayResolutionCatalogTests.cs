using Ironfront.Net.Unity;
using Xunit;

namespace Ironfront.Client.Flow.Tests;

public sealed class DisplayResolutionCatalogTests
{
    [Fact]
    public void BuildRemovesRefreshRateDuplicatesAndOrdersByIncreasingPixelArea()
    {
        DisplayResolutionOption[] result = DisplayResolutionCatalog.Build(new[]
        {
            new DisplayResolutionOption(1920, 1080),
            new DisplayResolutionOption(1280, 720),
            new DisplayResolutionOption(1920, 1080),
            new DisplayResolutionOption(1600, 900)
        });

        Assert.Equal(new[] { "1280 × 720", "1600 × 900", "1920 × 1080" },
            System.Array.ConvertAll(result, option => option.Label));
    }

    [Fact]
    public void FindBestIndexKeepsTheExactCurrentResolution()
    {
        DisplayResolutionOption[] options =
        {
            new DisplayResolutionOption(1280, 720),
            new DisplayResolutionOption(1600, 900),
            new DisplayResolutionOption(1920, 1080)
        };

        Assert.Equal(1, DisplayResolutionCatalog.FindBestIndex(options, 1600, 900));
    }

    [Fact]
    public void FindBestIndexUsesTheNearestDimensionsWhenTheCurrentModeIsMissing()
    {
        DisplayResolutionOption[] options =
        {
            new DisplayResolutionOption(1280, 720),
            new DisplayResolutionOption(1600, 900),
            new DisplayResolutionOption(1920, 1080)
        };

        Assert.Equal(1, DisplayResolutionCatalog.FindBestIndex(options, 1680, 1050));
    }

    [Fact]
    public void CallerSuppliedFallbackSurvivesAnEmptyScreenResolutionList()
    {
        DisplayResolutionOption fallback = new(1366, 768);

        DisplayResolutionOption[] result = DisplayResolutionCatalog.Build(new[] { fallback });

        Assert.Single(result);
        Assert.Equal(1366, result[0].Width);
        Assert.Equal(768, result[0].Height);
    }

    [Fact]
    public void FindBestIndexReturnsMinusOneForAnActuallyEmptyCatalog()
    {
        Assert.Equal(-1, DisplayResolutionCatalog.FindBestIndex(
            System.Array.Empty<DisplayResolutionOption>(), 1920, 1080));
    }
}
