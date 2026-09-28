using System.Collections.Generic;

namespace Ironfront.Net.Unity.Client
{
    /// <summary>What a key press did to the chat box.</summary>
    public enum ChatKeyOutcome
    {
        /// <summary>The key meant nothing in the box's current state.</summary>
        None,

        /// <summary>Enter on a closed box: the input line is open and owns the keyboard.</summary>
        Opened,

        /// <summary>Enter on an open box with something typed: send it, and the line closed.</summary>
        Submitted,

        /// <summary>Enter on an open box with nothing typed: the line closed, nothing to send.</summary>
        ClosedEmpty,

        /// <summary>Esc on an open box: the line closed and the draft was thrown away.</summary>
        Cancelled,
    }

    /// <summary>
    /// The in-match chat box's rules with no Unity input, network or drawing in them: what Enter
    /// and Esc do, which lines are kept, and how long the box stays on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The flow the owner asked for (playtest 2026-09-28).</b> Enter opens the box — players,
    /// history and an input line. Enter again sends what was typed and closes the input line, and
    /// the history stays up for <see cref="LingerSeconds"/> so the player sees their line land,
    /// then fades. Esc closes the box without sending. Enter never means anything else during a
    /// match: it used to toggle the deploy screen as well, which is the defect this replaces.
    /// </para>
    /// <para>
    /// <b>Split from <c>ClientChatSender</c> so the rules can be tested.</b> The component reads
    /// <c>Input</c> and draws with <c>OnGUI</c>, neither of which an EditMode test can drive; this
    /// class takes the key edges and the clock as arguments instead.
    /// </para>
    /// </remarks>
    public sealed class ChatPanelModel
    {
        /// <summary>Seconds the history stays up after a line is sent or received.</summary>
        public const float DefaultLingerSeconds = 5f;

        /// <summary>The last part of the linger, spent fading out rather than vanishing.</summary>
        public const float DefaultFadeSeconds = 1f;

        /// <summary>Lines kept for the history. Older ones fall off the front.</summary>
        public const int DefaultCapacity = 32;

        private readonly List<ChatEntry> _entries;
        private readonly int _capacity;
        private float _visibleUntil = float.NegativeInfinity;

        public ChatPanelModel(
            int capacity = DefaultCapacity,
            float lingerSeconds = DefaultLingerSeconds,
            float fadeSeconds = DefaultFadeSeconds)
        {
            _capacity = capacity < 1 ? 1 : capacity;
            _entries = new List<ChatEntry>(_capacity);
            LingerSeconds = lingerSeconds < 0f ? 0f : lingerSeconds;
            FadeSeconds = fadeSeconds < 0f ? 0f : (fadeSeconds > LingerSeconds ? LingerSeconds : fadeSeconds);
        }

        public float LingerSeconds { get; }

        public float FadeSeconds { get; }

        /// <summary>True while the input line is open and the keyboard belongs to it.</summary>
        public bool IsComposing { get; private set; }

        /// <summary>What has been typed so far. Written by the text field, cleared on every close.</summary>
        public string Draft { get; set; } = string.Empty;

        /// <summary>The kept history, oldest first.</summary>
        public IReadOnlyList<ChatEntry> History => _entries;

        /// <summary>
        /// Enter was pressed. Opens a closed box; sends and closes an open one.
        /// </summary>
        /// <param name="now">The clock the linger is measured on.</param>
        /// <param name="submitted">The draft to send, when the outcome is <see cref="ChatKeyOutcome.Submitted"/>.</param>
        public ChatKeyOutcome PressEnter(float now, out string submitted)
        {
            submitted = null;

            if (!IsComposing)
            {
                IsComposing = true;
                Draft = string.Empty;
                return ChatKeyOutcome.Opened;
            }

            string draft = Draft ?? string.Empty;
            Close();

            // The history stays up after an empty Enter too: the player looked at it on purpose,
            // and snapping it away the instant the line closes reads as the box glitching.
            Linger(now);

            if (draft.Trim().Length == 0) return ChatKeyOutcome.ClosedEmpty;

            submitted = draft;
            return ChatKeyOutcome.Submitted;
        }

        /// <summary>Esc was pressed. Closes an open box at once and throws the draft away.</summary>
        public ChatKeyOutcome PressEscape()
        {
            if (!IsComposing) return ChatKeyOutcome.None;

            Close();

            // Dismissed, so it goes now rather than lingering: Esc is the player saying they did
            // not want the box, and five more seconds of it is the opposite of that.
            _visibleUntil = float.NegativeInfinity;
            return ChatKeyOutcome.Cancelled;
        }

        /// <summary>
        /// Closes the box without sending and without a linger — the connection went away.
        /// </summary>
        public void Abandon()
        {
            Close();
            _visibleUntil = float.NegativeInfinity;
        }

        /// <summary>A line arrived. Kept, and the history comes up so it can be read.</summary>
        public void Add(byte speaker, string text, float now)
        {
            _entries.Add(new ChatEntry(speaker, text ?? string.Empty));
            while (_entries.Count > _capacity) _entries.RemoveAt(0);

            Linger(now);
        }

        /// <summary>Forgets the history, for a new match.</summary>
        public void Clear()
        {
            _entries.Clear();
            _visibleUntil = float.NegativeInfinity;
        }

        /// <summary>
        /// How opaque the box is at <paramref name="now"/>: 1 while composing or inside the
        /// linger, falling to 0 across its last <see cref="FadeSeconds"/>, 0 once it has passed.
        /// </summary>
        public float Opacity(float now)
        {
            if (IsComposing) return 1f;
            if (_entries.Count == 0) return 0f;

            float left = _visibleUntil - now;
            if (left <= 0f) return 0f;
            if (FadeSeconds <= 0f || left >= FadeSeconds) return 1f;

            return left / FadeSeconds;
        }

        private void Close()
        {
            IsComposing = false;
            Draft = string.Empty;
        }

        private void Linger(float now)
        {
            float until = now + LingerSeconds;
            if (until > _visibleUntil) _visibleUntil = until;
        }
    }

    /// <summary>One line of chat: who said it, and what.</summary>
    /// <remarks>
    /// <b>The speaker is an actor id, not a name.</b> Names arrive in <c>S_PLAYER_LIST</c> and can
    /// arrive after the line does; resolving at draw time is what lets a line that came in first
    /// pick its name up as soon as it is known.
    /// </remarks>
    public readonly struct ChatEntry
    {
        public readonly byte Speaker;
        public readonly string Text;

        public ChatEntry(byte speaker, string text)
        {
            Speaker = speaker;
            Text = text;
        }
    }
}
