using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Server-authoritative capture points: the original game's <c>CapturePoint.UpdateOwner</c>
    /// (tmp/recovered/src/Assembly-CSharp/CapturePoint.cs), and trap 3 (not spamming them).
    /// </summary>
    public sealed class CapturePointTests
    {
        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        private static CapturePointState Point(float captureSpeed = 0.05f)
            => new CapturePointState(0, Vec3.Zero, radius: 10f, captureSpeed: captureSpeed);

        private static CapturePointState OwnedBy(byte team, float captureSpeed = 0.05f)
        {
            CapturePointState point = Point(captureSpeed);
            point.AdoptOpeningOwner(team == TeamId.Team0 ? -1f : 1f);
            return point;
        }

        private static readonly MatchRules Rules = MatchRules.Default;

        /// <summary>One whole step: a second of ticks at the sim rate.</summary>
        private static bool Step(CapturePointState point, int team0, int team1)
        {
            bool asked = false;
            for (int i = 0; i < ProtocolConstants.SIM_TICK_RATE; i++)
                asked |= point.Tick(team0, team1, Tick, Rules);
            return asked;
        }

        // ------------------------------------------------------------------ the original rule

        [Fact]
        public void AnEmptyPointDoesNotMove()
        {
            CapturePointState point = Point();

            for (int i = 0; i < 10; i++) Step(point, 0, 0);

            Assert.Equal(0f, point.Owner);
            Assert.Equal(TeamId.None, point.OwningTeam);
        }

        [Fact]
        public void AnOwnedPointOpensWithItsFlagAtTheTopAndANeutralOneWithNone()
        {
            Assert.Equal(1f, OwnedBy(TeamId.Team1).Control);
            Assert.Equal(TeamId.Team1, OwnedBy(TeamId.Team1).OwningTeam);
            Assert.Equal(-1f, OwnedBy(TeamId.Team0).Owner);

            CapturePointState neutral = Point();
            neutral.AdoptOpeningOwner(0f);
            Assert.Equal(0f, neutral.Control);
            Assert.Equal(TeamId.None, neutral.OwningTeam);
        }

        [Fact]
        public void NothingMovesUntilTheFirstWholeSecond()
        {
            // InvokeRepeating("UpdateOwner", 1f, 1f): the first step is one second in, and a
            // step is a step whatever the tick rate of the caller.
            CapturePointState point = Point();

            for (int i = 0; i < ProtocolConstants.SIM_TICK_RATE - 1; i++) point.Tick(0, 1, Tick, Rules);
            Assert.Equal(TeamId.None, point.OwningTeam);

            point.Tick(0, 1, Tick, Rules);
            Assert.Equal(TeamId.Team1, point.OwningTeam);
        }

        [Fact]
        public void ANeutralPointIsTakenOnTheFirstStepWithItsFlagAtTheBottom()
        {
            // control 0 minus a step is <= 0, so SetOwner runs at once and control = 0.01.
            CapturePointState point = Point();

            Step(point, 1, 0);

            Assert.Equal(TeamId.Team0, point.OwningTeam);
            Assert.Equal(CapturePointState.ControlAfterCapture, point.Control, 5);
        }

        [Fact]
        public void TheOwnersFlagRisesByTheLeadEachStepAndStopsAtTheTop()
        {
            CapturePointState point = Point(captureSpeed: 0.05f);
            Step(point, 0, 2);                             // taken: 0.01

            Step(point, 0, 2);
            Assert.Equal(0.11f, point.Control, 4);         // 0.01 + 2 x 0.05

            for (int i = 0; i < 20; i++) Step(point, 0, 2);
            Assert.Equal(1f, point.Control);
        }

        [Fact]
        public void AnAttackerDrainsTheFlagAndThePointStaysTheOwnersUntilItReachesZero()
        {
            // The defect this port closes: the old slider handed the point to nobody as soon as
            // it dropped below 0.9. In the original the owner keeps it all the way down.
            CapturePointState point = OwnedBy(TeamId.Team1, captureSpeed: 0.05f);

            for (int i = 0; i < 19; i++)
            {
                Step(point, 1, 0);
                Assert.Equal(TeamId.Team1, point.OwningTeam);
            }
            Assert.Equal(0.05f, point.Control, 4);         // 1 - 19 x 0.05

            Step(point, 1, 0);
            Assert.Equal(TeamId.Team0, point.OwningTeam);
            Assert.Equal(CapturePointState.ControlAfterCapture, point.Control, 5);
        }

        [Fact]
        public void TheOwnerStandingOnItPushesTheFlagBackUp()
        {
            CapturePointState point = OwnedBy(TeamId.Team0, captureSpeed: 0.05f);
            for (int i = 0; i < 4; i++) Step(point, 0, 1);  // 0.8

            Step(point, 2, 0);

            Assert.Equal(TeamId.Team0, point.OwningTeam);
            Assert.Equal(0.9f, point.Control, 4);
        }

        [Theory]
        // team 0 leads: the original never records team 1's count, so the lead is team 0's
        // whole headcount. team 1 leads: the true difference. A tie goes to team 0 in full.
        [InlineData(3, 2, 0, 3)]
        [InlineData(2, 3, 1, 1)]
        [InlineData(2, 2, 0, 2)]
        [InlineData(0, 4, 1, 4)]
        public void TheLeadIsTheOriginalsRunnerUpArithmeticQuirkAndAll(
            int team0, int team1, int expectedLeader, int expectedLead)
        {
            // A point owned by neither side's leader: it drains by exactly lead x speed.
            byte defender = expectedLeader == 0 ? TeamId.Team1 : TeamId.Team0;
            CapturePointState point = OwnedBy(defender, captureSpeed: 0.05f);

            Step(point, team0, team1);

            Assert.Equal(1f - expectedLead * 0.05f, point.Control, 4);
            Assert.Equal(defender, point.OwningTeam);
        }

        [Fact]
        public void ThereIsNoHeadcountCap()
        {
            // The old slider capped the lead at four bodies. The original has no cap.
            CapturePointState point = OwnedBy(TeamId.Team0, captureSpeed: 0.05f);

            Step(point, 0, 16);

            Assert.Equal(0.2f, point.Control, 4);          // 1 - 16 x 0.05, not 1 - 4 x 0.05
            Assert.Equal(TeamId.Team0, point.OwningTeam);
        }

        [Fact]
        public void AnUncapturablePointNeverChanges()
        {
            // canBeCaptured == false reaches the server as a capture speed of zero.
            CapturePointState hq = OwnedBy(TeamId.Team1, captureSpeed: 0f);

            for (int i = 0; i < 30; i++) Assert.False(Step(hq, 8, 0));

            Assert.Equal(TeamId.Team1, hq.OwningTeam);
            Assert.Equal(1f, hq.Control);
        }

        [Fact]
        public void AnEqualStandoffStillMovesTheFlagTowardTeamZeroButIsContested()
        {
            // A tie is not a standoff in the original: team 0 is found first and leads by its
            // whole count. Contested (both teams present) is the wire's flag and is set.
            CapturePointState point = OwnedBy(TeamId.Team1, captureSpeed: 0.05f);

            Step(point, 3, 3);

            Assert.Equal(0.85f, point.Control, 4);
            Assert.True(point.IsContested);
        }

        // ------------------------------------------------------------------ trap 3

        [Fact]
        public void ThePointDoesNotAskToBeSentOnEveryTick()
        {
            // 5 points x 30 Hz x 16 clients = 2400 messages a second if every tick sends.
            CapturePointState point = OwnedBy(TeamId.Team0, captureSpeed: 0.05f);

            int sends = 0;
            for (int i = 0; i < ProtocolConstants.SIM_TICK_RATE * 3; i++)
            {
                if (!point.Tick(0, 1, Tick, Rules)) continue;
                sends++;
                point.MarkSent();
            }

            // One step a second, one message per step that moved the byte.
            Assert.Equal(3, sends);
        }

        [Fact]
        public void TheSendTestIsOnTheQuantizedValueNotTheFloat()
        {
            // A float that has moved but still packs to the same signed byte is a message that
            // changes nothing on the client.
            CapturePointState point = OwnedBy(TeamId.Team1, captureSpeed: 0.001f);

            bool asked = Step(point, 1, 0);

            Assert.True(point.Control < 1f, "the float should have moved");
            Assert.False(asked, "a sub-quantum move must not ask for a message");
        }

        [Fact]
        public void MarkSentIsWhatStopsTheResend()
        {
            CapturePointState point = OwnedBy(TeamId.Team0, captureSpeed: 0.2f);

            Assert.True(Step(point, 0, 1));

            // Not marked: the last-sent value still trails the live one, so a failed send is
            // retried rather than lost. That is the whole reason ToMessage and MarkSent are
            // separate calls.
            Assert.NotEqual(point.LastSentQ, point.ToMessage().OwnerQ);

            point.MarkSent();
            Assert.Equal(point.LastSentQ, point.ToMessage().OwnerQ);
        }

        // ------------------------------------------------------------------ geometry

        [Fact]
        public void ContainsUsesTheRadiusInThreeDimensions()
        {
            var point = new CapturePointState(0, new Vec3(10f, 0f, 10f), radius: 5f);

            Assert.True(point.Contains(new Vec3(10f, 0f, 10f)));
            Assert.True(point.Contains(new Vec3(13f, 0f, 12f)));
            Assert.False(point.Contains(new Vec3(20f, 0f, 10f)));
            Assert.False(point.Contains(new Vec3(10f, 20f, 10f)));
        }

        [Fact]
        public void ARadiusOfZeroIsRejected()
            => Assert.Throws<ArgumentOutOfRangeException>(
                () => new CapturePointState(0, Vec3.Zero, radius: 0f));

        [Fact]
        public void DeadActorsDoNotCapture()
        {
            var point = new CapturePointState(0, Vec3.Zero, 10f, captureSpeed: 0.05f);
            var match = new MatchStateMachine(
                new MatchRules { MinPlayersToStart = 1, WarmupSeconds = 0f }, point);

            var corpses = new[]
            {
                new ActorPresence(Vec3.Zero, TeamId.Team0, isAlive: false),
                new ActorPresence(Vec3.Zero, TeamId.Team0, isAlive: false),
            };

            for (int i = 0; i < 90; i++) match.Tick(Tick, 1, corpses);

            Assert.Equal(TeamId.None, point.OwningTeam);
        }

        [Fact]
        public void OnlyActorsInsideTheRadiusCount()
        {
            var point = new CapturePointState(0, Vec3.Zero, 10f, captureSpeed: 0.05f);
            var match = new MatchStateMachine(
                new MatchRules { MinPlayersToStart = 1, WarmupSeconds = 0f }, point);

            var actors = new[]
            {
                new ActorPresence(new Vec3(200f, 0f, 0f), TeamId.Team0, true),
                new ActorPresence(Vec3.Zero, TeamId.Team1, true),
            };

            for (int i = 0; i < 90; i++) match.Tick(Tick, 1, actors);

            Assert.Equal(TeamId.Team1, point.OwningTeam);
        }

        // ------------------------------------------------------------------ reset

        [Fact]
        public void ResetReturnsThePointToItsOpeningOwnerAndForgetsWhatWasSent()
        {
            CapturePointState point = OwnedBy(TeamId.Team0, captureSpeed: 0.2f);
            for (int i = 0; i < 8; i++) Step(point, 0, 4);
            point.MarkSent();
            Assert.Equal(TeamId.Team1, point.OwningTeam);

            point.Reset();

            Assert.Equal(TeamId.Team0, point.OwningTeam);
            Assert.Equal(1f, point.Control);
            Assert.False(point.IsContested);

            // The send threshold measures from the reset value afterwards, and the reset itself
            // is sent regardless: clients still connected are drawing the last match's end state
            // (CapturePointResetResendTests).
            Assert.Equal(point.ToMessage().OwnerQ, point.LastSentQ);
            Assert.True(point.ResendDue);
        }

        [Fact]
        public void ResetRestartsTheStepClock()
        {
            CapturePointState point = Point();
            for (int i = 0; i < ProtocolConstants.SIM_TICK_RATE / 2; i++) point.Tick(0, 1, Tick, Rules);

            point.Reset();

            // Half a second before the reset does not count toward the first step after it.
            for (int i = 0; i < ProtocolConstants.SIM_TICK_RATE / 2; i++) point.Tick(0, 1, Tick, Rules);
            Assert.Equal(TeamId.None, point.OwningTeam);
        }

        // ------------------------------------------------------------------ the wire message

        [Theory]
        [InlineData(-1f, -100)]
        [InlineData(-0.5f, -50)]
        [InlineData(0f, 0)]
        [InlineData(0.335f, 34)]
        [InlineData(1f, 100)]
        public void OwnershipQuantizesToOneSignedByte(float owner, int expected)
            => Assert.Equal((sbyte)expected, CapturePointMessage.PackOwner(owner));

        [Fact]
        public void OutOfRangeOwnershipIsClampedRatherThanWrapped()
        {
            Assert.Equal((sbyte)100, CapturePointMessage.PackOwner(50f));
            Assert.Equal((sbyte)(-100), CapturePointMessage.PackOwner(-50f));
            Assert.Equal((sbyte)0, CapturePointMessage.PackOwner(float.NaN));
        }

        [Fact]
        public void ACapturePointSurvivesTheWire()
        {
            var sent = new CapturePointMessage(3, -95, CaptureFlags.Contested);
            Span<byte> buffer = stackalloc byte[CapturePointMessage.Size];

            Assert.Equal(CapturePointMessage.Size, sent.Write(buffer));
            Assert.True(CapturePointMessage.TryParse(buffer, out CapturePointMessage received));

            Assert.Equal(3, received.PointId);
            Assert.Equal(-95, received.OwnerQ);
            Assert.True(received.IsContested);
            Assert.Equal(TeamId.Team0, received.OwningTeam);
        }

        [Theory]
        [InlineData(-100, TeamId.Team0)]
        [InlineData(-1, TeamId.Team0)]
        [InlineData(0, TeamId.None)]
        [InlineData(1, TeamId.Team1)]
        [InlineData(100, TeamId.Team1)]
        public void TheOwnerIsTheSignOfTheByte(int ownerQ, byte expected)
            => Assert.Equal(expected, new CapturePointMessage(0, (sbyte)ownerQ, CaptureFlags.None).OwningTeam);

        [Fact]
        public void AnOwnedPointNeverPacksToNeutral()
        {
            // 1 - 3 x 0.3333 = 0.0001: still team 1's, and a byte of 0 would say nobody's.
            CapturePointState point = OwnedBy(TeamId.Team1, captureSpeed: 0.3333f);
            for (int i = 0; i < 3; i++) Step(point, 1, 0);

            Assert.Equal(TeamId.Team1, point.OwningTeam);
            Assert.True(point.Control > 0f && point.Control < 0.005f);
            Assert.Equal((sbyte)1, point.ToMessage().OwnerQ);
        }

        [Fact]
        public void AnUnknownFlagBitIsRejectedRatherThanMaskedOff()
        {
            Span<byte> buffer = stackalloc byte[CapturePointMessage.Size];
            new CapturePointMessage(0, 0, CaptureFlags.None).Write(buffer);
            buffer[2] = 0x80;

            Assert.False(CapturePointMessage.TryParse(buffer, out _));
        }

        [Fact]
        public void AnOutOfRangeOwnershipByteIsRejected()
        {
            Span<byte> buffer = stackalloc byte[CapturePointMessage.Size];
            new CapturePointMessage(0, 0, CaptureFlags.None).Write(buffer);
            buffer[1] = unchecked((byte)(sbyte)-128);

            Assert.False(CapturePointMessage.TryParse(buffer, out _));
        }

        [Fact]
        public void TheServerAndTheClientReadTheOwnerFromTheSameByte()
        {
            // The server's kill multiplier counts OwningTeam and the client colours a flag on the
            // same property of the same message, freshly captured points included.
            CapturePointState point = OwnedBy(TeamId.Team0, captureSpeed: 0.05f);
            for (int i = 0; i < 20; i++)
            {
                Step(point, 0, 1);
                Assert.Equal(point.OwningTeam, point.ToMessage().OwningTeam);
            }
            Assert.Equal(TeamId.Team1, point.ToMessage().OwningTeam);
        }
    }
}
