using Ironfront.Net.Replication.Ai;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// How a bot drives round what is in front of it and gets a stuck vehicle free (owner,
    /// 2026-10-08: bots drive into everything, then leave the vehicle and walk).
    /// </summary>
    public sealed class DrivingRulesTests
    {
        private const float Clear = float.PositiveInfinity;
        private const float Look = 12f;

        [Fact]
        public void NothingAheadChangesNothing()
        {
            DrivingRules.Avoid(Clear, Clear, Clear, Look, out float steer, out float throttle);
            Assert.Equal(0f, steer);
            Assert.Equal(1f, throttle);

            DrivingRules.Avoid(Look + 5f, 30f, Look, Look, out steer, out throttle);
            Assert.Equal(0f, steer);
            Assert.Equal(1f, throttle);
        }

        [Fact]
        public void AnObstacleOnOneSidePushesTheSteeringAway()
        {
            DrivingRules.Avoid(3f, Clear, Clear, Look, out float steer, out float throttle);
            Assert.True(steer > 0f, "a tree on the left must steer right");
            Assert.Equal(1f, throttle);

            DrivingRules.Avoid(Clear, Clear, 3f, Look, out steer, out _);
            Assert.True(steer < 0f, "a tree on the right must steer left");
        }

        [Fact]
        public void AnObstacleDeadAheadTurnsTowardTheRoomAndSlows()
        {
            DrivingRules.Avoid(4f, 3f, 11f, Look, out float steer, out float throttle);
            Assert.True(steer > 0.5f, $"more room right must turn right hard, got {steer}");
            Assert.True(throttle < 1f && throttle >= DrivingRules.MinThrottleShare, $"throttle {throttle}");

            DrivingRules.Avoid(10f, 3f, 2f, Look, out steer, out _);
            Assert.True(steer < -0.5f, $"more room left must turn left hard, got {steer}");
        }

        [Fact]
        public void TheCloserTheObstacleTheHarderTheTurnAndTheSlowerTheThrottle()
        {
            DrivingRules.Avoid(Clear, 10f, Clear, Look, out float farSteer, out float farThrottle);
            DrivingRules.Avoid(Clear, 2f, Clear, Look, out float nearSteer, out float nearThrottle);
            Assert.True(System.Math.Abs(nearSteer) > System.Math.Abs(farSteer));
            Assert.True(nearThrottle < farThrottle);
        }

        [Fact]
        public void TheSteeringNeverLeavesItsRange()
        {
            DrivingRules.Avoid(0f, 0f, Clear, Look, out float steer, out float throttle);
            Assert.InRange(steer, -1f, 1f);
            Assert.InRange(throttle, DrivingRules.MinThrottleShare, 1f);
        }

        [Fact]
        public void TheLookaheadGrowsWithSpeedWithinItsBounds()
        {
            Assert.Equal(DrivingRules.MinLookahead, DrivingRules.Lookahead(0f));
            Assert.Equal(DrivingRules.MaxLookahead, DrivingRules.Lookahead(60f));
            Assert.Equal(13f, DrivingRules.Lookahead(10f), 3);
        }

        [Fact]
        public void ANudgeNeverUndoesAHardTurnThePathAsksFor()
        {
            // Full lock left, the feelers asking right: the path's turn stands.
            Assert.Equal(-1f, DrivingRules.BlendSteer(-1f, 0.9f), 3);
            // A hard turn holds its direction against the strongest nudge.
            Assert.True(DrivingRules.BlendSteer(-0.8f, 1f) < 0f);
            // A gentle path turn is still overruled by an obstacle close ahead.
            Assert.True(DrivingRules.BlendSteer(-0.2f, 0.9f) > 0f);
            // A nudge the same way as the turn adds to it, within the range.
            Assert.Equal(1f, DrivingRules.BlendSteer(0.8f, 0.6f), 3);
        }

        [Fact]
        public void BackingUpIsCappedAndThePastTheCapThrottleIsTheBrake()
        {
            Assert.Equal(-0.7f, DrivingRules.LimitReverse(-0.7f, -3f), 3);
            Assert.True(DrivingRules.LimitReverse(-0.7f, -(DrivingRules.MaxReverseSpeed + 1f)) > 0f);
            // Driving forward, nothing changes.
            Assert.Equal(0.8f, DrivingRules.LimitReverse(0.8f, 20f), 3);
        }

        [Fact]
        public void AFarWaypointBehindIsTurnedRoundToNotBackedUpTo()
        {
            Assert.True(DrivingRules.TurnAround(-30f, 40f));
            Assert.False(DrivingRules.TurnAround(-5f, 8f), "a waypoint just behind is backed up to");
            Assert.False(DrivingRules.TurnAround(30f, 40f), "a waypoint ahead is driven to");
        }

        [Fact]
        public void EachRecoveryBacksUpLongerThanTheLast()
        {
            Assert.Equal(1.2f, DrivingRules.ReverseSeconds(1), 3);
            Assert.Equal(2.0f, DrivingRules.ReverseSeconds(2), 3);
            Assert.Equal(2.8f, DrivingRules.ReverseSeconds(3), 3);
            Assert.Equal(3.6f, DrivingRules.ReverseSeconds(4), 3);
            Assert.Equal(3.6f, DrivingRules.ReverseSeconds(9), 3);
        }

        [Fact]
        public void ARecoveryTurnsTheNoseTowardTheRoom()
        {
            Assert.Equal(-1f, DrivingRules.NoseTurn(15f, 3f, 1));
            Assert.Equal(1f, DrivingRules.NoseTurn(3f, 15f, 1));

            // A car backs up with the wheels the other way round to get there.
            Assert.Equal(1f, DrivingRules.CarReverseSteer(15f, 3f, 1));
            Assert.Equal(-1f, DrivingRules.CarReverseSteer(3f, 15f, 1));
        }

        [Fact]
        public void WithNoSideClearerTheRecoveriesAlternate()
        {
            Assert.NotEqual(DrivingRules.NoseTurn(Clear, Clear, 1), DrivingRules.NoseTurn(Clear, Clear, 2));
            Assert.NotEqual(DrivingRules.NoseTurn(5f, 5f, 2), DrivingRules.NoseTurn(5f, 5f, 3));
        }

        [Fact]
        public void AVehicleIsGivenUpLaterThanTheOriginalDid()
        {
            // The original walked out after the third recovery inside 30 s.
            Assert.True(DrivingRules.RecoveriesBeforeGivingUp > 3);
            Assert.True(DrivingRules.RecoveryMemorySeconds >= 30f);
        }
    }
}
