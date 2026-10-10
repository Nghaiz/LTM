#nullable enable

using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The picture a map card shows: the chosen map, by day or in Night Mode (owner's run of
    /// 2026-10-10, task 4: "show the map and the mode on Create Room and Practice, not a
    /// placeholder"). The screens hold the pictures in <see cref="MapCatalog"/> order, each map's
    /// day then its night; the menu builder fills that list from <see cref="AssetName"/>.
    /// </summary>
    public static class MapPictureChoice
    {
        /// <summary>Pictures per map: by day, then at night.</summary>
        public const int PerMap = 2;

        /// <summary>How many pictures the screens hold.</summary>
        public static int Count => MapCatalog.All.Count * PerMap;

        /// <summary>Where the picture of <paramref name="mapId"/> in <paramref name="mode"/> sits in the list; -1 for a map not in the catalogue.</summary>
        public static int IndexOf(ushort mapId, GameMode mode)
        {
            for (int i = 0; i < MapCatalog.All.Count; i++)
            {
                if (MapCatalog.All[i].Id == mapId)
                    return (i * PerMap) + (mode == GameMode.Night ? 1 : 0);
            }
            return -1;
        }

        /// <summary>The picture's file under the menu's art folder: <c>maps/forestlake-night.png</c>.</summary>
        public static string AssetName(int index)
        {
            MapCatalog.MapEntry map = MapCatalog.All[index / PerMap];
            return "maps/" + map.SceneName.ToLowerInvariant() + (index % PerMap == 1 ? "-night.png" : "-day.png");
        }

        /// <summary>
        /// Puts the picture of <paramref name="mapId"/> in <paramref name="mode"/> on
        /// <paramref name="art"/>; leaves it as it is when there is none (a scene built before the
        /// pictures, or a map newer than them).
        /// </summary>
        public static void Show(Image? art, Sprite[]? pictures, ushort mapId, GameMode mode)
        {
            if (art == null || pictures == null) return;
            int index = IndexOf(mapId, mode);
            if (index < 0 || index >= pictures.Length || pictures[index] == null) return;
            if (art.sprite == pictures[index]) return;
            art.sprite = pictures[index];
            AspectRatioFitter fitter = art.GetComponent<AspectRatioFitter>();
            if (fitter != null) fitter.aspectRatio = art.sprite.rect.width / art.sprite.rect.height;
        }
    }
}
