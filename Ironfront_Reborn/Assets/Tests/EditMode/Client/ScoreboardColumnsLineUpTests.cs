using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// Every stat column of the Tab board is one line: its head's mark and name centred over the same
    /// box its numbers are centred in (owner's list of 2026-10-11, item 3: the heads read as crooked,
    /// each right-aligned name and mark sticking out by its own width).
    /// </summary>
    public sealed class ScoreboardColumnsLineUpTests
    {
        private const string PrefabPath = "Assets/Prefab/Ingame UI Container.prefab";

        private static readonly (string Head, string Cell)[] StatColumns =
        {
            ("KILLS", "Kills"), ("DEATHS", "Deaths"), ("K/D", "Ratio"), ("HEADS", "Headshots"),
            ("STREAK", "Streak Cell"), ("BEST", "Best"), ("SCORE", "Score"), ("PING", "Ping Cell"),
        };

        [Test]
        public void EveryStatHeadSitsOverItsNumbersAndBothAreCentred()
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            Assert.IsNotNull(prefab, PrefabPath);

            int sides = 0;
            foreach (Transform columns in prefab.GetComponentsInChildren<Transform>(true))
            {
                if (columns.name != "Columns" || columns.parent.Find("Rows") == null) continue;
                Transform row = columns.parent.Find("Rows/Row Template");
                Assert.IsNotNull(row, columns.parent.name + " has no row template");
                sides++;

                foreach ((string headName, string cellName) in StatColumns)
                {
                    var head = (RectTransform)Child(columns, headName);
                    var cell = (RectTransform)row.Find(cellName);
                    Assert.IsNotNull(head, headName);
                    Assert.IsNotNull(cell, cellName);
                    Assert.AreEqual(head.anchorMin.x, cell.anchorMin.x, headName + " anchor");
                    Assert.AreEqual(head.anchoredPosition.x, cell.anchoredPosition.x, 0.01f, headName + " x");
                    Assert.AreEqual(head.sizeDelta.x, cell.sizeDelta.x, 0.01f, headName + " width");

                    Assert.AreEqual(TextAnchor.LowerCenter, head.Find("Caption").GetComponent<Text>().alignment, headName);
                    Assert.AreEqual(0.5f, ((RectTransform)head.Find("Icon")).anchorMin.x, headName + " mark");
                    Text number = cell.GetComponent<Text>();
                    if (number != null) Assert.AreEqual(TextAnchor.MiddleCenter, number.alignment, cellName);
                    else Assert.AreEqual(TextAnchor.MiddleCenter, cell.GetComponent<HorizontalLayoutGroup>().childAlignment, cellName);
                }
            }
            Assert.AreEqual(2, sides, "the board has two sides");
        }

        /// <summary>A direct child by name: Transform.Find reads the slash in "K/D" as a path.</summary>
        private static Transform Child(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                if (parent.GetChild(i).name == name) return parent.GetChild(i);
            }
            return null;
        }
    }
}
