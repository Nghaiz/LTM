using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A bot is named by a callsign -- "VIPER" -- never by a made-up person's name a real player could
    /// share (owner ruling 2026-09-29), and no longer by its side and a number ("Blue Team Bot 3",
    /// owner's report of 2026-09-30).
    /// </summary>
    public sealed class BotNamingTests
    {
        private static SpawnActorMessage Spawn(ushort id, byte team, bool bot)
            => new SpawnActorMessage(id, team, bot ? SpawnFlags.IsBot : SpawnFlags.None, 0, 0, 0, 0, 100, 0);

        private static string Name(ushort id, BotRoster bots)
            => ActorNames.Display(id, new PlayerNameTable(), bots);

        [Fact]
        public void ABot_IsNamedByTheCallsignOfItsActor()
        {
            var bots = new BotRoster();
            bots.Apply(Spawn(3, TeamId.Team0, bot: true));
            bots.Apply(Spawn(4, TeamId.Team1, bot: true));

            Assert.Equal(BotCallsigns.For(3), Name(3, bots));
            Assert.Equal(BotCallsigns.For(4), Name(4, bots));
            Assert.NotEqual(Name(3, bots), Name(4, bots));
        }

        /// <summary>Every client holds the same spawns, so every client names a bot the same way.</summary>
        [Fact]
        public void TheCallsignDoesNotDependOnArrivalOrderOrOnTheOtherBots()
        {
            var first = new BotRoster();
            first.Apply(Spawn(9, TeamId.Team1, bot: true));
            first.Apply(Spawn(7, TeamId.Team1, bot: true));

            var second = new BotRoster();
            second.Apply(Spawn(7, TeamId.Team1, bot: true));

            Assert.Equal(Name(7, second), Name(7, first));
        }

        /// <summary>A bot keeps its callsign through a despawn, for a killfeed line that still names it.</summary>
        [Fact]
        public void ADespawnedBot_KeepsItsCallsign()
        {
            var bots = new BotRoster();
            bots.Apply(Spawn(3, TeamId.Team0, bot: true));
            string before = Name(3, bots);

            bots.Apply(new DespawnActorMessage(3, default));

            Assert.Equal(before, Name(3, bots));
        }

        [Fact]
        public void AHumanIsNeverNamedAsABot()
        {
            var bots = new BotRoster();
            bots.Apply(Spawn(2, TeamId.Team1, bot: false));

            Assert.Equal("actor 2", Name(2, bots));
        }

        /// <summary>Every actor id a match can hold gets a callsign no other id shares.</summary>
        [Fact]
        public void EveryActorInAMatch_HasADifferentCallsign()
        {
            var seen = new HashSet<string>();
            for (ushort id = 0; id < ProtocolConstants.MAX_ACTORS; id++)
                Assert.True(seen.Add(BotCallsigns.For(id)), "callsign repeated at actor " + id);

            Assert.True(BotCallsigns.Count >= ProtocolConstants.MAX_ACTORS);
        }

        /// <summary>A roster larger than the list stays unique: the callsign takes a number.</summary>
        [Fact]
        public void PastTheEndOfTheList_ACallsignTakesANumber()
        {
            ushort wrapped = (ushort)BotCallsigns.Count;
            Assert.Equal(BotCallsigns.For(0) + " 2", BotCallsigns.For(wrapped));
        }

        [Fact]
        public void ACallsignIsCapitals_SoItReadsAsAUnitRatherThanAPerson()
        {
            for (ushort id = 0; id < BotCallsigns.Count; id++)
            {
                string callsign = BotCallsigns.For(id);
                Assert.Equal(callsign.ToUpperInvariant(), callsign);
            }
        }
    }
}
