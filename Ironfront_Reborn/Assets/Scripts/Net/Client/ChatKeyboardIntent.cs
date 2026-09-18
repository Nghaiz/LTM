#nullable enable

namespace Ironfront.Net.Unity.Client
{
    public static class ChatKeyboardIntent
    {
        public static bool ShouldSend(bool composing, bool returnDown, bool keypadEnterDown)
            => composing && (returnDown || keypadEnterDown);
    }
}
