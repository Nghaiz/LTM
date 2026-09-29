using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// Which actors are bots, and on which side, as <c>S_SPAWN_ACTOR</c> and <c>S_DESPAWN_ACTOR</c>
    /// say. Playtest 2026-09-28, features 1 and 2.
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
    /// <b>A bot's name is its callsign</b> (<see cref="BotCallsigns"/>, owner's report of
    /// 2026-09-30), keyed on its actor id -- it used to be its side and a number, "Blue Team Bot 3".
    /// </para>
    /// <para>
    /// A despawned bot keeps what it was, for a killfeed line that still names it; the id is
    /// quarantined before reuse and the next spawn of it overwrites everything.
    /// </para>
    /// </remarks>
    public sealed class BotRoster
    {
        private readonly bool[] _known = new bool[ProtocolConstants.MAX_ACTORS];
        private readonly bool[] _bot = new bool[ProtocolConstants.MAX_ACTORS];
        private readonly bool[] _present = new bool[ProtocolConstants.MAX_ACTORS];
        private readonly byte[] _team = new byte[ProtocolConstants.MAX_ACTORS];

        /// <summary>Bumped whenever an answer changes, so a screen redraws only then.</summary>
        public int Revision { get; private set; }

        /// <summary>Records what one spawn says. Subscribe it to the router's spawn event.</summary>
        public void Apply(SpawnActorMessage message)
        {
            ushort id = message.ActorId;
            if (id >= _known.Length) return;

            bool bot = message.IsBot;
            byte team = message.Team;
            if (_known[id] && _present[id] && _bot[id] == bot && _team[id] == team) return;

            _known[id] = true;
            _present[id] = true;
            _bot[id] = bot;
            _team[id] = team;
            Revision++;
        }

        /// <summary>Records a despawn. Subscribe it to the router's despawn event.</summary>
        public void Apply(DespawnActorMessage message)
        {
            ushort id = message.ActorId;
            if (id >= _present.Length || !_present[id]) return;

            _present[id] = false;
            Revision++;
        }

        /// <summary>Whether a spawn has said anything about this actor yet.</summary>
        public bool IsKnown(ushort actorId) => actorId < _known.Length && _known[actorId];

        /// <summary>Whether this actor is a bot. False while unknown.</summary>
        public bool IsBot(ushort actorId) => actorId < _bot.Length && _bot[actorId];

        /// <summary>The side the spawn put this actor on, or <see cref="TeamId.None"/> while unknown.</summary>
        public byte TeamOf(ushort actorId) => IsKnown(actorId) ? _team[actorId] : TeamId.None;

        /// <summary>Forgets everything. Call when leaving a match.</summary>
        public void Reset()
        {
            Array.Clear(_known, 0, _known.Length);
            Array.Clear(_bot, 0, _bot.Length);
            Array.Clear(_present, 0, _present.Length);
            Array.Clear(_team, 0, _team.Length);
            Revision++;
        }
    }

    /// <summary>
    /// The one answer to "what is this actor called on screen", for every screen that asks.
    /// </summary>
    /// <remarks>
    /// A human's name from <c>S_PLAYER_LIST</c>; a bot, which that message never names, by its
    /// callsign ("VIPER", <see cref="BotCallsigns"/>) -- never a made-up person's name, which a real
    /// player could share (owner ruling 2026-09-29); and an actor nobody has described yet by its
    /// id, the killfeed's original fallback. The last is honest rather than friendly on purpose:
    /// guessing a bot's name for a human whose name is late would be a wrong answer that looks like
    /// a right one.
    /// </remarks>
    public static class ActorNames
    {
        public static string Display(ushort actorId, PlayerNameTable names, BotRoster bots)
        {
            string? name = names.NameOf(actorId);
            if (name != null) return name;

            return bots.IsBot(actorId)
                ? BotCallsigns.For(actorId)
                : "actor " + actorId;
        }
    }
}
