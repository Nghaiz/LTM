using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Client;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The killfeed keeps up with a busy match: nothing is dropped, every line is on screen long
    /// enough to read, a backlog is folded into "+N more" rather than lost, and the viewing player's
    /// own lines never wait behind anyone else's. Owner's report of 2026-09-30.
    /// </summary>
    public sealed class KillfeedFlowTests
    {
        private static KillfeedEntry Death(ushort victim, ushort killer, float at)
            => KillfeedEntry.From(new DeathMessage(victim, killer, CauseOfDeath.Bullet, 0, 0, 0, 0), at);

        [Fact]
        public void ABurstBiggerThanTheFeed_ShowsEveryLine_NoneIsDropped()
        {
            var feed = new KillfeedModel(capacity: 4);
            var seen = new HashSet<ushort>();

            for (ushort i = 1; i <= 10; i++) feed.Push(Death(i, 50, 0f));

            for (float t = 0f; t <= 30f; t += 0.05f)
            {
                feed.Advance(t);
                for (int row = 0; row < feed.Count; row++) seen.Add(feed[row].VictimActorId);
            }

            for (ushort i = 1; i <= 10; i++) Assert.Contains(i, seen);
            Assert.Equal(0, feed.Waiting);
            Assert.Equal(0, feed.FoldedLines);
        }

        [Fact]
        public void AWaitingLine_DoesNotTakeAPlaceBeforeTheOldestLineHadItsMinimum()
        {
            var feed = new KillfeedModel(capacity: 2);
            feed.Push(Death(1, 50, 0f));
            feed.Push(Death(2, 50, 0f));
            feed.Push(Death(3, 50, 0f));

            feed.Advance(KillfeedModel.MinHoldSeconds - 0.1f);
            Assert.Equal(1, feed.Waiting);
            Assert.Equal(2, feed[0].VictimActorId);
            Assert.Equal(1, feed[1].VictimActorId);

            feed.Advance(KillfeedModel.MinHoldSeconds + 0.01f);
            Assert.Equal(0, feed.Waiting);
            Assert.Equal(3, feed[0].VictimActorId);
            Assert.Equal(2, feed[1].VictimActorId);   // the OLDER line gave up its place
        }

        [Fact]
        public void WaitingLines_AreLetInOneInterval_Apart()
        {
            var feed = new KillfeedModel(capacity: 2);
            for (ushort i = 1; i <= 4; i++) feed.Push(Death(i, 50, 0f));

            float release = KillfeedModel.MinHoldSeconds;
            feed.Advance(release);
            Assert.Equal(1, feed.Waiting);
            Assert.Equal(3, feed[0].VictimActorId);

            feed.Advance(release + KillfeedModel.ReleaseIntervalSeconds * 0.5f);
            Assert.Equal(1, feed.Waiting);

            feed.Advance(release + KillfeedModel.ReleaseIntervalSeconds * 1.01f);
            Assert.Equal(0, feed.Waiting);
            Assert.Equal(4, feed[0].VictimActorId);
        }

        [Fact]
        public void ALineThatWaitsTooLong_IsFoldedIntoOneOverflowLine_AndEveryLineIsAccountedFor()
        {
            var feed = new KillfeedModel(capacity: 1);
            feed.HoldSeconds = 100f;   // lines leave only by giving up their place

            for (ushort i = 1; i <= 30; i++) feed.Push(Death(i, 50, 0f));

            var seen = new HashSet<long>();
            int deathsShown = 0;
            long foldedShown = 0;
            int overflowLines = 0;

            for (float t = 0f; t <= 200f; t += 0.05f)
            {
                feed.Advance(t);
                for (int row = 0; row < feed.Count; row++)
                {
                    if (!seen.Add(feed[row].Sequence)) continue;

                    if (feed[row].Kind == KillfeedKind.Overflow)
                    {
                        overflowLines++;
                        foldedShown += feed[row].SubjectIndex;
                    }
                    else
                    {
                        deathsShown++;
                    }
                }
            }

            // Every one of the thirty either reached the screen or is counted by the one line
            // that stands for it, and the two counts cannot drift apart.
            Assert.Equal(1, overflowLines);
            Assert.Equal(feed.FoldedLines, foldedShown);
            Assert.Equal(30, deathsShown + foldedShown);
            Assert.True(foldedShown > 0);
        }

        [Fact]
        public void TheViewingPlayersLine_JumpsTheQueue_AndIsNeverFolded()
        {
            var feed = new KillfeedModel(capacity: 1);
            feed.HoldSeconds = 100f;

            for (ushort i = 1; i <= 10; i++) feed.Push(Death(i, 50, 0f));
            feed.Push(Death(40, 41, 0f).WithPriority(true));

            feed.Advance(KillfeedModel.MinHoldSeconds);
            Assert.Equal(40, feed[0].VictimActorId);

            // A priority line still waiting past the maximum wait is kept, not folded.
            var busy = new KillfeedModel(capacity: 1);
            busy.HoldSeconds = 100f;
            busy.Push(Death(1, 50, 0f).WithPriority(true));
            busy.Push(Death(2, 50, 0f).WithPriority(true));
            busy.Push(Death(3, 50, 0f).WithPriority(true));

            busy.Advance(KillfeedModel.MaxWaitSeconds + 1f);
            Assert.Equal(0, busy.FoldedLines);
        }

        [Fact]
        public void TheRevision_MovesOnPushExpiryAndRelease_AndNotOnAQueuedPush()
        {
            var feed = new KillfeedModel(capacity: 1);
            long start = feed.Revision;

            feed.Push(Death(1, 50, 0f));
            long afterPush = feed.Revision;
            Assert.NotEqual(start, afterPush);

            feed.Push(Death(2, 50, 0f));
            Assert.Equal(afterPush, feed.Revision);

            feed.Advance(KillfeedModel.MinHoldSeconds);
            Assert.NotEqual(afterPush, feed.Revision);
        }

        [Fact]
        public void AFullQueue_FoldsItsOldestOrdinaryLines_RatherThanLosingTheNewOne()
        {
            var feed = new KillfeedModel(capacity: 1, queueCapacity: 4);
            feed.HoldSeconds = 100f;

            for (ushort i = 1; i <= 12; i++) feed.Push(Death(i, 50, 0f));

            // One on screen and four waiting: the summary of #2 to #9 first, then #10 to #12.
            Assert.Equal(4, feed.Waiting);
            Assert.Equal(8, feed.FoldedLines);

            feed.Advance(KillfeedModel.MinHoldSeconds);
            Assert.Equal(KillfeedKind.Overflow, feed[0].Kind);
            Assert.Equal(8, feed[0].SubjectIndex);

            feed.Advance(KillfeedModel.MinHoldSeconds * 2f);
            Assert.Equal(10, feed[0].VictimActorId);
        }

        [Fact]
        public void MatchEvents_AreLinesOfTheirOwnKind_AndAreNotCountedAsKills()
        {
            var feed = new KillfeedModel();
            feed.Push(KillfeedEntry.Flag(true, TeamId.Team0, 2, 0f));
            feed.Push(KillfeedEntry.Roster(true, 5, TeamId.Team1, 0f));
            feed.Push(KillfeedEntry.Round(false, TeamId.Team1, 0f));

            Assert.Equal(3, feed.Count);
            Assert.Equal(0, feed.TotalKills);
            Assert.Equal(KillfeedKind.RoundEnded, feed[0].Kind);
            Assert.Equal(KillfeedKind.PlayerJoined, feed[1].Kind);
            Assert.Equal(5, feed[1].SubjectActorId);
            Assert.Equal(KillfeedKind.FlagCaptured, feed[2].Kind);
            Assert.Equal(2, feed[2].SubjectIndex);
            Assert.Equal(TeamId.Team0, feed[2].SubjectTeam);
        }

        [Fact]
        public void ADeathFromAServerThatSentTheRange_KeepsIt()
        {
            var message = new DeathMessage(
                9, 3, CauseOfDeath.Bullet, 0, 0, 0, (byte)HitboxType.Head,
                WeaponIds.RECON_LRR, VehicleIds.NONE, DeathDetail.None, 212);

            KillfeedEntry entry = KillfeedEntry.From(in message, 0f);
            Assert.Equal(212, entry.DistanceMetres);
            Assert.True(entry.Headshot);
            Assert.True(entry.IsScoredKill);
        }

        [Fact]
        public void SidesResolvedWhenTheDeathArrived_AreKeptOnTheLine()
        {
            KillfeedEntry entry = Death(9, 3, 0f).WithTeams(TeamId.Team1, TeamId.Team0);

            var feed = new KillfeedModel();
            feed.Push(in entry);

            Assert.Equal(TeamId.Team1, feed[0].KillerTeam);
            Assert.Equal(TeamId.Team0, feed[0].VictimTeam);
        }
    }
}
