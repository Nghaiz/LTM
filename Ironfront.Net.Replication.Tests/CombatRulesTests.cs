using Ironfront.Net.Replication.Ai;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// How one bot fights (phase P28, part 2): the owner asked for bots that pick the right enemy,
    /// dodge, hide and fall back instead of standing in the open trading shots.
    /// </summary>
    public sealed class CombatRulesTests
    {
        // ------------------------------------------------------------------ targets

        [Fact]
        public void TheNearerEnemy_IsShotFirst_WhenNothingElseSetsThemApart()
            => Assert.True(
                CombatRules.TargetScore(20f, false, false, false)
                < CombatRules.TargetScore(40f, false, false, false));

        [Fact]
        public void AnEnemyShootingAtTheBot_ComesBeforeANearerOneThatIsNot()
        {
            float shooter = CombatRules.TargetScore(45f, false, shootingAtMe: true, false);
            float bystander = CombatRules.TargetScore(25f, false, shootingAtMe: false, false);
            Assert.True(shooter < bystander, "the bot turned its back on the enemy shooting at it");
        }

        [Fact]
        public void AnEnemyOnTheSquadsFlag_ComesBeforeAnEqualOneElsewhere()
            => Assert.True(
                CombatRules.TargetScore(30f, false, false, onObjective: true)
                < CombatRules.TargetScore(30f, false, false, onObjective: false));

        [Fact]
        public void AFallenEnemy_WaitsBehindAStandingOne_AsTheOriginalHadIt()
            => Assert.Equal(
                CombatRules.TargetScore(10f, false, false, false) + CombatRules.FallenOverPenalty,
                CombatRules.TargetScore(10f, fallenOver: true, false, false));

        [Fact]
        public void AShooterFarAway_DoesNotOutrankANearThreatOnceItIsOutOfReach()
        {
            // The bonus is a head start, not an override: a shooter 200 m off does not pull the
            // bot's aim off the enemy 20 m away.
            Assert.True(
                CombatRules.TargetScore(20f, false, false, false)
                < CombatRules.TargetScore(200f, false, shootingAtMe: true, false));
        }

        // ------------------------------------------------------------------ in the open

        [Theory]
        [InlineData(true, false, false, true)]
        [InlineData(false, false, false, false)]  // seated, or down
        [InlineData(true, true, false, false)]    // walking its path
        [InlineData(true, false, true, false)]    // in cover, or on its way
        public void OnlyABotOnItsFeetStandingOutsideCover_IsInTheOpen(
            bool onFoot, bool walkingPath, bool inOrBoundForCover, bool expected)
            => Assert.Equal(expected, CombatRules.InTheOpen(onFoot, walkingPath, inOrBoundForCover));

        [Fact]
        public void IncomingFire_MakesABotInTheOpenStepAside_WhateverItIsAimingAt()
        {
            Assert.Equal(OpenGroundMove.SideStep, CombatRules.InTheOpenMove(false, float.PositiveInfinity, true));
            Assert.Equal(OpenGroundMove.SideStep, CombatRules.InTheOpenMove(true, 200f, true));
        }

        [Fact]
        public void AFarTarget_IsShotCrouching()
        {
            Assert.Equal(OpenGroundMove.Crouch, CombatRules.InTheOpenMove(true, CombatRules.CrouchFireRange, false));
            Assert.Equal(OpenGroundMove.Crouch, CombatRules.InTheOpenMove(true, 120f, false));
        }

        [Fact]
        public void ANearTarget_IsFoughtOnTheMove()
            => Assert.Equal(OpenGroundMove.SideStep, CombatRules.InTheOpenMove(true, CombatRules.CrouchFireRange - 1f, false));

        [Fact]
        public void NothingToReactTo_MeansHolding()
            => Assert.Equal(OpenGroundMove.Hold, CombatRules.InTheOpenMove(false, float.PositiveInfinity, false));

        [Fact]
        public void ASideStep_IsAWalk_AndBrief()
        {
            Assert.True(CombatRules.SideStepSpeed < 3.2f, "a side-step faster than the bots' walk reads as a glitch");
            Assert.True(CombatRules.SideStepMinSeconds < CombatRules.SideStepMaxSeconds);
            Assert.True(CombatRules.SideStepPauseMinSeconds < CombatRules.SideStepPauseMaxSeconds);
            Assert.True(CombatRules.SideStepSpeed * CombatRules.SideStepMaxSeconds <= CombatRules.SideStepClearance,
                "a step can carry the bot past the ground it checked clear");
        }

        // ------------------------------------------------------------------ falling back

        [Fact]
        public void ABadlyHurtBotInAFight_FallsBack()
            => Assert.True(CombatRules.ShouldFallBack(CombatRules.FallBackHealth - 1f, engaged: true, inCover: false, onFoot: true));

        [Theory]
        [InlineData(80f, true, false, true)]   // not hurt enough
        [InlineData(20f, false, false, true)]  // nothing to fall back from
        [InlineData(20f, true, true, true)]    // already in cover
        [InlineData(20f, true, false, false)]  // in a vehicle
        [InlineData(0f, true, false, true)]    // dead
        public void ABotThatNeedNot_DoesNotFallBack(float health, bool engaged, bool inCover, bool onFoot)
            => Assert.False(CombatRules.ShouldFallBack(health, engaged, inCover, onFoot));

        // ------------------------------------------------------------------ holding cover

        [Fact]
        public void ADugInSquadStillFighting_HoldsItsCover()
            => Assert.True(CombatRules.HoldCover(dugIn: true, engaged: true, secondsDugIn: 4f));

        [Fact]
        public void ADugInSquadWithNobodyToShoot_MovesOn()
            => Assert.False(CombatRules.HoldCover(dugIn: true, engaged: false, secondsDugIn: 4f));

        [Fact]
        public void AStalemate_EndsAfterTheHold()
            => Assert.False(CombatRules.HoldCover(dugIn: true, engaged: true, secondsDugIn: CombatRules.HoldCoverSeconds));

        // ------------------------------------------------------------------ sneaking

        [Fact]
        public void ASneakingBot_HoldsFireOnAFarTarget()
            => Assert.True(CombatRules.HoldFire(sneaking: true, takingFire: false, CombatRules.AmbushRange + 20f));

        [Fact]
        public void ASneakingBot_OpensUpClose_OrOnceFound()
        {
            Assert.False(CombatRules.HoldFire(true, false, CombatRules.AmbushRange - 1f));
            Assert.False(CombatRules.HoldFire(true, takingFire: true, CombatRules.AmbushRange + 20f));
        }

        [Fact]
        public void ABotNotSneaking_NeverHoldsFire()
            => Assert.False(CombatRules.HoldFire(false, false, 300f));

        // ------------------------------------------------------------------ cover cost

        [Fact]
        public void TheNearerOfTwoEqualSpots_IsCheaper()
            => Assert.True(CombatRules.CoverCost(5f, 0f, true, false) < CombatRules.CoverCost(12f, 0f, true, false));

        [Fact]
        public void AFightingBot_PrefersASpotItCanShootFrom()
            => Assert.True(CombatRules.CoverCost(12f, 0f, canFire: true, false) < CombatRules.CoverCost(6f, 0f, canFire: false, false));

        [Fact]
        public void AFightingBot_WouldRatherNotGiveGround()
            => Assert.True(CombatRules.CoverCost(8f, 2f, true, false) < CombatRules.CoverCost(8f, -6f, true, false));

        [Fact]
        public void AFallingBackBot_PrefersGroundAwayFromTheEnemy_AndDoesNotNeedToShoot()
        {
            Assert.True(CombatRules.CoverCost(8f, -6f, false, fallingBack: true) < CombatRules.CoverCost(8f, 6f, true, fallingBack: true));
            Assert.Equal(CombatRules.CoverCost(8f, -6f, true, true), CombatRules.CoverCost(8f, -6f, false, true));
        }

        [Theory]
        [InlineData(3f, 5f, true, false)]
        [InlineData(3f, -5f, false, false)]
        [InlineData(3f, 5f, false, true)]
        [InlineData(3f, -5f, true, true)]
        [InlineData(0f, 0f, false, false)]
        public void ACostIsNeverBelowTheDistance_SoASearchMayStopEarly(float distance, float toward, bool canFire, bool fallingBack)
            => Assert.True(CombatRules.CoverCost(distance, toward, canFire, fallingBack) >= distance);
    }
}
