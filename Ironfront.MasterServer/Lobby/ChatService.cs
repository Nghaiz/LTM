using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;

namespace Ironfront.MasterServer.Lobby
{
    public sealed class ChatMessage
    {
        public byte Channel { get; init; }
        public int FromPlayerId { get; init; }
        public string FromName { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public long Timestamp { get; init; }
    }

    /// <summary>
    /// Why <see cref="ChatService.TryCreate"/> refused a line.
    /// </summary>
    /// <remarks>
    /// <b>This type exists because the method used to return <c>bool</c>.</b> Four unrelated
    /// refusals — an unknown channel, an empty line, an over-long one, and genuine flooding —
    /// collapsed into <c>false</c> at the call site, which had nothing left to report but
    /// <see cref="ErrorCode.RateLimited"/>. So a player who pasted 300 characters was told they
    /// were sending too often, waited, sent the identical text, and was told it again. The
    /// message was not merely unhelpful, it was the one piece of advice guaranteed not to work.
    /// </remarks>
    public enum ChatRejection
    {
        None = 0,

        /// <summary>Not a channel <see cref="MspChatChannel"/> defines.</summary>
        UnknownChannel,

        /// <summary>Blank, or nothing survived control-character stripping.</summary>
        Empty,

        /// <summary>Longer than <see cref="MspChatLimits.MaxTextCharacters"/>.</summary>
        TooLong,

        /// <summary>Over the per-player flood budget. This one IS "wait and try again".</summary>
        TooFast,
    }

    public sealed class ChatService
    {
        private const int MaxMessagesPerWindow = 5;
        private const long WindowMs = 10_000;
        private readonly Dictionary<int, Queue<long>> _timestampsByPlayer = new Dictionary<int, Queue<long>>();

        /// <summary>Seconds a flooding sender must wait, for the message that tells them so.</summary>
        public const int FloodRetryAfterSeconds = (int)(WindowMs / 1000);

        /// <summary>
        /// Judges one line and, if it survives, builds the message that will be pushed.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The order of the tests is the order of the player's interest.</b> Shape first
        /// (channel, emptiness, length), budget last — so a player who is BOTH over the flood
        /// budget and over the length limit is told about the length, which is the one they can
        /// fix by editing rather than by waiting. Reversed, the fixable fault would be masked by
        /// the transient one for as long as they kept retrying.
        /// </para>
        /// <para>
        /// <b>The budget is only spent on a line that would have been sent.</b> Refusing a long
        /// line used to consume one of the five slots in the window, so five rejected pastes
        /// silenced a player for ten seconds having delivered nothing.
        /// </para>
        /// </remarks>
        public bool TryCreate(
            byte channel, int playerId, string displayName, string? text, long now,
            out ChatMessage? message, out ChatRejection rejection)
        {
            message = null;

            if (!MspChatChannel.IsDefined(channel))
            {
                rejection = ChatRejection.UnknownChannel;
                return false;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                rejection = ChatRejection.Empty;
                return false;
            }

            // Before sanitizing: stripping can only shorten, so a 5,000-character paste is
            // refused without first allocating a cleaned copy of it.
            if (text.Length > MspChatLimits.MaxTextCharacters)
            {
                rejection = ChatRejection.TooLong;
                return false;
            }

            string sanitized = StripControlCharacters(text).Trim();
            if (sanitized.Length == 0)
            {
                rejection = ChatRejection.Empty;
                return false;
            }

            if (!AllowMessage(playerId, now))
            {
                rejection = ChatRejection.TooFast;
                return false;
            }

            rejection = ChatRejection.None;
            message = new ChatMessage
            {
                Channel = channel,
                FromPlayerId = playerId,
                FromName = displayName,
                Text = sanitized,
                Timestamp = now
            };
            return true;
        }

        public void RemovePlayer(int playerId) => _timestampsByPlayer.Remove(playerId);

        private bool AllowMessage(int playerId, long now)
        {
            if (!_timestampsByPlayer.TryGetValue(playerId, out Queue<long>? timestamps))
            {
                timestamps = new Queue<long>();
                _timestampsByPlayer.Add(playerId, timestamps);
            }

            while (timestamps.Count > 0 && now - timestamps.Peek() >= WindowMs)
                timestamps.Dequeue();
            if (timestamps.Count >= MaxMessagesPerWindow) return false;

            timestamps.Enqueue(now);
            return true;
        }

        private static string StripControlCharacters(string text)
        {
            var characters = new char[text.Length];
            int count = 0;
            foreach (char character in text)
                if (!char.IsControl(character) && char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.Format)
                    characters[count++] = character;
            return new string(characters, 0, count);
        }
    }
}
