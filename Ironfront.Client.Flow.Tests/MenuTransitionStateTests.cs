using Ironfront.Net.Unity.Client.Menu;
using Xunit;

namespace Ironfront.Client.Flow.Tests;

public sealed class MenuTransitionStateTests
{
    [Fact]
    public void ShowingStartsActiveButInputSafe()
    {
        MenuTransitionFrame frame = MenuTransitionState.Evaluate(
            startAlpha: 0f,
            targetVisible: true,
            normalizedTime: 0f);

        Assert.True(frame.Active);
        Assert.False(frame.Interactable);
        Assert.Equal(0f, frame.Alpha);
    }

    [Fact]
    public void ShowingEndsOpaqueAndInteractive()
    {
        MenuTransitionFrame frame = MenuTransitionState.Evaluate(0f, true, 1f);

        Assert.True(frame.Active);
        Assert.True(frame.Interactable);
        Assert.Equal(1f, frame.Alpha);
    }

    [Fact]
    public void HidingBlocksInputImmediatelyAndDeactivatesAtEnd()
    {
        MenuTransitionFrame start = MenuTransitionState.Evaluate(1f, false, 0f);
        MenuTransitionFrame end = MenuTransitionState.Evaluate(1f, false, 1f);

        Assert.True(start.Active);
        Assert.False(start.Interactable);
        Assert.Equal(1f, start.Alpha);
        Assert.False(end.Active);
        Assert.False(end.Interactable);
        Assert.Equal(0f, end.Alpha);
    }

    [Fact]
    public void InterruptedTransitionContinuesFromCurrentAlpha()
    {
        MenuTransitionFrame hiding = MenuTransitionState.Evaluate(1f, false, 0.5f);
        MenuTransitionFrame showing = MenuTransitionState.Evaluate(
            hiding.Alpha,
            targetVisible: true,
            normalizedTime: 0f);

        Assert.Equal(hiding.Alpha, showing.Alpha);
        Assert.True(showing.Active);
        Assert.False(showing.Interactable);
    }
}
