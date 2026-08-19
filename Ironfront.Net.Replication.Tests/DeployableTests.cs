using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Projectiles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-V7 task 7: ammo bags and medipacks as replicated world entities.
    /// </summary>
    public sealed class DeployableTests
    {
        private const ushort OwnerId  = 1;
        private const ushort NearbyId = 2;

        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        /// <summary>Ticks in one three-second resupply interval.</summary>
        private const uint ResupplyTicks =
            (uint)(ServerDeployableAuthority.ResupplyIntervalSeconds * ProtocolConstants.SIM_TICK_RATE);

        private static HitscanTarget AliveAt(ushort actorId, in Vec3 feetPosition)
            => new HitscanTarget(actorId, isAlive: true, HitboxSet.Humanoid(in feetPosition));

        private static (ServerDeployableAuthority Authority, RecordingSink Sink, ActorSpareAmmoPool Ammo)
            Build()
        {
            var sink = new RecordingSink();
            var ammo = new ActorSpareAmmoPool();
            var authority = new ServerDeployableAuthority(
                new ProjectileIdPool(32), sink, ammo, Tick);
            return (authority, sink, ammo);
        }

        /// <summary>
        /// V7-D8. A thrown bag settles in about two seconds, so the whole cost of a deployment
        /// is roughly twenty messages and then <b>nothing</b> — a bag on the ground is free. If
        /// this fails, every deployable in the match is paying 10 Hz forever.
        /// </summary>
        [Fact]
        public void ADeployableStopsReAnnouncingOnceAtRest()
        {
            (ServerDeployableAuthority authority, _, _) = Build();

            ushort id = authority.Deploy(
                ProjectileKind.AmmoBag, OwnerId, new Vec3(0f, 1f, 0f), new Vec3(0f, 0f, 6f),
                lifetimeSeconds: 60f, currentTick: 0);

            Span<ushort> reAnnounce = stackalloc ushort[8];
            Span<ushort> expired = stackalloc ushort[8];

            int whileMoving = 0;
            for (uint t = 1; t <= 60; t++)
            {
                authority.UpdatePose(id, new Vec3(0f, 1f, t * 0.2f), new Vec3(0f, 0f, 6f));
                whileMoving += authority
                    .Step(t, ReadOnlySpan<HitscanTarget>.Empty, reAnnounce, expired)
                    .ReAnnounceCount;
            }

            // Two seconds of movement at 10 Hz is about twenty messages.
            Assert.InRange(whileMoving, 15, 25);

            // Now it settles. Velocity below the rest threshold, and the traffic must stop dead.
            int afterRest = 0;
            for (uint t = 61; t <= 200; t++)
            {
                authority.UpdatePose(id, new Vec3(0f, 1f, 12f), Vec3.Zero);
                afterRest += authority
                    .Step(t, ReadOnlySpan<HitscanTarget>.Empty, reAnnounce, expired)
                    .ReAnnounceCount;
            }

            Assert.Equal(0, afterRest);
        }

        /// <summary>
        /// V7-D8's whole reason for the remaining-lifetime byte. A medipack subtracts five
        /// seconds from its own life per successful heal, which no client can predict from the
        /// spawn tick — so if the server does not re-announce, every client keeps rendering a
        /// pack that is already gone.
        /// </summary>
        [Fact]
        public void AMedipackShortensItsReplicatedLifetimePerHeal()
        {
            (ServerDeployableAuthority authority, RecordingSink sink, _) = Build();
            sink.SetHealth(NearbyId, 40f);

            ushort id = authority.Deploy(
                ProjectileKind.Medipack, OwnerId, new Vec3(0f, 0f, 0f), Vec3.Zero,
                lifetimeSeconds: 30f, currentTick: 0);

            float before = authority.RemainingLifetimeSeconds(id, 0);
            Assert.Equal(30f, before, 1);

            HitscanTarget[] actors = { AliveAt(NearbyId, new Vec3(0f, 0f, 1f)) };
            Span<ushort> reAnnounce = stackalloc ushort[8];
            Span<ushort> expired = stackalloc ushort[8];

            DeployableStepResult result =
                authority.Step(ResupplyTicks, actors, reAnnounce, expired);

            Assert.Equal(1, result.HealsApplied);
            Assert.Equal(70f, sink.HealthOf(NearbyId), 1);

            // 30 s authored, minus 3 s elapsed, minus the 5 s the heal cost it.
            float after = authority.RemainingLifetimeSeconds(id, ResupplyTicks);
            Assert.Equal(22f, after, 1);

            // And the shortening reached the wire: a countdown the client is already running
            // would have predicted 27 s, so silence here is a pack that despawns unannounced.
            Assert.Equal(1, result.ReAnnounceCount);
            Assert.Equal(id, reAnnounce[0]);
        }

        /// <summary>
        /// A full-health actor must not shorten the pack. Otherwise a squad standing on a
        /// medipack burns it down in seconds while healing nobody.
        /// </summary>
        [Fact]
        public void AMedipackIsNotShortenedByAnActorItCannotHeal()
        {
            (ServerDeployableAuthority authority, RecordingSink sink, _) = Build();
            sink.SetHealth(NearbyId, 100f);

            ushort id = authority.Deploy(
                ProjectileKind.Medipack, OwnerId, Vec3.Zero, Vec3.Zero,
                lifetimeSeconds: 30f, currentTick: 0);

            HitscanTarget[] actors = { AliveAt(NearbyId, new Vec3(0f, 0f, 1f)) };
            Span<ushort> reAnnounce = stackalloc ushort[8];
            Span<ushort> expired = stackalloc ushort[8];

            DeployableStepResult result =
                authority.Step(ResupplyTicks, actors, reAnnounce, expired);

            Assert.Equal(0, result.HealsApplied);
            Assert.Equal(27f, authority.RemainingLifetimeSeconds(id, ResupplyTicks), 1);
        }

        /// <summary>
        /// The bug this test exists for: a motionless medipack must be SILENT while its ordinary
        /// countdown runs.
        /// </summary>
        /// <remarks>
        /// The first implementation compared the current remaining lifetime against the last
        /// value it had ANNOUNCED. The client's own countdown is always already lower than that,
        /// so the comparison reported a divergence on every tick and re-announced a stationary
        /// pack at 30 Hz for its entire thirty-second life — roughly nine hundred messages for a
        /// thing lying on the ground, which is precisely the traffic V7-D8 exists to avoid. It
        /// passed every other test here, because the rest-and-silence test uses an ammo bag and
        /// the heal test only ever asserted that a message WAS sent.
        /// </remarks>
        [Fact]
        public void AMotionlessMedipackDoesNotReAnnounceItsOrdinaryCountdown()
        {
            (ServerDeployableAuthority authority, _, _) = Build();

            authority.Deploy(
                ProjectileKind.Medipack, OwnerId, Vec3.Zero, Vec3.Zero,
                lifetimeSeconds: 30f, currentTick: 0);

            Span<ushort> reAnnounce = stackalloc ushort[8];
            Span<ushort> expired = stackalloc ushort[8];

            int announcements = 0;
            for (uint t = 1; t < ResupplyTicks; t++)   // the whole first interval, nobody nearby
            {
                announcements += authority
                    .Step(t, ReadOnlySpan<HitscanTarget>.Empty, reAnnounce, expired)
                    .ReAnnounceCount;
            }

            Assert.Equal(0, announcements);
        }

        /// <summary>A deployable does not reach past its authored radius.</summary>
        [Fact]
        public void ADeployableDoesNotReachBeyondItsResupplyRange()
        {
            (ServerDeployableAuthority authority, RecordingSink sink, _) = Build();
            sink.SetHealth(NearbyId, 10f);

            authority.Deploy(
                ProjectileKind.Medipack, OwnerId, Vec3.Zero, Vec3.Zero, 30f, 0);

            float justOutside = ServerDeployableAuthority.ResupplyRange + 2f;
            HitscanTarget[] actors = { AliveAt(NearbyId, new Vec3(0f, 0f, justOutside)) };

            Span<ushort> reAnnounce = stackalloc ushort[8];
            Span<ushort> expired = stackalloc ushort[8];

            Assert.Equal(
                0,
                authority.Step(ResupplyTicks, actors, reAnnounce, expired).HealsApplied);
            Assert.Equal(10f, sink.HealthOf(NearbyId), 1);
        }

        /// <summary>
        /// <c>Actor.ResupplyAmmo</c> clamps to <c>weapon.configuration.spareAmmo</c>
        /// (<c>Actor.cs:1213</c>). A bag that banked rounds above the ceiling would let a player
        /// stockpile past what their loadout allows by standing on one.
        /// </summary>
        [Fact]
        public void AResupplyClampsToTheAuthoredSpareAmmoCeiling()
        {
            (ServerDeployableAuthority authority, _, ActorSpareAmmoPool ammo) = Build();

            // 50 rounds held, ceiling 60, a pulse adds 30. One pulse may add only 10.
            ammo.SetLoadout(NearbyId, slot: 0, rounds: 50, cap: 60, resupplyPerPulse: 30);

            authority.Deploy(ProjectileKind.AmmoBag, OwnerId, Vec3.Zero, Vec3.Zero, 60f, 0);

            HitscanTarget[] actors = { AliveAt(NearbyId, new Vec3(0f, 0f, 1f)) };
            Span<ushort> reAnnounce = stackalloc ushort[8];
            Span<ushort> expired = stackalloc ushort[8];

            authority.Step(ResupplyTicks, actors, reAnnounce, expired);

            WeaponRuntimeState unused = default;
            Assert.Equal(60, ammo.Remaining(NearbyId, 0, in unused));

            // A second pulse adds nothing at all -- the ceiling holds across pulses, not just
            // within one.
            authority.Step(ResupplyTicks * 2, actors, reAnnounce, expired);
            Assert.Equal(60, ammo.Remaining(NearbyId, 0, in unused));
        }

        /// <summary>
        /// A weapon that refuses resupply (the -1 NO-RESUPPLY sentinel) is never topped up, and
        /// the sentinel is never incremented into a value that stops being a sentinel.
        /// </summary>
        [Fact]
        public void AResupplyRespectsTheNoResupplySentinel()
        {
            var ammo = new ActorSpareAmmoPool();
            ammo.SetLoadout(NearbyId, slot: 0, rounds: -1, cap: 60, resupplyPerPulse: 30);

            Assert.Equal(0, ammo.Give(NearbyId, 0));

            WeaponRuntimeState unused = default;
            Assert.Equal(0, ammo.Remaining(NearbyId, 0, in unused));
        }

        /// <summary>
        /// <c>conventions.md</c> section 3.2. <c>ActorManager.AliveActorsInRange</c> returned a
        /// fresh <c>List&lt;Actor&gt;</c> and both <c>Resupply</c> bodies enumerated it — on a
        /// three-second repeat, per deployable, for the whole of every deployable's life. This
        /// is the sweep that replaced it.
        /// </summary>
        [Fact]
        public void AResupplySweepAllocatesNothing()
        {
            (ServerDeployableAuthority authority, RecordingSink sink, ActorSpareAmmoPool ammo) = Build();
            sink.SetHealth(NearbyId, 10f);
            ammo.SetLoadout(NearbyId, slot: 0, rounds: 0, cap: 600, resupplyPerPulse: 30);

            authority.Deploy(ProjectileKind.AmmoBag, OwnerId, Vec3.Zero, Vec3.Zero, 600f, 0);
            authority.Deploy(ProjectileKind.Medipack, OwnerId, Vec3.Zero, Vec3.Zero, 600f, 0);

            HitscanTarget[] actors = { AliveAt(NearbyId, new Vec3(0f, 0f, 1f)) };
            var reAnnounce = new ushort[8];
            var expired = new ushort[8];

            // Warm up: first-call JIT and any lazy initialisation must not be counted.
            for (uint t = 1; t <= ResupplyTicks * 2; t++)
            {
                authority.Step(t, actors, reAnnounce, expired);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (uint t = ResupplyTicks * 2 + 1; t <= ResupplyTicks * 8; t++)
            {
                authority.Step(t, actors, reAnnounce, expired);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);

            // INPUT-INTEGRITY GUARD. A zero above means nothing unless the counter is live: if
            // GC.GetAllocatedBytesForCurrentThread ever stopped reporting on this runtime, the
            // assertion would read green forever and this test would be decoration. Allocating
            // deliberately must move it.
            long beforeControl = GC.GetAllocatedBytesForCurrentThread();
            var deliberate = new byte[4096];
            long controlDelta = GC.GetAllocatedBytesForCurrentThread() - beforeControl;

            Assert.True(
                controlDelta >= deliberate.Length,
                "the allocation counter is not reporting; the zero above proves nothing.");
        }

        /// <summary>
        /// Phase-05 D5 and D9. A client that could run a resupply would move authoritative
        /// health and ammo, which is the single thing those decisions exist to prevent.
        /// </summary>
        /// <remarks>
        /// <b>Structural, and deliberately not a "run it as a client and check nothing happened"
        /// test.</b> There is no client role to pass in — the enforcement is that the objects
        /// which write health and ammo are reachable only from a type the client never
        /// constructs. So this asserts exactly that: nothing named <c>Client*</c> in the
        /// projectile namespace can hold a damage sink or an ammo pool. Wiring one in later
        /// turns this red, which a behavioural test could not do.
        /// </remarks>
        [Fact]
        public void AClientDeployableHealsNobody()
        {
            var forbidden = new HashSet<Type> { typeof(IActorDamageSink), typeof(ISpareAmmoPool), typeof(ActorSpareAmmoPool) };

            foreach (Type type in typeof(ClientProjectileTracker).Assembly.GetTypes())
            {
                if (type.Namespace != typeof(ClientProjectileTracker).Namespace) continue;
                if (!type.Name.StartsWith("Client", StringComparison.Ordinal)) continue;

                foreach (System.Reflection.ConstructorInfo ctor in type.GetConstructors())
                {
                    foreach (System.Reflection.ParameterInfo parameter in ctor.GetParameters())
                    {
                        Assert.DoesNotContain(parameter.ParameterType, forbidden);
                    }
                }

                foreach (System.Reflection.FieldInfo field in type.GetFields(
                             System.Reflection.BindingFlags.Instance
                             | System.Reflection.BindingFlags.NonPublic
                             | System.Reflection.BindingFlags.Public))
                {
                    Assert.DoesNotContain(field.FieldType, forbidden);
                }
            }

            // Input-integrity guard: the sweep above proves nothing if it inspected no types.
            Assert.Contains(
                typeof(ClientProjectileTracker).Assembly.GetTypes(),
                t => t == typeof(ClientProjectileTracker));

            // And the server side really does hold both, so "no Client* type holds one" is a
            // statement about the split rather than about nobody holding them at all.
            System.Reflection.ParameterInfo[] serverCtor =
                typeof(ServerDeployableAuthority).GetConstructors()[0].GetParameters();
            Assert.Contains(serverCtor, p => p.ParameterType == typeof(IActorDamageSink));
            Assert.Contains(serverCtor, p => p.ParameterType == typeof(ActorSpareAmmoPool));
        }

        /// <summary>
        /// A deployable expires on a tick, and its id goes back to the shared pool. A leak here
        /// is what brainstorm criterion 13's five-back-to-back-matches check exists to catch.
        /// </summary>
        [Fact]
        public void ADeployableExpiresAndReturnsItsId()
        {
            var pool = new ProjectileIdPool(16);
            var authority = new ServerDeployableAuthority(
                pool, new RecordingSink(), new ActorSpareAmmoPool(), Tick);

            ushort id = authority.Deploy(
                ProjectileKind.AmmoBag, OwnerId, Vec3.Zero, Vec3.Zero,
                lifetimeSeconds: 1f, currentTick: 0);

            Assert.Equal(1, pool.InUseCount);

            Span<ushort> reAnnounce = stackalloc ushort[8];
            Span<ushort> expired = stackalloc ushort[8];

            for (uint t = 1; t < 30; t++)
            {
                Assert.Equal(
                    0,
                    authority.Step(t, ReadOnlySpan<HitscanTarget>.Empty, reAnnounce, expired)
                        .ExpiredCount);
            }

            DeployableStepResult last =
                authority.Step(30, ReadOnlySpan<HitscanTarget>.Empty, reAnnounce, expired);

            Assert.Equal(1, last.ExpiredCount);
            Assert.Equal(id, expired[0]);
            Assert.Equal(0, authority.LiveCount);
            Assert.Equal(0, pool.InUseCount);
        }

        /// <summary>
        /// Deployables and projectiles draw from ONE id space. If they did not, a bag and a
        /// bullet could hold the same id and a re-announce for one would re-seat the other.
        /// </summary>
        [Fact]
        public void ADeployableAndAProjectileNeverShareAnId()
        {
            var pool = new ProjectileIdPool(16);
            var registry = new ServerProjectileRegistry(pool);
            var catalog = new ProjectileCatalog();
            catalog.Set(
                ProjectileKind.Bullet,
                new ProjectileConfig(300f, 60f, 70f, 60f, 200f, 300f, false));
            var projectiles = new ServerProjectileAuthority(registry, catalog, null, Tick);
            var deployables = new ServerDeployableAuthority(
                pool, new RecordingSink(), new ActorSpareAmmoPool(), Tick);

            var seen = new HashSet<ushort>();
            for (int i = 0; i < 5; i++)
            {
                Assert.True(seen.Add(projectiles.Launch(
                    ProjectileKind.Bullet, Vec3.Zero, new Vec3(0f, 0f, 1f), OwnerId, 0)));
                Assert.True(seen.Add(deployables.Deploy(
                    ProjectileKind.AmmoBag, OwnerId, Vec3.Zero, Vec3.Zero, 60f, 0)));
            }

            Assert.Equal(10, seen.Count);
            Assert.Equal(10, pool.InUseCount);
        }

        /// <summary>
        /// A test sink that records what a deployable did to whom. Mirrors
        /// <c>ServerActorDamageSink</c>'s heal semantics: 30 up to a ceiling of 100, refused on
        /// a corpse, returning the amount applied so a full-health actor costs a medipack
        /// nothing.
        /// </summary>
        private sealed class RecordingSink : IActorDamageSink
        {
            private readonly Dictionary<ushort, float> _health = new Dictionary<ushort, float>();

            public void SetHealth(ushort actorId, float health) => _health[actorId] = health;

            public float HealthOf(ushort actorId)
                => _health.TryGetValue(actorId, out float h) ? h : 0f;

            public DamageOutcome ApplyDamage(
                ushort victimId, float healthDamage, float balanceDamage, ushort attackerId)
                => DamageOutcome.NoOp;

            public float ApplyHeal(ushort actorId, float amount)
            {
                if (amount <= 0f) return 0f;
                if (!_health.TryGetValue(actorId, out float health)) return 0f;

                float after = health + amount;
                if (after > 100f) after = 100f;
                if (after <= health) return 0f;

                _health[actorId] = after;
                return after - health;
            }
        }
    }
}
