using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Projectiles;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-V7 tasks 1-3: the ballistics core, the server authority and the client's flight.
    /// </summary>
    public sealed class ProjectileTests
    {
        private const ushort ShooterId = 1;
        private const ushort VictimId  = 2;

        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        /// <summary>
        /// One position-quantization step. <c>Quantize.PackPos</c> maps ±2048 m onto an
        /// <c>i16</c>, so the finest difference the wire can even express is 4096/65536 m. Two
        /// simulations that agree to inside this are, as far as any observer is concerned, the
        /// same simulation.
        /// </summary>
        private const float OneQuantizationStep = 4096f / 65536f;

        private static ProjectileConfig Bullet(
            float speed = 300f, float lifetime = 2f, float damage = 70f,
            float dropoffEnd = 300f, float[]? dropoff = null)
            => new ProjectileConfig(
                speed, lifetime, damage, balanceDamage: 60f, impactForce: 200f,
                dropoffEnd: dropoffEnd, piercing: false,
                dropoffTable: dropoff ?? Array.Empty<float>());

        private static ProjectileCatalog CatalogOf(ProjectileKind kind, in ProjectileConfig config)
        {
            var catalog = new ProjectileCatalog();
            catalog.Set(kind, in config);
            return catalog;
        }

        // ------------------------------------------------------------------ task 1: ballistics

        /// <summary>
        /// Acceptance criterion 2. If this fails, a 30 Hz player and a 144 Hz player watching the
        /// same bullet see it at measurably different heights, and the server agrees with
        /// whichever one happens to share its tick rate.
        /// </summary>
        /// <remarks>
        /// Plain semi-implicit Euler — which is what <c>Projectile.Update</c> did and what V7's
        /// D4-local text described — drops an extra <c>0.5*g*dt*T</c>, about 33 cm over these two
        /// seconds at 30 Hz. That is five quantization steps, so this test could not have passed
        /// against the integrator the plan specified. <see cref="Ballistics.Step"/> carries the
        /// half-acceleration term and the reason.
        /// </remarks>
        [Fact]
        public void ABulletFollowsTheSameArcAtAnyTimestep()
        {
            ProjectileConfig config = Bullet();
            var origin   = new Vec3(0f, 10f, 0f);
            var velocity = new Vec3(0f, 0f, 1f) * config.Speed;
            Vec3 gravity = Ballistics.EarthGravity;

            var slow = new BallisticState(origin, velocity);
            for (int i = 0; i < 60; i++)   // 2 s at 30 Hz
            {
                Ballistics.Step(ref slow, in config, 1f / 30f, in gravity, out Vec3 d);
                Ballistics.Advance(ref slow, in d);
            }

            var fast = new BallisticState(origin, velocity);
            for (int i = 0; i < 288; i++)  // 2 s at 144 Hz
            {
                Ballistics.Step(ref fast, in config, 1f / 144f, in gravity, out Vec3 d);
                Ballistics.Advance(ref fast, in d);
            }

            float error = (slow.Position - fast.Position).Magnitude;
            Assert.True(
                error <= OneQuantizationStep,
                $"30 Hz and 144 Hz arcs diverged by {error:F4} m, past one quantization step "
                + $"({OneQuantizationStep:F4} m). The gravity term has stopped being exact.");
        }

        /// <summary>
        /// V7-D4-local, pinned deliberately. <b>This is not a bug to fix.</b> The accumulator
        /// advances by MUZZLE speed, so a projectile that has dropped forty metres still accrues
        /// distance as if it were flying flat. Every authored <c>damageDropOff</c> curve in the
        /// game was tuned against that behaviour; making it the true path length would silently
        /// rebalance every weapon.
        /// </summary>
        [Fact]
        public void TheDistanceAccumulatorUsesMuzzleSpeedNotPathLength()
        {
            ProjectileConfig config = Bullet(speed: 100f);

            // Deliberately launched steeply downward, so the true path length per step is much
            // larger than speed*dt and a "corrected" implementation would visibly disagree.
            var state = new BallisticState(new Vec3(0f, 100f, 0f), new Vec3(0f, -400f, 100f));

            Ballistics.Step(ref state, in config, Tick, in Ballistics.EarthGravity, out Vec3 delta);

            Assert.Equal(config.Speed * Tick, state.TravelDistance, precision: 4);
            Assert.True(
                delta.Magnitude > state.TravelDistance * 2f,
                "the fixture stopped exercising the difference: the true path length is no "
                + "longer far larger than the muzzle-speed accumulator, so this test would now "
                + "pass against either implementation.");
        }

        /// <summary>
        /// V7-D5-local. <c>Projectile.cs:105</c> swept <c>delta.magnitude * 2f</c> and then
        /// advanced by <c>delta</c>, so a thin collider was hit by a 30 Hz client and missed by a
        /// 144 Hz one. If this fails, hit registration is a function of framerate again.
        /// </summary>
        [Fact]
        public void ASweptSegmentIsNotDoubleCounted()
        {
            ProjectileConfig config = Bullet();
            var sweep = new RecordingWorldSweep();
            var registry = new ServerProjectileRegistry(new ProjectileIdPool(8));
            var authority = new ServerProjectileAuthority(
                registry, CatalogOf(ProjectileKind.Bullet, in config), sweep, Tick);

            authority.Launch(
                ProjectileKind.Bullet, new Vec3(0f, 0f, 0f), new Vec3(0f, 0f, 1f), ShooterId, 0);

            Span<ProjectileHit> hits = stackalloc ProjectileHit[4];

            // TWO steps, not one. With a single step the recorder has no previous origin to
            // difference against and reports the advance AS the segment, so the assertion below
            // compares a value with itself and cannot fail -- which is what the first version of
            // this test did.
            authority.StepAll(Tick, ReadOnlySpan<HitscanTarget>.Empty, 1, hits);
            authority.StepAll(Tick, ReadOnlySpan<HitscanTarget>.Empty, 2, hits);

            Assert.Equal(2, sweep.Calls);
            Assert.True(sweep.HasMeasuredAdvance, "the recorder never differenced two origins.");
            Assert.True(
                Math.Abs(sweep.PreviousSegmentLength - sweep.LastAdvanceLength) < 1e-4f,
                $"swept {sweep.PreviousSegmentLength:F4} m but then advanced "
                + $"{sweep.LastAdvanceLength:F4} m -- the sweep length has drifted away from the "
                + "step again. A ratio near 2.0 is the original bug returning.");
        }

        // ------------------------------------------------- task 2: authority, expiry, the cap

        /// <summary>
        /// Tick-counted expiry. <c>expireTime = Time.time + lifetime</c> gave the server and each
        /// client their own float deadline, so a projectile vanished on a different frame on
        /// every machine. Both sides now count the same integer from the same launch tick.
        /// </summary>
        [Fact]
        public void AProjectileExpiresOnTheSameTickOnBothSides()
        {
            ProjectileConfig config = Bullet(lifetime: 0.5f);   // 15 ticks at 30 Hz
            Assert.Equal(15u, config.LifetimeTicks(Tick));

            var registry = new ServerProjectileRegistry(new ProjectileIdPool(8));
            var authority = new ServerProjectileAuthority(
                registry, CatalogOf(ProjectileKind.Bullet, in config), null, Tick);

            ushort id = authority.Launch(
                ProjectileKind.Bullet, Vec3.Zero, new Vec3(0f, 0f, 1f), ShooterId, 100);
            Span<ProjectileHit> hits = stackalloc ProjectileHit[4];

            for (uint t = 101; t < 115; t++)
            {
                Assert.Equal(0, authority.StepAll(Tick, ReadOnlySpan<HitscanTarget>.Empty, t, hits));
                Assert.True(registry.IsLive(id), $"expired early, at tick {t}");
            }

            Assert.Equal(1, authority.StepAll(Tick, ReadOnlySpan<HitscanTarget>.Empty, 115, hits));
            Assert.Equal(ProjectileEndReason.Expired, hits[0].Reason);
            Assert.False(registry.IsLive(id));
        }

        /// <summary>
        /// Brainstorm criterion 5's "exactly once" clause, at the projectile level. A second hit
        /// from a projectile that already landed would double every damage number in the game.
        /// </summary>
        [Fact]
        public void AProjectileHitAppliesDamageOnce()
        {
            ProjectileConfig config = Bullet(speed: 30f, damage: 70f);
            var registry = new ServerProjectileRegistry(new ProjectileIdPool(8));
            var authority = new ServerProjectileAuthority(
                registry, CatalogOf(ProjectileKind.Bullet, in config), null, Tick);

            authority.Launch(
                ProjectileKind.Bullet, MuzzleAt(0f), Forward, ShooterId, 0);

            // One tick at 30 m/s advances 1 m; the target's torso sits inside that segment.
            HitscanTarget[] targets = { AliveAt(VictimId, new Vec3(0f, 0f, 0.5f)) };

            Span<ProjectileHit> hits = stackalloc ProjectileHit[4];
            int first = authority.StepAll(Tick, targets, 1, hits);

            Assert.Equal(1, first);
            Assert.Equal(ProjectileEndReason.Actor, hits[0].Reason);
            Assert.Equal(VictimId, hits[0].VictimActorId);
            Assert.True(hits[0].HealthDamage > 0f);
            Assert.Equal(0, registry.LiveCount);

            // The projectile is gone, so a second tick has nothing to resolve. If this ever
            // returns 1, the registry is keeping a resolved projectile alive.
            Assert.Equal(0, authority.StepAll(Tick, targets, 2, hits));
        }

        /// <summary>
        /// A projectile never hits its own shooter. <c>Projectile.cs:136-139</c> nudges past the
        /// owner's hitbox rather than detonating in their chest; losing this makes every weapon
        /// kill its user at point-blank range.
        /// </summary>
        [Fact]
        public void AProjectileDoesNotHitItsOwnShooter()
        {
            ProjectileConfig config = Bullet(speed: 30f);
            var registry = new ServerProjectileRegistry(new ProjectileIdPool(8));
            var authority = new ServerProjectileAuthority(
                registry, CatalogOf(ProjectileKind.Bullet, in config), null, Tick);

            authority.Launch(
                ProjectileKind.Bullet, MuzzleAt(0f), Forward, ShooterId, 0);

            HitscanTarget[] targets = { AliveAt(ShooterId, new Vec3(0f, 0f, 0.5f)) };

            Span<ProjectileHit> hits = stackalloc ProjectileHit[4];
            Assert.Equal(0, authority.StepAll(Tick, targets, 1, hits));
            Assert.Equal(1, registry.LiveCount);

            // INPUT-INTEGRITY GUARD, not a second assertion of the same thing. The check above
            // is "no hit", which a fixture that simply misses would also satisfy -- and an
            // earlier draft of this test did exactly that, firing between the target's ankles
            // and passing whether or not self-exclusion existed. Re-running the identical
            // geometry with the hitbox belonging to somebody else must HIT; if this line goes
            // red, the test above has stopped proving anything.
            var control = new ServerProjectileRegistry(new ProjectileIdPool(8));
            var controlAuthority = new ServerProjectileAuthority(
                control, CatalogOf(ProjectileKind.Bullet, in config), null, Tick);
            controlAuthority.Launch(
                ProjectileKind.Bullet, MuzzleAt(0f), Forward, ShooterId, 0);

            HitscanTarget[] stranger = { AliveAt(VictimId, new Vec3(0f, 0f, 0.5f)) };
            Assert.Equal(1, controlAuthority.StepAll(Tick, stranger, 1, hits));
        }

        /// <summary>
        /// V7 section 5's tick-budget guard, scored at 15. At the cap the OLDEST projectile is
        /// expired to make room, so the shot always happens — refusing the launch instead would
        /// make a weapon silently stop firing under exactly the load a player notices most.
        /// </summary>
        [Fact]
        public void ThePerShooterProjectileCapExpiresTheOldest()
        {
            ProjectileConfig config = Bullet(lifetime: 60f);
            var registry = new ServerProjectileRegistry(new ProjectileIdPool(64), perShooterCap: 3);
            var authority = new ServerProjectileAuthority(
                registry, CatalogOf(ProjectileKind.Bullet, in config), null, Tick);

            ushort first  = authority.Launch(ProjectileKind.Bullet, Vec3.Zero, Forward, ShooterId, 10);
            ushort second = authority.Launch(ProjectileKind.Bullet, Vec3.Zero, Forward, ShooterId, 11);
            ushort third  = authority.Launch(ProjectileKind.Bullet, Vec3.Zero, Forward, ShooterId, 12);

            Assert.Equal(3, registry.LiveCountFor(ShooterId));

            ushort fourth = authority.Launch(ProjectileKind.Bullet, Vec3.Zero, Forward, ShooterId, 13);

            Assert.Equal(3, registry.LiveCountFor(ShooterId));
            Assert.False(registry.IsLive(first));
            Assert.True(registry.IsLive(second));
            Assert.True(registry.IsLive(third));
            Assert.True(registry.IsLive(fourth));
        }

        /// <summary>
        /// The cap is per shooter, not global. If it were global, one player firing an automatic
        /// weapon would delete everyone else's rockets.
        /// </summary>
        [Fact]
        public void ThePerShooterCapDoesNotReachAnotherShootersProjectiles()
        {
            ProjectileConfig config = Bullet(lifetime: 60f);
            var registry = new ServerProjectileRegistry(new ProjectileIdPool(64), perShooterCap: 2);
            var authority = new ServerProjectileAuthority(
                registry, CatalogOf(ProjectileKind.Bullet, in config), null, Tick);

            ushort other = authority.Launch(ProjectileKind.Bullet, Vec3.Zero, Forward, 7, 10);
            authority.Launch(ProjectileKind.Bullet, Vec3.Zero, Forward, ShooterId, 11);
            authority.Launch(ProjectileKind.Bullet, Vec3.Zero, Forward, ShooterId, 12);
            authority.Launch(ProjectileKind.Bullet, Vec3.Zero, Forward, ShooterId, 13);

            Assert.True(registry.IsLive(other));
            Assert.Equal(2, registry.LiveCountFor(ShooterId));
        }

        /// <summary>
        /// V7 section 5 makes the hitscan fallback a PRECONDITION of the stepper, not a
        /// contingency: flipping it must divert bullets to the proven phase-05 path without
        /// changing what a hit is worth. At zero range no drop-off applies, so the stepper's
        /// number is the authored damage times the hitbox multiplier — the same two factors
        /// <see cref="ServerFireResolver"/> multiplies.
        /// </summary>
        [Fact]
        public void TheHitscanFallbackProducesTheSameDamageAsTheStepper()
        {
            ProjectileConfig config = Bullet(damage: 70f);
            var registry = new ServerProjectileRegistry(new ProjectileIdPool(8));
            var authority = new ServerProjectileAuthority(
                registry, CatalogOf(ProjectileKind.Bullet, in config), null, Tick);

            Assert.True(authority.StepsKind(ProjectileKind.Bullet));

            authority.HitscanBullets = true;

            Assert.False(authority.StepsKind(ProjectileKind.Bullet));
            Assert.Equal(
                0,
                authority.Launch(ProjectileKind.Bullet, Vec3.Zero, Forward, ShooterId, 0));
            Assert.Equal(0, registry.LiveCount);

            // Other kinds keep stepping -- the fallback is bullets only, because only bullets
            // arrive in the volume that threatens the tick budget.
            Assert.True(authority.StepsKind(ProjectileKind.Rocket));

            // And the two paths agree on the number at zero range -- computed by BOTH, rather
            // than by one and then compared with itself. ServerFireResolver.DamageFor is the
            // phase-05 hitscan path's damage function; ProjectileDamage.DamageFor is the
            // stepper's. A weapon config with the same damage and no drop-off inside the range
            // must produce the same number from each.
            var hitscanWeapon = new WeaponConfig(
                cooldown: 0.1f, spread: 0f, projectilesPerShot: 1, range: 300f,
                damage: 70f, force: 200f, clipSize: 30);

            float viaHitscan = ServerFireResolver.DamageFor(in hitscanWeapon, HitboxType.Body, 0f);
            float viaStepper = ProjectileDamage.DamageFor(in config, 0f)
                * ServerFireResolver.HitboxMultiplier(HitboxType.Body);

            Assert.Equal(viaHitscan, viaStepper, 3);
        }

        /// <summary>
        /// Task 8 plus brainstorm criterion 13. A headless server used to hold every detonated
        /// projectile alive for eighteen seconds so particles nobody could see could finish;
        /// sixteen players and thirty-two bots trading rockets grew that pile without bound.
        /// </summary>
        [Fact]
        public void TheServerProjectileCountReturnsToZeroWithinOneTickOfTheLastDetonation()
        {
            ProjectileConfig config = Bullet(speed: 30f);
            var pool = new ProjectileIdPool(32);
            var registry = new ServerProjectileRegistry(pool);
            var authority = new ServerProjectileAuthority(
                registry, CatalogOf(ProjectileKind.Bullet, in config), null, Tick);

            for (ushort shooter = 1; shooter <= 5; shooter++)
            {
                authority.Launch(ProjectileKind.Bullet, MuzzleAt(0f), Forward, shooter, 0);
            }
            Assert.Equal(5, registry.LiveCount);

            HitscanTarget[] targets = { AliveAt(60, new Vec3(0f, 0f, 0.5f)) };
            Span<ProjectileHit> hits = stackalloc ProjectileHit[8];
            Assert.Equal(5, authority.StepAll(Tick, targets, 1, hits));

            Assert.Equal(0, registry.LiveCount);

            // The ids go back too -- a pool that leaked would pass the count check above and
            // still exhaust itself across five back-to-back matches.
            Assert.Equal(0, pool.InUseCount);
            Assert.Equal(pool.Capacity, pool.FreeCount);
        }

        /// <summary>
        /// Brainstorm criterion 13, the id-pool half. Five back-to-back matches must not leave a
        /// single id behind.
        /// </summary>
        [Fact]
        public void TheProjectileIdPoolIsCleanAcrossFiveMatches()
        {
            ProjectileConfig config = Bullet(lifetime: 60f);
            var pool = new ProjectileIdPool(32);
            var registry = new ServerProjectileRegistry(pool);
            var authority = new ServerProjectileAuthority(
                registry, CatalogOf(ProjectileKind.Bullet, in config), null, Tick);

            for (int match = 0; match < 5; match++)
            {
                for (ushort shooter = 1; shooter <= 6; shooter++)
                {
                    authority.Launch(ProjectileKind.Bullet, Vec3.Zero, Forward, shooter, 0);
                }

                authority.Reset();

                Assert.Equal(0, registry.LiveCount);
                Assert.Equal(0, pool.InUseCount);
                Assert.Equal(pool.Capacity, pool.FreeCount);
            }
        }

        // -------------------------------------------------------- task 3: the client's flight

        /// <summary>
        /// A launch spends the one-way latency in flight. Rendering it at <c>origin</c> would put
        /// every tracer visibly behind where the server says it is, and the error grows with ping.
        /// </summary>
        /// <remarks>
        /// <b>The server reference is seeded from the DEQUANTIZED parameters, not the authored
        /// ones.</b> An earlier draft compared against the exact launch values and failed by
        /// 7.7 cm — which was not a fast-forward defect at all but the wire's own velocity
        /// quantization (<c>PackVel16</c> resolves to about 0.5 m/s, which over 200 ms of flight
        /// is 10 cm). Comparing against the authored values measures the encoder; comparing
        /// against what the message actually said measures the thing this test is named for.
        /// The encoder has its own tests.
        /// </remarks>
        [Fact]
        public void AFastForwardedProjectileMatchesTheServersPositionAtReceipt()
        {
            ProjectileConfig config = Bullet();
            var catalog = CatalogOf(ProjectileKind.Bullet, in config);

            var origin   = new Vec3(1f, 2f, 3f);
            var velocity = new Vec3(0f, 0f, 1f) * config.Speed;

            var message = new ProjectileSpawnMessage(
                projectileId: 5, ownerActorId: ShooterId, kind: ProjectileKind.Bullet,
                originX: Quantize.PackPos(origin.X),
                originY: Quantize.PackPos(origin.Y),
                originZ: Quantize.PackPos(origin.Z),
                velX: Quantize.PackVel16(velocity.X),
                velY: Quantize.PackVel16(velocity.Y),
                velZ: Quantize.PackVel16(velocity.Z),
                spawnTick: 1000,
                remainingLifetimeDeciseconds:
                    ProjectileSpawnMessage.PackRemainingLifetime(config.Lifetime));

            // What the server would compute from the parameters it actually sent.
            const int elapsed = 6;   // 200 ms of one-way latency
            var server = new BallisticState(
                new Vec3(
                    Quantize.UnpackPos(message.OriginX),
                    Quantize.UnpackPos(message.OriginY),
                    Quantize.UnpackPos(message.OriginZ)),
                new Vec3(
                    Quantize.UnpackVel16(message.VelX),
                    Quantize.UnpackVel16(message.VelY),
                    Quantize.UnpackVel16(message.VelZ)));
            Ballistics.FastForward(ref server, in config, elapsed, Tick, in Ballistics.EarthGravity);

            var tracker = new ClientProjectileTracker(catalog, Tick);
            ProjectileApplyResult result = tracker.Apply(in message, 1000 + elapsed);

            Assert.Equal(ProjectileApplyAction.Spawn, result.Action);
            Assert.Equal(elapsed, result.FastForwardedTicks);

            float error = (result.Position - server.Position).Magnitude;
            Assert.True(
                error <= 1e-3f,
                $"client placed the projectile {error:F4} m from where the server's own stepper "
                + "puts the parameters it sent -- the two integrations have diverged.");

            // And the catch-up is real: without it the client would render at the origin, a
            // whole 60 m behind at 300 m/s. This is what stops the test passing on a no-op.
            Assert.True(
                (result.Position - origin).Magnitude > 50f,
                "the fast-forward advanced almost nothing; the elapsed-tick calculation is wrong.");
        }

        /// <summary>
        /// V7-D6 and V7-D8's shared mechanism. Without it a Javelin re-parameterizing at 5 Hz is
        /// a NEW missile every 200 ms, and a two-second flight fills the sky with ten of them.
        /// </summary>
        [Fact]
        public void ARepeatedIdReSeatsRatherThanDuplicating()
        {
            ProjectileConfig config = Bullet(lifetime: 10f);
            var tracker = new ClientProjectileTracker(
                CatalogOf(ProjectileKind.GuidedMissile, in config), Tick);

            Assert.Equal(ProjectileApplyAction.Spawn, tracker.Apply(Missile(9, 100), 100).Action);
            Assert.Equal(1, tracker.LiveCount);

            Assert.Equal(ProjectileApplyAction.ReSeat, tracker.Apply(Missile(9, 106), 106).Action);
            Assert.Equal(ProjectileApplyAction.ReSeat, tracker.Apply(Missile(9, 112), 112).Action);
            Assert.Equal(1, tracker.LiveCount);

            // A different id is a different missile, and must still spawn.
            Assert.Equal(ProjectileApplyAction.Spawn, tracker.Apply(Missile(10, 112), 112).Action);
            Assert.Equal(2, tracker.LiveCount);
        }

        /// <summary>
        /// V7-D6, the server half: re-parameterizing writes through the SAME registry row rather
        /// than allocating a second id, so the server's own record of a guided missile stays one
        /// record.
        /// </summary>
        [Fact]
        public void AGuidedMissileReParameterizesWithTheSameId()
        {
            ProjectileConfig config = Bullet(lifetime: 10f);
            var pool = new ProjectileIdPool(16);
            var registry = new ServerProjectileRegistry(pool);
            var authority = new ServerProjectileAuthority(
                registry, CatalogOf(ProjectileKind.GuidedMissile, in config), null, Tick);

            ushort id = authority.Launch(
                ProjectileKind.GuidedMissile, Vec3.Zero, Forward, ShooterId, 0);

            int inUseAfterLaunch = pool.InUseCount;

            for (uint t = 6; t <= 60; t += 6)
            {
                var steered = new BallisticState(new Vec3(0f, t, 0f), new Vec3(1f, 0f, 0f));
                Assert.True(registry.ReSeat(id, in steered, expiryTick: 300));
            }

            Assert.Equal(inUseAfterLaunch, pool.InUseCount);
            Assert.Equal(1, registry.LiveCount);
            Assert.Equal(new Vec3(0f, 60f, 0f), registry.StateAt(registry.SlotOf(id)).Position);
        }

        /// <summary>
        /// Bandwidth, feeding criterion 9. V7-D6 budgeted "~95 B/s per missile in flight".
        /// </summary>
        /// <remarks>
        /// <b>Reads the rate constant the production driver reads</b>, rather than a literal 5.
        /// <c>ProjectileNetSync</c> paces its re-parameterization from
        /// <see cref="ServerDeployableAuthority.GuidedReAnnounceTicks"/>, so changing the rate
        /// moves this number with it and the budget cannot silently be exceeded. It measures
        /// only the payload: framing, the channel envelope and the per-recipient fan-out of a
        /// broadcast are all on top, which is why the ceiling has headroom rather than sitting
        /// exactly on the budget.
        /// </remarks>
        [Fact]
        public void AGuidedMissileCostsAboutOneHundredBytesPerSecondOfPayload()
        {
            float perSecond = ProtocolConstants.SIM_TICK_RATE
                / (float)ServerDeployableAuthority.GuidedReAnnounceTicks;

            Assert.Equal(5f, perSecond, 3);

            float bytesPerSecond = perSecond * ProjectileSpawnMessage.Size;
            Assert.Equal(100f, bytesPerSecond, 3);

            // The ceiling, and what to do if it is ever hit: V7 section 5 lists dropping this
            // rate from 5 Hz to 3 Hz as the FIRST bandwidth fallback, which is a change to
            // GuidedReAnnounceTicks and would move the number above with it.
            Assert.True(
                bytesPerSecond <= 110f,
                $"a missile now costs {bytesPerSecond} B/s of payload against a ~95 B/s budget. "
                + "Lower ServerDeployableAuthority.GuidedReAnnounceTicks rather than raising "
                + "this ceiling.");
        }

        /// <summary>
        /// V7-D3, enforced structurally rather than by inspection: the client's projectile object
        /// has no damage API at all, so there is no path from a modified client to a damage
        /// number. A member appearing here means someone gave the client one.
        /// </summary>
        [Fact]
        public void AClientProjectileAppliesNoDamage()
        {
            foreach (System.Reflection.MemberInfo member in
                     typeof(ClientProjectileTracker).GetMembers(
                         System.Reflection.BindingFlags.Public
                         | System.Reflection.BindingFlags.Instance
                         | System.Reflection.BindingFlags.Static
                         | System.Reflection.BindingFlags.DeclaredOnly))
            {
                Assert.DoesNotContain("Damage", member.Name, StringComparison.Ordinal);
                Assert.DoesNotContain("Heal", member.Name, StringComparison.Ordinal);
            }

            foreach (System.Reflection.FieldInfo field in
                     typeof(ProjectileApplyResult).GetFields())
            {
                Assert.DoesNotContain("Damage", field.Name, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// A launch too old to be worth drawing is dropped rather than rendered and instantly
        /// expired. Without the guard a heavily-retransmitted launch produces a tracer that
        /// appears and vanishes in the same frame.
        /// </summary>
        [Fact]
        public void ALaunchOlderThanItsLifetimeIsNotRendered()
        {
            ProjectileConfig config = Bullet();
            var tracker = new ClientProjectileTracker(
                CatalogOf(ProjectileKind.Bullet, in config), Tick);

            ProjectileApplyResult stale = tracker.Apply(
                Missile(3, 0, kind: ProjectileKind.Bullet),
                (uint)(ClientProjectileTracker.MaxFastForwardTicks + 1));

            Assert.Equal(ProjectileApplyAction.Ignore, stale.Action);
            Assert.Equal(0, tracker.LiveCount);
        }

        /// <summary>
        /// The client's countdown is monotonic and drives despawn on its own, so a projectile
        /// whose terminal event never arrives still goes away.
        /// </summary>
        [Fact]
        public void AClientProjectileExpiresOnItsOwnCountdown()
        {
            ProjectileConfig config = Bullet(lifetime: 1f);
            var tracker = new ClientProjectileTracker(
                CatalogOf(ProjectileKind.Bullet, in config), Tick);

            tracker.Apply(Missile(4, 500, kind: ProjectileKind.Bullet, lifetimeSeconds: 1f), 500);
            Assert.Equal(1, tracker.LiveCount);

            Span<ushort> expired = stackalloc ushort[4];
            int total = 0;
            for (int i = 0; i < 40; i++) total += tracker.Tick(Tick, expired);

            Assert.Equal(1, total);
            Assert.Equal(0, tracker.LiveCount);
        }

        // ------------------------------------------------------------------------- fixtures

        private static Vec3 Forward => new Vec3(0f, 0f, 1f);

        /// <summary>
        /// Torso height for a humanoid standing at y = 0. <see cref="HitboxSet.Humanoid"/> takes
        /// a FEET position and puts the torso 1.2 m above it, so a fixture that launches from
        /// the origin fires between a target's ankles -- and an "asserts no hit" test then passes
        /// for the wrong reason.
        /// </summary>
        private const float MuzzleHeight = 1.2f;

        private static Vec3 MuzzleAt(float z) => new Vec3(0f, MuzzleHeight, z);

        private static HitscanTarget AliveAt(ushort actorId, in Vec3 feetPosition)
            => new HitscanTarget(actorId, isAlive: true, HitboxSet.Humanoid(in feetPosition));

        private static ProjectileSpawnMessage Missile(
            ushort id, ushort spawnTick,
            ProjectileKind kind = ProjectileKind.GuidedMissile,
            float lifetimeSeconds = 10f)
            => new ProjectileSpawnMessage(
                id, ShooterId, kind,
                Quantize.PackPos(0f), Quantize.PackPos(50f), Quantize.PackPos(0f),
                Quantize.PackVel16(0f), Quantize.PackVel16(0f), Quantize.PackVel16(100f),
                spawnTick,
                ProjectileSpawnMessage.PackRemainingLifetime(lifetimeSeconds));

        /// <summary>
        /// Records the segment it was asked to sweep so
        /// <see cref="ASweptSegmentIsNotDoubleCounted"/> can compare it against the advance.
        /// Never blocks — a blocking sweep would end the projectile before the advance happened.
        /// </summary>
        private sealed class RecordingWorldSweep : IProjectileWorldSweep
        {
            public int Calls { get; private set; }

            /// <summary>The segment handed to the PREVIOUS call — the one the advance follows.</summary>
            public float PreviousSegmentLength { get; private set; }

            /// <summary>How far the projectile actually moved between the last two calls.</summary>
            public float LastAdvanceLength { get; private set; }

            /// <summary>
            /// False until two origins have been differenced. Without this the caller cannot
            /// tell a measured advance from the placeholder a single call leaves behind.
            /// </summary>
            public bool HasMeasuredAdvance { get; private set; }

            private float _lastSegmentLength;
            private Vec3 _lastFrom;
            private bool _haveLastFrom;

            public bool Sweep(in Vec3 from, in Vec3 to, out Vec3 hitPoint)
            {
                Calls++;

                if (_haveLastFrom)
                {
                    LastAdvanceLength     = (from - _lastFrom).Magnitude;
                    PreviousSegmentLength = _lastSegmentLength;
                    HasMeasuredAdvance    = true;
                }

                _lastSegmentLength = (to - from).Magnitude;
                _lastFrom          = from;
                _haveLastFrom      = true;

                hitPoint = default;
                return false;
            }
        }
    }
}
