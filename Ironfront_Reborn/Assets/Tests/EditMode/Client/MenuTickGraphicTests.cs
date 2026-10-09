#nullable enable

using System.Collections.Generic;
using System.Reflection;
using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The Remember me tick follows its box in the game, not only in the Editor run that built the
    /// menu (owner's report of 2026-10-09: "Remember me does not work").
    /// </summary>
    /// <remarks>
    /// The builder's <c>Configure</c> runs in the Editor, and a listener it added with
    /// <c>AddListener</c> was never saved into <c>Menu.unity</c>: in the game the click turned the
    /// box on and the tick stayed hidden. These tests rebuild that situation -- only the serialized
    /// fields survive, then Unity enables the component -- by calling the lifecycle message
    /// themselves, since EditMode does not send it.
    /// </remarks>
    public sealed class MenuTickGraphicTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject item in _objects)
                if (item != null) Object.DestroyImmediate(item);
            _objects.Clear();
        }

        [Test]
        public void InALoadedSceneAClickShowsTheTickAndASecondClickHidesIt()
        {
            (Toggle box, Image stroke, MenuTickGraphic tick) = BuiltCheckbox();
            Send(tick, "OnEnable");

            Click(box);
            Assert.IsTrue(box.isOn);
            Assert.IsTrue(stroke.enabled, "the box turned on but its tick did not show");

            Click(box);
            Assert.IsFalse(box.isOn);
            Assert.IsFalse(stroke.enabled);
        }

        [Test]
        public void AnAlreadyRememberedBoxShowsItsTickWhenTheScreenOpens()
        {
            (Toggle box, Image stroke, MenuTickGraphic tick) = BuiltCheckbox();
            box.isOn = true;

            Send(tick, "OnEnable");

            Assert.IsTrue(stroke.enabled);
        }

        [Test]
        public void ReopeningTheScreenDoesNotLeaveTheTickBehind()
        {
            (Toggle box, Image stroke, MenuTickGraphic tick) = BuiltCheckbox();
            Send(tick, "OnEnable");
            Send(tick, "OnDisable");
            Send(tick, "OnEnable");

            Click(box);
            Click(box);

            Assert.IsFalse(box.isOn);
            Assert.IsFalse(stroke.enabled);
        }

        /// <summary>A checkbox as <c>BuildMenuCanvas</c> leaves it in the scene: configured, unticked.</summary>
        private (Toggle Box, Image Stroke, MenuTickGraphic Tick) BuiltCheckbox()
        {
            GameObject go = Make("RememberMe");
            Toggle box = go.AddComponent<Toggle>();
            Image stroke = Make("TickLong").AddComponent<Image>();
            stroke.transform.SetParent(go.transform, false);
            box.isOn = false;
            MenuTickGraphic tick = go.AddComponent<MenuTickGraphic>();
            tick.Configure(box, stroke);
            // What saving the scene does to any listener the builder added with AddListener: only
            // persistent (Inspector) listeners are serialized, so the game loads none.
            box.onValueChanged.RemoveAllListeners();
            return (box, stroke, tick);
        }

        private static void Click(Toggle box)
            => box.OnPointerClick(new PointerEventData(null) { button = PointerEventData.InputButton.Left });

        private static void Send(MenuTickGraphic tick, string message)
            => typeof(MenuTickGraphic).GetMethod(message, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(tick, null);

        private GameObject Make(string name)
        {
            var item = new GameObject(name);
            _objects.Add(item);
            return item;
        }
    }
}
