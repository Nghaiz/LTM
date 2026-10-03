using System;
using Ironfront.Net.Replication.Ai;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// Phase P32: respawning bots go to the flags where their side's vehicles stand empty, one
    /// promised seat each, so the HQ vehicles are crewed instead of left parked all match.
    /// </summary>
    public class VehicleSeatPromisesTests
    {
        private static IdleVehicle Vehicle(int key, int flag, int seats)
            => new IdleVehicle { Key = key, Flag = flag, FreeSeats = seats };

        [Fact]
        public void NoEmptyVehicle_SendsNobody()
        {
            var promises = new VehicleSeatPromises();
            Assert.Equal(-1, promises.Choose(ReadOnlySpan<IdleVehicle>.Empty, 0f));
            Assert.Equal(-1, promises.Choose(new[] { Vehicle(1, 0, 0) }, 0f));
        }

        [Fact]
        public void AVehicleIsPromisedNoMoreBotsThanItHasSeats()
        {
            var promises = new VehicleSeatPromises();
            IdleVehicle[] jeep = { Vehicle(7, 2, 4) };

            for (int bot = 0; bot < 4; bot++)
            {
                Assert.Equal(0, promises.Choose(jeep, 1f));
            }

            Assert.Equal(-1, promises.Choose(jeep, 1f));
            Assert.Equal(4, promises.Promised(7, 1f));
        }

        [Fact]
        public void OneVehicleIsFilledBeforeTheNextIsStarted()
        {
            var promises = new VehicleSeatPromises();
            IdleVehicle[] pad = { Vehicle(1, 0, 4), Vehicle(2, 0, 4) };

            int first = promises.Choose(pad, 0f);
            for (int bot = 1; bot < 4; bot++)
            {
                Assert.Equal(first, promises.Choose(pad, 0f));
            }

            Assert.Equal(1 - first, promises.Choose(pad, 0f));
        }

        [Fact]
        public void TheVehicleWithTheMostSeatsLeftIsStartedFirst_TiesToTheLowerKey()
        {
            var promises = new VehicleSeatPromises();
            Assert.Equal(1, promises.Choose(new[] { Vehicle(5, 0, 2), Vehicle(9, 1, 4) }, 0f));

            var fresh = new VehicleSeatPromises();
            Assert.Equal(1, fresh.Choose(new[] { Vehicle(9, 0, 4), Vehicle(3, 1, 4) }, 0f));
        }

        [Fact]
        public void APromiseLapses_SoASeatNobodyTookIsOfferedAgain()
        {
            var promises = new VehicleSeatPromises();
            IdleVehicle[] quad = { Vehicle(4, 1, 1) };

            Assert.Equal(0, promises.Choose(quad, 10f));
            Assert.Equal(-1, promises.Choose(quad, 10f + VehicleSeatPromises.PromiseSeconds - 0.01f));
            Assert.Equal(0, promises.Choose(quad, 10f + VehicleSeatPromises.PromiseSeconds));
        }

        [Fact]
        public void ClaimedSeatsAreNotPromisedTwice()
        {
            // The caller reports seats left after squads' claims: a jeep two bots have claimed
            // offers two more, and the promises count against those, not against four.
            var promises = new VehicleSeatPromises();
            IdleVehicle[] jeep = { Vehicle(8, 0, 2) };

            Assert.Equal(0, promises.Choose(jeep, 0f));
            Assert.Equal(0, promises.Choose(jeep, 0f));
            Assert.Equal(-1, promises.Choose(jeep, 0f));
        }
    }
}
