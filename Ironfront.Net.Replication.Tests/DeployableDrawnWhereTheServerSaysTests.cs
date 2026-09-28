using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Projectiles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// A thrown medipack or ammo bag is drawn where the server's physics put it. Playtest
    /// 2026-09-28, bug 4.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A medipack thrown on an upper floor fell through to the floor below, and standing next to
    /// it healed nobody. The client fast-forwarded every announcement along the launch arc,
    /// gravity included, and teleported its own rigidbody there; a pose inside the floor was then
    /// dropped a storey by physics while the server's pack -- the one that heals -- stayed
    /// upstairs. Measured in the farmhouse on Island (2026-09-29): at 6-9 ticks of catch-up the
    /// client's copy fell through in five throws of eight, the server's in none. And the server
    /// never announced the pose a pack came to rest in, so nothing corrected it.
    /// </para>
    /// </remarks>
    public sealed class DeployableDrawnWhereTheServerSaysTests
    {
        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;
        private const ushort OwnerId = 7;

        [Theory]
        [InlineData(ProjectileKind.Medipack)]
        [InlineData(ProjectileKind.AmmoBag)]
        public void AFallingDeployableIsNotCarriedAlongItsArc(ProjectileKind kind)
        {
            ProjectileApplyResult result = Tracker(kind).Apply(Falling(kind, spawnTick: 91), nowTick: 100);

            Assert.Equal(9, result.FastForwardedTicks);
            Assert.Equal(Quantize.UnpackPos(Quantize.PackPos(75.6f)), result.Position.Y, 4);
        }

        [Fact]
        public void ARestingPackIsNotSunkByGravity()
        {
            // Nine ticks of gravity alone would draw it 0.44 m below where it lies.
            ProjectileApplyResult result = Tracker(ProjectileKind.Medipack)
                .Apply(Resting(ProjectileKind.Medipack, spawnTick: 91), nowTick: 100);

            Assert.Equal(Quantize.UnpackPos(Quantize.PackPos(75.6f)), result.Position.Y, 4);
        }

        [Fact]
        public void AGrenadeIsStillCarriedAlongItsArc()
        {
            // The control: fast-forward is right for a projectile nothing re-announces at rest,
            // and removing it wholesale would draw every grenade a round trip behind.
            ProjectileApplyResult result = Tracker(ProjectileKind.Grenade)
                .Apply(Falling(ProjectileKind.Grenade, spawnTick: 91), nowTick: 100);

            Assert.True(result.Position.Y < 75f,
                $"a grenade falling at 3 m/s was not fast-forwarded: drawn at {result.Position.Y:F2}");
        }

        [Fact]
        public void OnlyTheThrownSuppliesAreDeployables()
        {
            foreach (ProjectileKind kind in (ProjectileKind[])Enum.GetValues(typeof(ProjectileKind)))
            {
                bool expected = kind == ProjectileKind.Medipack || kind == ProjectileKind.AmmoBag;
                Assert.Equal(expected, DeployableKinds.Is(kind));
            }
        }

        [Fact]
        public void APackKnockedAlongIsAnnouncedWhereItStopsAgain()
        {
            var authority = new ServerDeployableAuthority(
                new ProjectileIdPool(32), new NoSink(), new ActorSpareAmmoPool(), Tick);

            ushort id = authority.Deploy(
                ProjectileKind.Medipack, OwnerId, new Vec3(0f, 1f, 0f), new Vec3(0f, 0f, 5f),
                lifetimeSeconds: 60f, currentTick: 0);

            Span<ushort> reAnnounce = stackalloc ushort[8];
            Span<ushort> expired = stackalloc ushort[8];

            // Lands and is announced at rest.
            authority.UpdatePose(id, new Vec3(0f, 1f, 3f), Vec3.Zero);
            Assert.Equal(1, authority.Step(1, ReadOnlySpan<HitscanTarget>.Empty, reAnnounce, expired).ReAnnounceCount);
            Assert.Equal(0, authority.Step(2, ReadOnlySpan<HitscanTarget>.Empty, reAnnounce, expired).ReAnnounceCount);

            // A blast knocks it along, and it stops again somewhere new.
            authority.UpdatePose(id, new Vec3(0f, 1f, 4f), new Vec3(0f, 0f, 4f));
            authority.Step(10, ReadOnlySpan<HitscanTarget>.Empty, reAnnounce, expired);
            authority.UpdatePose(id, new Vec3(0f, 1f, 6f), Vec3.Zero);

            DeployableStepResult stopped = authority.Step(11, ReadOnlySpan<HitscanTarget>.Empty, reAnnounce, expired);

            Assert.Equal(1, stopped.ReAnnounceCount);
            Assert.Equal(id, reAnnounce[0]);
            Assert.Equal(6f, authority.PositionOf(id).Z, 3);
        }

        // ------------------------------------------------------------------ helpers

        private static ClientProjectileTracker Tracker(ProjectileKind kind)
        {
            var catalog = new ProjectileCatalog();
            var config = new ProjectileConfig(
                speed: 5f, lifetime: 60f, damage: 0f, balanceDamage: 0f,
                impactForce: 0f, dropoffEnd: 1f, piercing: false);
            catalog.Set(kind, in config);
            return new ClientProjectileTracker(catalog, Tick);
        }

        /// <summary>Announced mid-bounce, just above an upper floor, falling at 3 m/s.</summary>
        private static ProjectileSpawnMessage Falling(ProjectileKind kind, ushort spawnTick)
            => Message(kind, spawnTick, velocityY: -3f);

        private static ProjectileSpawnMessage Resting(ProjectileKind kind, ushort spawnTick)
            => Message(kind, spawnTick, velocityY: 0f);

        private static ProjectileSpawnMessage Message(ProjectileKind kind, ushort spawnTick, float velocityY)
            => new ProjectileSpawnMessage(
                12, OwnerId, kind,
                Quantize.PackPos(303f), Quantize.PackPos(75.6f), Quantize.PackPos(440f),
                Quantize.PackVel16(0f), Quantize.PackVel16(velocityY), Quantize.PackVel16(0f),
                spawnTick,
                ProjectileSpawnMessage.PackRemainingLifetime(20f));

        private sealed class NoSink : IActorDamageSink
        {
            public DamageOutcome ApplyDamage(ushort victimId, float healthDamage, float balanceDamage, ushort attackerId)
                => DamageOutcome.NoOp;

            public float ApplyHeal(ushort actorId, float amount) => 0f;
        }
    }
}
