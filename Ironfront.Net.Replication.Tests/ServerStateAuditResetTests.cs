using System.Collections.Generic;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Interest;
using Ironfront.Net.Replication.Projectiles;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// X-73 and X-74. The audit predicate was unsatisfiable on the shipping map, and that is
    /// how a real leak one field over went unseen for the life of the process.
    /// </summary>
    /// <remarks>
    /// Every test here was observed RED against the pre-fix tree before the fix was written:
    /// <c>ARetainedActorIdIsNotALeak</c> failed because <c>ActorIdsInUse == 0</c> can never hold
    /// on a map whose actors outlive the round; <c>AResetReleasesProjectileIds</c> failed
    /// because <c>ResetForNewMatch</c> cleared eight tables and not the projectile pool; and
    /// <c>AnUncleanSnapshotNamesEveryFailingTerm</c> failed because the predicate was a
    /// short-circuiting <c>&amp;&amp;</c> chain that reported one bool and named nothing.
    /// </remarks>
    public sealed class ServerStateAuditResetTests
    {
        private static ServerStateAudit Audit(
            ActorIdPool ids,
            ProjectileIdPool? projectiles = null,
            int sessions = 0)
            => new ServerStateAudit(
                ids,
                new HitboxHistory(),
                new InterestManager(),
                new SpawnAckTracker(),
                () => sessions,
                projectileIds: projectiles);

        // ---------------------------------------------------------------- X-74

        [Fact]
        public void ARetainedActorIdIsNotALeak()
        {
            // The shipping Dustbowl map keeps 41 scene-resident bots across the match cycle and
            // ResetForNewMatch is told to retain them. The audit then read ActorIdsInUse == 41
            // and called it a leak, so its ERROR fired at EVERY round transition -- which is
            // exactly the crying-wolf failure the IsClean/IsCleanOfActorState split already
            // fixed once, one field over, for Sessions.
            var pool = new ActorIdPool(8);
            ServerStateAudit audit = Audit(pool);

            pool.TryAcquire(0f, out ushort survivor);
            pool.TryAcquire(0f, out _);

            audit.ResetForNewMatch(new[] { survivor });

            ServerStateSnapshot state = audit.Capture();

            Assert.Equal(1, state.ActorIdsInUse);
            Assert.Equal(1, state.RetainedActorIds);
            Assert.True(
                state.IsCleanOfActorState,
                $"a retained id was read as a leak — {state}");
        }

        [Fact]
        public void AnIdBeyondTheRetainedSetIsStillALeak()
        {
            // The other direction, and the reason this is a comparison rather than a waiver:
            // retaining one id must not excuse a second that nothing asked to keep.
            var pool = new ActorIdPool(8);
            ServerStateAudit audit = Audit(pool);

            pool.TryAcquire(0f, out ushort survivor);
            audit.ResetForNewMatch(new[] { survivor });

            pool.TryAcquire(0f, out _);

            ServerStateSnapshot state = audit.Capture();

            Assert.Equal(2, state.ActorIdsInUse);
            Assert.Equal(1, state.RetainedActorIds);
            Assert.False(state.IsCleanOfActorState);
            Assert.Contains("actorIdsInUse", state.UncleanTerms);
        }

        [Fact]
        public void AResetWithNothingRetainedStillDemandsZero()
        {
            // The lobby-driven teardown case, unchanged: retain nothing, and the old question
            // is the new question.
            var pool = new ActorIdPool(8);
            ServerStateAudit audit = Audit(pool);

            pool.TryAcquire(0f, out _);
            audit.ResetForNewMatch();

            ServerStateSnapshot state = audit.Capture();

            Assert.Equal(0, state.RetainedActorIds);
            Assert.True(state.IsCleanOfActorState);
        }

        // ---------------------------------------------------------------- X-73

        [Fact]
        public void AResetReleasesProjectileIds()
        {
            // A projectile in flight when a round ends kept its id for the life of the process:
            // ResetForNewMatch cleared the vehicle registry, both interest tables, the vehicle
            // id pool, the mounted weapons and the turrets -- and not this pool. Eight rounds of
            // the P7 soak is what surfaced it.
            var pool = new ActorIdPool(8);
            var projectiles = new ProjectileIdPool(16);
            ServerStateAudit audit = Audit(pool, projectiles);

            projectiles.TryAcquire(out _);
            projectiles.TryAcquire(out _);
            Assert.Equal(2, audit.Capture().ProjectileIdsInUse);

            audit.ResetForNewMatch();

            ServerStateSnapshot state = audit.Capture();

            Assert.Equal(0, state.ProjectileIdsInUse);
            Assert.True(state.IsCleanOfVehicleState, $"projectile ids survived a reset — {state}");
        }

        // ---------------------------------------------------------------- the hiding itself

        [Fact]
        public void AnUncleanSnapshotNamesEveryFailingTerm()
        {
            // This is the fix for the failure MODE, not for either defect. X-73 was invisible
            // for as long as it was because a short-circuiting && chain answers "unclean" and
            // names nothing: the actor term failed first on every single round transition, so
            // the projectile term's answer never reached anybody's eyes. A predicate that names
            // all of its failing terms cannot hide the second defect behind the first.
            var pool = new ActorIdPool(8);
            var projectiles = new ProjectileIdPool(16);
            ServerStateAudit audit = Audit(pool, projectiles);

            pool.TryAcquire(0f, out _);
            projectiles.TryAcquire(out _);

            ServerStateSnapshot state = audit.Capture();

            Assert.False(state.IsCleanOfActorState);

            string terms = state.UncleanTerms;
            Assert.Contains("actorIdsInUse=1", terms);
            Assert.Contains("projectileIdsInUse=1", terms);
        }

        [Fact]
        public void ACleanSnapshotNamesNoTerms()
        {
            ServerStateSnapshot state = Audit(new ActorIdPool(8), sessions: 3).Capture();

            Assert.True(state.IsCleanOfActorState);
            Assert.Equal(string.Empty, state.UncleanTerms);
        }

        [Fact]
        public void TheRenderedSnapshotCarriesTheFailingTerms()
        {
            // MatchController logs `state`, so whatever ToString does not say never reaches the
            // server log where this class's whole value is realised.
            var pool = new ActorIdPool(8);
            ServerStateAudit audit = Audit(pool);
            pool.TryAcquire(0f, out _);

            string rendered = audit.Capture().ToString();

            Assert.Contains("unclean: actorIdsInUse=1", rendered);
        }
    }
}
