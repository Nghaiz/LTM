using System.Collections.Generic;
using Ironfront.Net.Unity.Client.Hud;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The Tab board pages instead of shrinking its rows (owner's list of 2026-10-09, item 2): a page
    /// holds what fits at full size, and every page says what its rows are.
    /// </summary>
    public sealed class ScoreboardPagingTests
    {
        private const float Heading = 28f;
        private const float Player = 44f;
        private const float Bot = 34f;

        [Test]
        public void NobodyIsOneEmptyPage()
        {
            List<List<ScoreboardPaging.Slot>> pages = ScoreboardPaging.Paginate(0, 0, 600f, Heading, Player, Bot);

            Assert.AreEqual(1, pages.Count);
            Assert.IsEmpty(pages[0]);
        }

        [Test]
        public void ABoardThatFitsIsOnePageWithBothHeadings()
        {
            List<List<ScoreboardPaging.Slot>> pages = ScoreboardPaging.Paginate(2, 3, 600f, Heading, Player, Bot);

            Assert.AreEqual(1, pages.Count);
            Assert.AreEqual(
                new[]
                {
                    ScoreboardPaging.SlotKind.PlayersHeading, ScoreboardPaging.SlotKind.Player, ScoreboardPaging.SlotKind.Player,
                    ScoreboardPaging.SlotKind.BotsHeading, ScoreboardPaging.SlotKind.Bot, ScoreboardPaging.SlotKind.Bot,
                    ScoreboardPaging.SlotKind.Bot,
                },
                Kinds(pages[0]));
            Assert.AreEqual(Heading + 2f * Player, pages[0][3].Top, 0.001f, "The BOTS heading sits under the last player.");
        }

        [Test]
        public void EveryRowIsOnExactlyOnePageAndNoPageOverflows()
        {
            const float available = 640f;
            List<List<ScoreboardPaging.Slot>> pages = ScoreboardPaging.Paginate(8, 50, available, Heading, Player, Bot);

            var players = new HashSet<int>();
            var bots = new HashSet<int>();
            foreach (List<ScoreboardPaging.Slot> page in pages)
            {
                ScoreboardPaging.Slot last = page[page.Count - 1];
                Assert.LessOrEqual(last.Top + last.Height, available + 0.001f, "A page runs past the column.");
                foreach (ScoreboardPaging.Slot slot in page)
                {
                    if (slot.Kind == ScoreboardPaging.SlotKind.Player) Assert.IsTrue(players.Add(slot.Index), $"Player {slot.Index} twice.");
                    if (slot.Kind == ScoreboardPaging.SlotKind.Bot) Assert.IsTrue(bots.Add(slot.Index), $"Bot {slot.Index} twice.");
                }
            }

            Assert.AreEqual(8, players.Count);
            Assert.AreEqual(50, bots.Count);
            Assert.Greater(pages.Count, 1, "Fifty-eight full-size rows were expected not to fit one column.");
        }

        [Test]
        public void APageThatOpensMidGroupRepeatsItsHeading()
        {
            List<List<ScoreboardPaging.Slot>> pages = ScoreboardPaging.Paginate(1, 40, 400f, Heading, Player, Bot);

            for (int p = 1; p < pages.Count; p++)
            {
                Assert.AreEqual(ScoreboardPaging.SlotKind.BotsHeading, pages[p][0].Kind, $"Page {p + 1} does not say its rows are bots.");
                Assert.AreEqual(0f, pages[p][0].Top);
            }
        }

        [Test]
        public void AHeadingNeverEndsAPage()
        {
            // Room for the players' heading and two players, then 20 units: not enough for the
            // bots' heading and a bot together.
            float available = Heading + 2f * Player + 20f;
            List<List<ScoreboardPaging.Slot>> pages = ScoreboardPaging.Paginate(2, 3, available, Heading, Player, Bot);

            foreach (List<ScoreboardPaging.Slot> page in pages)
            {
                ScoreboardPaging.SlotKind last = page[page.Count - 1].Kind;
                Assert.AreNotEqual(ScoreboardPaging.SlotKind.BotsHeading, last, "A page ended on a heading with no row under it.");
                Assert.AreNotEqual(ScoreboardPaging.SlotKind.PlayersHeading, last, "A page ended on a heading with no row under it.");
            }
        }

        [Test]
        public void PageOfFindsTheRow()
        {
            List<List<ScoreboardPaging.Slot>> pages = ScoreboardPaging.Paginate(6, 50, 640f, Heading, Player, Bot);

            Assert.AreEqual(0, ScoreboardPaging.PageOf(pages, ScoreboardPaging.SlotKind.Player, 5));
            Assert.AreEqual(pages.Count - 1, ScoreboardPaging.PageOf(pages, ScoreboardPaging.SlotKind.Bot, 49));
        }

        private static ScoreboardPaging.SlotKind[] Kinds(List<ScoreboardPaging.Slot> page)
        {
            var kinds = new ScoreboardPaging.SlotKind[page.Count];
            for (int i = 0; i < page.Count; i++) kinds[i] = page[i].Kind;
            return kinds;
        }
    }
}
