using System.Runtime;
using Xunit;

namespace Ironfront.Net.Replication.Tests
{
    /// <summary>
    /// The four zero-allocation tests (ClientEventConsumptionTests, DeployableTests,
    /// ObjectiveAuthorityTests, VehicleCaptureTests) read
    /// <c>GC.GetAllocatedBytesForCurrentThread</c>, and a background GC inflates that counter on a
    /// thread that allocated nothing: it empties the thread's allocation buffer without retiring
    /// the unused bytes (dotnet/runtime#134724). The .csproj turns background GC off for that
    /// reason. Without it those four fail at random, about one loaded run in thirty, which
    /// reads as a flaky test rather than as a missing setting.
    /// </summary>
    public sealed class AllocationCounterTests
    {
        [Fact]
        public void TheTestHostRunsWithoutBackgroundGc()
        {
            // Workstation GC reports Batch exactly when concurrent collection is off.
            Assert.True(
                GCSettings.LatencyMode == GCLatencyMode.Batch,
                $"GC latency mode is {GCSettings.LatencyMode}, so background GC is on and the " +
                "zero-allocation tests can read bytes nothing allocated. Keep " +
                "<ConcurrentGarbageCollection>false</ConcurrentGarbageCollection> in " +
                "Ironfront.Net.Replication.Tests.csproj.");
        }
    }
}
