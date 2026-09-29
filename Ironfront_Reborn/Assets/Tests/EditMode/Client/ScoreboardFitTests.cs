using Ironfront.Net.Unity.Client.Hud;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The Tab board fits the screen it is drawn on. Release test 2026-09-29: in a narrow window
    /// the 1760-unit board ran off both edges, which a 4:3 screen would do as well.
    /// </summary>
    /// <remarks>
    /// Canvas sizes are what the readout's scaler (1920x1080 reference, match 0.5) makes of each
    /// screen: 1920x1080 at 16:9, about 1822x1139 at 16:10, about 1663x1247 at 4:3.
    /// </remarks>
    public sealed class ScoreboardFitTests
    {
        private static readonly Vector2 Board = new Vector2(1760f, 1040f);

        [Test]
        public void OnWideScreens_TheBoardKeepsItsSize()
        {
            Assert.AreEqual(1f, ScoreboardView.FitScale(new Vector2(1920f, 1080f), Board, ScoreboardView.FitMargin));
            Assert.AreEqual(1f, ScoreboardView.FitScale(new Vector2(1822f, 1139f), Board, ScoreboardView.FitMargin));
        }

        [Test]
        public void OnANarrowScreen_TheBoardShrinksToFitWithItsMargin()
        {
            var area = new Vector2(1663f, 1247f);
            float fit = ScoreboardView.FitScale(area, Board, ScoreboardView.FitMargin);

            Assert.Less(fit, 1f);
            Assert.LessOrEqual(Board.x * fit, area.x - 2f * ScoreboardView.FitMargin + 0.01f,
                "the shrunk board still runs past the screen's edges");
        }

        [Test]
        public void TheBoardNeverGrowsPastItsDrawnSize()
            => Assert.AreEqual(1f, ScoreboardView.FitScale(new Vector2(3840f, 2160f), Board, ScoreboardView.FitMargin));
    }
}
