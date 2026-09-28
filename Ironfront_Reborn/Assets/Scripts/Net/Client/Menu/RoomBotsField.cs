#nullable enable
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The create-room form's Bots field: how a typed value reads, what an empty one means, and
    /// what the preview card shows for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Bots PER TEAM, 0 to <see cref="ProtocolConstants.MAX_BOTS_PER_TEAM"/></b> (owner ruling,
    /// 2026-09-28), and not capped by the seat count: bots fill a side, they do not take a
    /// player's seat.
    /// </para>
    /// <para>
    /// <b>An empty field is the design roster, <see cref="ProtocolConstants.DEFAULT_BOTS_PER_TEAM"/>,
    /// and the preview says so.</b> It used to read as 0 and preview as "BOTS 0" while the game
    /// server released 16 a side regardless -- so the room's number was a lie in both directions
    /// once the server started honouring it, unless the empty field kept meaning what matches
    /// have always had.
    /// </para>
    /// <para>
    /// Engine-free so <c>Ironfront.Client.Flow.Tests</c> can compile it; the screen only reads
    /// its InputField's text into it.
    /// </para>
    /// </remarks>
    public static class RoomBotsField
    {
        /// <summary>The line the form shows for a value it refuses.</summary>
        public static string RangeError =>
            $"Bots per team must be a number between 0 and {ProtocolConstants.MAX_BOTS_PER_TEAM}.";

        /// <summary>
        /// Reads the field. False for a non-number or a count outside
        /// 0..<see cref="ProtocolConstants.MAX_BOTS_PER_TEAM"/>.
        /// </summary>
        public static bool TryRead(string? text, out int botsPerTeam)
        {
            string trimmed = text?.Trim() ?? string.Empty;
            if (trimmed.Length == 0)
            {
                botsPerTeam = ProtocolConstants.DEFAULT_BOTS_PER_TEAM;
                return true;
            }

            return int.TryParse(trimmed, out botsPerTeam)
                   && botsPerTeam >= 0
                   && botsPerTeam <= ProtocolConstants.MAX_BOTS_PER_TEAM;
        }

        /// <summary>What the map preview's BOTS cell shows while the field reads <paramref name="text"/>.</summary>
        public static string Preview(string? text)
        {
            string trimmed = text?.Trim() ?? string.Empty;
            return trimmed.Length == 0
                ? ProtocolConstants.DEFAULT_BOTS_PER_TEAM.ToString()
                : trimmed;
        }
    }
}
