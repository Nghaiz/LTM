using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;
using NUnit.Framework;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Every map the lobby offers Night Mode on has a night (owner's run of 2026-10-10: Island and
    /// Dustbowl get Forest Lake's night). The rule (<see cref="RoomRules.ModeAllowedOn"/>) allows it
    /// on any map, so a map without <c>Resources/NightMode/&lt;scene&gt;</c> would be offered a night
    /// and quietly played by day.
    /// </summary>
    public sealed class NightModeConfigCoverageTests
    {
        [Test]
        public void EveryCatalogueMapHasANightModeConfig()
        {
            foreach (MapCatalog.MapEntry map in MapCatalog.All)
            {
                Assert.IsTrue(RoomRules.ModeAllowedOn(GameMode.Night, map.Id), map.SceneName);
                Object config = Resources.Load("NightMode/" + map.SceneName);
                Assert.IsNotNull(config, "Resources/NightMode/" + map.SceneName + " is missing");
                Assert.AreEqual("NightModeConfig", config.GetType().Name, map.SceneName);
            }
        }
    }
}
