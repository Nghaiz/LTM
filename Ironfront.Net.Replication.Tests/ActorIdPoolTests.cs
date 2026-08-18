using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-03 trap 2: an actor id must not come back until every packet naming its previous
    /// occupant is gone.
    /// </summary>
    public sealed class ActorIdPoolTests
    {
        [Fact]
        public void IdsStartAtOneBecauseZeroMeansUnassigned()
        {
            var pool = new ActorIdPool(capacity: 4);

            Assert.True(pool.TryAcquire(0f, out ushort id));
            Assert.Equal(ActorIdPool.FirstId, id);
            Assert.NotEqual(0, id);
        }

        [Fact]
        public void AReleasedIdIsNotHandedOutAgainImmediately()
        {
            var pool = new ActorIdPool(capacity: 2, quarantineSeconds: 5f);

            pool.TryAcquire(0f, out ushort first);
            pool.TryAcquire(0f, out ushort second);
            pool.Release(first, 0f);

            // Both live ids are gone and the released one is cooling, so there is nothing to
            // hand out — which is the point. Reissuing `first` here is exactly the bug: a
            // client with a stale snapshot in flight applies the dead actor's state to the
            // new one, and it looks like a player who teleported and lost health.
            Assert.False(pool.TryAcquire(1f, out _));
            Assert.Equal(1, pool.QuarantinedCount);
            Assert.NotEqual(first, second);
        }

        [Fact]
        public void TheQuarantineExpiresExactlyAtItsDeadline()
        {
            var pool = new ActorIdPool(capacity: 1, quarantineSeconds: 5f);

            pool.TryAcquire(0f, out ushort id);
            pool.Release(id, 10f);

            Assert.False(pool.TryAcquire(14.999f, out _));
            Assert.True(pool.TryAcquire(15f, out ushort reissued));
            Assert.Equal(id, reissued);
        }

        [Fact]
        public void TheQuarantineOutlastsFragmentReassembly()
        {
            // The bound the 5 s default is chosen against: a fragment of an old snapshot can
            // legitimately still be waiting for reassembly for FRAGMENT_TIMEOUT_MS.
            var pool = new ActorIdPool(capacity: 1);
            pool.TryAcquire(0f, out ushort id);
            pool.Release(id, 0f);

            float fragmentTimeoutSeconds = ProtocolConstants.FRAGMENT_TIMEOUT_MS / 1000f;
            Assert.False(pool.TryAcquire(fragmentTimeoutSeconds, out _));
        }

        [Fact]
        public void IdsRotateRatherThanReusingTheSameFewOverAndOver()
        {
            var pool = new ActorIdPool(capacity: 4, quarantineSeconds: 1f);
            var seen = new List<ushort>();

            for (int round = 0; round < 4; round++)
            {
                float now = round * 2f;
                Assert.True(pool.TryAcquire(now, out ushort id));
                seen.Add(id);
                pool.Release(id, now);
            }

            // A stack would hand back the same id every round, concentrating whatever residual
            // stale-packet window remains onto one id. A queue spreads it.
            Assert.Equal(4, new HashSet<ushort>(seen).Count);
        }

        [Fact]
        public void ADoubleReleaseIsRejectedRatherThanDuplicatingTheId()
        {
            var pool = new ActorIdPool(capacity: 4);
            pool.TryAcquire(0f, out ushort id);

            Assert.True(pool.Release(id, 0f));
            Assert.False(pool.Release(id, 0f));
            Assert.Equal(1, pool.QuarantinedCount);
        }

        [Fact]
        public void ReleasingAnIdThatWasNeverIssuedChangesNothing()
        {
            var pool = new ActorIdPool(capacity: 4);

            Assert.False(pool.Release(999, 0f));
            Assert.Equal(4, pool.FreeCount);
        }

        [Fact]
        public void ExhaustionIsReportedRatherThanThrown()
        {
            var pool = new ActorIdPool(capacity: 3);

            for (int i = 0; i < 3; i++) Assert.True(pool.TryAcquire(0f, out _));

            // A full server is a normal operating condition, not an exception — the same call
            // WorldSnapshot.Add makes.
            Assert.False(pool.TryAcquire(0f, out ushort none));
            Assert.Equal(0, none);
        }

        [Fact]
        public void InUseAndFreeAndQuarantinedAlwaysAccountForEveryId()
        {
            var pool = new ActorIdPool(capacity: 8, quarantineSeconds: 3f);

            pool.TryAcquire(0f, out ushort a);
            pool.TryAcquire(0f, out _);
            pool.Release(a, 0f);

            Assert.Equal(8, pool.FreeCount + pool.QuarantinedCount + pool.InUseCount);
        }

        [Fact]
        public void ResetAllSkipsTheQuarantineBecauseEveryClientIsBeingToldToForgetEverything()
        {
            var pool = new ActorIdPool(capacity: 4, quarantineSeconds: 5f);
            pool.TryAcquire(0f, out ushort a);
            pool.TryAcquire(0f, out ushort b);
            pool.Release(a, 0f);

            pool.ResetAll();

            // Five rounds back to back with a 5 s cooldown each would leave the pool starved at
            // the top of a round. After a reset no packet from the old round means anything.
            Assert.Equal(4, pool.FreeCount);
            Assert.Equal(0, pool.QuarantinedCount);
            Assert.True(pool.IsFullyReleased);
            Assert.False(pool.IsInUse(b));
        }

        [Fact]
        public void ResetAllKeepsIdsStillHeldByLiveActorsMarkedInUse()
        {
            var pool = new ActorIdPool(capacity: 4, quarantineSeconds: 5f);
            pool.TryAcquire(0f, out ushort held);
            pool.TryAcquire(0f, out ushort gone);
            pool.Release(gone, 0f);

            // Dustbowl's bots are scene-resident: the match cycles round to round while they
            // keep existing, and keep holding their ids.
            pool.ResetAll(new[] { held });

            Assert.True(pool.IsInUse(held));
            Assert.Equal(1, pool.InUseCount);
            Assert.False(pool.IsFullyReleased);

            // Quarantine still cleared -- that part of the reset was never the defect.
            Assert.Equal(0, pool.QuarantinedCount);
            Assert.Equal(3, pool.FreeCount);
        }

        [Fact]
        public void ResetAllNeverReissuesAnIdALiveActorStillHolds()
        {
            var pool = new ActorIdPool(capacity: 4, quarantineSeconds: 5f);
            pool.TryAcquire(0f, out ushort held);

            pool.ResetAll(new[] { held });

            // Drain the pool. Before this fix the reset re-enqueued the whole id space, so the
            // very first acquire could hand `held` to a second actor -- the duplicate-id state
            // the quarantine and Register's guard exist to prevent.
            var issued = new List<ushort>();
            while (pool.TryAcquire(0f, out ushort id)) issued.Add(id);

            Assert.DoesNotContain(held, issued);
            Assert.Equal(3, issued.Count);
        }

        [Fact]
        public void ResetAllIgnoresRetainedIdsOutsideThePoolRatherThanThrowing()
        {
            var pool = new ActorIdPool(capacity: 4, quarantineSeconds: 5f);

            // 0 is "unassigned" everywhere in the protocol; 99 was never issued by this pool.
            // A caller enumerating a live scene should not have to pre-filter either.
            pool.ResetAll(new ushort[] { 0, 99 });

            Assert.Equal(4, pool.FreeCount);
            Assert.True(pool.IsFullyReleased);
        }

        [Fact]
        public void ResetAllWithNoRetainedIdsMatchesTheParameterlessForm()
        {
            var withNull = new ActorIdPool(capacity: 4, quarantineSeconds: 5f);
            var bare     = new ActorIdPool(capacity: 4, quarantineSeconds: 5f);

            withNull.TryAcquire(0f, out ushort _);
            bare.TryAcquire(0f, out ushort _);

            withNull.ResetAll(null);
            bare.ResetAll();

            Assert.Equal(bare.FreeCount, withNull.FreeCount);
            Assert.Equal(bare.InUseCount, withNull.InUseCount);
            Assert.Equal(bare.QuarantinedCount, withNull.QuarantinedCount);
        }

        [Fact]
        public void AZeroQuarantineHandsIdsStraightBack()
        {
            var pool = new ActorIdPool(capacity: 1, quarantineSeconds: 0f);
            pool.TryAcquire(0f, out ushort id);
            pool.Release(id, 0f);

            Assert.True(pool.TryAcquire(0f, out ushort again));
            Assert.Equal(id, again);
        }

        [Fact]
        public void SweepingIsIdempotent()
        {
            var pool = new ActorIdPool(capacity: 2, quarantineSeconds: 1f);
            pool.TryAcquire(0f, out ushort id);
            pool.Release(id, 0f);

            pool.ReleaseExpired(5f);
            pool.ReleaseExpired(5f);
            pool.ReleaseExpired(5f);

            Assert.Equal(2, pool.FreeCount);
        }

        [Fact]
        public void AZeroCapacityPoolIsRejected()
            => Assert.Throws<ArgumentOutOfRangeException>(() => new ActorIdPool(capacity: 0));

        [Fact]
        public void ANegativeQuarantineIsRejected()
            => Assert.Throws<ArgumentOutOfRangeException>(
                () => new ActorIdPool(capacity: 4, quarantineSeconds: -1f));
    }
}
