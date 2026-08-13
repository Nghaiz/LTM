using System;
using System.Collections.Generic;

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

    public sealed class ChatService
    {
        private const int MaxMessagesPerWindow = 5;
        private const long WindowMs = 10_000;
        private readonly Dictionary<int, Queue<long>> _timestampsByPlayer = new Dictionary<int, Queue<long>>();

        public bool TryCreate(byte channel, int playerId, string displayName, string? text, long now, out ChatMessage? message)
        {
            message = null;
            if (channel > 1 || string.IsNullOrWhiteSpace(text) || text.Length > 200 || !AllowMessage(playerId, now))
                return false;

            string sanitized = StripControlCharacters(text).Trim();
            if (sanitized.Length == 0) return false;

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
