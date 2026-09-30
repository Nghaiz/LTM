using System;
using Ironfront.Net.Replication.Ai;
using Ironfront.Net.Replication.Movement;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Lone bots folding back into squads (phase P29): a live match drifted to one bot per squad,
    /// because nothing in the original ever put two squads together again.
    /// </summary>
    public sealed class SquadRegroupTests
    {
        private static SquadInfo Squad(int id, float x, float z, int size,
            SquadRole role = SquadRole.Attack, int flag = 0, bool inVehicle = false, bool engaged = false)
            => new SquadInfo
            {
                Id = id,
                Position = new Vec3(x, 0f, z),
                Size = size,
                Role = role,
                Flag = flag,
                InVehicle = inVehicle,
                Engaged = engaged,
            };

        private static SquadMerge[] Plan(params SquadInfo[] squads)
        {
            var merges = new SquadMerge[squads.Length];
            int written = new SquadRegroup().Plan(squads, merges);
            return merges.AsSpan(0, written).ToArray();
        }

        [Fact]
        public void ALoneBot_JoinsTheNearestSquadWithRoom()
        {
            SquadMerge[] merges = Plan(
                Squad(1, 0f, 0f, 1),
                Squad(2, 30f, 0f, 3),
                Squad(3, 10f, 0f, 2));

            Assert.Single(merges);
            Assert.Equal(0, merges[0].From);
            Assert.Equal(2, merges[0].Into);
        }

        [Fact]
        public void TwoLoneBots_PairUp()
        {
            SquadMerge[] merges = Plan(
                Squad(1, 0f, 0f, 1),
                Squad(2, 5f, 0f, 1));

            Assert.Single(merges);
            Assert.Equal(0, merges[0].From);
            Assert.Equal(1, merges[0].Into);
        }

        [Fact]
        public void ThreeLoneBots_BecomeOneSquad_NotTwo()
        {
            // The first joins the second; the pair then has room for the third.
            SquadMerge[] merges = Plan(
                Squad(1, 0f, 0f, 1),
                Squad(2, 5f, 0f, 1),
                Squad(3, 10f, 0f, 1));

            Assert.Equal(2, merges.Length);
            Assert.Equal(merges[0].Into, merges[1].Into);
        }

        [Fact]
        public void NoSquadGrowsPastTheLargestASpawnWaveForms()
        {
            SquadMerge[] merges = Plan(
                Squad(1, 0f, 0f, 1),
                Squad(2, 5f, 0f, SquadRegroup.MaxSize));

            Assert.Empty(merges);
        }

        [Fact]
        public void ALoneBotBeyondTheJoinRadius_StaysAlone()
        {
            SquadMerge[] merges = Plan(
                Squad(1, 0f, 0f, 1),
                Squad(2, SquadRegroup.JoinRadius + 1f, 0f, 2));

            Assert.Empty(merges);
        }

        [Fact]
        public void ABotInAFight_IsNotPulledOut_ButAFightingSquadMayTakeOneIn()
        {
            Assert.Empty(Plan(
                Squad(1, 0f, 0f, 1, engaged: true),
                Squad(2, 5f, 0f, 2)));

            SquadMerge[] merges = Plan(
                Squad(1, 0f, 0f, 1),
                Squad(2, 5f, 0f, 2, engaged: true));
            Assert.Single(merges);
            Assert.Equal(1, merges[0].Into);
        }

        [Fact]
        public void VehiclesAreLeftAlone_OnEitherSide()
        {
            Assert.Empty(Plan(
                Squad(1, 0f, 0f, 1, inVehicle: true),
                Squad(2, 5f, 0f, 2)));

            Assert.Empty(Plan(
                Squad(1, 0f, 0f, 1),
                Squad(2, 5f, 0f, 2, inVehicle: true)));
        }

        [Fact]
        public void ASquadOnTheSameJob_BeatsASlightlyNearerOneBoundElsewhere()
        {
            SquadMerge[] merges = Plan(
                Squad(1, 0f, 0f, 1, SquadRole.Attack, flag: 4),
                Squad(2, 10f, 0f, 2, SquadRole.Defend, flag: 1),
                Squad(3, 20f, 0f, 2, SquadRole.Attack, flag: 4));

            Assert.Single(merges);
            Assert.Equal(2, merges[0].Into);
        }

        [Fact]
        public void WholeSquadsAreNotTouched()
        {
            Assert.Empty(Plan(
                Squad(1, 0f, 0f, 2),
                Squad(2, 5f, 0f, 2),
                Squad(3, 10f, 0f, 3)));
        }

        [Fact]
        public void APlanIsTheSameWhateverOrderTheSquadsAreListedIn()
        {
            SquadInfo a = Squad(7, 0f, 0f, 1);
            SquadInfo b = Squad(3, 5f, 0f, 1);
            SquadInfo c = Squad(5, 8f, 0f, 2);

            SquadMerge[] first = Plan(a, b, c);
            SquadMerge[] second = Plan(c, b, a);

            // Same bots end up together: ids 3 and 7 both in the squad with id 5.
            Assert.Equal(2, first.Length);
            Assert.Equal(2, second.Length);
            Assert.All(first, m => Assert.Equal(2, m.Into));
            Assert.All(second, m => Assert.Equal(0, m.Into));
        }
    }
}
