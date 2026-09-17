#nullable enable

using System.Collections.Generic;
using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class MenuKeyboardNavigatorTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private MenuKeyboardNavigator _navigator = null!;
        private Button _first = null!;
        private Button _second = null!;
        private Button _third = null!;
        private Button _submit = null!;
        private Button _cancel = null!;

        [SetUp]
        public void SetUp()
        {
            var eventSystem = Make("EventSystem");
            eventSystem.AddComponent<EventSystem>();
            eventSystem.AddComponent<StandaloneInputModule>();

            _navigator = Make("Navigator").AddComponent<MenuKeyboardNavigator>();
            _first = MakeButton("First");
            _second = MakeButton("Second");
            _third = MakeButton("Third");
            _submit = MakeButton("Submit");
            _cancel = MakeButton("Cancel");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject item in _objects)
                Object.DestroyImmediate(item);
            _objects.Clear();
        }

        [Test]
        public void FocusFirstSelectsFirstLiveControl()
        {
            _first.interactable = false;
            _navigator.Configure(new Selectable[] { _first, _second }, _submit, _cancel);

            _navigator.FocusFirst();

            Assert.AreSame(_second.gameObject, EventSystem.current.currentSelectedGameObject);
        }

        [Test]
        public void MoveForwardWrapsAndSkipsDisabledControls()
        {
            _second.interactable = false;
            _navigator.Configure(
                new Selectable[] { _first, _second, _third }, _submit, _cancel);
            EventSystem.current.SetSelectedGameObject(_third.gameObject);

            _navigator.Move(backwards: false);

            Assert.AreSame(_first.gameObject, EventSystem.current.currentSelectedGameObject);
        }

        [Test]
        public void MoveBackwardWraps()
        {
            _navigator.Configure(new Selectable[] { _first, _third }, _submit, _cancel);
            EventSystem.current.SetSelectedGameObject(_first.gameObject);

            _navigator.Move(backwards: true);

            Assert.AreSame(_third.gameObject, EventSystem.current.currentSelectedGameObject);
        }

        [Test]
        public void SubmitAndCancelInvokeLiveButtonsOnce()
        {
            int submitCount = 0;
            int cancelCount = 0;
            _submit.onClick.AddListener(() => submitCount++);
            _cancel.onClick.AddListener(() => cancelCount++);
            _navigator.Configure(new Selectable[] { _first }, _submit, _cancel);

            _navigator.Submit();
            _navigator.Cancel();

            Assert.AreEqual(1, submitCount);
            Assert.AreEqual(1, cancelCount);
        }

        [Test]
        public void DisabledPrimaryDoesNotSubmit()
        {
            int submitCount = 0;
            _submit.onClick.AddListener(() => submitCount++);
            _submit.interactable = false;
            _navigator.Configure(new Selectable[] { _first }, _submit, _cancel);

            _navigator.Submit();

            Assert.AreEqual(0, submitCount);
        }

        [Test]
        public void MultilineInputKeepsEnterForItsOwnEditing()
        {
            int submitCount = 0;
            _submit.onClick.AddListener(() => submitCount++);
            InputField multiline = Make("Multiline").AddComponent<InputField>();
            multiline.lineType = InputField.LineType.MultiLineNewline;
            _navigator.Configure(new Selectable[] { multiline, _submit }, _submit, _cancel);
            EventSystem.current.SetSelectedGameObject(multiline.gameObject);

            _navigator.Submit();

            Assert.AreEqual(0, submitCount);
        }

        private GameObject Make(string name)
        {
            var item = new GameObject(name);
            _objects.Add(item);
            return item;
        }

        private Button MakeButton(string name) => Make(name).AddComponent<Button>();
    }
}
