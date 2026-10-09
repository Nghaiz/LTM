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
        private int _saved;

        [SetUp]
        public void SetUp()
        {
            _saved = PlayerPrefs.GetInt(GoldenWrench.UnlockedKey, 0);
            PlayerPrefs.DeleteKey(GoldenWrench.UnlockedKey);
        }

        [TearDown]
        public void TearDown()
        {
            if (_saved == 1) PlayerPrefs.SetInt(GoldenWrench.UnlockedKey, 1);
            else PlayerPrefs.DeleteKey(GoldenWrench.UnlockedKey);
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
            Assert.AreEqual(1, PlayerPrefs.GetInt(GoldenWrench.UnlockedKey, 0));
        }
    }
}
