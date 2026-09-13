using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Handoff section 5.2: one press is one shot on a semi-automatic, and an automatic's rate
    /// follows the cooldown rather than the number of packets a client chose to send.
    /// </summary>
    public sealed class SemiAutoTriggerEdgeTests
    {
        private const ushort Shooter = TriggerFixture.Shooter;

        [Fact]
        public void ASemiAutomaticHeldForThirtyTicksFiresOnce()
        {
            var fixture = new TriggerFixture(SprintFireGateTests.SemiAuto);

            int fired = fixture.StepFor(30, InputButtons.Fire);

            Assert.Equal(1, fired);
            Assert.Equal(1, fixture.Weapon.ClipSpent(fixture.Config));
        }

        [Fact]
        public void ReleasingAndPressingAgainArmsTheEdgeForASecondRound()
        {
            var fixture = new TriggerFixture(SprintFireGateTests.SemiAuto);

            Assert.Equal(1, fixture.StepFor(30, InputButtons.Fire));
            Assert.Equal(0, fixture.StepFor(1, InputButtons.None, startAt: 1f));
            Assert.Equal(1, fixture.StepFor(30, InputButtons.Fire, startAt: 2f));

            Assert.Equal(2, fixture.Weapon.ClipSpent(fixture.Config));
        }

        [Fact]
        public void EnteringSprintReArmsTheSemiAutoEdge()
        {
            // Holding Fire through a sprint and coming out of it is a trigger EDGE, matching the
            // original controller -- not a continuation that the player has to release and press
            // again to recover from.
            var fixture = new TriggerFixture(SprintFireGateTests.SemiAuto);

            Assert.Equal(1, fixture.StepFor(3, InputButtons.Fire));
            Assert.Equal(0, fixture.StepFor(3, InputButtons.Fire | InputButtons.Sprint, startAt: 1f));
            Assert.Equal(1, fixture.StepFor(3, InputButtons.Fire, startAt: 2f));
        }

        [Fact]
        public void AnAutomaticFiresOnTheCooldownWhileTheTriggerIsHeld()
        {
            // Asserted as the SPACING between shots rather than as a count, because the count
            // is 9 or 10 depending on how 1/30 accumulates in a float and pinning either number
            // would pin that arithmetic instead of the rule. The rule is that consecutive shots
            // are at least one cooldown apart and that the trigger being held does not add any.
            var fixture = new TriggerFixture(Automatic);
            var firedAt = new System.Collections.Generic.List<float>();

            for (int tick = 0; tick < 30; tick++)
            {
                float now = tick / (float)ProtocolConstants.SIM_TICK_RATE;
                if (fixture.Step(now, InputButtons.Fire).Fired) firedAt.Add(now);
            }

            Assert.InRange(firedAt.Count, 9, 10);

            for (int i = 1; i < firedAt.Count; i++)
                Assert.True(
                    firedAt[i] - firedAt[i - 1] >= Automatic.Cooldown - 1e-4f,
                    $"shot {i} came {firedAt[i] - firedAt[i - 1]} s after the last, "
                    + $"inside the {Automatic.Cooldown} s cooldown");
        }

        [Fact]
        public void AnAutomaticsRateDoesNotChangeWhenInputRedundancyIsRaised()
        {
            // The half of section 10.2 that catches counting PACKETS instead of ticks. A client
            // sends INPUT_REDUNDANCY copies of each frame; if the trigger advanced per arrival
            // rather than per processed frame, three copies would be three shots.
            int once = FireThroughTheSession(redundancy: 1, ticks: 30);
            int thrice = FireThroughTheSession(
                redundancy: ProtocolConstants.INPUT_REDUNDANCY, ticks: 30);

            Assert.Equal(once, thrice);
            Assert.True(once > 1, "the run fired nothing, so the comparison proves nothing");
        }

        [Fact]
        public void ADuplicatedInputTickFiresNothingAndSpendsNothing()
        {
            var fixture = new TriggerFixture(Automatic);
            var session = new ClientSession(connectionId: 3, actorId: Shooter);
            var observer = new CombatObserver(fixture);

            session.EnqueueInput(1, TriggerFixture.Frame(InputButtons.Fire));
            InputAuthority.ApplyPendingInput(session, Dt, Still, observer);

            int afterFirst = fixture.Weapon.ClipSpent(fixture.Config);
            Assert.Equal(1, afterFirst);

            // The same tick again, exactly as the redundancy in the next packet repeats it.
            session.EnqueueInput(1, TriggerFixture.Frame(InputButtons.Fire));
            InputAuthority.ApplyPendingInput(session, Dt, Still, observer);

            Assert.Equal(afterFirst, fixture.Weapon.ClipSpent(fixture.Config));
            Assert.Equal(1, observer.FramesSeen);
        }

        /// <summary>
        /// Drives <paramref name="ticks"/> ticks of held Fire through the real accepted-input
        /// path, sending each frame <paramref name="redundancy"/> times.
        /// </summary>
        private static int FireThroughTheSession(int redundancy, int ticks)
        {
            var fixture = new TriggerFixture(Automatic);
            var session = new ClientSession(connectionId: 3, actorId: Shooter);
            var observer = new CombatObserver(fixture);

            for (uint tick = 1; tick <= ticks; tick++)
            {
                for (int copy = 0; copy < redundancy; copy++)
                    session.EnqueueInput(tick, TriggerFixture.Frame(InputButtons.Fire));

                InputAuthority.ApplyPendingInput(session, Dt, Still, observer);
            }

            return observer.Shots;
        }

        private const float Dt = 1f / ProtocolConstants.SIM_TICK_RATE;

        /// <summary>Collision stands still: these tests grade the trigger, not the move.</summary>
        private static Vec3 Still(Vec3 motion) => motion;

        /// <summary>
        /// Steps combat for every frame the input path ACCEPTS, which is the seam the server
        /// itself uses. Driving the authority directly would skip the tick dedup, which is the
        /// mechanism under test.
        /// </summary>
        private sealed class CombatObserver : IAcceptedFrameObserver
        {
            private readonly TriggerFixture _fixture;

            public CombatObserver(TriggerFixture fixture) => _fixture = fixture;

            public int Shots { get; private set; }

            public int FramesSeen { get; private set; }

            public void OnAcceptedFrame(
                ClientSession session, uint tick, in InputFrame frame, in MoveInput input)
            {
                FramesSeen++;

                float now = tick / (float)ProtocolConstants.SIM_TICK_RATE;

                if (_fixture.Authority.Step(
                        ref _fixture.Weapon, ref _fixture.Trigger, in FixtureConfig, Shooter,
                        in frame, in _fixture.State, ReadOnlySpan<HitscanTarget>.Empty,
                        in _fixture.Actor, in _fixture.Ammo, now, smoothedRttMs: 0f,
                        currentTick: tick, _fixture.Hits).Fired)
                    Shots++;
            }
        }

        /// <summary>A plain automatic: 0.1 s between shots, thirty rounds.</summary>
        private static readonly WeaponConfig Automatic = WeaponConfig.Rifle;

        private static readonly WeaponConfig FixtureConfig = Automatic;
    }
}
