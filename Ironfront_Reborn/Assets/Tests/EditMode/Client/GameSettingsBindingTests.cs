#nullable enable

using System.Reflection;
using Ironfront.Net.Unity;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class GameSettingsBindingTests
    {
        private IGameSettingsLauncher? _previous;

        [SetUp]
        public void SetUp() => _previous = NetClientBindings.Settings;

        [TearDown]
        public void TearDown() => NetClientBindings.Settings = _previous;

        [Test]
        public void RegisteredLauncherCanBeInvokedThroughTheSharedBinding()
        {
            FakeSettingsLauncher fake = new FakeSettingsLauncher();
            NetClientBindings.Settings = fake;

            Assert.True(NetClientBindings.Settings.IsAvailable);
            NetClientBindings.Settings.ShowSettings();

            Assert.AreEqual(1, fake.ShowCount);
        }

        [Test]
        public void SubsystemResetClearsASettingsLauncherFromThePreviousPlaySession()
        {
            NetClientBindings.Settings = new FakeSettingsLauncher();
            MethodInfo? reset = typeof(NetClientBindings).GetMethod(
                "ResetOnLoad", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.NotNull(reset, "The production reset seam must remain discoverable.");
            reset!.Invoke(null, null);

            Assert.IsNull(NetClientBindings.Settings);
        }

        private sealed class FakeSettingsLauncher : IGameSettingsLauncher
        {
            public bool IsAvailable => true;
            public int ShowCount { get; private set; }
            public void ShowSettings() => ShowCount++;
        }
    }
}
