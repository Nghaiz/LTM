using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Replication.Match;
using Ironfront.Net.Replication.Movement;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Pins the writeback that closes phase-V8's reported bug: whatever
    /// <see cref="CapturePointState.OwningTeam"/> says, that is what the scene's
    /// <c>SpawnPoint.owner</c> is told — on every tick, including the tick it flips.
    /// </summary>
    /// <remarks>
    /// The real directory dereferences <c>CapturePoint</c>, which lives in
    /// <c>Assembly-CSharp</c> and which no test assembly can see. Behind
    /// <see cref="ICapturePointDirectory"/> the writeback is an ordinary function over an array,
    /// exactly as <see cref="SpawnPointSelectionTests"/> made spawn selection one.
    /// </remarks>
    public sealed class CapturePointSlaveTests
    {
        /// <summary>Records everything the slave writes, and what it was asked.</summary>
        private sealed class FakeCapturePoints : ICapturePointDirectory
        {
            private readonly bool[] _hostile;

            internal FakeCapturePoints(int count)
            {
                _hostile = new bool[count];
                Owners = new int[count];
                Controls = new float[count];
                Contested = new bool[count];

                for (int i = 0; i < count; i++) Owners[i] = int.MinValue;
            }

            internal int[] Owners { get; }
            internal float[] Controls { get; }
            internal bool[] Contested { get; }
            internal int PresenceRefreshes { get; private set; }
            internal int OwnerWrites { get; private set; }

            internal void SetHostile(int index, bool hostile) => _hostile[index] = hostile;

            public int Count => Owners.Length;

            public int Bind(Transform[] authored, out bool discovered, out int skipped)
            {
                discovered = false;
                skipped = 0;
                return Owners.Length;
            }

            public CapturePointDefinition GetDefinition(int index)
                => new CapturePointDefinition(Vector3.zero, 10f, 0.2f, $"point-{index}");

            public void ApplyAuthoritativeOwner(int index, int spawnPointOwner, float control, bool contested)
            {
                Owners[index] = spawnPointOwner;
                Controls[index] = control;
                Contested[index] = contested;
                OwnerWrites++;
            }

            public bool RefreshPresence(int index, ReadOnlySpan<ActorPresence> actors)
            {
                PresenceRefreshes++;
                return _hostile[index];
            }

            public int CountSpawnPointsOwnedBy(int team) => 0;
        }

        private const float Tick = 1f / ProtocolConstants.SIM_TICK_RATE;

        private static readonly MatchRules Rules = MatchRules.Default;

        [Test]
        public void SpawnPointOwnerMatchesTheAuthorityOnEveryTickIncludingTheFlip()
        {
            var state = new CapturePointState(0, Vec3.Zero, radius: 10f, captureSpeed: 1f);
            var states = new List<CapturePointState> { state };
            var directory = new FakeCapturePoints(1);
            var slave = new CapturePointSlave(directory, 1);

            int flips = 0;
            byte previous = state.OwningTeam;

            for (int i = 0; i < 400; i++)
            {
                // Team 1 pushes it over, then team 0 pushes it back, so the assertion sees both
                // thresholds crossed and neutral in between.
                if (i < 150) state.Tick(0, 2, Tick, Rules);
                else state.Tick(2, 0, Tick, Rules);

                slave.Apply(states, ReadOnlySpan<ActorPresence>.Empty);

                Assert.AreEqual(Expected(state.OwningTeam), directory.Owners[0],
                    $"tick {i}: SpawnPoint.owner disagreed with the authority");

                if (state.OwningTeam != previous)
                {
                    flips++;
                    previous = state.OwningTeam;
                }
            }

            Assert.GreaterOrEqual(flips, 2, "the trace never changed hands twice — it proves nothing");
        }

        [Test]
        public void ANeutralPointLeavesSpawnPointOwnerAtMinusOne()
        {
            var states = new List<CapturePointState> { new CapturePointState(0, Vec3.Zero, radius: 10f) };
            var directory = new FakeCapturePoints(1);

            new CapturePointSlave(directory, 1).Apply(states, ReadOnlySpan<ActorPresence>.Empty);

            Assert.AreEqual(-1, directory.Owners[0]);
        }

        [Test]
        public void ControlIsTheMagnitudeSoTheFlagPoleRisesForEitherTeam()
        {
            var state = new CapturePointState(0, Vec3.Zero, radius: 10f, captureSpeed: 1f);
            var states = new List<CapturePointState> { state };
            var directory = new FakeCapturePoints(1);
            var slave = new CapturePointSlave(directory, 1);

            for (int i = 0; i < 200; i++)
            {
                state.Tick(2, 0, Tick, Rules);
                slave.Apply(states, ReadOnlySpan<ActorPresence>.Empty);

                Assert.GreaterOrEqual(directory.Controls[0], 0f,
                    "control went negative — the flag pole would sink as team 0 captured");
                Assert.LessOrEqual(directory.Controls[0], 1f);
            }

            Assert.AreEqual(1f, directory.Controls[0], 0.001f);
        }

        [Test]
        public void PresenceIsRefreshedOnTheDividerAndOwnershipEveryTick()
        {
            var states = new List<CapturePointState> { new CapturePointState(0, Vec3.Zero, radius: 10f) };
            var directory = new FakeCapturePoints(1);
            var slave = new CapturePointSlave(directory, 1);

            const int ticks = 60;
            for (int i = 0; i < ticks; i++) slave.Apply(states, ReadOnlySpan<ActorPresence>.Empty);

            Assert.AreEqual(ticks, directory.OwnerWrites, "ownership must be exact on the tick it flips");
            Assert.AreEqual(ticks / CapturePointSlave.ContestedRefreshTicks, directory.PresenceRefreshes);
        }

        /// <summary>
        /// The contested flag is only recomputed on refresh ticks, so its last value has to
        /// survive the ticks in between — otherwise a contested point reads as safe on five of
        /// every six ticks and a defender's spawn direction flickers.
        /// </summary>
        [Test]
        public void TheContestedFlagPersistsBetweenRefreshes()
        {
            var states = new List<CapturePointState> { new CapturePointState(0, Vec3.Zero, radius: 10f) };
            var directory = new FakeCapturePoints(1);
            var slave = new CapturePointSlave(directory, 1);

            directory.SetHostile(0, true);
            for (int i = 0; i < CapturePointSlave.ContestedRefreshTicks; i++)
            {
                slave.Apply(states, ReadOnlySpan<ActorPresence>.Empty);
                Assert.IsTrue(directory.Contested[0], $"tick {i} lost the contested flag between refreshes");
            }
        }

        [Test]
        public void AResetRewindsTheDividerSoANewRoundOpensOnARefresh()
        {
            var states = new List<CapturePointState> { new CapturePointState(0, Vec3.Zero, radius: 10f) };
            var directory = new FakeCapturePoints(1);
            var slave = new CapturePointSlave(directory, 1);

            slave.Apply(states, ReadOnlySpan<ActorPresence>.Empty);
            slave.Apply(states, ReadOnlySpan<ActorPresence>.Empty);
            Assert.AreEqual(1, directory.PresenceRefreshes);

            slave.Reset();
            slave.Apply(states, ReadOnlySpan<ActorPresence>.Empty);

            Assert.AreEqual(2, directory.PresenceRefreshes);
        }

        /// <summary>
        /// A directory shorter than the state list — a scene edited between binds — must not
        /// throw on the tick path. Writing off the end of the array is a crash; stopping at the
        /// shorter of the two is a visibly stale flag, and only one of those is recoverable.
        /// </summary>
        [Test]
        public void AShorterDirectoryDoesNotWalkOffTheEnd()
        {
            var states = new List<CapturePointState>
            {
                new CapturePointState(0, Vec3.Zero, radius: 10f),
                new CapturePointState(1, Vec3.Zero, radius: 10f),
                new CapturePointState(2, Vec3.Zero, radius: 10f),
            };

            var directory = new FakeCapturePoints(2);
            var slave = new CapturePointSlave(directory, 2);

            Assert.DoesNotThrow(() => slave.Apply(states, ReadOnlySpan<ActorPresence>.Empty));
            Assert.AreEqual(2, directory.OwnerWrites);
        }

        private static int Expected(byte owningTeam)
            => owningTeam == TeamId.Team0 ? 0 : owningTeam == TeamId.Team1 ? 1 : -1;
    }
}
