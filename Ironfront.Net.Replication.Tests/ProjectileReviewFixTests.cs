using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Projectiles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The defects the V7 adversarial review found, each pinned so it cannot come back.
    /// </summary>
    public sealed class ProjectileReviewFixTests
    {
        private const ushort ShooterId = 1;
        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        private static ProjectileConfig Config(float lifetime = 10f)
            => new ProjectileConfig(
                speed: 300f, lifetime: lifetime, damage: 70f, balanceDamage: 60f,
                impactForce: 200f, dropoffEnd: 300f, piercing: false);

        private static ProjectileCatalog CatalogOf(params ProjectileKind[] kinds)
        {
            var catalog = new ProjectileCatalog();
            ProjectileConfig config = Config();
            for (int i = 0; i < kinds.Length; i++) catalog.Set(kinds[i], in config);
            return catalog;
        }

        private static ProjectileSpawnMessage Message(
            ushort id, ushort spawnTick, ProjectileKind kind, float lifetimeSeconds = 10f)
            => new ProjectileSpawnMessage(
                id, ShooterId, kind,
                Quantize.PackPos(0f), Quantize.PackPos(50f), Quantize.PackPos(0f),
                Quantize.PackVel16(0f), Quantize.PackVel16(0f), Quantize.PackVel16(100f),
                spawnTick,
                ProjectileSpawnMessage.PackRemainingLifetime(lifetimeSeconds));

        /// <summary>
        /// A recycled id naming a DIFFERENT kind must spawn, not re-seat.
        /// </summary>
        /// <remarks>
        /// Matching on id alone teleported whatever prefab that id last named — a medipack, say
        /// — onto the new grenade's arc, and never spawned the grenade at all. Silently: the
        /// wrong object simply went on being the wrong object in the right place. The window is
        /// real because the server frees an id the moment a projectile ends while the client
        /// only learns of the end when a terminal message reaches it.
        /// </remarks>
        [Fact]
        public void ARecycledIdNamingADifferentKindSpawnsRatherThanReSeats()
        {
            var tracker = new ClientProjectileTracker(
                CatalogOf(ProjectileKind.Medipack, ProjectileKind.Grenade), Tick);

            Assert.Equal(
                ProjectileApplyAction.Spawn,
                tracker.Apply(Message(9, 100, ProjectileKind.Medipack), 100).Action);

            ProjectileApplyResult recycled =
                tracker.Apply(Message(9, 140, ProjectileKind.Grenade), 140);

            Assert.Equal(ProjectileApplyAction.Spawn, recycled.Action);
            Assert.Equal(ProjectileKind.Grenade, recycled.Kind);
            Assert.Equal(1, tracker.ReplacedIds);

            // Same id AND same kind is still a re-seat -- the check must not have broken V7-D6.
            Assert.Equal(
                ProjectileApplyAction.ReSeat,
                tracker.Apply(Message(9, 146, ProjectileKind.Grenade), 146).Action);
            Assert.Equal(1, tracker.ReplacedIds);
        }

        /// <summary>
        /// A grenade is never stepped by the ballistic stepper, and it would be a bug with teeth
        /// if it were.
        /// </summary>
        /// <remarks>
        /// The stepper terminates a projectile on the first surface its swept segment touches,
        /// which is right for a bullet and exactly wrong for a grenade — whose entire behaviour
        /// is to BOUNCE off that surface. Nothing in this library models a bounce, so a stepped
        /// grenade detonates on the first wall it grazes. Deployables are excluded for the
        /// neighbouring reason: their pose comes from a Rigidbody the engine owns.
        /// </remarks>
        [Fact]
        public void ABouncingOrRigidbodyProjectileIsNotBallisticallyStepped()
        {
            ProjectileConfig config = Config();
            var registry = new ServerProjectileRegistry(new ProjectileIdPool(8));
            var authority = new ServerProjectileAuthority(
                registry, CatalogOf(ProjectileKind.Grenade), null, Tick);

            Assert.False(authority.StepsKind(ProjectileKind.Grenade));
            Assert.False(authority.StepsKind(ProjectileKind.AmmoBag));
            Assert.False(authority.StepsKind(ProjectileKind.Medipack));

            Assert.Equal(
                0,
                authority.Launch(
                    ProjectileKind.Grenade, Vec3.Zero, new Vec3(0f, 0f, 1f), ShooterId, 0));
            Assert.Equal(0, registry.LiveCount);

            // The kinds that DO fly straight and terminate on impact are still stepped, so this
            // is a statement about which kinds rather than about the stepper being off.
            Assert.True(authority.StepsKind(ProjectileKind.Rocket));
            Assert.True(authority.StepsKind(ProjectileKind.Shell));
            Assert.True(authority.StepsKind(ProjectileKind.GuidedMissile));
        }

        /// <summary>
        /// A lifetime past the byte's range must make a client despawn LATE, never early.
        /// </summary>
        /// <remarks>
        /// <c>PackRemainingLifetime</c> saturates at 255 and a medipack is authored at thirty
        /// seconds. Reading 255 as a literal 25.5 s despawns it four and a half seconds early,
        /// which is the one direction V7-D8 promises never happens — and it would have done so
        /// on every deployable in the game, not in some edge case.
        /// </remarks>
        [Fact]
        public void ALifetimeBeyondTheBytesRangeDespawnsLateNotEarly()
        {
            const float authored = 30f;

            Assert.Equal(
                ProjectileSpawnMessage.LifetimeUnknown,
                ProjectileSpawnMessage.PackRemainingLifetime(authored));

            var catalog = new ProjectileCatalog();
            ProjectileConfig config = Config(lifetime: authored);
            catalog.Set(ProjectileKind.Medipack, in config);

            var tracker = new ClientProjectileTracker(catalog, Tick);

            ProjectileApplyResult result =
                tracker.Apply(Message(11, 0, ProjectileKind.Medipack, authored), 0);

            Assert.Equal(authored, result.RemainingLifetimeSeconds, 1);
            Assert.True(
                result.RemainingLifetimeSeconds > 25.5f,
                "the client took the saturated byte literally and will despawn early.");
        }

        /// <summary>
        /// A lifetime the byte CAN express is still taken from the byte, because that is where
        /// a medipack's self-shortening becomes visible.
        /// </summary>
        /// <remarks>
        /// Companion to the test above, and the reason it is not enough on its own: a fallback
        /// that fired for every value would throw away the shortened lifetime entirely and
        /// leave V7-D8 with no mechanism at all.
        /// </remarks>
        [Fact]
        public void AnExpressibleLifetimeIsTakenFromTheWireNotTheConfig()
        {
            var catalog = new ProjectileCatalog();
            ProjectileConfig config = Config(lifetime: 30f);
            catalog.Set(ProjectileKind.Medipack, in config);

            var tracker = new ClientProjectileTracker(catalog, Tick);

            // The server says twelve seconds are left, well inside the byte. The client must
            // believe it rather than falling back to the authored thirty.
            ProjectileApplyResult result =
                tracker.Apply(Message(12, 0, ProjectileKind.Medipack, 12f), 0);

            Assert.Equal(12f, result.RemainingLifetimeSeconds, 1);
        }

        /// <summary>
        /// The id pool hands an id to exactly one owner, and takes it back.
        /// </summary>
        /// <remarks>
        /// Engine-simulated projectiles (grenades, deployables, guided missiles) draw ids from
        /// the same pool the ballistic registry does, and release them from the engine side. A
        /// leak here is one id per grenade thrown, which brainstorm criterion 13's
        /// five-back-to-back-matches check is what would eventually surface.
        /// </remarks>
        [Fact]
        public void AnEngineSimulatedIdIsReturnedToTheSamePoolTheStepperUses()
        {
            var pool = new ProjectileIdPool(8);
            var registry = new ServerProjectileRegistry(pool);
            var authority = new ServerProjectileAuthority(
                registry, CatalogOf(ProjectileKind.Rocket), null, Tick);

            // One stepped, one engine-simulated, from one pool.
            ushort stepped = authority.Launch(
                ProjectileKind.Rocket, Vec3.Zero, new Vec3(0f, 0f, 1f), ShooterId, 0);
            Assert.True(pool.TryAcquire(out ushort engineSimulated));

            Assert.NotEqual(stepped, engineSimulated);
            Assert.Equal(2, pool.InUseCount);

            Assert.True(pool.Release(engineSimulated));
            Assert.True(registry.Remove(stepped));

            Assert.Equal(0, pool.InUseCount);
            Assert.Equal(pool.Capacity, pool.FreeCount);
        }
    }
}
