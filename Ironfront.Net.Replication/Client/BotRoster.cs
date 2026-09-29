using System;
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Replication.Client
{
    /// <summary>
    /// Which actors are bots, on which side, and what number each bot goes by, as
    /// <c>S_SPAWN_ACTOR</c> and <c>S_DESPAWN_ACTOR</c> say. Playtest 2026-09-28, features 1 and 2.
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
    /// <b>A bot is numbered within its side</b> ("Blue Team Bot 3", owner ruling 2026-09-29): its
    /// place among the side's present bots in actor-id order. Every client holds the same spawns,
    /// so every client counts the same numbers with nothing new on the wire; a bot keeps its number
    /// through every death and respawn because it keeps its actor. The numbers are recounted when
    /// the side's bots change, which is a round reset.
    /// </para>
    /// <para>
    /// A despawned bot keeps its name and number, for a killfeed line that still names it; the id
    /// is quarantined before reuse and the next spawn of it overwrites everything.
    /// </para>
    /// </remarks>
    public sealed class BotRoster
    {
        private readonly bool[] _known = new bool[ProtocolConstants.MAX_ACTORS];
        private readonly bool[] _bot = new bool[ProtocolConstants.MAX_ACTORS];
        private readonly bool[] _present = new bool[ProtocolConstants.MAX_ACTORS];
        private readonly byte[] _team = new byte[ProtocolConstants.MAX_ACTORS];
        private readonly int[] _number = new int[ProtocolConstants.MAX_ACTORS];

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
            Renumber();
            Revision++;
        }

        /// <summary>Records a despawn. Subscribe it to the router's despawn event.</summary>
        public void Apply(DespawnActorMessage message)
        {
            ushort id = message.ActorId;
            if (id >= _present.Length || !_present[id]) return;

            _present[id] = false;
            Renumber();
            Revision++;
        }

        /// <summary>Whether a spawn has said anything about this actor yet.</summary>
        public bool IsKnown(ushort actorId) => actorId < _known.Length && _known[actorId];

        /// <summary>Whether this actor is a bot. False while unknown.</summary>
        public bool IsBot(ushort actorId) => actorId < _bot.Length && _bot[actorId];

        /// <summary>The side the spawn put this actor on, or <see cref="TeamId.None"/> while unknown.</summary>
        public byte TeamOf(ushort actorId) => IsKnown(actorId) ? _team[actorId] : TeamId.None;

        /// <summary>A bot's number within its side, from 1; 0 for anything that is not a numbered bot.</summary>
        public int NumberOf(ushort actorId) => IsBot(actorId) ? _number[actorId] : 0;

        /// <summary>Forgets everything. Call when leaving a match.</summary>
        public void Reset()
        {
            Array.Clear(_known, 0, _known.Length);
            Array.Clear(_bot, 0, _bot.Length);
            Array.Clear(_present, 0, _present.Length);
            Array.Clear(_team, 0, _team.Length);
            Array.Clear(_number, 0, _number.Length);
            Revision++;
        }

        /// <summary>Counts each side's present bots in actor-id order. A departed bot keeps its number.</summary>
        private void Renumber()
        {
            int blue = 0;
            int red = 0;

            for (int id = 0; id < _known.Length; id++)
            {
                if (!_present[id] || !_bot[id]) continue;

                if (_team[id] == TeamId.Team0) _number[id] = ++blue;
                else if (_team[id] == TeamId.Team1) _number[id] = ++red;
            }
        }
    }

    /// <summary>
    /// The one answer to "what is this actor called on screen", for every screen that asks.
    /// </summary>
    /// <remarks>
    /// A human's name from <c>S_PLAYER_LIST</c>; a bot, which that message never names, as its side
    /// and number ("Blue Team Bot 3") -- never a made-up person's name, which a real player could
    /// share (owner ruling 2026-09-29); and an actor nobody has described yet by its id, the
    /// killfeed's original fallback. The last is honest rather than friendly on purpose: guessing a
    /// bot's name for a human whose name is late would be a wrong answer that looks like a right one.
    /// </remarks>
    public static class ActorNames
    {
        public static string Display(ushort actorId, PlayerNameTable names, BotRoster bots)
        {
            string? name = names.NameOf(actorId);
            if (name != null) return name;

            return bots.IsBot(actorId)
                ? BotName(bots.TeamOf(actorId), bots.NumberOf(actorId))
                : "actor " + actorId;
        }

        /// <summary>"Blue Team Bot 3": the side, then the bot's number within it.</summary>
        public static string BotName(byte team, int number)
            => (team == TeamId.Team0 ? "Blue Team Bot "
              : team == TeamId.Team1 ? "Red Team Bot "
              : "Bot ")
               + number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
