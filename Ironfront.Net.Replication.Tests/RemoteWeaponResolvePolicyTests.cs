using Ironfront.Net.Replication.Client;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    public sealed class RemoteWeaponResolvePolicyTests
    {
        [Fact]
        public void SameIdRetriesWhenTheFirstPresentationLookupFailed()
        {
            Assert.True(RemoteWeaponResolvePolicy.ShouldResolve(
                requestedId: 1, appliedId: 1, activeWeaponExists: false));
        }

        [Fact]
        public void SameLiveWeaponDoesNotInstantiateAgainEveryFrame()
        {
            Assert.False(RemoteWeaponResolvePolicy.ShouldResolve(
                requestedId: 1, appliedId: 1, activeWeaponExists: true));
        }

        [Fact]
        public void ChangedIdAlwaysResolves()
        {
            Assert.True(RemoteWeaponResolvePolicy.ShouldResolve(
                requestedId: 2, appliedId: 1, activeWeaponExists: true));
        }
    }
}
