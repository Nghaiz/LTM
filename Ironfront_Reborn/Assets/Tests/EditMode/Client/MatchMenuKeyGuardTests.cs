#nullable enable

using System.Collections.Generic;
using Ironfront.Net.Unity.Client.Hud;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    /// <summary>
    /// The match's keys reach a menu only while one is on screen (owner's report of 2026-10-09:
    /// practice quit itself, or dropped the player at the menu, when DEPLOY stayed selected and
    /// strafing left plus a jump pressed the hidden EXIT GAME).
    /// </summary>
    public sealed class MatchMenuKeyGuardTests
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
        public void WhilePlayingTheClickedButtonIsDroppedAndTheKeysStopSteering()
        {
            EventSystem events = Make("EventSystem").AddComponent<EventSystem>();
            Button deploy = Make("Deploy Button").AddComponent<Button>();
            events.SetSelectedGameObject(deploy.gameObject);

            MatchMenuKeyGuard.Apply(events, menuShowing: false, typing: false);

            Assert.IsNull(events.currentSelectedGameObject,
                "a button left selected after its menu closed is pressed by the next Space");
            Assert.IsFalse(events.sendNavigationEvents,
                "WASD and Space must not move or press anything while the player is playing");
        }

        [Test]
        public void AMenuOnScreenKeepsItsSelectionAndItsKeys()
        {
            EventSystem events = Make("EventSystem").AddComponent<EventSystem>();
            Button resume = Make("Resume Button").AddComponent<Button>();
            events.SetSelectedGameObject(resume.gameObject);

            MatchMenuKeyGuard.Apply(events, menuShowing: true, typing: false);

            Assert.AreSame(resume.gameObject, events.currentSelectedGameObject);
            Assert.IsTrue(events.sendNavigationEvents);
        }

        [Test]
        public void TheChatBoxKeepsItsFieldWhileThePlayerTypes()
        {
            EventSystem events = Make("EventSystem").AddComponent<EventSystem>();
            InputField chat = Make("Chat Input").AddComponent<InputField>();
            events.SetSelectedGameObject(chat.gameObject);

            MatchMenuKeyGuard.Apply(events, menuShowing: false, typing: true);

            Assert.AreSame(chat.gameObject, events.currentSelectedGameObject,
                "typing reaches the field only while it is selected");
            Assert.IsFalse(events.sendNavigationEvents);
        }

        [Test]
        public void TheGuardRunsBeforeTheEventSystemReadsTheKeys()
        {
            object[] order = typeof(MatchMenuKeyGuard).GetCustomAttributes(typeof(DefaultExecutionOrder), false);

            Assert.AreEqual(1, order.Length);
            Assert.Less(((DefaultExecutionOrder)order[0]).order, 0,
                "the EventSystem runs at order 0; the guard must already have run that frame");
        }

        private GameObject Make(string name)
        {
            var item = new GameObject(name);
            _objects.Add(item);
            return item;
        }
    }
}
