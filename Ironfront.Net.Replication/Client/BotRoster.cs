using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// Which actors are bots, as <c>S_SPAWN_ACTOR</c> says. Playtest 2026-09-28, features 1 and 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why a table of its own.</b> <c>S_PLAYER_LIST</c> names humans only, so a bot has no name
    /// on the wire and every screen that lists actors -- the killfeed, the scoreboard, the name
    /// over a head -- has to know whether an unnamed actor is a bot or a human whose name has not
    /// arrived yet. The spawn is the one message that says, and the server announces every actor
    /// to every client (<c>AnnounceNewActors</c>), not only the ones in its interest radius.
    /// </para>
    /// <para>
    /// Kept across a despawn: an actor id is quarantined before reuse and re-announced when it
    /// is, and that later spawn overwrites the flag.
    /// </para>
    /// </remarks>
    public sealed class BotRoster
    {
        private readonly bool[] _known = new bool[ProtocolConstants.MAX_ACTORS];
        private readonly bool[] _bot = new bool[ProtocolConstants.MAX_ACTORS];

        /// <summary>Bumped whenever an answer changes, so a screen redraws only then.</summary>
        public int Revision { get; private set; }

        /// <summary>Records what one spawn says. Subscribe it to the router's spawn event.</summary>
        public void Apply(SpawnActorMessage message)
        {
            ushort id = message.ActorId;
            if (id >= _known.Length) return;

            bool bot = message.IsBot;
            if (_known[id] && _bot[id] == bot) return;

            _known[id] = true;
            _bot[id] = bot;
            Revision++;
        }

        /// <summary>Whether a spawn has said anything about this actor yet.</summary>
        public bool IsKnown(ushort actorId) => actorId < _known.Length && _known[actorId];

        /// <summary>Whether this actor is a bot. False while unknown.</summary>
        public bool IsBot(ushort actorId) => actorId < _bot.Length && _bot[actorId];

        /// <summary>Forgets everything. Call when leaving a match.</summary>
        public void Reset()
        {
            Array.Clear(_known, 0, _known.Length);
            Array.Clear(_bot, 0, _bot.Length);
            Revision++;
        }
    }

    /// <summary>
    /// The one answer to "what is this actor called on screen", for every screen that asks.
    /// </summary>
    /// <remarks>
    /// A human's name from <c>S_PLAYER_LIST</c>; a bot, which that message never names, as
    /// "Bot 23"; and an actor nobody has described yet by its id, the killfeed's original
    /// fallback. The last is honest rather than friendly on purpose: guessing "Bot" for a human
    /// whose name is late would be a wrong answer that looks like a right one.
    /// </remarks>
    public static class ActorNames
    {
        public static string Display(ushort actorId, PlayerNameTable names, BotRoster bots)
        {
            string? name = names.NameOf(actorId);
            if (name != null) return name;

            return bots.IsBot(actorId) ? "Bot " + actorId : "actor " + actorId;
        }
    }
}
