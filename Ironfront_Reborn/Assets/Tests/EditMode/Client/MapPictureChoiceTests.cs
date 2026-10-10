using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The map cards on Create Room and Practice show the chosen map by day or in Night Mode
    /// (owner's run of 2026-10-10, task 4), from one list in catalogue order.
    /// </summary>
    public sealed class MapPictureChoiceTests
    {
        private const string ArtRoot = "Assets/UI/IronfrontReborn/";

        [Test]
        public void EachMapHasItsDayThenItsNight()
        {
            Assert.AreEqual(MapCatalog.All.Count * 2, MapPictureChoice.Count);
            for (int i = 0; i < MapCatalog.All.Count; i++)
            {
                MapCatalog.MapEntry map = MapCatalog.All[i];
                int day = MapPictureChoice.IndexOf(map.Id, GameMode.PointMatch);
                int night = MapPictureChoice.IndexOf(map.Id, GameMode.Night);
                Assert.AreEqual(i * 2, day, map.SceneName);
                Assert.AreEqual(day + 1, night, map.SceneName);
                StringAssert.Contains(map.SceneName.ToLowerInvariant() + "-day", MapPictureChoice.AssetName(day));
                StringAssert.Contains(map.SceneName.ToLowerInvariant() + "-night", MapPictureChoice.AssetName(night));
            }
            Assert.AreEqual(-1, MapPictureChoice.IndexOf(0, GameMode.PointMatch), "no map 0");
        }

        [Test]
        public void EveryPictureIsASprite()
        {
            for (int i = 0; i < MapPictureChoice.Count; i++)
            {
                string path = ArtRoot + MapPictureChoice.AssetName(i);
                Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<Sprite>(path), path + " is missing or not a sprite");
            }
        }
    }
}
