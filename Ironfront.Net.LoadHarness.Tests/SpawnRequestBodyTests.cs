using System;
using Ironfront.Net.LoadHarness;
using Ironfront.Net.Protocol;
using Xunit;

namespace Ironfront.Net.LoadHarness.Tests
{
    /// <summary>
    /// The synthetic client's <c>C_SPAWN_REQUEST</c> body must be one the server accepts.
    /// </summary>
    /// <remarks>
    /// It was empty from the harness's first day, and from X-11 (2026-09-03) the server's router
    /// drops a body <see cref="SpawnRequestMessage.TryParse"/> refuses. So every synthetic client
    /// joined, asked to deploy and never got a body: a lane-A combat run on 2026-09-27 held 8/8
    /// clients to the end with zero trigger ticks and zero vehicle snapshots. The assertion is
    /// against the server's own parser, so a later change to the message shape fails here rather
    /// than in a run nobody reads the counters of.
    /// </remarks>
    public class SpawnRequestBodyTests
    {
        [Fact]
        public void TheSpawnRequestBodyParsesAsTheServerParsesIt()
        {
            Span<byte> body = stackalloc byte[SpawnRequestMessage.Size];

            int length = SyntheticClient.BuildSpawnRequestBody(body);

            Assert.Equal(SpawnRequestMessage.Size, length);
            Assert.True(SpawnRequestMessage.TryParse(body.Slice(0, length), out SpawnRequestMessage parsed));
            Assert.Equal(SyntheticClient.HarnessLoadout.Primary, parsed.Primary);
            Assert.Equal(SyntheticClient.HarnessLoadout.Gear1, parsed.Gear1);
        }

        [Fact]
        public void TheHarnessDeploysWithAGunAndAThrowable()
        {
            SpawnRequestMessage loadout = SyntheticClient.HarnessLoadout;

            Assert.NotEqual(WeaponIds.NONE, loadout.Primary);
            Assert.Equal(WeaponIds.FRAG, loadout.Gear1);
            Assert.Equal(SpawnRequestMessage.NoSpawnPointPreference, loadout.SpawnPointIndex);
        }
    }
}
