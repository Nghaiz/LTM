using System;
using System.Collections.Generic;
using System.IO;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Movement;
using Ironfront.Net.Replication.Server;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// <see cref="InputAuthority.ConsumePendingInputSeated"/>: a player in a vehicle keeps having
    /// their input accepted, acknowledged and seen by combat, and is never stepped on foot.
    /// </summary>
    /// <remarks>
    /// 2026-09-23: the server stepped a seated player's on-foot capsule through collision from
    /// inside the vehicle's hull, and the vehicle stood still under full throttle. These pin the
    /// replacement's contract; the source-invariant test below pins that the server uses it.
    /// </remarks>
    public sealed class SeatedInputTests
    {
        private static readonly InputFrame Forward =
            InputFrame.FromFloats(0f, 1f, 90f, 0f, InputButtons.Fire);

        [Fact]
        public void SeatedFramesAreAcceptedAcknowledgedAndObserved()
        {
            var session = new ClientSession(1, 7);
            var observer = new RecordingObserver();
            Assert.True(session.EnqueueInput(10, in Forward));

            int accepted = InputAuthority.ConsumePendingInputSeated(
                session, new Vec3(5f, 1f, 5f), observer);

            Assert.Equal(1, accepted);
            Assert.Equal(10u, session.LastProcessedInputTick);
            Assert.True(session.HasInput);
            Assert.Equal(new uint[] { 10 }, observer.Ticks);
        }

        [Fact]
        public void TheSessionRidesTheSeatAndIsNeverSteppedOnFoot()
        {
            var session = new ClientSession(1, 7);
            session.EnqueueInput(10, in Forward);

            var seat = new Vec3(100f, 12f, 200f);
            InputAuthority.ConsumePendingInputSeated(session, in seat);

            // Pinned to the seat, not seat + a walking step, and at rest: a seated body banks no
            // on-foot velocity to carry into the first step after it gets out.
            Assert.Equal(seat, session.State.Position);
            Assert.Equal(Vec3.Zero, session.State.Velocity);
            Assert.Equal(seat, session.PreviousPosition);
        }

        [Fact]
        public void AVehicleCarryingThePlayerFasterThanARunIsNotASpeedViolation()
        {
            var session = new ClientSession(1, 7);

            // 20 m/s at 30 Hz, three times MaxMovePerTick's budget per tick.
            for (uint tick = 1; tick <= 30; tick++)
            {
                session.EnqueueInput(tick, in Forward);
                InputAuthority.ConsumePendingInputSeated(
                    session, new Vec3(tick * (20f / ProtocolConstants.SIM_TICK_RATE), 0f, 0f));
            }

            Assert.Equal(0, session.SpeedViolations);
            Assert.Equal(20f, session.State.Position.X, 3);
        }

        [Fact]
        public void TheInputBudgetStillMetersAFlood()
        {
            var session = new ClientSession(1, 7);
            for (uint tick = 1; tick <= 50; tick++) session.EnqueueInput(tick, in Forward);

            int accepted = InputAuthority.ConsumePendingInputSeated(session, Vec3.Zero);

            Assert.True(accepted <= InputAuthority.MaxInputBurst);
            Assert.True(session.PendingInputCount > 0);
        }

        /// <summary>
        /// The server takes the seated branch BEFORE the on-foot step, and turns the capsule off.
        /// </summary>
        [Fact]
        public void TheServerPlayerConsumesSeatedInputInsteadOfWalkingTheCapsule()
        {
            string player = File.ReadAllText(Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Net", "Server", "ServerPlayer.cs"));

            int seated = player.IndexOf("InputAuthority.ConsumePendingInputSeated(", StringComparison.Ordinal);
            int onFoot = player.IndexOf(
                "InputAuthority.ApplyPendingInput(Session, dt, _moveThroughCollision, this);",
                StringComparison.Ordinal);

            Assert.True(seated >= 0, "ServerPlayer no longer consumes seated input.");
            Assert.True(onFoot > seated,
                "The seated branch must come before the on-foot step, or a seated capsule walks "
                + "inside its own vehicle again.");
            Assert.Contains("agent.SetSeated(true);", player, StringComparison.Ordinal);
            Assert.Contains("agent.SetSeated(false);", player, StringComparison.Ordinal);
        }

        /// <summary>
        /// The client keeps a seated player's buttons and aim on the wire; only the axes go.
        /// </summary>
        /// <remarks>
        /// Without it the server never saw Fire from anyone in a vehicle, however the server side
        /// was fixed: the prediction clock sent <c>default</c> for every suspended tick.
        /// </remarks>
        [Fact]
        public void TheClientSendsASeatedPlayersTriggerAndAim()
        {
            string clock = File.ReadAllText(Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Net", "Shared", "NetPredictionClock.cs"));
            Assert.Contains(
                "else if (KeepButtonsWhileSuspended != null && KeepButtonsWhileSuspended())",
                clock, StringComparison.Ordinal);
            Assert.Contains("input = input.WithAxes(0f, 0f);", clock, StringComparison.Ordinal);

            string controller = File.ReadAllText(Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Assembly-CSharp", "FpsActorController.cs"));
            Assert.Contains(
                "clock.KeepButtonsWhileSuspended = () => inputEnabled && actor != null && !actor.dead && actor.IsSeated();",
                controller, StringComparison.Ordinal);
        }

        /// <summary>
        /// A declined server bootstrap never clears a client's role on teardown.
        /// </summary>
        [Fact]
        public void TearingDownADeclinedServerBootstrapLeavesTheClientRoleAlone()
        {
            string bootstrap = File.ReadAllText(Path.Combine(
                RepoRoot(), "Ironfront_Reborn", "Assets", "Scripts", "Net", "Server", "NetServerBootstrap.cs"));
            Assert.Contains("if (NetContext.IsServer) NetContext.Clear();", bootstrap, StringComparison.Ordinal);
            int clears = 0;
            for (int at = 0; (at = bootstrap.IndexOf("NetContext.Clear();", at, StringComparison.Ordinal)) >= 0; at++) clears++;
            Assert.Equal(1, clears);
        }

        private static string RepoRoot()
        {
            for (DirectoryInfo? d = new DirectoryInfo(Directory.GetCurrentDirectory());
                 d != null;
                 d = d.Parent)
            {
                if (File.Exists(Path.Combine(d.FullName, "Ironfront.sln"))) return d.FullName;
            }

            throw new InvalidOperationException(
                "Ironfront.sln not found walking up from " + Directory.GetCurrentDirectory());
        }

        private sealed class RecordingObserver : IAcceptedFrameObserver
        {
            public readonly List<uint> Ticks = new List<uint>();

            public void OnAcceptedFrame(
                ClientSession session, uint frameTick, in InputFrame frame, in MoveInput input)
                => Ticks.Add(frameTick);
        }
    }
}
