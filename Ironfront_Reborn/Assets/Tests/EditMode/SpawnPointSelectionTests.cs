using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Server.Tests
{
    /// <summary>
    /// Pins the respawn point selection that <c>ServerCombatBridge</c> performs.
    /// </summary>
    /// <remarks>
    /// This logic used to sit against <c>ActorManager.instance</c> inside a MonoBehaviour-bound
    /// assembly, which is why nothing tested it: no test assembly could see the type, and
    /// exercising it needed a loaded scene with authored spawn points. Behind
    /// <see cref="ISpawnPointDirectory"/> it is an ordinary function over an array.
    /// </remarks>
    public sealed class SpawnPointSelectionTests
    {
        /// <summary>A directory backed by owner ids; <see langword="null"/> is an empty slot.</summary>
        private sealed class FakeSpawnPoints : ISpawnPointDirectory
        {
            private readonly int?[] _owners;

            internal FakeSpawnPoints(params int?[] owners) => _owners = owners;

            /// <summary>How many times a position was actually asked for.</summary>
            internal int PositionsRequested { get; private set; }

            public int Count => _owners.Length;

            public bool IsEligible(int index, int team)
            {
                int? owner = _owners[index];
                if (owner == null) return false;              // the point == null branch
                return owner.Value < 0 || owner.Value == team; // owner < 0 means any team
            }

            public Vector3 GetSpawnPosition(int index)
            {
                PositionsRequested++;
                return new Vector3(index, 0f, 0f);
            }
        }

        [Test]
        public void AnEmptyDirectoryChoosesNothing()
        {
            Assert.AreEqual(-1, ServerCombatBridge.ChooseSpawnIndex(new FakeSpawnPoints(), 0));
        }

        [Test]
        public void SlotsHoldingNoPointAreSkipped()
        {
            var points = new FakeSpawnPoints(null, null, null);

            Assert.AreEqual(-1, ServerCombatBridge.ChooseSpawnIndex(points, 0));
        }

        [Test]
        public void OnlyPointsThisTeamOwnsAreEverChosen()
        {
            // Owners 0, 1, 1, 0. Team 1 may only ever land on index 1 or 2.
            var points = new FakeSpawnPoints(0, 1, 1, 0);

            for (int attempt = 0; attempt < 200; attempt++)
            {
                int chosen = ServerCombatBridge.ChooseSpawnIndex(points, 1);
                Assert.That(chosen, Is.EqualTo(1).Or.EqualTo(2),
                    $"attempt {attempt} put team 1 on index {chosen}, which another team owns");
            }
        }

        [Test]
        public void APointOwnedByNobodyIsEligibleForEveryTeam()
        {
            var points = new FakeSpawnPoints(-1);

            for (int team = 0; team < 4; team++)
                Assert.AreEqual(0, ServerCombatBridge.ChooseSpawnIndex(points, team));
        }

        [Test]
        public void ATeamWithNoPointOfItsOwnChoosesNothing()
        {
            // Every point belongs to team 0; team 3 has nowhere to go and must stay put rather
            // than spawn on someone else's line.
            var points = new FakeSpawnPoints(0, 0, 0);

            Assert.AreEqual(-1, ServerCombatBridge.ChooseSpawnIndex(points, 3));
        }

        [Test]
        public void SamplingReachesEveryEligiblePointRatherThanAlwaysTheFirst()
        {
            var points = new FakeSpawnPoints(-1, -1, -1);
            var seen = new bool[3];

            for (int attempt = 0; attempt < 300; attempt++)
                seen[ServerCombatBridge.ChooseSpawnIndex(points, 0)] = true;

            Assert.IsTrue(seen[0] && seen[1] && seen[2],
                $"300 draws only ever reached [{seen[0]}, {seen[1]}, {seen[2]}] — the reservoir "
                + "sampling has collapsed onto one point");
        }

        [Test]
        public void ChoosingAPointDoesNotAskAnyPointForItsPosition()
        {
            // SpawnPoint.GetSpawnPosition is virtual and subclasses jitter the result, so asking
            // every candidate in order to pick between them would be a different behaviour.
            var points = new FakeSpawnPoints(-1, -1, -1);

            ServerCombatBridge.ChooseSpawnIndex(points, 0);

            Assert.AreEqual(0, points.PositionsRequested);
        }
    }
}
