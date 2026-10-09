using NUnit.Framework;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Pins the "flag any" draw that the server and, since phase P35, the waiting client both make
    /// (<see cref="DeployFlagDraw"/>): the client draws it early so it can read the grass where the
    /// deploy will land, which is only harmless if the draw is still fair.
    /// </summary>
    public sealed class DeployFlagDrawTests
    {
        private static readonly int[] Owners = { 0, 1, 0, -1, 0, 1, 0 };

        [Test]
        public void OnlyFlagsTheTeamOwnsAreEverDrawn()
        {
            var random = new System.Random(7);
            for (int draw = 0; draw < 2000; draw++)
            {
                int index = DeployFlagDraw.Uniform(Owners.Length, i => Owners[i] == 1, random.Next);
                Assert.That(index, Is.EqualTo(1).Or.EqualTo(5),
                    "A deploy must never be sent to a flag the team does not own.");
            }
        }

        [Test]
        public void EveryOwnedFlagIsEquallyLikely()
        {
            var random = new System.Random(11);
            var hits = new int[Owners.Length];
            const int draws = 40000;
            for (int draw = 0; draw < draws; draw++)
                hits[DeployFlagDraw.Uniform(Owners.Length, i => Owners[i] == 0, random.Next)]++;

            // Team 0 owns indices 0, 2, 4 and 6: a quarter each, within 2 points.
            foreach (int owned in new[] { 0, 2, 4, 6 })
                Assert.AreEqual(0.25, hits[owned] / (double)draws, 0.02,
                    $"Flag {owned} must be drawn as often as any other flag the team owns; drawing " +
                    "early must not change where players land.");
        }

        [Test]
        public void ATeamWithNoFlagDrawsNothing()
        {
            Assert.AreEqual(-1, DeployFlagDraw.Uniform(Owners.Length, i => Owners[i] == 2, n => 0),
                "With no flag owned there is nowhere to deploy; the caller must see -1, not flag 0.");
        }
    }
}
