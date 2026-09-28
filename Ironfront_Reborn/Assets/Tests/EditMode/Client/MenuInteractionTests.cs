#nullable enable

using System.Collections.Generic;
using System.Linq;
using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class MenuInteractionTests
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
        public void TransitionUsesHtmlScaleAndCubicEase()
        {
            MenuTransitionFrame start = MenuTransitionState.Evaluate(0f, true, 0f);
            MenuTransitionFrame half = MenuTransitionState.Evaluate(0f, true, 0.5f);
            MenuTransitionFrame end = MenuTransitionState.Evaluate(0f, true, 1f);

            Assert.AreEqual(1.008f, start.Scale, 0.0001f);
            Assert.Greater(half.Alpha, 0.5f);
            Assert.AreEqual(1f, end.Scale, 0.0001f);
            Assert.IsTrue(end.Interactable);
        }

        [Test]
        public void RepeatedVisibilityRequestDoesNotRestartAnAnimationAlreadyHeadingThere()
        {
            Assert.IsFalse(MenuTransitionState.ShouldRestart(
                currentTarget: false,
                requestedTarget: false,
                animationRunning: true,
                active: true,
                alpha: 0.4f));

            Assert.IsTrue(MenuTransitionState.ShouldRestart(
                currentTarget: false,
                requestedTarget: true,
                animationRunning: true,
                active: true,
                alpha: 0.4f));
        }

        [Test]
        public void KeyboardNavigationWrapsAndEnterInvokesSelectedButton()
        {
            EventSystem eventSystem = Make("EventSystem").AddComponent<EventSystem>();
            MenuKeyboardNavigator navigator = Make("Navigator").AddComponent<MenuKeyboardNavigator>();
            Button first = Make("First").AddComponent<Button>();
            Button disabled = Make("Disabled").AddComponent<Button>();
            Button last = Make("Last").AddComponent<Button>();
            disabled.interactable = false;
            int invoked = 0;
            first.onClick.AddListener(() => invoked++);
            navigator.Configure(new Selectable[] { first, disabled, last }, last, null, eventSystem);
            eventSystem.SetSelectedGameObject(last.gameObject);

            navigator.Move(backwards: false);
            navigator.Submit();

            Assert.AreSame(first.gameObject, eventSystem.currentSelectedGameObject);
            Assert.AreEqual(1, invoked);
        }

        [Test]
        public void ChatEnterSendsTrimmedTextOnceAndRestoresFocus()
        {
            EventSystem eventSystem = Make("EventSystem").AddComponent<EventSystem>();
            GameObject root = Make("ChatRoot");
            InputField field = Make("ChatInput").AddComponent<InputField>();
            field.transform.SetParent(root.transform, false);
            MenuChatInput input = root.AddComponent<MenuChatInput>();
            var submitted = new List<string>();
            input.Configure(field, submitted.Add, () => true, eventSystem);
            field.text = "  hold the line  ";

            field.onEndEdit.Invoke(field.text);

            CollectionAssert.AreEqual(new[] { "hold the line" }, submitted);
            Assert.AreEqual(string.Empty, field.text);
            Assert.AreSame(field.gameObject, eventSystem.currentSelectedGameObject);
        }

        /// <summary>
        /// A log longer than its label shows its newest lines, not its oldest.
        /// </summary>
        /// <remarks>
        /// The label's own overflow handling (Truncate) keeps the TOP lines, so an eight-line
        /// backlog in a label three or four lines tall showed the oldest messages and hid every
        /// new one.
        /// </remarks>
        [Test]
        public void ChatLogShowsTheNewestLinesThatFit()
        {
            Text log = Make("ChatLog").AddComponent<Text>();
            log.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            log.fontSize = 22;
            log.rectTransform.sizeDelta = new Vector2(1400f, 100f);
            string[] lines = new string[MenuScreenController.ChatLines];
            for (int i = 0; i < lines.Length; i++) lines[i] = $"Player{i + 1}: line {i + 1}";

            string[] shown = MenuRoomLobbyScreen.NewestThatFit(log, string.Join("\n", lines)).Split('\n');

            Assert.Less(shown.Length, lines.Length, "The label holds the whole backlog; the test proves nothing.");
            Assert.Greater(shown.Length, 1);
            CollectionAssert.AreEqual(lines.Skip(lines.Length - shown.Length), shown);

            // A single line taller than the label is the newest thing said: kept, not emptied.
            string wall = "Player9: " + new string('x', 2000);
            Assert.AreEqual(wall, MenuRoomLobbyScreen.NewestThatFit(log, wall));
        }

        [Test]
        public void FieldCaretIsAtLeastItsScreenWidthAtEveryCanvasScale()
        {
            foreach (float scale in new[] { 0.25f, 0.49f, 0.5f, 0.75f, 1f, 1.5f, 2f, 3f })
                Assert.GreaterOrEqual(MenuFieldCaret.WidthFor(scale) * scale, MenuFieldCaret.ScreenPixels,
                    $"canvas scale {scale}");
        }

        [Test]
        public void DevelopmentToastUsesTheRequiredMessage()
        {
            GameObject root = Make("Toast");
            Text label = Make("ToastLabel").AddComponent<Text>();
            label.transform.SetParent(root.transform, false);
            MenuToast toast = root.AddComponent<MenuToast>();
            toast.Configure(label, 2f);

            toast.ShowDevelopment();

            Assert.AreEqual(MenuToast.DevelopmentMessage, label.text);
            Assert.AreEqual("Tính năng đang được phát triển", label.text);
            Assert.IsTrue(root.activeSelf);
        }

        [Test]
        public void DevelopmentControlsRouteClicksToTheSharedToast()
        {
            GameObject root = Make("DevelopmentControl");
            Button button = Make("Unsupported").AddComponent<Button>();
            Text label = Make("ToastLabel").AddComponent<Text>();
            MenuToast toast = Make("Toast").AddComponent<MenuToast>();
            toast.Configure(label);
            MenuDevelopmentControls controls = root.AddComponent<MenuDevelopmentControls>();
            controls.Configure(toast, button);

            button.onClick.Invoke();

            Assert.AreEqual(MenuToast.DevelopmentMessage, label.text);
        }

        [Test]
        public void SerializedTopBarNavigationStillInvokesItsControllerAtRuntime()
        {
            MenuScreenController controller = Make("Controller").AddComponent<MenuScreenController>();
            GameObject item = Make("SettingsLink");
            Button button = item.AddComponent<Button>();
            item.AddComponent<MenuNavigationButton>()
                .Configure(controller, MenuNavigationAction.Settings);

            button.onClick.Invoke();

            Assert.IsTrue(controller.IsSettingsScreenOpen);
        }

        [Test]
        public void PasswordRevealButtonTogglesMaskWithoutChangingTheValue()
        {
            InputField field = Make("Password").AddComponent<InputField>();
            field.contentType = InputField.ContentType.Password;
            field.text = "secret-value";
            Button button = Make("Reveal").AddComponent<Button>();
            Text caption = Make("Caption").AddComponent<Text>();
            MenuPasswordReveal reveal = Make("RevealBinding").AddComponent<MenuPasswordReveal>();
            reveal.Configure(field, button, caption);

            button.onClick.Invoke();
            Assert.AreEqual(InputField.ContentType.Standard, field.contentType);
            Assert.AreEqual("HIDE", caption.text);
            Assert.AreEqual("secret-value", field.text);

            button.onClick.Invoke();
            Assert.AreEqual(InputField.ContentType.Password, field.contentType);
            Assert.AreEqual("SHOW", caption.text);
        }

        private GameObject Make(string name)
        {
            var item = new GameObject(name);
            _objects.Add(item);
            return item;
        }
    }
}
