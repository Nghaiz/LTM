using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// What "Remember me" keeps on this machine: the username and the master's token, never a
    /// password (owner's list of 2026-10-09, item 1).
    /// </summary>
    public sealed class MenuLoginScreenTests
    {
        [SetUp]
        public void ClearRememberedSignIn() => RememberedSignIn.Forget();

        [TearDown]
        public void RestoreRememberedSignIn() => RememberedSignIn.Forget();

        [Test]
        public void RememberKeepsTheUsernameAndTheTokenButNoPassword()
        {
            RememberedSignIn.Remember("PilotTwo", "token-from-the-master");

            Assert.AreEqual("PilotTwo", RememberedSignIn.Username);
            Assert.AreEqual("token-from-the-master", RememberedSignIn.Token);
            Assert.IsTrue(RememberedSignIn.CanSignInAutomatically);
            Assert.IsFalse(PlayerPrefs.HasKey("ironfront.menu.password"),
                "The menu must never create a password preference.");
        }

        [Test]
        public void ForgetRemovesBoth()
        {
            RememberedSignIn.Remember("PilotTwo", "token");
            RememberedSignIn.Forget();

            Assert.AreEqual(string.Empty, RememberedSignIn.Username);
            Assert.IsFalse(RememberedSignIn.CanSignInAutomatically);
            Assert.IsFalse(PlayerPrefs.HasKey(RememberedSignIn.UsernameKey));
            Assert.IsFalse(PlayerPrefs.HasKey(RememberedSignIn.TokenKey));
        }

        [Test]
        public void ARefusedTokenIsForgottenButTheNameStaysInTheField()
        {
            RememberedSignIn.Remember("PilotTwo", "token");
            RememberedSignIn.ForgetToken();

            Assert.AreEqual("PilotTwo", RememberedSignIn.Username);
            Assert.IsFalse(RememberedSignIn.CanSignInAutomatically);
        }

        [Test]
        public void AUsedTokenIsReplacedByTheNextOne()
        {
            RememberedSignIn.Remember("PilotTwo", "first");
            RememberedSignIn.ReplaceToken("second");
            Assert.AreEqual("second", RememberedSignIn.Token);

            RememberedSignIn.ReplaceToken(string.Empty);
            Assert.IsFalse(RememberedSignIn.CanSignInAutomatically);
        }
    }
}
