#nullable enable

using System.Collections.Generic;
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
