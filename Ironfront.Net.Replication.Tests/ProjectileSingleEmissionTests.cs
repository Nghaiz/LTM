using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Projectiles;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Protocol-10 handoff § 6 and § 10.3: one accepted shot, one projectile, one explosion —
    /// including when input redundancy offers the same shot three times.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These grade <see cref="ProjectileEmissionLedger"/> rather than
    /// <c>ServerProjectileBridge</c>, because the bridge needs a <c>ServerTickLoop</c> and a
    /// transport and nothing in <c>dotnet test</c> can build one. The mechanism is deliberately
    /// all on this side of that line: the bridge's own use of it is three calls, and the thing
    /// with the rules in it is here where a rule can fail in CI.
    /// </para>
    /// <para>
    /// <b>Not proved here, and stated rather than implied:</b> that the bridge actually consults
    /// the ledger. That wiring is graded by reading <c>ServerProjectileBridge.Launch</c>, which
    /// returns the previously launched id on a refusal — an assertion no .NET test host can make
    /// against a MonoBehaviour-driven server.
    /// </para>
    /// </remarks>
    public sealed class ProjectileSingleEmissionTests
    {
        private const ushort ShooterId = 4;
        private const uint FireTick = 900;

        /// <summary>
        /// § 10.3, first row. One accepted rocket is one launch; the rocket kind is stepped by
        /// the authority, so its id comes from the ballistic registry.
        /// </summary>
        [Fact]
        public void OneAcceptedRocketClaimsOneLaunch()
        {
            var ledger = new ProjectileEmissionLedger();

            Assert.True(ledger.TryClaimLaunch(ShooterId, FireTick, ProjectileKind.Rocket, out _));
            ledger.RecordLaunch(ShooterId, FireTick, ProjectileKind.Rocket, 11);

            Assert.Equal(0L, ledger.DuplicateLaunchesSuppressed);
            Assert.True(ledger.TryClaimDetonation(11));
            Assert.Equal(0L, ledger.DuplicateDetonationsSuppressed);
        }

        /// <summary>
        /// § 10.3, second row, and the reason this lane exists. <c>C_INPUT</c> carries each frame
        /// up to three times; the second and third copies of one trigger pull must produce no
        /// second projectile. § 16 forbids removing the redundancy, so this is the only place the
        /// duplicate can be stopped.
        /// </summary>
        /// <remarks>
        /// The refusal hands back the FIRST launch's id rather than 0. That is load-bearing:
        /// <c>ProjectileNetAnnouncer</c> stamps the returned id onto the prefab, and 0 means "not
        /// replicated" — so answering 0 to a redundant copy would strip the id off a projectile
        /// that really is in flight, and its blast would arrive with nothing to despawn.
        /// </remarks>
        [Fact]
        public void ARedundantInputFrameProducesNoSecondProjectile()
        {
            var ledger = new ProjectileEmissionLedger();

            Assert.True(ledger.TryClaimLaunch(ShooterId, FireTick, ProjectileKind.Rocket, out _));
            ledger.RecordLaunch(ShooterId, FireTick, ProjectileKind.Rocket, 11);

            for (int copy = 0; copy < 2; copy++)
            {
                Assert.False(
                    ledger.TryClaimLaunch(ShooterId, FireTick, ProjectileKind.Rocket, out ushort again));
                Assert.Equal((ushort)11, again);
            }

            Assert.Equal(2L, ledger.DuplicateLaunchesSuppressed);
        }

        /// <summary>A grenade behaves exactly as a rocket does — § 6's parity clause.</summary>
        /// <remarks>
        /// Their presentation differs and their flight differs — a grenade bounces and is stepped
        /// by the engine, a rocket is not — but owner, spawn and explosion lifecycle are the same
        /// three facts, so the same record governs both.
        /// </remarks>
        [Fact]
        public void AGrenadeHasTheSameLaunchParityAsARocket()
        {
            var ledger = new ProjectileEmissionLedger();

            Assert.True(ledger.TryClaimLaunch(ShooterId, FireTick, ProjectileKind.Grenade, out _));
            ledger.RecordLaunch(ShooterId, FireTick, ProjectileKind.Grenade, 12);

            Assert.False(
                ledger.TryClaimLaunch(ShooterId, FireTick, ProjectileKind.Grenade, out ushort again));
            Assert.Equal((ushort)12, again);
        }

        /// <summary>
        /// The next tick is a different shot. Automatic fire is bounded by the weapon cooldown,
        /// not by this, so a ledger that refused consecutive ticks would silently cap every
        /// launcher at one shot per actor for the round.
        /// </summary>
        [Fact]
        public void TheNextTickIsANewShot()
        {
            var ledger = new ProjectileEmissionLedger();

            Assert.True(ledger.TryClaimLaunch(ShooterId, FireTick, ProjectileKind.Rocket, out _));
            ledger.RecordLaunch(ShooterId, FireTick, ProjectileKind.Rocket, 11);

            Assert.True(ledger.TryClaimLaunch(ShooterId, FireTick + 1, ProjectileKind.Rocket, out _));
            Assert.Equal(0L, ledger.DuplicateLaunchesSuppressed);
        }

        /// <summary>Two shooters firing on the same tick are two shots, not a duplicate.</summary>
        [Fact]
        public void TwoShootersOnOneTickAreTwoShots()
        {
            var ledger = new ProjectileEmissionLedger();

            Assert.True(ledger.TryClaimLaunch(1, FireTick, ProjectileKind.Rocket, out _));
            ledger.RecordLaunch(1, FireTick, ProjectileKind.Rocket, 11);

            Assert.True(ledger.TryClaimLaunch(2, FireTick, ProjectileKind.Rocket, out _));
            Assert.Equal(0L, ledger.DuplicateLaunchesSuppressed);
        }

        /// <summary>
        /// A shot that could not be replicated still counts as the shot. Recording the failure
        /// is what stops a redundant copy from retrying the pool and succeeding where the first
        /// attempt failed — one trigger pull, one outcome, even when the outcome is nothing.
        /// </summary>
        [Fact]
        public void AShotThatFailedToGetAnIdStillSuppressesItsRedundantCopy()
        {
            var ledger = new ProjectileEmissionLedger();

            Assert.True(ledger.TryClaimLaunch(ShooterId, FireTick, ProjectileKind.Rocket, out _));
            ledger.RecordLaunch(ShooterId, FireTick, ProjectileKind.Rocket, 0);

            Assert.False(
                ledger.TryClaimLaunch(ShooterId, FireTick, ProjectileKind.Rocket, out ushort again));
            Assert.Equal((ushort)0, again);
        }

        /// <summary>
        /// § 10.3, first row, second half. One projectile detonates once however many engine
        /// paths reach it, and § 16 forbids a second one to cover a client that failed to render
        /// the first.
        /// </summary>
        [Fact]
        public void OneProjectileAnnouncesOneExplosion()
        {
            var ledger = new ProjectileEmissionLedger();
            ledger.RecordLaunch(ShooterId, FireTick, ProjectileKind.Rocket, 11);

            Assert.True(ledger.TryClaimDetonation(11));
            Assert.False(ledger.TryClaimDetonation(11));
            Assert.False(ledger.TryClaimDetonation(11));

            Assert.True(ledger.HasDetonated(11));
            Assert.Equal(2L, ledger.DuplicateDetonationsSuppressed);
        }

        /// <summary>
        /// A reissued id detonates again, and it must: <see cref="ProjectileIdPool"/> runs
        /// without a quarantine, so id 11 comes back around within a round. The bit is cleared on
        /// the ANNOUNCE rather than on the release, so an id whose release was missed — a prefab
        /// destroyed by a scene teardown, a slot reclaimed by the per-shooter cap — is not left
        /// permanently unable to explode.
        /// </summary>
        [Fact]
        public void AReissuedIdMayDetonateAgain()
        {
            var ledger = new ProjectileEmissionLedger();

            ledger.RecordLaunch(ShooterId, FireTick, ProjectileKind.Rocket, 11);
            Assert.True(ledger.TryClaimDetonation(11));

            ledger.RecordLaunch(ShooterId, FireTick + 60, ProjectileKind.Rocket, 11);
            Assert.False(ledger.HasDetonated(11));
            Assert.True(ledger.TryClaimDetonation(11));
        }

        /// <summary>
        /// An unreplicated projectile never claims a detonation slot. Id 0 means "not on the
        /// wire", and refusing its blast would silence every explosion on a server whose pool had
        /// run dry — the opposite of the failure this class exists for.
        /// </summary>
        [Fact]
        public void IdZeroNeverSuppressesABlast()
        {
            var ledger = new ProjectileEmissionLedger();

            Assert.True(ledger.TryClaimDetonation(0));
            Assert.True(ledger.TryClaimDetonation(0));
            Assert.Equal(0L, ledger.DuplicateDetonationsSuppressed);
        }

        /// <summary>
        /// A dead shooter's last shot is forgotten, so the first shot of the next life is never
        /// read as a redundant copy of it.
        /// </summary>
        [Fact]
        public void ForgettingAnActorReleasesItsLastShot()
        {
            var ledger = new ProjectileEmissionLedger();

            ledger.RecordLaunch(ShooterId, FireTick, ProjectileKind.Rocket, 11);
            ledger.ForgetActor(ShooterId);

            Assert.True(ledger.TryClaimLaunch(ShooterId, FireTick, ProjectileKind.Rocket, out _));
        }

        /// <summary>
        /// § 10.3, last row: a world reset leaks no projectile id and no record of one. The
        /// pools are the ids; the ledger is the memory of the shots that held them, and a
        /// surviving memory would suppress the new round's opening shot.
        /// </summary>
        [Fact]
        public void AWorldResetLeaksNoProjectileIdAndNoShot()
        {
            var pool = new ProjectileIdPool();
            var registry = new ServerProjectileRegistry(pool);
            var ledger = new ProjectileEmissionLedger();

            var state = new BallisticState(Vec3.Zero, new Vec3(0f, 0f, 30f));
            for (int i = 0; i < 8; i++)
            {
                ushort id = registry.Add(ProjectileKind.Rocket, in state, ShooterId, FireTick, FireTick + 60);
                Assert.NotEqual((ushort)0, id);
                ledger.RecordLaunch(ShooterId, FireTick + (uint)i, ProjectileKind.Rocket, id);
                ledger.TryClaimDetonation(id);
            }

            Assert.Equal(8, pool.InUseCount);

            registry.Reset();
            ledger.Reset();

            Assert.Equal(0, pool.InUseCount);
            Assert.Equal(0, registry.LiveCount);
            Assert.Equal((int)ProjectileIdPool.DefaultCapacity, pool.FreeCount);
            Assert.True(ledger.TryClaimLaunch(ShooterId, FireTick, ProjectileKind.Rocket, out _));
            Assert.False(ledger.HasDetonated(11));
        }

        /// <summary>
        /// § 10.3, last row, actor half: a world reset returns every actor id. The quarantine is
        /// what <see cref="ActorIdPool.ResetAll()"/> empties, and § 16's "never reuse an id
        /// before its quarantine expires" is about the ordinary release path, not this one.
        /// </summary>
        [Fact]
        public void AWorldResetLeaksNoActorId()
        {
            var pool = new ActorIdPool();

            for (int i = 0; i < 12; i++)
            {
                Assert.True(pool.TryAcquire(0f, out ushort id));
                Assert.NotEqual((ushort)0, id);
            }

            pool.Release(3, 0f);
            pool.ResetAll();

            Assert.Equal(0, pool.InUseCount);
            Assert.Equal(0, pool.QuarantinedCount);
            Assert.Equal(pool.Capacity, pool.FreeCount);
            Assert.True(pool.IsFullyReleased);
        }
    }
}
