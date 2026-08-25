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

        // ---- PinnedSpawnPointDirectory - the X-22 fix -----------------------------------

        [Test]
        public void APinnedDirectoryReturnsThatIndexOnEveryDraw()
        {
            // The mirror of SamplingReachesEveryEligiblePointRatherThanAlwaysTheFirst, and the
            // whole point of X-22: with one candidate, Random.Range(0, 1) has a single value, so
            // the result does not depend on where in the draw sequence this call landed. That is
            // what a seed alone could not deliver - three clients join over a real socket at
            // times nobody controls.
            var points = new PinnedSpawnPointDirectory(new FakeSpawnPoints(-1, -1, -1), 2);

            for (int attempt = 0; attempt < 300; attempt++)
            {
                Assert.AreEqual(2, ServerCombatBridge.ChooseSpawnIndex(points, 0),
                    $"attempt {attempt} escaped the pin - the run is a coin flip again");
            }
        }

        [Test]
        public void APinnedDirectoryNarrowsAndNeverWidens()
        {
            // Pinning index 0, which team 0 owns. Team 1 must still get nothing rather than be
            // handed a point the team rule forbids: the pin removes candidates, it does not
            // grant eligibility.
            var points = new PinnedSpawnPointDirectory(new FakeSpawnPoints(0, -1, -1), 0);

            Assert.AreEqual(0, ServerCombatBridge.ChooseSpawnIndex(points, 0));
            Assert.AreEqual(-1, ServerCombatBridge.ChooseSpawnIndex(points, 1),
                "the pin widened eligibility - a pinned point owned by one team must starve the "
                + "other, loudly, rather than silently admit it");
        }

        [Test]
        public void PinningAnEmptySlotChoosesNothingRatherThanFallingBack()
        {
            // A fallback to sampling here would be the exact failure X-22 describes: a run that
            // quietly stopped being deterministic. -1 trips MoveToSpawnPoint's existing warning.
            var points = new PinnedSpawnPointDirectory(new FakeSpawnPoints(-1, null, -1), 1);

            Assert.AreEqual(-1, ServerCombatBridge.ChooseSpawnIndex(points, 0));
        }

        [Test]
        public void APinnedIndexIsASlotAndNeverTheNoPointSentinel()
        {
            // -1 is what ChooseSpawnIndex RETURNS for "nowhere to go". Accepting it as an input
            // would make a typo read as a deliberate pin onto nothing.
            Assert.Throws<System.ArgumentOutOfRangeException>(
                () => new PinnedSpawnPointDirectory(new FakeSpawnPoints(-1), -1));
        }

        [Test]
        public void APinnedDirectoryDelegatesCountAndPosition()
        {
            var inner = new FakeSpawnPoints(-1, -1, -1);
            var points = new PinnedSpawnPointDirectory(inner, 1);

            Assert.AreEqual(3, points.Count, "Count must stay the scene's, not the pin's");
            Assert.AreEqual(1, points.PinnedIndex);
            Assert.AreEqual(new Vector3(1f, 0f, 0f), points.GetSpawnPosition(1));
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
