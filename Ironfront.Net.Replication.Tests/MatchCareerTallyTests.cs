using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Replication.Match;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The per-round facts the achievements are judged from (achievements v2,
    /// <c>docs/achievements.md</c>). Each test pins one fact to the events that make it, and the
    /// near-miss that does not.
    /// </summary>
    public sealed class MatchCareerTallyTests
    {
        private const ushort Me = 3;
        private const ushort Mate = 4;
        private const ushort Enemy = 9;
        private const ushort Other = 10;
        private const ushort Human = 11;

        private static CareerKill Kill(ushort victim, int distance = 10, bool headshot = false, bool bot = true,
            CauseOfDeath cause = CauseOfDeath.Bullet, byte weapon = WeaponIds.RK44, byte vehicle = VehicleIds.NONE,
            DeathDetail detail = DeathDetail.None, bool sameTeam = false, ushort killer = Me)
            => new CareerKill
            {
                Killer = killer, Victim = victim, SameTeam = sameTeam, VictimIsBot = bot, Headshot = headshot,
                DistanceMetres = distance, Cause = cause, WeaponId = weapon, VehicleType = vehicle, Detail = detail,
                VictimPilotHeightMetres = -1f,
            };

        private static CareerKill Seated(ushort victim, ushort vehicleId, byte vehicleType, byte seat = 0)
        {
            CareerKill kill = Kill(victim, weapon: WeaponIds.NONE, vehicle: vehicleType, detail: DeathDetail.KillerInVehicle);
            kill.KillerVehicleId = vehicleId;
            kill.KillerVehicleType = vehicleType;
            kill.KillerSeat = seat;
            return kill;
        }

        private static long Fact(MatchCareerTally tally, RoundFact fact, ushort actor = Me) => tally.Get(actor, fact);

        // ------------------------------------------------------------------ kills

        [Fact]
        public void KillsWithinThreeSecondsOfTheLastMakeARampage()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy), 10f);
            tally.RecordKill(Kill(Other), 12.5f);
            tally.RecordKill(Kill(Enemy), 15.4f);
            Assert.Equal(3, Fact(tally, RoundFact.BestMultiKill));

            tally.RecordKill(Kill(Other), 18.5f);
            Assert.Equal(3, Fact(tally, RoundFact.BestMultiKill));
        }

        [Fact]
        public void ATeamKillIsNoKillForAnyAchievement()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Mate, sameTeam: true), 1f);

            Assert.Equal(0, Fact(tally, RoundFact.BotKills));
            Assert.Equal(0, Fact(tally, RoundFact.BestMultiKill));
        }

        [Fact]
        public void ThreeEnemiesInOneGrenadesTickAreOneBlast()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy, weapon: WeaponIds.FRAG, cause: CauseOfDeath.Explosion), 20f);
            tally.RecordKill(Kill(Other, weapon: WeaponIds.FRAG, cause: CauseOfDeath.Explosion), 20f);
            tally.RecordKill(Kill(Human, weapon: WeaponIds.FRAG, cause: CauseOfDeath.Explosion, bot: false), 20f);
            Assert.Equal(3, Fact(tally, RoundFact.BestGrenadeBlast));

            var later = new MatchCareerTally();
            later.RecordKill(Kill(Enemy, weapon: WeaponIds.FRAG), 20f);
            later.RecordKill(Kill(Other, weapon: WeaponIds.FRAG), 20.1f);
            Assert.Equal(1, Fact(later, RoundFact.BestGrenadeBlast));
        }

        [Fact]
        public void AGrenadeThatKillsAfterItsThrowerDiedIsFromTheGrave()
        {
            var tally = new MatchCareerTally();
            tally.WatchHealth(Me, alive: true, health: 100f);
            tally.RecordKill(Kill(Me, killer: Enemy), 10f);
            tally.RecordKill(Kill(Enemy, weapon: WeaponIds.FRAG, cause: CauseOfDeath.Explosion), 11f);
            tally.RecordKill(Kill(Other, weapon: WeaponIds.FRAG, cause: CauseOfDeath.Explosion), 11f);
            tally.RecordKill(Kill(Human, weapon: WeaponIds.FRAG, cause: CauseOfDeath.Explosion), 11f);
            Assert.Equal(3, Fact(tally, RoundFact.FromTheGraveBest));

            // Alive again: grenade kills are no longer from the grave.
            tally.WatchHealth(Me, alive: true, health: 100f);
            tally.RecordKill(Kill(Enemy, weapon: WeaponIds.FRAG), 40f);
            Assert.Equal(3, Fact(tally, RoundFact.FromTheGraveBest));
        }

        [Fact]
        public void ASpearheadIsAGrenadeLikeAFrag()
        {
            // Owner's run of 2026-10-10: the explosive grenade with the bigger pouch counted for no
            // grenade achievement at all.
            var tally = new MatchCareerTally();
            tally.WatchHealth(Me, alive: true, health: 100f);
            tally.RecordKill(Kill(Enemy, weapon: WeaponIds.SPEARHEAD, cause: CauseOfDeath.Explosion), 5f);
            tally.RecordKill(Kill(Other, weapon: WeaponIds.SPEARHEAD, cause: CauseOfDeath.Explosion), 5f);
            tally.RecordKill(Kill(Human, weapon: WeaponIds.SPEARHEAD, cause: CauseOfDeath.Explosion, bot: false), 5f);
            Assert.Equal(3, Fact(tally, RoundFact.BestGrenadeBlast));
            Assert.Equal(3, Fact(tally, RoundFact.GrenadeKills));

            tally.RecordKill(Kill(Me, killer: Enemy), 10f);
            tally.RecordKill(Kill(Enemy, weapon: WeaponIds.SPEARHEAD, cause: CauseOfDeath.Explosion), 11f);
            Assert.Equal(1, Fact(tally, RoundFact.FromTheGraveBest));

            var life = new MatchCareerTally();
            life.RecordKill(Kill(Enemy, weapon: WeaponIds.RK44), 1f);
            life.RecordKill(Kill(Enemy, weapon: WeaponIds.SIND7), 2f);
            life.RecordKill(Kill(Enemy, weapon: WeaponIds.SPEARHEAD), 3f);
            life.RecordKill(Kill(Enemy, weapon: WeaponIds.BEU_AW1), 4f);
            Assert.Equal(1, Fact(life, RoundFact.JackOfAllTrades));

            Assert.False(CareerWeapons.IsGrenade(WeaponIds.BEU_AW1));
            Assert.False(CareerWeapons.IsGrenade(WeaponIds.AMMO_BAG));
        }

        [Fact]
        public void DyingInYourOwnBlastBesideAnEnemyIsMutualDestructionOnce()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy, weapon: WeaponIds.FRAG, cause: CauseOfDeath.Explosion), 30f);
            tally.RecordKill(Kill(Other, weapon: WeaponIds.FRAG, cause: CauseOfDeath.Explosion), 30f);
            tally.RecordKill(Kill(Me, killer: Me, weapon: WeaponIds.FRAG, cause: CauseOfDeath.Explosion), 30f);
            Assert.Equal(1, Fact(tally, RoundFact.MutualDestructions));

            var apart = new MatchCareerTally();
            apart.RecordKill(Kill(Enemy, cause: CauseOfDeath.Explosion), 30f);
            apart.RecordKill(Kill(Me, killer: Me, cause: CauseOfDeath.Explosion), 31f);
            Assert.Equal(0, Fact(apart, RoundFact.MutualDestructions));
        }

        [Fact]
        public void APrimaryAPistolAGrenadeAndALauncherInOneLifeAreAJackOfAllTrades()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy, weapon: WeaponIds.RK44), 1f);
            tally.RecordKill(Kill(Enemy, weapon: WeaponIds.SIND7), 10f);
            tally.RecordKill(Kill(Enemy, weapon: WeaponIds.FRAG), 20f);
            Assert.Equal(0, Fact(tally, RoundFact.JackOfAllTrades));
            tally.RecordKill(Kill(Enemy, weapon: WeaponIds.BIL_SCALPEL, cause: CauseOfDeath.Explosion), 30f);
            Assert.Equal(1, Fact(tally, RoundFact.JackOfAllTrades));

            var died = new MatchCareerTally();
            died.RecordKill(Kill(Enemy, weapon: WeaponIds.RK44), 1f);
            died.RecordKill(Kill(Enemy, weapon: WeaponIds.SIND7), 2f);
            died.RecordKill(Kill(Enemy, weapon: WeaponIds.FRAG), 3f);
            died.RecordKill(Kill(Me, killer: Enemy), 4f);
            died.RecordKill(Kill(Enemy, weapon: WeaponIds.BEU_AW1), 9f);
            Assert.Equal(0, Fact(died, RoundFact.JackOfAllTrades));
        }

        [Fact]
        public void EveryCarriedWeaponWithAKillIsMarked()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy, weapon: WeaponIds.WRENCH, detail: DeathDetail.Melee), 1f);
            tally.RecordKill(Kill(Enemy, weapon: WeaponIds.SPEARHEAD), 2f);
            tally.RecordKill(Kill(Enemy, weapon: WeaponIds.BINOCS), 3f);

            long mask = Fact(tally, RoundFact.WeaponKillMask);
            Assert.Equal((1L << WeaponIds.WRENCH) | (1L << WeaponIds.SPEARHEAD), mask);
            Assert.Equal(1, Fact(tally, RoundFact.MeleeKills));
        }

        [Fact]
        public void DistancesAreKeptByKind()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy, distance: 61, weapon: WeaponIds.EAGLE_76), 1f);
            CareerKill pilot = Kill(Enemy, distance: 320, headshot: true, weapon: WeaponIds.SL_DEFENDER);
            pilot.VictimPilotHeightMetres = 12f;
            tally.RecordKill(pilot, 2f);
            CareerKill grounded = Kill(Enemy, distance: 500, headshot: true);
            grounded.VictimPilotHeightMetres = 2f;
            tally.RecordKill(grounded, 3f);
            CareerKill counter = Kill(Enemy, distance: 160, weapon: WeaponIds.SIND7);
            counter.VictimWeaponId = WeaponIds.RECON_LRR;
            tally.RecordKill(counter, 4f);
            CareerKill pistolOnRifle = Kill(Enemy, distance: 190, weapon: WeaponIds.SIND7);
            pistolOnRifle.VictimWeaponId = WeaponIds.RK44;
            tally.RecordKill(pistolOnRifle, 5f);

            Assert.Equal(61, Fact(tally, RoundFact.LongestShotgunKillMetres));
            Assert.Equal(320, Fact(tally, RoundFact.LongestPilotHeadshotMetres));
            Assert.Equal(500, Fact(tally, RoundFact.LongestHeadshotMetres));
            Assert.Equal(160, Fact(tally, RoundFact.LongestPistolOnSniperMetres));
        }

        [Fact]
        public void NemesisCountsOneHumansDeathsUntilTheyKillYou()
        {
            var tally = new MatchCareerTally();
            for (int i = 0; i < 6; i++) tally.RecordKill(Kill(Human, bot: false), i * 10f);
            Assert.Equal(6, Fact(tally, RoundFact.NemesisBest));

            tally.RecordKill(Kill(Me, killer: Human, bot: false), 70f);
            tally.RecordKill(Kill(Human, bot: false), 80f);
            Assert.Equal(6, Fact(tally, RoundFact.NemesisBest));

            for (int i = 0; i < 7; i++) tally.RecordKill(Kill(Enemy), 100f + i);
            Assert.Equal(6, Fact(tally, RoundFact.NemesisBest));
        }

        [Fact]
        public void HeadshotsInARowResetOnABodyShotAndOnDeath()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy, headshot: true), 1f);
            tally.RecordKill(Kill(Enemy, headshot: true), 10f);
            tally.RecordKill(Kill(Enemy), 20f);
            tally.RecordKill(Kill(Enemy, headshot: true), 30f);
            tally.RecordKill(Kill(Me, killer: Enemy), 40f);
            tally.RecordKill(Kill(Enemy, headshot: true), 50f);
            Assert.Equal(2, Fact(tally, RoundFact.HeadshotRun));
        }

        // ------------------------------------------------------------------ vehicles

        [Fact]
        public void RoadkillsAreCountedByTheVehicleThatRanThemOver()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy, cause: CauseOfDeath.Vehicle, vehicle: VehicleIds.RHIB, detail: DeathDetail.KillerInVehicle, weapon: WeaponIds.NONE), 1f);
            tally.RecordKill(Kill(Enemy, cause: CauseOfDeath.Vehicle, vehicle: VehicleIds.HELICOPTER, detail: DeathDetail.KillerInVehicle, weapon: WeaponIds.NONE), 9f);
            tally.RecordKill(Kill(Enemy, cause: CauseOfDeath.Vehicle, vehicle: VehicleIds.JEEP, detail: DeathDetail.KillerInVehicle, weapon: WeaponIds.NONE), 19f);

            Assert.Equal(3, Fact(tally, RoundFact.Roadkills));
            Assert.Equal(1, Fact(tally, RoundFact.BoatRoadkills));
            Assert.Equal(1, Fact(tally, RoundFact.HeliRoadkills));
        }

        [Fact]
        public void AHornWithinThreeSecondsOfARoadkillIsAVictoryLapOnce()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Kill(Enemy, cause: CauseOfDeath.Vehicle, vehicle: VehicleIds.JEEP, detail: DeathDetail.KillerInVehicle), 10f);
            tally.RecordHorn(Me, 13f);
            tally.RecordHorn(Me, 13.5f);
            Assert.Equal(1, Fact(tally, RoundFact.HornAfterRoadkill));

            var late = new MatchCareerTally();
            late.RecordKill(Kill(Enemy, cause: CauseOfDeath.Vehicle, vehicle: VehicleIds.JEEP, detail: DeathDetail.KillerInVehicle), 10f);
            late.RecordHorn(Me, 13.1f);
            Assert.Equal(0, Fact(late, RoundFact.HornAfterRoadkill));
        }

        [Fact]
        public void ATankStintSurvivesASeatChangeButAHelicopterStintDoesNot()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Seated(Enemy, vehicleId: 5, VehicleIds.TANK, seat: 0), 1f);
            tally.NoteSeat(Me, 5, 1);
            tally.RecordKill(Seated(Enemy, vehicleId: 5, VehicleIds.TANK, seat: 1), 2f);
            Assert.Equal(2, Fact(tally, RoundFact.TankStintBest));

            tally.NoteSeat(Me, 0, 0);
            tally.RecordKill(Seated(Enemy, vehicleId: 5, VehicleIds.TANK, seat: 0), 3f);
            Assert.Equal(2, Fact(tally, RoundFact.TankStintBest));

            tally.RecordKill(Seated(Enemy, vehicleId: 7, VehicleIds.HELICOPTER, seat: 0), 4f);
            tally.NoteSeat(Me, 7, 1);
            tally.RecordKill(Seated(Enemy, vehicleId: 7, VehicleIds.HELICOPTER, seat: 1), 5f);
            Assert.Equal(1, Fact(tally, RoundFact.HeliStintBest));
        }

        [Fact]
        public void AVehicleGoingDownEndsItsCrewsStint()
        {
            var tally = new MatchCareerTally();
            tally.RecordKill(Seated(Enemy, vehicleId: 5, VehicleIds.TANK), 1f);
            tally.RecordVehicleDown(new CareerVehicleDown { VehicleId = 5, VehicleType = VehicleIds.TANK, Destroyer = Enemy });
            tally.RecordKill(Seated(Enemy, vehicleId: 5, VehicleIds.TANK), 2f);
            Assert.Equal(1, Fact(tally, RoundFact.TankStintBest));
        }

        [Fact]
        public void ShootingDownAHelicopterIsCountedByWhereTheShooterWas()
        {
            var tally = new MatchCareerTally();
            CareerVehicleDown heli = new CareerVehicleDown
            {
                VehicleId = 8, VehicleType = VehicleIds.HELICOPTER, Destroyer = Me, EnemyVehicle = true,
                EnemyPilotAboard = true, HeightMetres = 12f,
            };
            tally.RecordVehicleDown(heli);

            // From a helicopter in the air, by its pilot's rockets or its door gun: the seat is not asked.
            heli.DestroyerVehicleType = VehicleIds.HELICOPTER;
            heli.DestroyerHeightMetres = 30f;
            tally.RecordVehicleDown(heli);

            // ...but a helicopter still on its pad is not in a dogfight.
            CareerVehicleDown grounded = heli;
            grounded.DestroyerHeightMetres = 4.9f;
            tally.RecordVehicleDown(grounded);

            heli.DestroyerVehicleType = VehicleIds.TANK;
            heli.ByTankMainGun = true;
            tally.RecordVehicleDown(heli);

            CareerVehicleDown parked = heli;
            parked.HeightMetres = 4.9f;
            tally.RecordVehicleDown(parked);

            CareerVehicleDown empty = heli;
            empty.EnemyPilotAboard = false;
            tally.RecordVehicleDown(empty);

            Assert.Equal(1, Fact(tally, RoundFact.HelisDownedOnFoot));
            Assert.Equal(1, Fact(tally, RoundFact.Dogfights));
            Assert.Equal(1, Fact(tally, RoundFact.ImpossibleAngles));
            Assert.Equal(6, Fact(tally, RoundFact.VehiclesDestroyed));
        }

        [Fact]
        public void OnlyAnEnemysVehicleCountsAsDestroyed()
        {
            var tally = new MatchCareerTally();
            tally.RecordVehicleDown(new CareerVehicleDown { VehicleId = 2, Destroyer = Me, EnemyVehicle = false });
            Assert.Equal(0, Fact(tally, RoundFact.VehiclesDestroyed));
        }

        // ------------------------------------------------------------------ the body

        [Fact]
        public void OnBorrowedTimeStartsWhenAnEnemyLeavesFiveHealthAndEndsOnAHeal()
        {
            var tally = new MatchCareerTally();
            tally.WatchHealth(Me, true, 100f);
            tally.RecordDamage(Me, byEnemy: true, healthAfter: 5f, CauseOfDeath.Bullet);
            tally.WatchHealth(Me, true, 5f);
            for (int i = 0; i < 3; i++) tally.RecordKill(Kill(Enemy), 10f + i * 5f);
            Assert.Equal(3, Fact(tally, RoundFact.ClutchBest));

            tally.WatchHealth(Me, true, 35f);
            tally.RecordKill(Kill(Enemy), 40f);
            Assert.Equal(3, Fact(tally, RoundFact.ClutchBest));
        }

        [Fact]
        public void OnBorrowedTimeIgnoresAFallAndAnAlly()
        {
            var tally = new MatchCareerTally();
            tally.RecordDamage(Me, byEnemy: true, healthAfter: 4f, CauseOfDeath.Fall);
            tally.RecordDamage(Me, byEnemy: false, healthAfter: 4f, CauseOfDeath.Bullet);
            tally.RecordDamage(Me, byEnemy: true, healthAfter: 6f, CauseOfDeath.Bullet);
            tally.RecordKill(Kill(Enemy), 10f);
            Assert.Equal(0, Fact(tally, RoundFact.ClutchBest));
        }

        [Fact]
        public void DamageTakenIsEveryDropInHealthWhileAlive()
        {
            var tally = new MatchCareerTally();
            tally.WatchHealth(Me, true, 100f);
            tally.WatchHealth(Me, true, 70f);
            tally.WatchHealth(Me, true, 100f);
            tally.WatchHealth(Me, true, 99.5f);
            Assert.Equal(31, Fact(tally, RoundFact.DamageTaken));

            var untouched = new MatchCareerTally();
            untouched.WatchHealth(Me, true, 100f);
            untouched.WatchHealth(Me, true, 100f);
            Assert.Equal(0, Fact(untouched, RoundFact.DamageTaken));
        }

        [Fact]
        public void ALandingOnFiveHealthOrLessIsNineLives()
        {
            var tally = new MatchCareerTally();
            tally.RecordLanding(Me, 5f);
            tally.RecordLanding(Me, 5.5f);
            tally.RecordLanding(Me, 0f);
            Assert.Equal(1, Fact(tally, RoundFact.FallsSurvivedLow));
        }

        [Fact]
        public void AShotgunShotIsOneHitHoweverManyPelletsLand()
        {
            var tally = new MatchCareerTally();
            tally.RecordShot(Me, WeaponIds.EAGLE_76);
            tally.RecordHit(Me, WeaponIds.EAGLE_76, 42);
            tally.RecordHit(Me, WeaponIds.EAGLE_76, 42);
            tally.RecordShot(Me, WeaponIds.EAGLE_76);
            tally.RecordShot(Me, WeaponIds.FRAG);

            Assert.Equal(2, Fact(tally, RoundFact.Shots));
            Assert.Equal(1, Fact(tally, RoundFact.Hits));
        }

        // ------------------------------------------------------------------ the report

        [Fact]
        public void TheReportRanksAPlayerAmongEveryoneStillInTheRound()
        {
            var tally = new MatchCareerTally();
            var scores = new MatchScoreTally();
            tally.BeginRound(100f, mapFlags: 4);
            tally.NoteJoined(Me, 160f);
            tally.CreditCapture(Me, 1);
            tally.CreditCapture(Me, 1);
            tally.CreditCapture(Me, 2);
            tally.CreditCapture(Enemy, 3);
            tally.RecordKill(Kill(Enemy), 200f);
            scores.RecordDeath(Human, Enemy);
            scores.RecordDeath(Human, Enemy);

            RoundActor[] present =
            {
                new RoundActor(Me, TeamId.Team0, human: true),
                new RoundActor(Enemy, TeamId.Team1, human: false),
                new RoundActor(Human, TeamId.Team1, human: true),
            };
            var sheet = new RoundSheet();
            tally.Fill(Me, TeamId.Team0, TeamId.Team0, now: 760f, finished: true, scores, present, sheet);

            Assert.Equal(600, sheet.Get(RoundFact.SecondsPlayed));
            Assert.Equal(1, sheet.Get(RoundFact.Won));
            Assert.Equal(4, sheet.Get(RoundFact.MapFlags));
            Assert.Equal(2, sheet.Get(RoundFact.FlagsHelpedDistinct));
            Assert.Equal(3, sheet.Get(RoundFact.FlagsCaptured));
            Assert.Equal(1, sheet.Get(RoundFact.HumansOwnSide));
            Assert.Equal(1, sheet.Get(RoundFact.HumansEnemySide));
            Assert.Equal(2, sheet.Get(RoundFact.MaxOtherHumanDeaths));
            Assert.Equal(1, sheet.Get(RoundFact.MaxOtherCaptures));
            Assert.Equal(-1, sheet.Get(RoundFact.BestOtherAccuracyPermille));
            Assert.True(sheet.Has(RoundFact.MinOtherPoints));
        }

        [Fact]
        public void AReportMidRoundOrForALeaverCarriesNoRanks()
        {
            var tally = new MatchCareerTally();
            var sheet = new RoundSheet();
            tally.Fill(Me, TeamId.Team0, TeamId.None, 50f, finished: false, new MatchScoreTally(),
                ReadOnlySpan<RoundActor>.Empty, sheet);

            Assert.Equal(0, sheet.Get(RoundFact.Finished));
            Assert.False(sheet.Has(RoundFact.MinOtherPoints));
            Assert.False(sheet.Has(RoundFact.HumansOwnSide));
        }

        [Fact]
        public void TheEnemyComingWithinTenPointsIsANearLossUnderEitherRule()
        {
            var margin = new MatchCareerTally();
            margin.NoteScores(10, 200, firstToRule: false, victoryPoints: 200);
            var sheet = new RoundSheet();
            margin.Fill(Me, TeamId.Team0, TeamId.Team0, 0f, false, new MatchScoreTally(), ReadOnlySpan<RoundActor>.Empty, sheet);
            Assert.Equal(1, sheet.Get(RoundFact.NearLoss));

            var target = new MatchCareerTally();
            target.NoteScores(100, 489, firstToRule: true, victoryPoints: 500);
            var short1 = new RoundSheet();
            target.Fill(Me, TeamId.Team0, TeamId.Team0, 0f, false, new MatchScoreTally(), ReadOnlySpan<RoundActor>.Empty, short1);
            Assert.Equal(0, short1.Get(RoundFact.NearLoss));
        }

        [Fact]
        public void ASlotPassingToANewPlayerStartsClean()
        {
            var tally = new MatchCareerTally();
            for (int i = 0; i < 3; i++) tally.RecordKill(Kill(Human, bot: false), i);
            tally.NoteJoined(Me, 10f);
            Assert.Equal(0, Fact(tally, RoundFact.PlayerKills));
            tally.RecordKill(Kill(Human, bot: false), 11f);
            Assert.Equal(1, Fact(tally, RoundFact.NemesisBest));
        }
    }
}
