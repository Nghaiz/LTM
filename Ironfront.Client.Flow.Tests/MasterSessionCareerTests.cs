using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client;
using Xunit;

namespace Ironfront.Client.Flow.Tests
{
    /// <summary>
    /// The career requests the ranking and achievement pages make (owner's list of 2026-10-09,
    /// item 4): asked only when signed in, failing into their own error and not the screen's.
    /// </summary>
    public sealed class MasterSessionCareerTests
    {
        private static (FakeMasterClient Master, MasterSession Session, GameFlowController Flow) Rig()
        {
            var master = new FakeMasterClient();
            var flow = new GameFlowController();
            var session = new MasterSession(master, flow, new FakeTransportClient(), _ => 1);
            return (master, session, flow);
        }

        private static async Task<(FakeMasterClient, MasterSession)> SignedInAsync()
        {
            (FakeMasterClient master, MasterSession session, GameFlowController flow) = Rig();
            flow.Transition(GameFlowState.LoginScreen);
            Assert.True(await session.LoginAsync("tester", "hunter2"));
            return (master, session);
        }

        [Fact]
        public async Task NothingIsAskedBeforeSigningIn()
        {
            (FakeMasterClient master, MasterSession session, _) = Rig();
            master.ThrowOnNextCall = new InvalidOperationException("the master must not be asked");

            Assert.Null(await session.GetLeaderboardAsync());
            Assert.Null(await session.GetAchievementsAsync());
            Assert.Contains("Sign in", session.CareerError);
            Assert.NotNull(master.ThrowOnNextCall);
        }

        [Fact]
        public async Task TheRankingComesBackAsTheMasterSentIt()
        {
            (FakeMasterClient master, MasterSession session) = await SignedInAsync();
            master.NextLeaderboard = new Leaderboard
            {
                Players = 3,
                Rows = new[] { new LeaderboardRow { Rank = 1, PlayerId = 7, Name = "ace", Score = 90 } },
                You = new LeaderboardRow { Rank = 2, PlayerId = 42, Name = "tester", Score = 40 },
            };

            Leaderboard? board = await session.GetLeaderboardAsync();

            Assert.NotNull(board);
            Assert.Equal("ace", board!.Rows[0].Name);
            Assert.Equal(2, board.You!.Rank);
            Assert.Equal(string.Empty, session.CareerError);
        }

        [Fact]
        public async Task AClaimSendsTheIdsAndAnEmptyOneOnlyAsks()
        {
            (FakeMasterClient master, MasterSession session) = await SignedInAsync();

            Assert.NotNull(await session.ClaimAchievementsAsync(new[] { "basic_training", "student_of_war" }));
            Assert.Equal(new[] { "basic_training", "student_of_war" }, master.LastClaim);

            Assert.NotNull(await session.ClaimAchievementsAsync(Array.Empty<string>()));
            Assert.Equal(2, master.LastClaim!.Count);
        }

        [Fact]
        public async Task AFailedRequestNamesItsOwnErrorAndLeavesTheScreensAlone()
        {
            (FakeMasterClient master, MasterSession session) = await SignedInAsync();
            string? screenError = null;
            session.OnError += message => screenError = message;
            master.ThrowOnNextCall = new MasterServerException((int)ErrorCode.SessionExpired, "expired");

            Assert.Null(await session.GetAchievementsAsync());

            Assert.NotEqual(string.Empty, session.CareerError);
            Assert.Null(screenError);
            Assert.Equal(string.Empty, session.LastError);
        }

        [Fact]
        public async Task TheMastersUnlockPushIsPassedOn()
        {
            (FakeMasterClient master, MasterSession session) = await SignedInAsync();
            var heard = new List<string>();
            session.OnAchievementsUnlocked += ids => heard.AddRange(ids);

            master.PushUnlocked("first_blood", "victory");
            master.PushUnlocked();

            Assert.Equal(new[] { "first_blood", "victory" }, heard);
        }
    }
}
