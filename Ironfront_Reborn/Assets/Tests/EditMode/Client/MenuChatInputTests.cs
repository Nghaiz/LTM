#nullable enable

using System.Collections.Generic;
using Ironfront.Net.Unity.Client.Menu;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Tests
{
    public sealed class MenuChatInputTests
    {
        private GameObject _root = null!;
        private InputField _field = null!;
        private MenuChatInput _input = null!;
        private EventSystem _eventSystem = null!;
        private readonly List<string> _submitted = new List<string>();
        private bool _submitKeyDown;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("Chat Test");
            GameObject eventSystemObject = new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
            _eventSystem = eventSystemObject.GetComponent<EventSystem>();
            GameObject fieldObject = new GameObject("ChatInput", typeof(RectTransform), typeof(Image), typeof(InputField));
            fieldObject.transform.SetParent(_root.transform, false);
            _field = fieldObject.GetComponent<InputField>();
            _input = _root.AddComponent<MenuChatInput>();
            _input.Configure(_field, text => _submitted.Add(text), () => _submitKeyDown, _eventSystem);
        }

        [TearDown]
        public void TearDown()
        {
            if (_eventSystem != null) Object.DestroyImmediate(_eventSystem.gameObject);
            Object.DestroyImmediate(_root);
            _submitted.Clear();
        }

        [Test]
        public void EndEditWithEnterSubmitsTrimmedTextOnceAndKeepsChatFocused()
        {
            _field.text = "  hello squad  ";
            _submitKeyDown = true;

            _field.onEndEdit.Invoke(_field.text);

            CollectionAssert.AreEqual(new[] { "hello squad" }, _submitted);
            Assert.AreEqual(string.Empty, _field.text);
            Assert.AreSame(_field.gameObject, _eventSystem.currentSelectedGameObject);
        }

        [Test]
        public void LosingFocusWithoutEnterDoesNotSend()
        {
            _field.text = "do not send";
            _submitKeyDown = false;

            _field.onEndEdit.Invoke(_field.text);

            Assert.IsEmpty(_submitted);
            Assert.AreEqual("do not send", _field.text);
        }

        [Test]
        public void WhitespaceIsNotSentAndTheFieldRemainsReady()
        {
            _field.text = "   ";
            _submitKeyDown = true;

            _field.onEndEdit.Invoke(_field.text);

            Assert.IsEmpty(_submitted);
            Assert.AreSame(_field.gameObject, _eventSystem.currentSelectedGameObject);
        }

        [Test]
        public void SendButtonPathUsesTheSameSingleSubmissionPipeline()
        {
            _field.text = "button message";

            _input.SubmitCurrent();

            CollectionAssert.AreEqual(new[] { "button message" }, _submitted);
            Assert.AreEqual(string.Empty, _field.text);
        }
    }
}
