using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// What a kill earns on the killfeed -- multi-kills, streaks, first blood, revenge, shutdowns,
    /// team kills and long shots -- and the words and pictures the feed shows for it. Owner's
    /// report of 2026-09-30: richer content, more icons.
    /// </summary>
    public sealed class KillfeedAccoladeTests
    {
        private const ushort Minh = 1;
        private const ushort Lan = 2;
        private const ushort Bot = 33;
        private const ushort World = DeathMessage.EnvironmentKiller;

        private static KillfeedEntry Kill(ushort killer, ushort victim, byte killerTeam = TeamId.Team0,
                                          byte victimTeam = TeamId.Team1, ushort metres = 0)
            => KillfeedEntry.From(
                    new DeathMessage(victim, killer, CauseOfDeath.Bullet, 0, 0, 0, 0,
                                     WeaponIds.RK44, VehicleIds.NONE, DeathDetail.None, metres), 0f)
                .WithTeams(killerTeam, victimTeam);

        // ------------------------------------------------------------------ accolades

        [Fact]
        public void KillsInsideTheWindow_ChainIntoADoubleAndATripleKill()
        {
            var tracker = new KillfeedAccoladeTracker();

            KillfeedAccolades first = tracker.Observe(Kill(Minh, 40), 10f);
            KillfeedAccolades second = tracker.Observe(Kill(Minh, 41), 12f);
            KillfeedAccolades third = tracker.Observe(Kill(Minh, 42), 15.5f);

            Assert.False(first.Has(KillfeedAccolade.MultiKill));
            Assert.True(second.Has(KillfeedAccolade.MultiKill));
            Assert.Equal(2, second.MultiKill);
            Assert.Equal(3, third.MultiKill);
            Assert.Equal("TRIPLE KILL", KillfeedWording.BadgeOf(third).Text);
        }

        [Fact]
        public void AKillOutsideTheWindow_StartsANewChain()
        {
            var tracker = new KillfeedAccoladeTracker();
            tracker.Observe(Kill(Minh, 40), 10f);

            KillfeedAccolades late = tracker.Observe(Kill(Minh, 41), 10f + KillfeedAccoladeTracker.MultiKillWindowSeconds + 0.1f);
            Assert.False(late.Has(KillfeedAccolade.MultiKill));
            Assert.Equal(1, late.MultiKill);
        }

        [Fact]
        public void DyingEndsTheChainAndTheStreak()
        {
            var tracker = new KillfeedAccoladeTracker();
            tracker.Observe(Kill(Minh, 40), 10f);
            tracker.Observe(Kill(Bot, Minh, TeamId.Team1, TeamId.Team0), 10.5f);

            KillfeedAccolades afterRespawn = tracker.Observe(Kill(Minh, 41), 11f);
            Assert.False(afterRespawn.Has(KillfeedAccolade.MultiKill));
            Assert.Equal(1, afterRespawn.Streak);
        }

        [Fact]
        public void EveryFifthKillWithoutDying_IsAStreak_NamedByItsLength()
        {
            var tracker = new KillfeedAccoladeTracker();
            KillfeedAccolades fifth = default;
            KillfeedAccolades tenth = default;

            for (int i = 1; i <= 10; i++)
            {
                KillfeedAccolades earned = tracker.Observe(Kill(Minh, (ushort)(40 + i)), i * 10f);
                if (i == 5) fifth = earned;
                if (i == 10) tenth = earned;
                if (i != 5 && i != 10) Assert.False(earned.Has(KillfeedAccolade.Streak));
            }

            Assert.True(fifth.Has(KillfeedAccolade.Streak));
            Assert.Equal("KILLING SPREE " + KillfeedWording.Times + "5", KillfeedWording.BadgeOf(fifth).Text);
            Assert.Equal("RAMPAGE " + KillfeedWording.Times + "10", KillfeedWording.BadgeOf(tenth).Text);
        }

        [Fact]
        public void FirstBlood_IsTheRoundsFirstEnemyKill_AndComesBackWithANewRound()
        {
            var tracker = new KillfeedAccoladeTracker();

            Assert.True(tracker.Observe(Kill(Minh, 40), 1f).Has(KillfeedAccolade.FirstBlood));
            Assert.False(tracker.Observe(Kill(Lan, 41), 20f).Has(KillfeedAccolade.FirstBlood));

            tracker.Reset();
            Assert.True(tracker.Observe(Kill(Lan, 42), 1f).Has(KillfeedAccolade.FirstBlood));
        }

        [Fact]
        public void KillingWhoeverLastKilledYou_IsARevenge_Once()
        {
            var tracker = new KillfeedAccoladeTracker();
            tracker.Observe(Kill(Bot, Minh, TeamId.Team1, TeamId.Team0), 1f);

            KillfeedAccolades payback = tracker.Observe(Kill(Minh, Bot), 20f);
            Assert.True(payback.Has(KillfeedAccolade.Revenge));

            tracker.Observe(Kill(Minh, 44), 40f);
            Assert.False(tracker.Observe(Kill(Minh, Bot), 60f).Has(KillfeedAccolade.Revenge));
        }

        [Fact]
        public void EndingAStreakOfFiveOrMore_IsAShutdown()
        {
            var tracker = new KillfeedAccoladeTracker();
            for (int i = 1; i <= KillfeedAccoladeTracker.ShutdownMinStreak; i++)
                tracker.Observe(Kill(Bot, (ushort)(i + 2), TeamId.Team1, TeamId.Team0), i * 10f);

            KillfeedAccolades shutdown = tracker.Observe(Kill(Minh, Bot), 100f);
            Assert.True(shutdown.Has(KillfeedAccolade.Shutdown));
            Assert.Equal(KillfeedAccoladeTracker.ShutdownMinStreak, shutdown.EndedStreak);
            Assert.Equal("SHUTDOWN", KillfeedWording.BadgeOf(shutdown).Text);
        }

        [Fact]
        public void ATeamKill_IsAnnouncedAsOne_AndEarnsNothingElse()
        {
            var tracker = new KillfeedAccoladeTracker();
            KillfeedAccolades teamKill = tracker.Observe(Kill(Minh, Lan, TeamId.Team0, TeamId.Team0), 1f);

            Assert.Equal(KillfeedAccolade.TeamKill, teamKill.Flags);
            Assert.Equal(KillfeedTone.TeamKill, KillfeedWording.BadgeOf(teamKill).Tone);

            // Not first blood and not a streak step either: the next enemy kill is still the first.
            Assert.True(tracker.Observe(Kill(Minh, Bot), 2f).Has(KillfeedAccolade.FirstBlood));
        }

        [Fact]
        public void AnUnknownSide_IsNeverATeamKill()
        {
            var tracker = new KillfeedAccoladeTracker();
            KillfeedAccolades earned = tracker.Observe(Kill(Minh, Lan, TeamId.None, TeamId.None), 1f);
            Assert.False(earned.Has(KillfeedAccolade.TeamKill));
        }

        [Fact]
        public void TheWorldsDeathsAndSuicides_EarnNothing_ButEndTheVictimsStreak()
        {
            var tracker = new KillfeedAccoladeTracker();
            tracker.Observe(Kill(Minh, 40), 1f);
            tracker.Observe(Kill(Minh, 41), 20f);

            KillfeedEntry drowned = KillfeedEntry.From(
                new DeathMessage(Minh, World, CauseOfDeath.Drown, 0, 0, 0, 0), 30f);
            Assert.Equal(KillfeedAccolade.None, tracker.Observe(drowned, 30f).Flags);

            Assert.Equal(1, tracker.Observe(Kill(Minh, 42), 40f).Streak);
        }

        [Fact]
        public void AKillFromFarEnough_IsALongShot()
        {
            var tracker = new KillfeedAccoladeTracker();
            tracker.Observe(Kill(Lan, 40), 1f);   // first blood is spent elsewhere

            Assert.False(tracker.Observe(Kill(Minh, 41, metres: KillfeedAccoladeTracker.LongShotMetres - 1), 10f)
                .Has(KillfeedAccolade.LongShot));
            KillfeedAccolades far = tracker.Observe(Kill(Minh, 42, metres: 312), 30f);
            Assert.True(far.Has(KillfeedAccolade.LongShot));
            Assert.Equal("312 m", KillfeedWording.Distance(312));
        }

        [Fact]
        public void TheBadge_IsTheLoudestThingEarned()
        {
            var both = new KillfeedAccolades(
                KillfeedAccolade.MultiKill | KillfeedAccolade.Revenge | KillfeedAccolade.FirstBlood, 2, 2, 0);
            Assert.Equal("DOUBLE KILL", KillfeedWording.BadgeOf(both).Text);

            var tripleAndStreak = new KillfeedAccolades(
                KillfeedAccolade.MultiKill | KillfeedAccolade.Streak, 3, 5, 0);
            Assert.Equal("TRIPLE KILL", KillfeedWording.BadgeOf(tripleAndStreak).Text);

            var streakAndDouble = new KillfeedAccolades(
                KillfeedAccolade.MultiKill | KillfeedAccolade.Streak, 2, 10, 0);
            Assert.Equal(KillfeedTone.Streak, KillfeedWording.BadgeOf(streakAndDouble).Tone);

            Assert.True(KillfeedWording.BadgeOf(default).IsEmpty);
        }

        [Theory]
        [InlineData(2, "DOUBLE KILL")]
        [InlineData(3, "TRIPLE KILL")]
        [InlineData(4, "QUAD KILL")]
        public void MultiKillNames(int kills, string expected)
            => Assert.Equal(expected, KillfeedWording.MultiKillName(kills));

        [Fact]
        public void AFifthKillInAChain_IsCounted()
            => Assert.Equal("MULTI KILL " + KillfeedWording.Times + "5", KillfeedWording.MultiKillName(5));

        // ------------------------------------------------------------------ pictures

        [Fact]
        public void AVehicleKill_IsDrawnAsTheVehicle_WithTheRoadkillInItsChip()
        {
            var roadkill = KillfeedEntry.From(
                new DeathMessage(9, 3, CauseOfDeath.Vehicle, 0, 0, 0, 0,
                                 WeaponIds.NONE, VehicleIds.QUADBIKE, DeathDetail.KillerInVehicle), 0f);

            KillfeedWording wording = KillfeedWording.For(in roadkill);
            Assert.Equal(KillfeedGlyph.QuadBike, wording.Glyph);
            Assert.Equal("ROADKILL", wording.RestAfterGlyph);
            Assert.Equal("QUAD BIKE  ·  ROADKILL", wording.Label);
        }

        [Fact]
        public void TheCrewOfADestroyedVehicle_IsDrawnWithTheVehicle_Destroyed()
        {
            var crew = KillfeedEntry.From(
                new DeathMessage(9, 3, CauseOfDeath.Explosion, 0, 0, 0, 0,
                                 WeaponIds.NONE, VehicleIds.HELICOPTER, DeathDetail.WentDownWithVehicle), 0f);

            KillfeedWording wording = KillfeedWording.For(in crew);
            Assert.Equal(KillfeedGlyph.Helicopter, wording.Glyph);
            Assert.Equal("DESTROYED", wording.RestAfterGlyph);
        }

        [Fact]
        public void AWeaponKill_IsDrawnWithTheWeaponsOwnPicture_NotAGlyph()
        {
            KillfeedWording wording = KillfeedWording.For(Kill(Minh, 40));
            Assert.Equal(WeaponIds.RK44, wording.WeaponId);
            Assert.Equal(KillfeedGlyph.None, wording.Glyph);
        }

        [Theory]
        [InlineData(CauseOfDeath.Drown, KillfeedGlyph.Drowned)]
        [InlineData(CauseOfDeath.Fall, KillfeedGlyph.Fall)]
        [InlineData(CauseOfDeath.Explosion, KillfeedGlyph.Explosion)]
        [InlineData(CauseOfDeath.Bullet, KillfeedGlyph.Skull)]
        public void ADeathNobodyScored_LeadsWithAPictureOfTheCause(CauseOfDeath cause, KillfeedGlyph expected)
        {
            var death = KillfeedEntry.From(new DeathMessage(9, World, cause, 0, 0, 0, 0), 0f);
            Assert.Equal(expected, KillfeedWording.For(in death).Glyph);
        }

        // ------------------------------------------------------------------ event words

        [Theory]
        [InlineData("Fortress Capture Point", 0, "FORTRESS")]
        [InlineData("Capture Point Beach", 4, "BEACH")]
        [InlineData("capture point (Old Mill)", 1, "OLD MILL")]
        [InlineData("Capture Point", 2, "FLAG 3")]
        [InlineData(null, 0, "FLAG 1")]
        public void AFlagIsNamedTheWayAPlayerReadsIt(string? authored, int index, string expected)
            => Assert.Equal(expected, KillfeedWording.FlagName(authored, index));

        [Fact]
        public void TheRoundAndOverflowLinesSayWhatHappened()
        {
            Assert.Equal("ROUND STARTED", KillfeedWording.RoundWords(true, TeamId.None));
            Assert.Equal("wins the round", KillfeedWording.RoundWords(false, TeamId.Team1));
            Assert.Equal("ROUND DRAWN", KillfeedWording.RoundWords(false, TeamId.None));
            Assert.Equal("+1 more event", KillfeedWording.OverflowWords(1));
            Assert.Equal("+7 more events", KillfeedWording.OverflowWords(7));
        }
    }
}
