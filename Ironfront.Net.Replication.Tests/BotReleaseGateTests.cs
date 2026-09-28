using System;
using Ironfront.Net.Replication.Match;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The bot release gate: no bot exists before a human is in the world, and none for
    /// <see cref="BotReleaseGate.DefaultDelaySeconds"/> after the first one arrives.
    /// </summary>
    /// <remarks>
    /// Written against the FAILURE states rather than the happy path, per
    /// green-that-proves-nothing.md: a gate that is only ever asked "does it open eventually"
    /// would pass while permanently open, which is the bug it exists to prevent.
    /// </remarks>
    public sealed class BotReleaseGateTests
    {
        [Fact]
        public void ClosedBeforeAnyPlayerSpawns()
        {
            var gate = new BotReleaseGate(delaySeconds: 30f);

            Assert.False(gate.HasAnchor);
            Assert.False(gate.IsReleasedAt(0f));
            Assert.False(gate.IsReleasedAt(30f));

            // THE no-timeout decision, pinned. An unattended server must never release bots:
            // no humans, no bots, however long it has been up.
            Assert.False(gate.IsReleasedAt(100_000f));
            Assert.Equal(float.PositiveInfinity, gate.SecondsUntilRelease(100_000f));
        }

        [Fact]
        public void ClosedUntilTheDelayHasFullyElapsed()
        {
            var gate = new BotReleaseGate(delaySeconds: 30f);
            gate.NotifyPlayerSpawned(10f);

            Assert.True(gate.HasAnchor);
            Assert.False(gate.IsReleasedAt(10f));
            Assert.False(gate.IsReleasedAt(39.9f));
            Assert.True(gate.IsReleasedAt(40f));
            Assert.True(gate.IsReleasedAt(41f));
        }

        [Fact]
        public void AnchorIsTheFirstSpawnAndLaterSpawnsDoNotPushItOut()
        {
            var gate = new BotReleaseGate(delaySeconds: 30f);
            gate.NotifyPlayerSpawned(0f);

            // A respawn, and a second player joining, 25s in. On a busy server these arrive
            // constantly; if any of them re-anchored the clock the gate would never open.
            gate.NotifyPlayerSpawned(25f);
            gate.NotifyPlayerSpawned(29f);

            Assert.True(gate.IsReleasedAt(30f));
        }

        [Fact]
        public void ZeroIsAValidAnchorAndIsNotReadAsUnset()
        {
            // Time.time is 0 on the frame a scene loads, so a sentinel-based anchor would read
            // "spawned at 0" as "never spawned" and hold the gate shut for the whole round.
            var gate = new BotReleaseGate(delaySeconds: 30f);
            gate.NotifyPlayerSpawned(0f);

            Assert.True(gate.HasAnchor);
            Assert.True(gate.IsReleasedAt(30f));
        }

        [Fact]
        public void ResetReArmsForTheNextRound()
        {
            var gate = new BotReleaseGate(delaySeconds: 30f);
            gate.NotifyPlayerSpawned(0f);
            Assert.True(gate.IsReleasedAt(30f));

            gate.Reset();

            // Round two must be as closed as round one was, or the delay protects only the
            // first round of a server's life.
            Assert.False(gate.HasAnchor);
            Assert.False(gate.IsReleasedAt(60f));
            Assert.False(gate.IsReleasedAt(100_000f));

            gate.NotifyPlayerSpawned(60f);
            Assert.False(gate.IsReleasedAt(89f));
            Assert.True(gate.IsReleasedAt(90f));
        }

        [Fact]
        public void CountdownReportsRemainingTimeAndFloorsAtZero()
        {
            var gate = new BotReleaseGate(delaySeconds: 30f);
            gate.NotifyPlayerSpawned(10f);

            Assert.Equal(30f, gate.SecondsUntilRelease(10f));
            Assert.Equal(5f, gate.SecondsUntilRelease(35f));
            Assert.Equal(0f, gate.SecondsUntilRelease(40f));
            Assert.Equal(0f, gate.SecondsUntilRelease(400f));
        }

        [Fact]
        public void ADelayOfZeroReleasesOnTheSpawnItselfButStillNotBeforeIt()
        {
            var gate = new BotReleaseGate(delaySeconds: 0f);

            Assert.False(gate.IsReleasedAt(5f));

            gate.NotifyPlayerSpawned(5f);
            Assert.True(gate.IsReleasedAt(5f));
        }

        [Fact]
        public void NegativeDelayIsRefused()
            => Assert.Throws<ArgumentOutOfRangeException>(() => new BotReleaseGate(-1f));

        [Fact]
        public void DefaultDelayIsThirtySeconds()
            => Assert.Equal(30f, new BotReleaseGate().DelaySeconds);
    }
}
