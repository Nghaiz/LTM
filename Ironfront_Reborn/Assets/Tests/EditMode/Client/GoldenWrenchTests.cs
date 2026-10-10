#nullable enable

using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The golden wrench: hidden until <c>ISEEGOLD</c>, then offered in practice only, and
    /// remembered (owner's list of 2026-10-09, achievements item).
    /// </summary>
    public sealed class GoldenWrenchTests
    {
        private string _folder = string.Empty;

        [SetUp]
        public void SetUp()
        {
            _folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ironfront-vault-" + System.Guid.NewGuid().ToString("N"));
            AchievementVault.UseFolderForTests(_folder);
        }

        [TearDown]
        public void TearDown()
        {
            AchievementVault.UseFolderForTests(null);
            if (System.IO.Directory.Exists(_folder)) System.IO.Directory.Delete(_folder, true);
        }

        [Test]
        public void ANormalWeaponIsAlwaysOffered()
        {
            Assert.IsTrue(GoldenWrench.IsOffered(hiddenEntry: false, offline: true));
            Assert.IsTrue(GoldenWrench.IsOffered(hiddenEntry: false, offline: false));
        }

        [Test]
        public void TheHiddenWrenchIsNotOfferedBeforeTheCode()
        {
            Assert.IsFalse(GoldenWrench.IsOffered(hiddenEntry: true, offline: true));
            Assert.IsFalse(GoldenWrench.IsOffered(hiddenEntry: true, offline: false));
        }

        [Test]
        public void AfterTheCodeTheWrenchIsOfferedInPracticeAndNeverOnline()
        {
            GoldenWrench.Unlock(null);

            Assert.IsTrue(GoldenWrench.IsOffered(hiddenEntry: true, offline: true));
            Assert.IsFalse(GoldenWrench.IsOffered(hiddenEntry: true, offline: false),
                "online the server treats the golden wrench as inert, so it must not be offered");
        }

        [Test]
        public void TheUnlockIsRememberedAndAnnouncedOnce()
        {
            int announced = 0;
            void Count(Sprite? _) => announced++;
            GoldenWrench.Revealed += Count;
            try
            {
                GoldenWrench.Unlock(null);
                GoldenWrench.Unlock(null);
            }
            finally
            {
                GoldenWrench.Revealed -= Count;
            }

            Assert.AreEqual(1, announced, "typing the code again on an unlocked machine is not news");
            Assert.IsTrue(AchievementVault.Data.GoldenWrench);
            AchievementVault.UseFolderForTests(_folder);
            Assert.IsTrue(GoldenWrench.IsUnlocked, "the unlock is on disk, not only in memory");
        }
    }
}
