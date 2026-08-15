using System;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase-01 acceptance criteria 1 (steady 30 Hz), 5 (speed hacks blocked) and 6 (missing
    /// input does not freeze the character).
    /// </summary>
    public sealed class ServerAuthorityTests
    {
        private const float Dt = 1f / ProtocolConstants.SIM_TICK_RATE;

        // ------------------------------------------------------------------ tick pacing

        [Fact]
        public void HoldsThirtyTicksPerSecondOnASteadyClock()
        {
            var scheduler = new ServerTickScheduler();

            scheduler.Advance(0);
            int ticks = 0;
            for (int frame = 1; frame <= 300; frame++)
            {
                int owed = scheduler.Advance(frame * 10.0); // a 100 Hz driving loop
                for (int i = 0; i < owed; i++)
                {
                    scheduler.BeginTick();
                    ticks++;
                }
            }

            // 3 seconds of wall clock at 30 Hz.
            Assert.InRange(ticks, 89, 91);
            Assert.Equal(0, scheduler.DroppedTicks);
        }

        [Fact]
        public void SnapshotsLandAtTwentyHertzAgainstAThirtyHertzSimulation()
        {
            var scheduler = new ServerTickScheduler();
            int snapshots = 0;

            // 300 ticks is 10 seconds at 30 Hz, so 20 Hz should yield 200 snapshots.
            for (int i = 0; i < 300; i++)
            {
                scheduler.BeginTick();
                if (scheduler.ShouldSendSnapshot()) snapshots++;
            }

            Assert.InRange(snapshots, 199, 201);
        }

        [Fact]
        public void ClampsTheBacklogInsteadOfSpiralling()
        {
            // A 2-second stall must not owe 60 ticks; it must discard the backlog visibly.
            var scheduler = new ServerTickScheduler(maxCatchUpTicks: 3);

            scheduler.Advance(0);
            int owed = scheduler.Advance(2000);

            Assert.Equal(3, owed);
            Assert.True(scheduler.DroppedTicks > 50);
        }

        [Fact]
        public void TheFirstAdvanceEstablishesTheTimeBaseRatherThanOwingMillions()
        {
            var scheduler = new ServerTickScheduler();

            // A real process hands over Environment.TickCount-scale values on the first call.
            Assert.Equal(0, scheduler.Advance(4_000_000.0));
        }

        [Fact]
        public void ABackwardClockDoesNotRewindTheAccumulator()
        {
            var scheduler = new ServerTickScheduler();
            scheduler.Advance(1000);
            Assert.Equal(0, scheduler.Advance(900));
            Assert.Equal(1, scheduler.Advance(934));
        }

        [Fact]
        public void PercentilesReportTheTailNotTheAverage()
        {
            // 2 bad ticks in 100, so the top 1% really is bad and p99 must say so.
            var stats = new TickTimeStats(capacity: 100);

            for (int i = 0; i < 98; i++) stats.Record(8.0);
            stats.Record(300.0);
            stats.Record(300.0);

            Assert.Equal(8.0, stats.Percentile(50));
            Assert.Equal(300.0, stats.Percentile(99));
            Assert.Equal(300.0, stats.MaxEver);

            // The mean hides exactly the hitch a player would complain about: under 14 ms,
            // which reads as a perfectly healthy server.
            Assert.True(stats.Mean() < 14.0);
        }

        [Fact]
        public void ASingleHitchInAHundredTicksIsNotP99()
        {
            // Nearest-rank, and the distinction is the point: "p99 < 33 ms" is a claim that
            // 99% of ticks were under budget. One bad tick in a hundred does not violate
            // that — it is the 100th percentile. Reporting it as p99 would make the server
            // look broken every time a single GC pause landed.
            var stats = new TickTimeStats(capacity: 100);

            for (int i = 0; i < 99; i++) stats.Record(8.0);
            stats.Record(300.0);

            Assert.Equal(8.0, stats.Percentile(99));
            Assert.Equal(300.0, stats.Percentile(100));
            Assert.Equal(300.0, stats.MaxEver);
        }

        [Fact]
        public void TheRingForgetsSamplesOlderThanItsCapacity()
        {
            var stats = new TickTimeStats(capacity: 10);

            for (int i = 0; i < 10; i++) stats.Record(100.0);
            Assert.Equal(100.0, stats.Percentile(50));

            for (int i = 0; i < 10; i++) stats.Record(5.0);

            // The window has rolled over entirely, so the recent-history percentile recovers.
            Assert.Equal(5.0, stats.Percentile(50));

            // MaxEver deliberately does not forget — it is the "did this ever happen" counter.
            Assert.Equal(100.0, stats.MaxEver);
        }

        [Fact]
        public void OverloadIsFlaggedOnlyWhenTheTailExceedsTheBudget()
        {
            var healthy = new ServerTickScheduler(tickTimeHistory: 50);
            for (int i = 0; i < 50; i++) healthy.RecordTickTime(8.0);
            Assert.False(healthy.IsOverloaded());

            var struggling = new ServerTickScheduler(tickTimeHistory: 50);
            for (int i = 0; i < 50; i++) struggling.RecordTickTime(40.0);
            Assert.True(struggling.IsOverloaded());
        }

        // ------------------------------------------------------------------ criterion 5

        [Fact]
        public void TheDiagonalSpeedHackGainsNothing()
        {
            // The classic: moveX = moveZ = 127 for an input vector of length 1.41.
            var hacked = new MoveInput(1f, 1f, 0f, false, false, false);
            MoveInput normalized = InputAuthority.Normalize(in hacked);

            float magnitude = (float)Math.Sqrt(
                normalized.MoveX * normalized.MoveX + normalized.MoveZ * normalized.MoveZ);

            Assert.Equal(1f, magnitude, 4);
        }

        [Fact]
        public void NormalizeOnlyShrinksAndNeverAmplifies()
        {
            var gentle = new MoveInput(0.2f, 0.1f, 0f, false, false, false);
            MoveInput result = InputAuthority.Normalize(in gentle);

            Assert.Equal(0.2f, result.MoveX, 5);
            Assert.Equal(0.1f, result.MoveZ, 5);
        }

        [Fact]
        public void AMaliciousClientCannotOutrunTheSpeedClamp()
        {
            // A cheating client that teleports its actor 50 m in one tick.
            var session = new ClientSession(connectionId: 1, actorId: 1);
            session.State = MoveState.AtRest(Vec3.Zero);
            session.PreviousPosition = Vec3.Zero;
            session.State.Position = new Vec3(50f, 0f, 0f);

            bool clamped = InputAuthority.ClampMovement(session, Dt);

            Assert.True(clamped);
            Assert.Equal(1, session.SpeedViolations);
            Assert.True(
                session.State.Position.Magnitude <= InputAuthority.MaxMovePerTick(Dt) + 0.001f,
                $"clamped to {session.State.Position.Magnitude:F3} m, " +
                $"limit is {InputAuthority.MaxMovePerTick(Dt):F3} m");
        }

        [Fact]
        public void LegitimateSprintingIsNotClamped()
        {
            var session = new ClientSession(connectionId: 1, actorId: 1);
            session.State = MoveState.AtRest(Vec3.Zero);
            session.PreviousPosition = Vec3.Zero;

            var sprint = new MoveInput(0f, 1f, 0f, jump: false, sprint: true, crouch: false);

            for (int tick = 0; tick < 90; tick++)
            {
                Vec3 motion = MovementCore.Step(ref session.State, in sprint, Dt);
                session.State.Position += motion;
                InputAuthority.ClampMovement(session, Dt);
            }

            Assert.Equal(0, session.SpeedViolations);

            // 3 seconds of sprinting should cover close to 6.5 * 3 metres.
            Assert.InRange(session.State.Position.Z, 19.0f, 20.0f);
        }

        [Fact]
        public void AJumpIsNotMistakenForASpeedHack()
        {
            // A clamp derived from horizontal speed alone would fire every time a player
            // jumps and drag them back down through their own arc.
            var session = new ClientSession(connectionId: 1, actorId: 1);
            session.State = MoveState.AtRest(Vec3.Zero, grounded: true);
            session.PreviousPosition = Vec3.Zero;

            var jump = new MoveInput(0f, 1f, 0f, jump: true, sprint: true, crouch: false);
            Vec3 motion = MovementCore.Step(ref session.State, in jump, Dt);
            session.State.Position += motion;

            Assert.False(InputAuthority.ClampMovement(session, Dt));
            Assert.Equal(0, session.SpeedViolations);
        }

        [Fact]
        public void AnAbnormalForwardTickJumpIsRefused()
        {
            var session = new ClientSession(connectionId: 1, actorId: 1);
            session.HasInput = true;
            session.LastProcessedInputTick = 100;

            InputFrame frame = InputFrame.FromFloats(0f, 1f, 0f, 0f, InputButtons.None);

            // 100 -> 5000 is a claim to have skipped 160 seconds.
            Assert.True(InputAuthority.TryAccept(session, 5000, in frame, out _));
            Assert.Equal(1, session.TickJumpViolations);
            Assert.Equal(4999u, session.LastProcessedInputTick);
        }

        [Fact]
        public void RedundantInputCopiesAreAppliedOnlyOnce()
        {
            // The client repeats its 3 most recent frames every packet (spec § 4.2). Applying
            // a repeat would move the player twice for one keypress.
            var session = new ClientSession(connectionId: 1, actorId: 1);
            InputFrame frame = InputFrame.FromFloats(0f, 1f, 0f, 0f, InputButtons.None);

            Assert.True(session.EnqueueInput(10, in frame));
            Assert.True(session.EnqueueInput(11, in frame));

            session.HasInput = true;
            session.LastProcessedInputTick = 11;

            Assert.False(session.EnqueueInput(11, in frame));
            Assert.False(session.EnqueueInput(9, in frame));
            Assert.False(InputAuthority.TryAccept(session, 11, in frame, out _));
        }

        [Fact]
        public void AFloodingClientCannotGrowTheInputBuffer()
        {
            var session = new ClientSession(connectionId: 1, actorId: 1);
            InputFrame frame = InputFrame.FromFloats(0f, 1f, 0f, 0f, InputButtons.None);

            for (uint tick = 1; tick <= 10_000; tick++) session.EnqueueInput(tick, in frame);

            Assert.Equal(ClientSession.InputBufferCapacity, session.PendingInputCount);

            // The oldest were dropped, so what remains is the newest window.
            Assert.True(session.TryDequeueInput(out uint oldest, out _));
            Assert.Equal(10_000u - ClientSession.InputBufferCapacity + 1, oldest);
        }

        // ------------------------------------------------------------------ criterion 6

        [Fact]
        public void ThreeTicksOfMissingInputKeepTheCharacterMoving()
        {
            var session = new ClientSession(connectionId: 1, actorId: 1);
            session.State = MoveState.AtRest(Vec3.Zero, grounded: true);
            session.PreviousPosition = Vec3.Zero;

            InputFrame running = InputFrame.FromFloats(0f, 1f, 0f, 0f, InputButtons.None);
            session.EnqueueInput(1, in running);

            Assert.Equal(1, ApplyTick(session));
            float afterRealInput = session.State.Position.Z;
            Assert.True(afterRealInput > 0f);

            // Three empty ticks: the player is assumed to still be holding the key.
            float previous = afterRealInput;
            for (int missed = 1; missed <= InputAuthority.MaxMissedInputTicks; missed++)
            {
                Assert.Equal(1, ApplyTick(session));
                Assert.True(
                    session.State.Position.Z > previous,
                    $"the character stalled on missed tick {missed}");
                previous = session.State.Position.Z;
            }

            // Past the cap it stops rather than running to the horizon on a dead connection.
            Assert.Equal(0, ApplyTick(session));
            Assert.Equal(previous, session.State.Position.Z, 5);
        }

        [Fact]
        public void FreshInputResetsTheMissedTickCounter()
        {
            var session = new ClientSession(connectionId: 1, actorId: 1);
            session.State = MoveState.AtRest(Vec3.Zero, grounded: true);

            InputFrame running = InputFrame.FromFloats(0f, 1f, 0f, 0f, InputButtons.None);
            session.EnqueueInput(1, in running);
            ApplyTick(session);

            ApplyTick(session);
            ApplyTick(session);
            Assert.Equal(2, session.MissedInputTicks);

            session.EnqueueInput(2, in running);
            ApplyTick(session);
            Assert.Equal(0, session.MissedInputTicks);
        }

        [Fact]
        public void NoInputAtAllMeansNoMovement()
        {
            // A connected client that has never sent anything must not coast on a default
            // frame — MoveState is zeroed, but the guard is what makes that explicit.
            var session = new ClientSession(connectionId: 1, actorId: 1);
            session.State = MoveState.AtRest(Vec3.Zero, grounded: true);

            Assert.Equal(0, ApplyTick(session));
            Assert.Equal(0f, session.State.Position.Magnitude, 6);
        }

        // ------------------------------------------------------------------ input flooding

        [Fact]
        public void AFloodedInputRingIsMeteredToTheServersOwnTickRate()
        {
            // The speed hack: keep the 32-frame ring saturated and the server used to drain all
            // of it in one tick, each frame moving a full tick's length — 32x run speed, with
            // SpeedViolations at zero because no individual step broke the per-step clamp.
            var flooded = new ClientSession(connectionId: 1, actorId: 1);
            flooded.State = MoveState.AtRest(Vec3.Zero, grounded: true);
            flooded.PreviousPosition = Vec3.Zero;

            InputFrame running = InputFrame.FromFloats(0f, 1f, 0f, 0f, InputButtons.None);
            for (uint tick = 1; tick <= 32; tick++) flooded.EnqueueInput(tick, in running);

            int firstTick = ApplyTick(flooded);

            Assert.True(
                firstTick <= InputAuthority.MaxInputBurst,
                $"{firstTick} frames applied in one tick, budget is {InputAuthority.MaxInputBurst}");

            // The surplus is held, not dropped — a throttled client loses no intent.
            Assert.True(flooded.PendingInputCount > 0);
            Assert.True(flooded.InputThrottleEvents > 0);

            // And the sustained rate is one frame per tick, not the ring's depth.
            for (int tick = 0; tick < 8; tick++) Assert.Equal(1, ApplyTick(flooded));
        }

        [Fact]
        public void ABurstAfterPacketLossIsAllowedToCatchUp()
        {
            // The honest case the budget must NOT punish: frames for several ticks arrive in
            // one delivery after a hiccup. They represent ticks the player really did intend to
            // move for, so they are applied together.
            var session = new ClientSession(connectionId: 1, actorId: 1);
            session.State = MoveState.AtRest(Vec3.Zero, grounded: true);
            session.PreviousPosition = Vec3.Zero;

            InputFrame running = InputFrame.FromFloats(0f, 1f, 0f, 0f, InputButtons.None);

            // Idle ticks bank the budget.
            session.EnqueueInput(1, in running);
            ApplyTick(session);
            ApplyTick(session);
            ApplyTick(session);

            session.EnqueueInput(2, in running);
            session.EnqueueInput(3, in running);
            session.EnqueueInput(4, in running);

            Assert.Equal(3, ApplyTick(session));
            Assert.Equal(0, session.InputThrottleEvents);
        }

        [Fact]
        public void OneFrameATickIsNeverThrottled()
        {
            var session = new ClientSession(connectionId: 1, actorId: 1);
            session.State = MoveState.AtRest(Vec3.Zero, grounded: true);
            session.PreviousPosition = Vec3.Zero;

            InputFrame running = InputFrame.FromFloats(0f, 1f, 0f, 0f, InputButtons.None);

            for (uint tick = 1; tick <= 20; tick++)
            {
                session.EnqueueInput(tick, in running);
                Assert.Equal(1, ApplyTick(session));
            }

            Assert.Equal(0, session.InputThrottleEvents);
            Assert.Equal(0, session.SpeedViolations);
        }

        /// <summary>
        /// Runs one authoritative tick with straight-line integration standing in for
        /// collision — the seam a Unity server fills with CharacterController.Move.
        /// </summary>
        private static int ApplyTick(ClientSession session)
        {
            // Reads the position at call time rather than capturing it once: a single tick can
            // apply several buffered frames, and each must start from where the previous one
            // finished.
            return InputAuthority.ApplyPendingInput(
                session, Dt, motion => session.State.Position + motion);
        }
    }
}
