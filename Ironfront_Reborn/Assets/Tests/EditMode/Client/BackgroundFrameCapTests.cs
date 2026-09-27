using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The background cap takes a window down to the tick rate and gives back exactly what it
    /// took. The second half is the one that matters to a player: a VSync or frame limit chosen in
    /// the settings screen must survive every alt-tab.
    /// </summary>
    public sealed class BackgroundFrameCapTests
    {
        private int _vSync;
        private int _target;

        [SetUp]
        public void SetUp()
        {
            _vSync = QualitySettings.vSyncCount;
            _target = Application.targetFrameRate;
        }

        [TearDown]
        public void TearDown()
        {
            QualitySettings.vSyncCount = _vSync;
            Application.targetFrameRate = _target;
        }

        [Test]
        public void LosingFocusCapsToTheTickRateWithVSyncOff()
        {
            QualitySettings.vSyncCount = 1;
            Application.targetFrameRate = -1;
            var cap = new BackgroundFrameCap();

            cap.OnFocusChanged(false);

            // Unity ignores targetFrameRate while vSyncCount is non-zero, so a cap that left
            // VSync on would cap nothing.
            Assert.AreEqual(0, QualitySettings.vSyncCount);
            Assert.AreEqual(BackgroundFrameCap.BackgroundFrameRate, Application.targetFrameRate);
        }

        [Test]
        public void RegainingFocusRestoresWhatThePlayerHad()
        {
            QualitySettings.vSyncCount = 1;
            Application.targetFrameRate = 144;
            var cap = new BackgroundFrameCap();

            cap.OnFocusChanged(false);
            cap.OnFocusChanged(true);

            Assert.AreEqual(1, QualitySettings.vSyncCount);
            Assert.AreEqual(144, Application.targetFrameRate);
        }

        [Test]
        public void ARepeatedFocusLossDoesNotRememberTheCapAsThePlayersChoice()
        {
            // Two losses in a row -- a window unfocused, then minimised -- must not overwrite the
            // remembered settings with the cap itself, or refocusing would leave the game at 30.
            QualitySettings.vSyncCount = 1;
            Application.targetFrameRate = -1;
            var cap = new BackgroundFrameCap();

            cap.OnFocusChanged(false);
            cap.OnFocusChanged(false);
            cap.OnFocusChanged(true);

            Assert.AreEqual(1, QualitySettings.vSyncCount);
            Assert.AreEqual(-1, Application.targetFrameRate);
        }

        [Test]
        public void GainingFocusWithoutHavingLostItChangesNothing()
        {
            QualitySettings.vSyncCount = 1;
            Application.targetFrameRate = 144;
            var cap = new BackgroundFrameCap();

            cap.OnFocusChanged(true);

            Assert.AreEqual(1, QualitySettings.vSyncCount);
            Assert.AreEqual(144, Application.targetFrameRate);
        }
    }
}
