using Ironfront.Net.Unity.Client.Menu;
using Xunit;

namespace Ironfront.Client.Flow.Tests;

public sealed class MenuNavigationCycleTests
{
    [Fact]
    public void ForwardWrapsAndSkipsUnavailableControls()
    {
        int next = MenuNavigationCycle.NextIndex(
            currentIndex: 3,
            backwards: false,
            canSelect: new[] { true, false, true, true });

        Assert.Equal(0, next);
    }

    [Fact]
    public void BackwardWrapsAndSkipsUnavailableControls()
    {
        int next = MenuNavigationCycle.NextIndex(
            currentIndex: 0,
            backwards: true,
            canSelect: new[] { true, false, true, true });

        Assert.Equal(3, next);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 2)]
    public void MissingSelectionStartsAtTheExpectedEdge(bool backwards, int expected)
    {
        int next = MenuNavigationCycle.NextIndex(
            currentIndex: -1,
            backwards: backwards,
            canSelect: new[] { true, false, true });

        Assert.Equal(expected, next);
    }

    [Fact]
    public void NoAvailableControlReturnsMinusOne()
    {
        int next = MenuNavigationCycle.NextIndex(
            currentIndex: 1,
            backwards: false,
            canSelect: new[] { false, false, false });

        Assert.Equal(-1, next);
    }
}
