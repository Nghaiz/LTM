using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A bot is named by its side and its number within it -- "Blue Team Bot 3" -- never by a
    /// made-up person's name a real player could share (owner ruling 2026-09-29).
    /// </summary>
    public sealed class BotNamingTests
    {
        private static SpawnActorMessage Spawn(ushort id, byte team, bool bot)
            => new SpawnActorMessage(id, team, bot ? SpawnFlags.IsBot : SpawnFlags.None, 0, 0, 0, 0, 100, 0);

        private static string Name(ushort id, BotRoster bots)
            => ActorNames.Display(id, new PlayerNameTable(), bots);

        /// <summary>Each side counts its own bots from 1, in actor-id order, on every client alike.</summary>
        [Fact]
        public void EachSideNumbersItsBotsFromOne()
        {
            var bots = new BotRoster();
            bots.Apply(Spawn(3, TeamId.Team0, bot: true));
            bots.Apply(Spawn(4, TeamId.Team1, bot: true));
            bots.Apply(Spawn(5, TeamId.Team0, bot: true));
            bots.Apply(Spawn(6, TeamId.Team1, bot: true));
            bots.Apply(Spawn(1, TeamId.Team0, bot: false));   // a human counts for nobody's number

            Assert.Equal("Blue Team Bot 1", Name(3, bots));
            Assert.Equal("Blue Team Bot 2", Name(5, bots));
            Assert.Equal("Red Team Bot 1", Name(4, bots));
            Assert.Equal("Red Team Bot 2", Name(6, bots));
        }

        /// <summary>The same spawns in another order name the same bots the same way.</summary>
        [Fact]
        public void TheNumbersDoNotDependOnArrivalOrder()
        {
            var bots = new BotRoster();
            bots.Apply(Spawn(9, TeamId.Team1, bot: true));
            bots.Apply(Spawn(7, TeamId.Team1, bot: true));

            Assert.Equal("Red Team Bot 1", Name(7, bots));
            Assert.Equal("Red Team Bot 2", Name(9, bots));
        }

        /// <summary>
        /// A new round's bots are numbered from 1 again, while a departed bot keeps its name for any
        /// killfeed line that still names it.
        /// </summary>
        [Fact]
        public void ADespawnFreesItsNumber_ButKeepsItsName()
        {
            var bots = new BotRoster();
            bots.Apply(Spawn(3, TeamId.Team0, bot: true));
            bots.Apply(Spawn(5, TeamId.Team0, bot: true));

            bots.Apply(new DespawnActorMessage(3, default));

            Assert.Equal("Blue Team Bot 1", Name(5, bots));
            Assert.Equal("Blue Team Bot 1", Name(3, bots));
        }

        [Fact]
        public void AHumanIsNeverNamedAsABot()
        {
            var bots = new BotRoster();
            bots.Apply(Spawn(2, TeamId.Team1, bot: false));

            Assert.Equal("actor 2", Name(2, bots));
            Assert.Equal(0, bots.NumberOf(2));
        }
    }
}
