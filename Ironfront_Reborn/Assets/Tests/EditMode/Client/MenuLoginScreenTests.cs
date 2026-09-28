using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class MenuLoginScreenTests
    {
        [SetUp]
        public void ClearRememberedUsername()
            => PlayerPrefs.DeleteKey(MenuLoginScreen.RememberedUsernameKey);

        [TearDown]
        public void RestoreRememberedUsername()
        {
            PlayerPrefs.DeleteKey(MenuLoginScreen.RememberedUsernameKey);
            PlayerPrefs.Save();
        }

        [Test]
        public void RememberUsername_StoresOnlyTheUsernameWhenEnabled()
        {
            MenuLoginScreen.StoreRememberedUsername(true, "PilotTwo");

            Assert.AreEqual("PilotTwo", MenuLoginScreen.ReadRememberedUsername());
            Assert.IsFalse(PlayerPrefs.HasKey("ironfront.menu.password"),
                "The menu must never create a password preference.");
        }

        [Test]
        public void RememberUsername_RemovesStoredNameWhenDisabled()
        {
            MenuLoginScreen.StoreRememberedUsername(true, "PilotTwo");
            MenuLoginScreen.StoreRememberedUsername(false, "PilotTwo");

            Assert.AreEqual(string.Empty, MenuLoginScreen.ReadRememberedUsername());
            Assert.IsFalse(PlayerPrefs.HasKey(MenuLoginScreen.RememberedUsernameKey));
        }
    }
}
