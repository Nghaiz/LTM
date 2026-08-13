using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-03 task 2: server-authoritative capture points, and trap 3 (not spamming them).
    /// </summary>
    public sealed class CapturePointTests
    {
        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        private static CapturePointState Point(float captureSpeed = 0.2f)
            => new CapturePointState(0, Vec3.Zero, radius: 10f, captureSpeed: captureSpeed);

        private static readonly MatchRules Rules = MatchRules.Default;

        // ------------------------------------------------------------------ ownership

        [Fact]
        public void AnEmptyPointDoesNotMove()
        {
            CapturePointState point = Point();

            for (int i = 0; i < 100; i++) point.Tick(0, 0, Tick, Rules);

            Assert.Equal(0f, point.Owner);
            Assert.Equal(TeamId.None, point.OwningTeam);
        }

        [Fact]
        public void AnEqualStandoffDoesNotMoveTheBarButIsContested()
        {
            CapturePointState point = Point();

            for (int i = 0; i < 100; i++) point.Tick(3, 3, Tick, Rules);

            Assert.Equal(0f, point.Owner);
            Assert.True(point.IsContested);
        }

        [Fact]
        public void TeamZeroPushesOwnershipNegativeAndTeamOnePositive()
        {
            CapturePointState zero = Point(captureSpeed: 1f);
            CapturePointState one = Point(captureSpeed: 1f);

            for (int i = 0; i < 30; i++)
            {
                zero.Tick(1, 0, Tick, Rules);
                one.Tick(0, 1, Tick, Rules);
            }

            Assert.True(zero.Owner < 0f);
            Assert.True(one.Owner > 0f);
        }

        [Fact]
        public void OwnershipSaturatesAtTheEnds()
        {
            CapturePointState point = Point(captureSpeed: 5f);

            for (int i = 0; i < 300; i++) point.Tick(4, 0, Tick, Rules);

            Assert.Equal(-1f, point.Owner);
            Assert.Equal(TeamId.Team0, point.OwningTeam);
        }

        [Fact]
        public void MoreAttackersCaptureFasterUpToTheCap()
        {
            float OwnerAfterOneSecond(int attackers)
            {
                CapturePointState point = Point(captureSpeed: 0.05f);
                for (int i = 0; i < ProtocolConstants.SIM_TICK_RATE; i++)
                    point.Tick(0, attackers, Tick, Rules);
                return point.Owner;
            }

            float one = OwnerAfterOneSecond(1);
            float four = OwnerAfterOneSecond(4);
            float sixteen = OwnerAfterOneSecond(16);

            Assert.True(four > one, "four attackers should be faster than one");

            // Capped, or sixteen players walk onto a point and take it instantly.
            Assert.Equal(four, sixteen, 4);
        }

        [Fact]
        public void OwnershipCrossesTheThresholdOnlyNearTheEnd()
        {
            CapturePointState point = Point(captureSpeed: 1f);

            for (int i = 0; i < 20; i++) point.Tick(0, 1, Tick, Rules);
            Assert.True(point.Owner > 0f && point.Owner < CapturePointMessage.OwnedThreshold);

            // Partial progress is not ownership — a point at 0.5 must not bleed the enemy.
            Assert.Equal(TeamId.None, point.OwningTeam);

            for (int i = 0; i < 40; i++) point.Tick(0, 1, Tick, Rules);
            Assert.Equal(TeamId.Team1, point.OwningTeam);
        }

        // ------------------------------------------------------------------ trap 3

        [Fact]
        public void ThePointDoesNotAskToBeSentOnEveryTick()
        {
            // 5 points x 30 Hz x 16 clients = 2400 messages a second if every tick sends.
            CapturePointState point = Point(captureSpeed: 0.2f);

            int sends = 0;
            for (int i = 0; i < ProtocolConstants.SIM_TICK_RATE; i++)
            {
                if (!point.Tick(0, 1, Tick, Rules)) continue;
                sends++;
                point.MarkSent();
            }

            // At 0.2/s the bar moves 0.2 in a second, and the send threshold is 2%, so the
            // ceiling is ten messages plus at most one for the ownership flip — a third of the
            // tick rate, and a twentieth of what sending unconditionally would cost.
            Assert.True(sends <= 11, $"{sends} sends in one second of capturing");
            Assert.True(sends > 0, "a moving bar must be sent at least once");
        }

        [Fact]
        public void TheSendTestIsOnTheQuantizedValueNotTheFloat()
        {
            // A float that has moved but still packs to the same signed byte is a message that
            // changes nothing on the client. Same reasoning as WorldSnapshot storing quantized
            // entries in phase 01.
            CapturePointState point = Point(captureSpeed: 0.001f);

            bool asked = false;
            for (int i = 0; i < 5; i++) asked |= point.Tick(0, 1, Tick, Rules);

            Assert.True(point.Owner > 0f, "the float should have moved");
            Assert.False(asked, "a sub-quantum move must not ask for a message");
        }

        [Fact]
        public void MarkSentIsWhatStopsTheResend()
        {
            CapturePointState point = Point(captureSpeed: 1f);

            Assert.True(point.Tick(0, 1, Tick, Rules));

            // Not marked: the last-sent value still trails the live one, so a failed send is
            // retried rather than lost. That is the whole reason ToMessage and MarkSent are
            // separate calls.
            Assert.NotEqual(point.LastSentQ, CapturePointMessage.PackOwner(point.Owner));

            point.MarkSent();
            Assert.Equal(point.LastSentQ, CapturePointMessage.PackOwner(point.Owner));
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
            var point = new CapturePointState(0, Vec3.Zero, 10f, captureSpeed: 1f);
            var match = new MatchStateMachine(
                new MatchRules { MinPlayersToStart = 1, WarmupSeconds = 0f }, point);

            var corpses = new[]
            {
                new ActorPresence(Vec3.Zero, TeamId.Team0, isAlive: false),
                new ActorPresence(Vec3.Zero, TeamId.Team0, isAlive: false),
            };

            for (int i = 0; i < 60; i++) match.Tick(Tick, 1, corpses);

            Assert.Equal(0f, point.Owner);
        }

        [Fact]
        public void OnlyActorsInsideTheRadiusCount()
        {
            var point = new CapturePointState(0, Vec3.Zero, 10f, captureSpeed: 1f);
            var match = new MatchStateMachine(
                new MatchRules { MinPlayersToStart = 1, WarmupSeconds = 0f }, point);

            var actors = new[]
            {
                new ActorPresence(new Vec3(200f, 0f, 0f), TeamId.Team0, true),
                new ActorPresence(Vec3.Zero, TeamId.Team1, true),
            };

            for (int i = 0; i < 60; i++) match.Tick(Tick, 1, actors);

            Assert.True(point.Owner > 0f, "the actor standing on it should be taking it");
        }

        // ------------------------------------------------------------------ reset

        [Fact]
        public void ResetReturnsThePointToNeutralAndForgetsWhatWasSent()
        {
            CapturePointState point = Point(captureSpeed: 5f);
            for (int i = 0; i < 60; i++) point.Tick(0, 4, Tick, Rules);
            point.MarkSent();

            point.Reset();

            Assert.Equal(0f, point.Owner);
            Assert.False(point.IsContested);

            // LastSentQ must reset too. Leaving it at the old value means a point that ends one
            // match at neutral and starts the next at neutral never sends its opening state to
            // the clients that joined in between.
            Assert.Equal(CapturePointMessage.PackOwner(0f), point.LastSentQ);
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
        public void TheServerAndTheClientApplyTheSameOwnershipThreshold()
        {
            // The one number both sides need to agree on. The server bleeds tickets on
            // OwningTeam and the client colours a flag on the same property of the same
            // message, so there is one implementation and no chance of two thresholds.
            CapturePointState point = Point(captureSpeed: 5f);
            for (int i = 0; i < 300; i++) point.Tick(4, 0, Tick, Rules);

            CapturePointMessage message = point.ToMessage();
            Assert.Equal(point.OwningTeam, message.OwningTeam);
        }
    }
}
