using Ironfront.Net.Protocol;
using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The chat flow the owner asked for after the 2026-09-28 playtest: Enter opens the box,
    /// Enter sends and closes the input line while the history stays up for five seconds, Esc
    /// closes it without sending.
    /// </summary>
    /// <remarks>
    /// Every test here fails against the box that shipped in v1.0.0, which had no model to ask:
    /// T opened it, lines expired ten seconds after ARRIVING regardless of what the player did,
    /// and there was no history once they had.
    /// </remarks>
    public sealed class ChatPanelModelTests
    {
        [Test]
        public void EnterOnAClosedBoxOpensItWithAnEmptyDraft()
        {
            var model = new ChatPanelModel();
            model.Draft = "left over";

            ChatKeyOutcome outcome = model.PressEnter(10f, out string submitted);

            Assert.AreEqual(ChatKeyOutcome.Opened, outcome);
            Assert.IsNull(submitted);
            Assert.IsTrue(model.IsComposing);
            Assert.AreEqual(string.Empty, model.Draft);
            Assert.AreEqual(1f, model.Opacity(10f), "an open box is fully visible even with no history");
        }

        [Test]
        public void EnterOnAnOpenBoxSendsTheDraftAndClosesTheInputLine()
        {
            var model = new ChatPanelModel();
            model.PressEnter(10f, out _);
            model.Draft = "xin chào";

            ChatKeyOutcome outcome = model.PressEnter(11f, out string submitted);

            Assert.AreEqual(ChatKeyOutcome.Submitted, outcome);
            Assert.AreEqual("xin chào", submitted);
            Assert.IsFalse(model.IsComposing, "sending closes the input line");
            Assert.AreEqual(string.Empty, model.Draft);
        }

        [Test]
        public void EnterOnAnOpenBoxWithNothingTypedClosesWithoutSending()
        {
            var model = new ChatPanelModel();
            model.PressEnter(10f, out _);
            model.Draft = "   ";

            ChatKeyOutcome outcome = model.PressEnter(11f, out string submitted);

            Assert.AreEqual(ChatKeyOutcome.ClosedEmpty, outcome);
            Assert.IsNull(submitted);
            Assert.IsFalse(model.IsComposing);
        }

        [Test]
        public void EscapeClosesTheBoxWithoutSendingAndHidesItAtOnce()
        {
            var model = new ChatPanelModel();
            model.Add(3, ChatChannel.All, "earlier line", 9f);
            model.PressEnter(10f, out _);
            model.Draft = "never mind";

            ChatKeyOutcome outcome = model.PressEscape();

            Assert.AreEqual(ChatKeyOutcome.Cancelled, outcome);
            Assert.IsFalse(model.IsComposing);
            Assert.AreEqual(string.Empty, model.Draft, "the draft is thrown away");
            Assert.AreEqual(0f, model.Opacity(10f), "Esc dismisses the box, it does not linger");
        }

        [Test]
        public void EscapeOnAClosedBoxMeansNothing()
        {
            var model = new ChatPanelModel();

            Assert.AreEqual(ChatKeyOutcome.None, model.PressEscape());
            Assert.IsFalse(model.IsComposing);
        }

        [Test]
        public void TheHistoryStaysUpForFiveSecondsAfterSendingThenFades()
        {
            var model = new ChatPanelModel(lingerSeconds: 5f, fadeSeconds: 1f);
            model.Add(1, ChatChannel.All, "the line just sent, echoed back", 20f);
            model.PressEnter(20f, out _);
            model.Draft = "hello";
            model.PressEnter(20f, out _);

            Assert.AreEqual(1f, model.Opacity(20f));
            Assert.AreEqual(1f, model.Opacity(23.9f), "fully visible for most of the linger");
            Assert.AreEqual(0.5f, model.Opacity(24.5f), 0.001f, "fading across the last second");
            Assert.AreEqual(0f, model.Opacity(25f), "gone once the linger has passed");
            Assert.AreEqual(0f, model.Opacity(60f));
        }

        [Test]
        public void AnIncomingLineBringsTheHistoryBackForTheLinger()
        {
            var model = new ChatPanelModel(lingerSeconds: 5f, fadeSeconds: 1f);
            model.Add(2, ChatChannel.All, "first", 0f);
            Assert.AreEqual(0f, model.Opacity(30f));

            model.Add(4, ChatChannel.All, "someone else talking", 30f);

            Assert.AreEqual(1f, model.Opacity(31f));
            Assert.AreEqual(0f, model.Opacity(35f));
            Assert.AreEqual(2, model.History.Count, "the history is kept, not just the newest line");
        }

        [Test]
        public void TheHistoryDropsItsOldestLineAtCapacity()
        {
            var model = new ChatPanelModel(capacity: 3);
            for (byte i = 0; i < 5; i++) model.Add(i, ChatChannel.All, "line " + i, i);

            Assert.AreEqual(3, model.History.Count);
            Assert.AreEqual("line 2", model.History[0].Text);
            Assert.AreEqual("line 4", model.History[2].Text);
            Assert.AreEqual((byte)4, model.History[2].Speaker);
        }

        [Test]
        public void AbandonClosesWithoutLingering()
        {
            var model = new ChatPanelModel();
            model.Add(1, ChatChannel.All, "line", 0f);
            model.PressEnter(1f, out _);
            model.Draft = "half typed";

            model.Abandon();

            Assert.IsFalse(model.IsComposing);
            Assert.AreEqual(string.Empty, model.Draft);
            Assert.AreEqual(0f, model.Opacity(1f));
        }

        [Test]
        public void TheKeyThatClosesTheBoxStillBelongsToItForTheRestOfThatFrame()
        {
            bool before = LocalTextEntry.Composing;
            try
            {
                LocalTextEntry.Composing = true;
                Assert.IsTrue(LocalTextEntry.OwnsKeyboard, "typing owns the keyboard");

                LocalTextEntry.Composing = false;

                // Same frame: the Enter or Esc that closed the box must not reach the deploy
                // screen or the pause menu, which read it after the chat box does.
                Assert.IsTrue(LocalTextEntry.OwnsKeyboard,
                    "the frame the box closed on is still the box's");
            }
            finally
            {
                LocalTextEntry.Composing = before;
            }
        }

        // ---------------------------------------------------------------- team chat (2026-09-29)

        [Test]
        public void EnterOpensOnAll_AndShiftEnterOpensOnTeam()
        {
            var model = new ChatPanelModel();
            model.PressEnter(1f, out _);
            Assert.AreEqual(ChatChannel.All, model.Channel);

            model.PressEscape();
            model.PressEnter(2f, out _, ChatChannel.Team);
            Assert.AreEqual(ChatChannel.Team, model.Channel);
        }

        [Test]
        public void TabSwapsTheChannel_OnlyWhileTheBoxIsOpen()
        {
            var model = new ChatPanelModel();
            Assert.IsFalse(model.ToggleChannel(), "a closed box has no channel to swap");

            model.PressEnter(1f, out _);
            Assert.IsTrue(model.ToggleChannel());
            Assert.AreEqual(ChatChannel.Team, model.Channel);
            Assert.IsTrue(model.ToggleChannel());
            Assert.AreEqual(ChatChannel.All, model.Channel);
        }

        /// <summary>
        /// The arrows pick a channel only while nothing is typed; with a draft they belong to the
        /// caret, or a typo in the middle of a line could never be reached.
        /// </summary>
        [Test]
        public void TheArrowsPickAChannel_OnlyBeforeAnythingIsTyped()
        {
            var model = new ChatPanelModel();
            model.PressEnter(1f, out _);

            Assert.IsTrue(model.PressArrow(right: true));
            Assert.AreEqual(ChatChannel.Team, model.Channel);
            Assert.IsTrue(model.PressArrow(right: false));
            Assert.AreEqual(ChatChannel.All, model.Channel);

            model.Draft = "push B";
            Assert.IsFalse(model.PressArrow(right: true), "the arrow was taken from the caret");
            Assert.AreEqual(ChatChannel.All, model.Channel);
        }

        [Test]
        public void AClickedTabPicksItsChannel()
        {
            var model = new ChatPanelModel();
            Assert.IsFalse(model.SelectChannel(ChatChannel.Team), "a closed box has no tabs to click");

            model.PressEnter(1f, out _);
            Assert.IsTrue(model.SelectChannel(ChatChannel.Team));
            Assert.AreEqual(ChatChannel.Team, model.Channel);
        }

        /// <summary>The sender reads where a submitted line was meant to go after the box closed.</summary>
        [Test]
        public void ASubmittedLineKeepsItsChannel()
        {
            var model = new ChatPanelModel();
            model.PressEnter(1f, out _, ChatChannel.Team);
            model.Draft = "on me";

            Assert.AreEqual(ChatKeyOutcome.Submitted, model.PressEnter(2f, out string submitted));
            Assert.AreEqual("on me", submitted);
            Assert.AreEqual(ChatChannel.Team, model.Channel);
        }

        [Test]
        public void AReceivedLineRemembersItsChannel()
        {
            var model = new ChatPanelModel();
            model.Add(4, ChatChannel.Team, "flank left", 1f);

            Assert.AreEqual(ChatChannel.Team, model.History[0].Channel);
        }
    }
}
