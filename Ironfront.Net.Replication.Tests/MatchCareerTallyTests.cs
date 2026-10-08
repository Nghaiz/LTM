using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Replication.Match;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The per-round career numbers the achievements are judged from (owner's list of 2026-10-09,
    /// item 4). Each test pins one feat to the kills that make it, and the near-miss that does not.
    /// </summary>
    public sealed class MatchCareerTallyTests
    {
        private const ushort Me = 3;
        private const ushort Enemy = 9;
        private const ushort Other = 10;
        private const ushort Mate = 4;

        private static CareerKill Kill(ushort victim, float distance = 10f, bool headshot = false, bool bot = true,
            CauseOfDeath cause = CauseOfDeath.Bullet, byte weapon = WeaponIds.RK44, byte vehicle = VehicleIds.NONE,
            DeathDetail detail = DeathDetail.None, bool sameTeam = false, ushort killer = Me)
            => new CareerKill(killer, victim, sameTeam, bot, headshot, (int)distance, cause, weapon, vehicle, detail);

        [Fact]
        public void KillsWithinThreeSecondsOfTheLastMakeAMultiKill()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy), 10f);
            tally.RecordKill(Kill(Other), 12.5f);
            tally.RecordKill(Kill(Enemy), 15.4f);
            Assert.Equal(3, tally.Get(Me, CareerStat.BestMultiKill));

            tally.RecordKill(Kill(Other), 30f);
            Assert.Equal(3, tally.Get(Me, CareerStat.BestMultiKill));
        }

        [Fact]
        public void ADeathEndsTheMultiKillAndTheHeadshotRun()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy, headshot: true), 1f);
            tally.RecordKill(Kill(Other, headshot: true), 2f);
            tally.RecordKill(Kill(Me, killer: Enemy), 2.5f);
            tally.RecordKill(Kill(Enemy, headshot: true), 3f);

            Assert.Equal(2, tally.Get(Me, CareerStat.HeadshotRun));
            Assert.Equal(2, tally.Get(Me, CareerStat.BestMultiKill));
        }

        [Fact]
        public void KillingYourLastKillerIsPaybackOnce()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Me, killer: Enemy), 1f);
            tally.RecordKill(Kill(Other), 2f);
            Assert.Equal(0, tally.Get(Me, CareerStat.RevengeKills));

            tally.RecordKill(Kill(Enemy), 3f);
            tally.RecordKill(Kill(Enemy), 4f);
            Assert.Equal(1, tally.Get(Me, CareerStat.RevengeKills));
        }

        [Fact]
        public void TwoGrenadeKillsFromOneBlastAreAGrenadeDouble()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy, cause: CauseOfDeath.Explosion, weapon: WeaponIds.FRAG), 5f);
            tally.RecordKill(Kill(Other, cause: CauseOfDeath.Explosion, weapon: WeaponIds.FRAG), 5.2f);
            tally.RecordKill(Kill(Enemy, cause: CauseOfDeath.Explosion, weapon: WeaponIds.FRAG), 9f);

            Assert.Equal(1, tally.Get(Me, CareerStat.GrenadeDoubleKills));
            Assert.Equal(3, tally.Get(Me, CareerStat.GrenadeKills));
            Assert.Equal(3, tally.Get(Me, CareerStat.ExplosiveKills));
        }

        [Fact]
        public void VehicleKillsAreSortedByTheVehicle()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy, cause: CauseOfDeath.Vehicle, vehicle: VehicleIds.JEEP, detail: DeathDetail.KillerInVehicle), 1f);
            tally.RecordKill(Kill(Enemy, vehicle: VehicleIds.TANK, detail: DeathDetail.KillerInVehicle), 2f);
            tally.RecordKill(Kill(Enemy, vehicle: VehicleIds.HELICOPTER, detail: DeathDetail.KillerInVehicle), 3f);
            tally.RecordKill(Kill(Enemy, vehicle: VehicleIds.RHIB, detail: DeathDetail.KillerInVehicle), 4f);
            tally.RecordKill(Kill(Enemy, cause: CauseOfDeath.Explosion, weapon: WeaponIds.SPEARHEAD, vehicle: VehicleIds.TANK, detail: DeathDetail.WentDownWithVehicle), 5f);

            Assert.Equal(1, tally.Get(Me, CareerStat.Roadkills));
            Assert.Equal(1, tally.Get(Me, CareerStat.TankKills));
            Assert.Equal(1, tally.Get(Me, CareerStat.HelicopterKills));
            Assert.Equal(1, tally.Get(Me, CareerStat.BoatKills));
            Assert.Equal(1, tally.Get(Me, CareerStat.TanksDestroyed));
        }

        [Fact]
        public void ATeamKillCountsOnlyAsATeamKill()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Mate, sameTeam: true, distance: 400, headshot: true), 1f);

            Assert.Equal(1, tally.Get(Me, CareerStat.TeamKills));
            Assert.Equal(0, tally.Get(Me, CareerStat.LongestHeadshotMetres));
            Assert.Equal(0, tally.Get(Me, CareerStat.BotKills));
        }

        [Fact]
        public void TheWorldsDeathsAreCountedOnTheVictim()
        {
            var tally = new MatchCareerTally();
            tally.RecordDeath(Me, DeathMessage.EnvironmentKiller, CauseOfDeath.Fall);
            tally.RecordDeath(Me, DeathMessage.EnvironmentKiller, CauseOfDeath.Drown);
            tally.RecordKill(Kill(Me, killer: Me, cause: CauseOfDeath.Explosion, weapon: WeaponIds.FRAG), 3f);

            Assert.Equal(1, tally.Get(Me, CareerStat.FallDeaths));
            Assert.Equal(1, tally.Get(Me, CareerStat.DrownDeaths));
            Assert.Equal(1, tally.Get(Me, CareerStat.OwnExplosiveDeaths));
            Assert.Equal(0, tally.Get(Me, CareerStat.GrenadeKills));
        }

        [Fact]
        public void AKillJustAfterDeployingIsQuick()
        {
            var tally = new MatchCareerTally();
            tally.NoteSpawn(Me, 100f);
            tally.RecordKill(Kill(Enemy), 104f);
            Assert.Equal(0, tally.Get(Me, CareerStat.QuickKills));

            tally.NoteSpawn(Me, 200f);
            tally.RecordKill(Kill(Enemy), 202f);
            Assert.Equal(1, tally.Get(Me, CareerStat.QuickKills));
        }

        [Fact]
        public void LongShotsKeepTheFarthestAndHeadshotsTheirOwn()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy, distance: 320), 1f);
            tally.RecordKill(Kill(Other, distance: 180, headshot: true), 2f);

            Assert.Equal(320, tally.Get(Me, CareerStat.LongestKillMetres));
            Assert.Equal(180, tally.Get(Me, CareerStat.LongestHeadshotMetres));
        }

        [Fact]
        public void TheRoundsResultIsFilledFromTheScoreTally()
        {
            var tally = new MatchCareerTally();
            var scores = new MatchScoreTally();
            for (int i = 0; i < MatchCareerTally.FlawlessKills; i++)
            {
                scores.RecordDeath(Enemy, Me);
                scores.CreditKill(Me, headshot: i % 2 == 0, points: 2);
            }
            tally.NoteScores(0, MatchCareerTally.ComebackDeficit);
            tally.NoteScores(250, 120);

            var round = new long[CareerStats.Count];
            tally.Fill(Me, scores, TeamId.Team0, TeamId.Team0, secondsPlayed: 900, mostPoints: true, round);

            Assert.Equal(1, round[(int)CareerStat.Matches]);
            Assert.Equal(1, round[(int)CareerStat.Wins]);
            Assert.Equal(MatchCareerTally.FlawlessKills, round[(int)CareerStat.Kills]);
            Assert.Equal(8, round[(int)CareerStat.Headshots]);
            Assert.Equal(30, round[(int)CareerStat.Score]);
            Assert.Equal(1, round[(int)CareerStat.FlawlessRounds]);
            Assert.Equal(1, round[(int)CareerStat.MvpRounds]);
            Assert.Equal(1, round[(int)CareerStat.ComebackWins]);
            Assert.Equal(900, round[(int)CareerStat.SecondsPlayed]);

            tally.Fill(Me, scores, TeamId.Team0, TeamId.Team1, 900, true, round);
            Assert.Equal(0, round[(int)CareerStat.Wins]);
            Assert.Equal(0, round[(int)CareerStat.MvpRounds]);
            Assert.Equal(0, round[(int)CareerStat.ComebackWins]);
        }

        [Fact]
        public void EveryStatHasAStableUniqueKey()
        {
            var seen = new System.Collections.Generic.HashSet<string>();
            for (int i = 0; i < CareerStats.Count; i++)
            {
                string key = CareerStats.Key((CareerStat)i);
                Assert.True(seen.Add(key), key);
                Assert.True(CareerStats.TryParse(key, out CareerStat back));
                Assert.Equal((CareerStat)i, back);
            }
        }
    }
}
