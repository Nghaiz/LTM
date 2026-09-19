using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class MenuTitleScreenTests
    {
        private IMenuPlatformActions _previous;

        [SetUp]
        public void CaptureBinding() => _previous = NetClientBindings.MenuPlatformActions;

        [TearDown]
        public void RestoreBinding() => NetClientBindings.MenuPlatformActions = _previous;

        [Test]
        public void PlatformActionsBinding_ForwardsSettingsAndExit()
        {
            var actions = new FakeActions();
            NetClientBindings.MenuPlatformActions = actions;

            MenuTitleScreen.OpenSettings();
            MenuTitleScreen.ExitGame();

            Assert.AreEqual(1, actions.SettingsCalls);
            Assert.AreEqual(1, actions.ExitCalls);
        }

        private sealed class FakeActions : IMenuPlatformActions
        {
            public int SettingsCalls { get; private set; }
            public int ExitCalls { get; private set; }
            public void OpenSettings() => SettingsCalls++;
            public void ExitGame() => ExitCalls++;
        }
    }
}
