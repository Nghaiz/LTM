using Ironfront.Net.Unity.Client;
using Xunit;

namespace Ironfront.Client.Flow.Tests;

public sealed class ChatKeyboardIntentTests
{
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void EitherEnterKeySendsWhileComposing(bool composing, bool returnDown, bool keypadDown)
    {
        Assert.True(ChatKeyboardIntent.ShouldSend(composing, returnDown, keypadDown));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    public void EnterNeverSendsWhileClosedOrWhenNoEnterWasPressed(
        bool composing, bool returnDown, bool keypadDown)
    {
        Assert.False(ChatKeyboardIntent.ShouldSend(composing, returnDown, keypadDown));
    }
}
