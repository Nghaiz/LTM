using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-02 tasks 2 and 3, criteria 3 (hit rate at 150 ms), 4 (hitboxes never stuck in the
    /// past) and 5 (rewind clamped at 200 ms).
    /// </summary>
    public sealed class LagCompensationTests
    {
        private const ushort Shooter = 1;
        private const ushort Target = 2;

        /// <summary>Eye height. Aims at the torso of a target standing on the ground plane.</summary>
        private static readonly Vec3 MuzzleOrigin = new Vec3(0f, 1.5f, 0f);

        // ------------------------------------------------------------------ criterion 5

        [Fact]
        public void RewindIsClampedAtTwoHundredMilliseconds()
        {
            // Criterion 5. The clamp is the anti-abuse limit from protocol-spec.md section 7.2:
            // without it, a cheater inflating their reported ping shoots arbitrarily far into
            // the past.
            Assert.Equal(ProtocolConstants.MAX_REWIND_TICKS, LagCompensator.RewindTicks(1000f));
            Assert.Equal(6, ProtocolConstants.MAX_REWIND_TICKS);
            Assert.True(LagCompensator.RewindTicks(99999f) <= 6);
        }

        [Fact]
        public void RewindMatchesTheSpecFormula()
        {
            // rewindMs = rtt/2 + INTERP_BUFFER_MS, divided by 33.33 ms per tick.
            // 100 ms rtt: (50 + 100) / 33.33 = 4.5 -> 4 (banker's rounding on the .5).
            Assert.Equal(4, LagCompensator.RewindTicks(100f));

            // 150 ms rtt: (75 + 100) / 33.33 = 5.25 -> 5.
            Assert.Equal(5, LagCompensator.RewindTicks(150f));

            // 0 ms rtt still rewinds by the interpolation buffer alone: 100 / 33.33 = 3.
            Assert.Equal(3, LagCompensator.RewindTicks(0f));
        }

        [Fact]
        public void RewindNeverGoesNegativeOnGarbageInput()
        {
            Assert.Equal(3, LagCompensator.RewindTicks(-500f));
            Assert.Equal(3, LagCompensator.RewindTicks(float.NaN));
        }

        [Fact]
        public void TheTargetTickSaturatesAtZeroRatherThanWrapping()
        {
            // In the first 200 ms of a match currentTick is smaller than the rewind. An
            // unsigned subtraction there lands near four billion, which no history frame
            // matches, so every opening shot would silently fall back to the present.
            Assert.Equal(0u, LagCompensator.ResolveTargetTick(2, 150f));
            Assert.Equal(95u, LagCompensator.ResolveTargetTick(100, 150f));
        }

        // ------------------------------------------------------------------ criterion 3

        [Fact]
        public void AtOneHundredFiftyMillisecondsAStrafingTargetIsHitAtLeastSeventyFivePercent()
        {
            // Criterion 3. The shooter aims at where their client RENDERED the target, which is
            // rtt/2 + interp behind the server. Lag compensation rewinds to that moment.
            var scenario = new StrafingScenario(strafeSpeed: 5f, rangeMetres: 20f);
            int hits = scenario.FireVolley(shots: 20, rttMs: 150f, compensated: true);

            Assert.True(hits >= 15, $"expected at least 15 of 20 hits, landed {hits}");
        }

        [Fact]
        public void WithoutLagCompensationTheSameVolleyMostlyMisses()
        {
            // The control for the report's headline chart. Same aim points, same target, the
            // only difference is that the server resolves against the present instead of the
            // moment the shooter saw.
            var scenario = new StrafingScenario(strafeSpeed: 5f, rangeMetres: 20f);
            int hits = scenario.FireVolley(shots: 20, rttMs: 150f, compensated: false);

            Assert.True(hits <= 5, $"expected at most 5 of 20 hits uncompensated, landed {hits}");
        }

        [Theory]
        [InlineData(50f)]
        [InlineData(100f)]
        [InlineData(150f)]
        [InlineData(200f)]
        public void CompensationHoldsTheHitRateUpAcrossEveryTestedPing(float rttMs)
        {
            // The experiment table in the phase document: hit rate against RTT, with and
            // without. The point of the chart is that the compensated series stays flat.
            var scenario = new StrafingScenario(strafeSpeed: 5f, rangeMetres: 20f);

            int compensated = scenario.FireVolley(shots: 20, rttMs, compensated: true);
            int uncompensated = scenario.FireVolley(shots: 20, rttMs, compensated: false);

            Assert.True(compensated >= 15, $"{rttMs} ms: only {compensated}/20 compensated hits");
            Assert.True(compensated > uncompensated,
                $"{rttMs} ms: compensation made no difference ({compensated} vs {uncompensated})");
        }

        [Fact]
        public void PastTheRewindClampAFastTargetStartsToBeMissed()
        {
            // Proof that the corrected fixture is actually sensitive, and the clamp is real.
            //
            // At 300 ms the client saw the world 250 ms ago, but MAX_REWIND_MS caps the server
            // at 200 — a deliberate 50 ms disagreement, because the alternative is letting a
            // cheater inflate their ping and shoot arbitrarily far into the past. At 5 m/s that
            // is 0.25 m of strafe, which is still inside the hitbox, so the volley at that speed
            // reports 100% and the clamp is invisible. Wind the target up to 20 m/s and the same
            // 50 ms is a full metre, which is not.
            //
            // This is also the test that would have caught the self-fulfilling fixture: when the
            // aim point was derived by calling RewindTicks, the clamp applied to BOTH sides and
            // this stayed at 100% no matter how fast the target moved.
            var fast = new StrafingScenario(strafeSpeed: 20f, rangeMetres: 20f);

            int insideTheClamp = fast.FireVolley(shots: 20, rttMs: 200f, compensated: true);
            int pastTheClamp = fast.FireVolley(shots: 20, rttMs: 400f, compensated: true);

            Assert.True(insideTheClamp >= 15,
                $"compensation should still work at 200 ms; landed {insideTheClamp}/20");
            Assert.True(pastTheClamp < insideTheClamp,
                $"the rewind clamp had no effect on a 20 m/s target: {pastTheClamp}/20 past the "
                + $"clamp against {insideTheClamp}/20 inside it — the aim point is probably still "
                + "being derived from the code under test");
        }

        [Fact]
        public void AStationaryTargetIsHitWithOrWithoutCompensation()
        {
            // The sanity check that keeps the volley tests honest: if this failed, the strafing
            // result would be measuring a broken raycast rather than compensation.
            var scenario = new StrafingScenario(strafeSpeed: 0f, rangeMetres: 20f);

            Assert.Equal(20, scenario.FireVolley(shots: 20, rttMs: 150f, compensated: true));
            Assert.Equal(20, scenario.FireVolley(shots: 20, rttMs: 150f, compensated: false));
        }

        // ------------------------------------------------------------------ criterion 4

        [Fact]
        public void AThrowingOcclusionCallbackLeavesNothingStuckInThePast()
        {
            // Criterion 4, restated for a design where nothing moves. Task 3's shape mutates
            // every actor's hitboxes into the past and restores them in a finally; forgetting
            // the finally leaves them stuck there permanently, and the symptom is bullets
            // hitting empty air minutes later. Storing world-space poses in history means the
            // rewound pose is a value the raycast reads, so there is no state an exception
            // could leave behind. This test proves that by throwing from the one callback that
            // reaches out of the resolver, then re-firing.
            var world = new RewindWorld();
            HitResult before = world.Fire(rttMs: 150f, currentTick: 100);
            Assert.True(before.Hit);

            world.Compensator.Occlusion = (_, _, _) => throw new InvalidOperationException("boom");
            Assert.Throws<InvalidOperationException>(() => world.Fire(rttMs: 150f, currentTick: 100));

            world.Compensator.Occlusion = null;
            HitResult after = world.Fire(rttMs: 150f, currentTick: 100);

            Assert.True(after.Hit);
            Assert.Equal(before.TargetActorId, after.TargetActorId);
            Assert.Equal(before.HitboxType, after.HitboxType);
            Assert.Equal(before.Distance, after.Distance, 5);
        }

        [Fact]
        public void HistoryIsUnchangedByResolvingAShot()
        {
            var world = new RewindWorld();
            Assert.True(world.History.TryGetFrame(Target, 95, out HitboxHistory.Frame before));

            world.Fire(rttMs: 150f, currentTick: 100);

            Assert.True(world.History.TryGetFrame(Target, 95, out HitboxHistory.Frame after));
            Assert.Equal(before.Boxes.Torso.Center.X, after.Boxes.Torso.Center.X, 5);
            Assert.Equal(before.Tick, after.Tick);
        }

        // ------------------------------------------------------------------ trap 5

        [Fact]
        public void TheShooterIsNeverRewoundIntoTheirOwnShot()
        {
            // Trap 5. The shooter sits at the origin with the muzzle inside their own hitboxes,
            // so if they were a candidate they would be hit by every shot they fired.
            var history = new HitboxHistory();
            var compensator = new LagCompensator(history);

            var targets = new[]
            {
                new HitscanTarget(Shooter, true, HitboxSet.Humanoid(Vec3.Zero)),
            };

            HitResult hit = compensator.ResolveHitscan(
                targets, Shooter, MuzzleOrigin, new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.False(hit.Hit);
        }

        [Fact]
        public void ADeadActorIsNotAValidTarget()
        {
            var compensator = new LagCompensator(new HitboxHistory());
            var targets = new[]
            {
                new HitscanTarget(Target, false, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))),
            };

            HitResult hit = compensator.ResolveHitscan(
                targets, Shooter, MuzzleOrigin, new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.False(hit.Hit);
        }

        // ------------------------------------------------------------------ resolution details

        [Fact]
        public void TheNearestOfTwoStackedTargetsIsTheOneHit()
        {
            var compensator = new LagCompensator(new HitboxHistory());
            var targets = new[]
            {
                new HitscanTarget(5, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 30f))),
                new HitscanTarget(6, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))),
            };

            HitResult hit = compensator.ResolveHitscan(
                targets, Shooter, MuzzleOrigin, new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.True(hit.Hit);
            Assert.Equal(6, hit.TargetActorId);
        }

        [Fact]
        public void AShotBeyondWeaponRangeMisses()
        {
            var compensator = new LagCompensator(new HitboxHistory());
            var targets = new[]
            {
                new HitscanTarget(Target, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 200f))),
            };

            HitResult hit = compensator.ResolveHitscan(
                targets, Shooter, MuzzleOrigin, new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.False(hit.Hit);
        }

        [Fact]
        public void AHeadshotIsReportedAsAHeadshot()
        {
            var compensator = new LagCompensator(new HitboxHistory());
            HitboxSet target = HitboxSet.Humanoid(new Vec3(0f, 0f, 10f));

            // Aim flat at head height rather than through the torso.
            var origin = new Vec3(0f, target.Head.Center.Y, 0f);

            HitResult hit = compensator.ResolveHitscan(
                new[] { new HitscanTarget(Target, true, target) },
                Shooter, origin, new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.True(hit.Hit);
            Assert.Equal(HitboxType.Head, hit.HitboxType);
            Assert.True(hit.IsHeadshot);
        }

        [Fact]
        public void AWallBetweenShooterAndTargetBlocksTheShot()
        {
            // architecture.md section 9: "shooting through walls — the server raycasts itself,
            // with a line-of-sight check". This is that check's seam.
            var compensator = new LagCompensator(new HitboxHistory())
            {
                Occlusion = (_, _, _) => true,
            };

            HitResult hit = compensator.ResolveHitscan(
                new[] { new HitscanTarget(Target, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))) },
                Shooter, MuzzleOrigin, new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.False(hit.Hit);
            Assert.Equal(1, compensator.ShotsOccluded);
        }

        [Fact]
        public void OcclusionIsAskedOnceForTheWinnerNotOncePerCandidate()
        {
            int calls = 0;
            var compensator = new LagCompensator(new HitboxHistory())
            {
                Occlusion = (_, _, _) => { calls++; return false; },
            };

            var targets = new[]
            {
                new HitscanTarget(5, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))),
                new HitscanTarget(6, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 20f))),
                new HitscanTarget(7, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 30f))),
            };

            compensator.ResolveHitscan(
                targets, Shooter, MuzzleOrigin, new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.Equal(1, calls);
        }

        [Fact]
        public void AZeroDirectionResolvesAsAMissRatherThanNaN()
        {
            var compensator = new LagCompensator(new HitboxHistory());

            HitResult hit = compensator.ResolveHitscan(
                new[] { new HitscanTarget(Target, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))) },
                Shooter, MuzzleOrigin, Vec3.Zero,
                maxDistance: 100f, smoothedRttMs: 0f, currentTick: 10);

            Assert.False(hit.Hit);
        }

        [Fact]
        public void AnActorWithNoHistoryFallsBackToItsPresentPose()
        {
            // The case the task document does not cover: an actor that has just re-entered the
            // relevance filter has no frame at the rewind tick. Declaring it unhittable for up
            // to a second would be worse than resolving against the present.
            var compensator = new LagCompensator(new HitboxHistory());

            HitResult hit = compensator.ResolveHitscan(
                new[] { new HitscanTarget(Target, true, HitboxSet.Humanoid(new Vec3(0f, 0f, 10f))) },
                Shooter, MuzzleOrigin, new Vec3(0f, 0f, 1f),
                maxDistance: 100f, smoothedRttMs: 150f, currentTick: 100);

            Assert.True(hit.Hit);
            Assert.True(hit.UsedPresentFallback);
            Assert.Equal(1, compensator.PresentFallbacks);
        }

        [Fact]
        public void TheResolvedTickIsReportedOnTheResult()
        {
            var world = new RewindWorld();
            HitResult hit = world.Fire(rttMs: 150f, currentTick: 100);

            Assert.Equal(95u, hit.ResolvedAtTick);
            Assert.False(hit.UsedPresentFallback);
        }

        [Fact]
        public void StatisticsCountShotsAndHits()
        {
            var world = new RewindWorld();
            world.Fire(rttMs: 150f, currentTick: 100);
            world.Fire(rttMs: 150f, currentTick: 100);

            Assert.Equal(2, world.Compensator.ShotsResolved);
            Assert.Equal(2, world.Compensator.ShotsHit);
            Assert.Equal(100.0, world.Compensator.HitRatePercent, 3);

            world.Compensator.ResetStatistics();
            Assert.Equal(0, world.Compensator.ShotsResolved);
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// A target standing still at 10 m, with a full second of history behind it.
        /// </summary>
        private sealed class RewindWorld
        {
            public readonly HitboxHistory History = new HitboxHistory();
            public readonly LagCompensator Compensator;

            private readonly HitscanTarget[] _targets;

            public RewindWorld()
            {
                Compensator = new LagCompensator(History);

                HitboxSet boxes = HitboxSet.Humanoid(new Vec3(0f, 0f, 10f));
                for (uint tick = 80; tick <= 100; tick++) History.Capture(tick, Target, in boxes);

                _targets = new[] { new HitscanTarget(Target, true, boxes) };
            }

            public HitResult Fire(float rttMs, uint currentTick)
                => Compensator.ResolveHitscan(
                    _targets, Shooter, MuzzleOrigin, new Vec3(0f, 0f, 1f),
                    maxDistance: 100f, smoothedRttMs: rttMs, currentTick: currentTick);
        }

        /// <summary>
        /// A target strafing across the shooter's view, with the history the server would have
        /// captured for it.
        /// </summary>
        /// <remarks>
        /// The shooter aims at the pose their client RENDERED, which is
        /// <c>rtt/2 + INTERP_BUFFER_MS</c> behind the server — the whole premise of section 7.
        /// Passing <c>compensated: false</c> resolves against an empty history so every target
        /// falls back to its present pose, which is exactly what "lag compensation off" means.
        /// </remarks>
        /// <summary>
        /// A target strafing across the shooter's view, with the history the server would have
        /// captured for it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The aim point is derived from the spec, not from the code under test.</b> The
        /// obvious way to write this fixture is to call
        /// <see cref="LagCompensator.RewindTicks"/> to find which tick the client was seeing and
        /// aim at the hitbox stored in history for exactly that tick. That makes the experiment
        /// self-fulfilling: the aim point and the rewound pose are then the same value read
        /// twice, so the compensated arm reports 100% whatever <c>RewindTicks</c> returns, and
        /// an off-by-one — or an off-by-anything constant — shifts both sides together and
        /// still passes.
        /// </para>
        /// <para>
        /// Instead the client's view time is computed here in MILLISECONDS from
        /// protocol-spec.md section 7.1's own definition — the client renders
        /// <c>rtt/2 + INTERP_BUFFER_MS</c> behind the server — and the target's position at that
        /// continuous instant is what the crosshair sits on. The server then rewinds by whatever
        /// its own tick arithmetic decides. If the two disagree, the shot misses, which is the
        /// whole property being measured.
        /// </para>
        /// <para>
        /// One consequence worth stating: past the <see cref="ProtocolConstants.MAX_REWIND_MS"/>
        /// clamp the two are SUPPOSED to disagree. A 300 ms client is compensated as though it
        /// were at 200 ms, so its hit rate degrades — and the fixture now shows that instead of
        /// hiding it behind a shared constant.
        /// </para>
        /// </remarks>
        private sealed class StrafingScenario
        {
            /// <summary>Tick the volley opens on. The target is at the origin here.</summary>
            private const uint FirstShotTick = 200;

            private readonly float _strafeSpeed;
            private readonly float _range;

            public StrafingScenario(float strafeSpeed, float rangeMetres)
            {
                _strafeSpeed = strafeSpeed;
                _range = rangeMetres;
            }

            public int FireVolley(int shots, float rttMs, bool compensated)
            {
                // Captured tick by tick as the volley advances, the way the server does it. A
                // fixture that pre-fills every tick up front measures nothing: the ring holds
                // one second, so filling it to tick 400 and then firing at tick 200 finds every
                // needed frame already overwritten and silently tests the present-pose fallback.
                var history = new HitboxHistory();
                var compensator = new LagCompensator(compensated ? history : new HitboxHistory());
                int hits = 0;
                uint capturedThrough = 0;

                for (int shot = 0; shot < shots; shot++)
                {
                    // Spread the volley over the strafe so the sample is not one lucky phase.
                    uint currentTick = FirstShotTick + (uint)(shot * 3);

                    for (uint tick = capturedThrough; tick <= currentTick; tick++)
                        history.Capture(tick, Target, HitboxSet.Humanoid(PositionAt(tick)));

                    capturedThrough = currentTick + 1;

                    // protocol-spec.md section 7.1, in milliseconds and independent of the
                    // implementation: half the trip plus the interpolation buffer.
                    float clientViewLagMs = rttMs * 0.5f + ProtocolConstants.INTERP_BUFFER_MS;
                    float seenTimeMs = currentTick * ProtocolConstants.MS_PER_TICK - clientViewLagMs;

                    Vec3 aimPoint = HitboxSet.Humanoid(PositionAtTime(seenTimeMs)).Torso.Center;

                    var present = new[]
                    {
                        new HitscanTarget(Target, true, HitboxSet.Humanoid(PositionAt(currentTick))),
                    };

                    HitResult hit = compensator.ResolveHitscan(
                        present, Shooter, MuzzleOrigin, aimPoint - MuzzleOrigin,
                        maxDistance: 100f, smoothedRttMs: rttMs, currentTick: currentTick);

                    if (hit.Hit) hits++;
                }

                return hits;
            }

            private Vec3 PositionAt(uint tick)
                => PositionAtTime(tick * ProtocolConstants.MS_PER_TICK);

            /// <summary>Where the target is at a continuous instant, in milliseconds.</summary>
            /// <remarks>
            /// Measured from <see cref="FirstShotTick"/> so the target crosses in front of the
            /// shooter during the volley instead of starting wherever `speed x tick` happens to
            /// put it. Without the offset a fast target has already run past the weapon's range
            /// by the first shot, and the volley measures "out of range" while looking like it
            /// measures compensation.
            /// </remarks>
            private Vec3 PositionAtTime(float milliseconds)
                => new Vec3(
                    _strafeSpeed * (milliseconds - FirstShotTick * ProtocolConstants.MS_PER_TICK)
                        / 1000f,
                    0f,
                    _range);
        }
    }
}
