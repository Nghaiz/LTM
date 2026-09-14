using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Combat;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// <see cref="ServerCombatAuthority.TriggerFramesSeen"/>: telling a client that stopped
    /// sending the Fire bit apart from a gate that refuses it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The shape these reproduce is a SILENT client, not a steady-rate advance.</b> On
    /// 2026-09-14 four Island <c>p10-sprint</c> runs held Fire for four seconds after a sprint
    /// and the server fired nothing, and the sprint gate was blamed. It was innocent: the
    /// driver had sprinted into the sea, <c>Actor.Update</c>'s water branch felled the body,
    /// <c>FpsActorController.DisableInput</c> cleared <c>inputEnabled</c>, and
    /// <c>NetPredictionClock</c> substituted <c>input = default</c> on every tick thereafter —
    /// so the frames kept arriving on schedule with every button zeroed. The local combat
    /// driver reads the input SOURCE rather than that frame, so the client went on predicting
    /// shots the server was never told about. Full chain:
    /// <c>docs/island-sprint-fire-drowning-2026-09-14.md</c>.
    /// </para>
    /// <para>
    /// A test that advanced the trigger frame by frame at a steady rate could not have failed
    /// for that reason, because the defect is in what the frames CARRY rather than in when they
    /// arrive. So the fault is injected the way it really happens: button-less frames at the
    /// full sim rate, indistinguishable from a healthy idle client by every counter that
    /// existed before this one.
    /// </para>
    /// </remarks>
    public sealed class TriggerArrivalCounterTests
    {
        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        /// <summary>
        /// The two faults that used to read identically, side by side. This is the whole reason
        /// the counter exists, and it fails if the counter is ever moved below a gate.
        /// </summary>
        [Fact]
        public void ASilentClientAndARefusingGateBothFireNothingAndTheCounterSeparatesThem()
        {
            var silent = new TriggerFixture();
            var refused = new TriggerFixture();

            // A drowned client: frames at the full rate, every button zeroed.
            int silentShots = silent.StepFor(30, InputButtons.None);

            // A healthy client the sprint rule is refusing, over the same span.
            int refusedShots = refused.StepFor(30, InputButtons.Fire | InputButtons.Sprint);

            // Identical from every counter that predates TriggerFramesSeen...
            Assert.Equal(0, silentShots);
            Assert.Equal(0, refusedShots);
            Assert.Equal(silent.Weapon.AmmoInClip, refused.Weapon.AmmoInClip);

            // ...and opposite faults in opposite processes, which this says in one number.
            Assert.Equal(0L, silent.Authority.TriggerFramesSeen);
            Assert.Equal(30L, refused.Authority.TriggerFramesSeen);
        }

        /// <summary>
        /// The Island run, end to end: sprint with Fire held, then the client goes silent for
        /// the window the programme spends holding Fire.
        /// </summary>
        /// <remarks>
        /// The load-bearing assertion is the LAST one. Proving the server fired nothing repeats
        /// the symptom; proving it was WILLING the entire time is what acquits the gate, and it
        /// is the step the four failing runs could not take without a counter.
        /// </remarks>
        [Fact]
        public void AClientThatGoesSilentAfterSprintingLeavesAWillingServerIdle()
        {
            var fixture = new TriggerFixture();
            byte loaded = fixture.Weapon.AmmoInClip;

            // t = 0.0 .. 6.0 — sprinting with Fire held. Refused, and counted as arriving.
            const int sprintTicks = 6 * ProtocolConstants.SIM_TICK_RATE;
            Assert.Equal(0, fixture.StepFor(sprintTicks, InputButtons.Fire | InputButtons.Sprint));

            long seenWhileSprinting = fixture.Authority.TriggerFramesSeen;
            Assert.Equal(sprintTicks, seenWhileSprinting);
            Assert.Equal(sprintTicks, fixture.Authority.SprintBlockedTriggers);

            // t = 6.0 .. 10.0 — the body is in the water. The frames keep coming at the sim
            // rate and carry nothing, which is exactly what NetPredictionClock sends once
            // SimulationEnabled() goes false.
            const int silentTicks = 4 * ProtocolConstants.SIM_TICK_RATE;
            Assert.Equal(0, fixture.StepFor(silentTicks, InputButtons.None, startAt: 6f));

            // Flat across the silent window: nothing asked the server to fire.
            Assert.Equal(seenWhileSprinting, fixture.Authority.TriggerFramesSeen);
            Assert.Equal(loaded, fixture.Weapon.AmmoInClip);

            // And the gate was willing the whole time. The sprint custody raised the weapon on
            // the first non-sprinting frame and the block expired a fifth of a second later, so
            // one arriving Fire bit fires immediately. A weapon still holstered here, or a shot
            // still refused, would mean the sprint rule really was the cause.
            Assert.True(fixture.Weapon.Unholstered);
            Assert.True(EffectiveTriggerPolicy.SprintAllowsFire(in fixture.Trigger, 10f));
            Assert.True(fixture.Step(10f, InputButtons.Fire).Fired);
            Assert.Equal(seenWhileSprinting + 1, fixture.Authority.TriggerFramesSeen);
        }

        /// <summary>
        /// Counted before the sprint gate, not after it — a refused frame still ARRIVED.
        /// </summary>
        /// <remarks>
        /// Moving the increment below any gate would make this a second shot counter, and the
        /// discrimination in the first test here collapses to nothing.
        /// </remarks>
        [Fact]
        public void ARefusedFrameStillCountsAsSeen()
        {
            var fixture = new TriggerFixture();

            // Dead, so the refusal is not the sprint rule's either: every gate is downstream.
            fixture.Actor = ActorFireEligibility.OnFoot(isAlive: false);

            Assert.Equal(0, fixture.StepFor(5, InputButtons.Fire));

            Assert.Equal(5L, fixture.Authority.TriggerFramesSeen);
        }

        /// <summary>A frame with no Fire bit is not an arrival, whatever else it carries.</summary>
        [Fact]
        public void MovementAndReloadFramesAreNotTriggerArrivals()
        {
            var fixture = new TriggerFixture();

            fixture.StepFor(5, InputButtons.Sprint);
            fixture.StepFor(5, InputButtons.Reload, startAt: 5f * Tick);

            Assert.Equal(0L, fixture.Authority.TriggerFramesSeen);
        }

        [Fact]
        public void ResetStatisticsZeroesTheArrivalCounter()
        {
            var fixture = new TriggerFixture();

            fixture.StepFor(3, InputButtons.Fire);
            Assert.Equal(3L, fixture.Authority.TriggerFramesSeen);

            fixture.Authority.ResetStatistics();

            Assert.Equal(0L, fixture.Authority.TriggerFramesSeen);
        }
    }
}
