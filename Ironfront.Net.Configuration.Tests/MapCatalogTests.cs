using System.Collections.Generic;
using Ironfront.Net.Configuration;
using Xunit;

namespace Ironfront.Net.Configuration.Tests
{
    /// <summary>
    /// The one mapId-to-scene table, which both ends of the connection now read. P8 task 3.2.
    /// </summary>
    /// <remarks>
    /// The interesting assertions here are the ones about 0 and about an unknown id. Both used
    /// to be unreachable states because no table existed; now they are the two ways a
    /// deployment can be wrong, and the whole value of the table is that it distinguishes them
    /// from a successful lookup rather than resolving everything to Dustbowl.
    /// </remarks>
    public sealed class MapCatalogTests
    {
        [Fact]
        public void EveryPlayableMapResolvesBothWays()
        {
            foreach (MapCatalog.MapEntry entry in MapCatalog.All)
            {
                Assert.True(MapCatalog.TryGetScene(entry.Id, out string scene));
                Assert.Equal(entry.SceneName, scene);

                Assert.True(MapCatalog.TryGetId(entry.SceneName, out ushort id));
                Assert.Equal(entry.Id, id);
            }
        }

        [Fact]
        public void NoTwoRowsShareAnIdOrAScene()
        {
            // A duplicate id makes TryGetScene answer with whichever row is first, and a
            // duplicate scene does the same to TryGetId -- so the server would announce one map
            // and the client would load another, with both lookups reporting success.
            var ids = new HashSet<ushort>();
            var scenes = new HashSet<string>();

            foreach (MapCatalog.MapEntry entry in MapCatalog.All)
            {
                Assert.True(ids.Add(entry.Id), $"map id {entry.Id} is claimed by two rows");
                Assert.True(scenes.Add(entry.SceneName), $"scene '{entry.SceneName}' is claimed by two rows");
            }
        }

        [Fact]
        public void ZeroIsNotAMap()
        {
            // 0 is the value an unset ushort takes, and CONNECT_ACCEPTED carried exactly that on
            // every connection until P8. It has to stay distinguishable from a real id or the
            // client cannot tell "the server told me nothing" from "the server said Dustbowl".
            Assert.False(MapCatalog.TryGetScene(0, out string scene));
            Assert.Equal(string.Empty, scene);
        }

        [Fact]
        public void AnIdNobodyClaimsIsReportedRatherThanGuessedAt()
        {
            Assert.False(MapCatalog.TryGetScene(9999, out _));
        }

        [Fact]
        public void TheDefaultMapIdNamesARealScene()
        {
            // Three defaults have to agree -- this one, DedicatedServerSceneBootstrap.DefaultScene
            // and the lane-B harness's -- or a client and a server that both fell back land on
            // different maps while both logs read as healthy.
            Assert.True(MapCatalog.TryGetScene(MapCatalog.DefaultMapId, out string scene));
            Assert.Equal("Dustbowl", scene);
        }

        [Fact]
        public void SceneOrDefaultSaysWhetherItActuallyResolved()
        {
            string known = MapCatalog.SceneOrDefault(MapCatalog.DefaultMapId, out bool resolved);
            Assert.True(resolved);
            Assert.Equal("Dustbowl", known);

            // The out-parameter is the whole point: without it a caller cannot log "map 7 is not
            // in this build" and instead loads the default in silence.
            string fallback = MapCatalog.SceneOrDefault(7, out bool guessed);
            Assert.False(guessed);
            Assert.Equal("Dustbowl", fallback);
        }

        [Theory]
        [InlineData("dustbowl")]
        [InlineData("DUSTBOWL")]
        [InlineData("Dust Bowl")]
        [InlineData("")]
        [InlineData(null)]
        public void SceneLookupIsOrdinalBecauseSceneManagerIs(string? candidate)
        {
            // A case-insensitive match here would hand back an id whose scene name then fails to
            // load, and the error would blame the id rather than the casing.
            Assert.False(MapCatalog.TryGetId(candidate, out ushort id));
            Assert.Equal((ushort)0, id);
        }

        [Fact]
        public void SurroundingWhitespaceIsToleratedBecauseEnvironmentValuesCarryIt()
        {
            Assert.True(MapCatalog.TryGetId("  Dustbowl  ", out ushort id));
            Assert.Equal(MapCatalog.DefaultMapId, id);
        }

        [Fact]
        public void TheShellScenesAreNotPlayableMaps()
        {
            // Splash and Menu carry no NetServerBootstrap, so a room advertising one names a map
            // on which no match can be hosted.
            Assert.False(MapCatalog.TryGetId("Menu", out _));
            Assert.False(MapCatalog.TryGetId("Splash", out _));
        }
    }
}
