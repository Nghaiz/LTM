#nullable enable

using System.Linq;
using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class MenuRuntimeThemeTests
    {
        private GameObject _root = null!;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Multiplayer Menu", typeof(RectTransform), typeof(Canvas));
            GameObject title = Child(_root, "Title", typeof(Image));
            Child(title, "Heading", typeof(Text)).GetComponent<Text>().text = "IRONFRONT";
            MakeButton(title, "Multiplayer", "MULTIPLAYER");
            MakeButton(title, "Practice", "Practice (offline)");
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        [Test]
        public void ApplyAddsBrandCardNavigationAndTransition()
        {
            MenuRuntimeTheme.Apply(_root);

            GameObject title = _root.transform.Find("Title").gameObject;
            string copy = string.Join(
                "\n",
                title.GetComponentsInChildren<Text>(true).Select(item => item.text));
            StringAssert.Contains("IRONFRONT REBORN", copy);
            StringAssert.Contains("TEAM 10 LTM", copy);
            Assert.NotNull(title.GetComponent<MenuKeyboardNavigator>());
            Assert.NotNull(title.GetComponent<MenuScreenTransition>());
            Assert.NotNull(title.transform.Find("Theme Content Card"));
            Assert.NotNull(title.transform.Find("Theme Brand Rail"));
        }

        [Test]
        public void ApplyStylesButtonsWithVisibleInteractionStates()
        {
            MenuRuntimeTheme.Apply(_root);

            Button button = _root.GetComponentsInChildren<Button>(true).First();
            ColorBlock colors = button.colors;
            Assert.AreNotEqual(colors.normalColor, colors.highlightedColor);
            Assert.AreNotEqual(colors.normalColor, colors.selectedColor);
            Assert.AreNotEqual(colors.normalColor, colors.disabledColor);
        }

        [Test]
        public void ApplyIsIdempotent()
        {
            MenuRuntimeTheme.Apply(_root);
            int childCount = _root.GetComponentsInChildren<Transform>(true).Length;

            MenuRuntimeTheme.Apply(_root);

            Assert.AreEqual(childCount, _root.GetComponentsInChildren<Transform>(true).Length);
        }

        private static GameObject Child(GameObject parent, string name, params System.Type[] types)
        {
            var child = new GameObject(name, types);
            child.transform.SetParent(parent.transform, worldPositionStays: false);
            return child;
        }

        private static void MakeButton(GameObject parent, string name, string caption)
        {
            GameObject host = Child(parent, name, typeof(Image), typeof(Button));
            GameObject label = Child(host, "Caption", typeof(Text));
            label.GetComponent<Text>().text = caption;
        }
    }
}
