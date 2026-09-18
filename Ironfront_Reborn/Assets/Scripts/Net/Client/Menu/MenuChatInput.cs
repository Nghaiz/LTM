#nullable enable

using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>Owns the single submit path shared by lobby chat's keyboard and Send button.</summary>
    [DisallowMultipleComponent]
    public sealed class MenuChatInput : MonoBehaviour
    {
        private InputField? _field;
        private Action<string>? _submit;
        private Func<bool>? _submitKeyDown;
        private EventSystem? _eventSystemOverride;
        private bool _subscribed;
        private bool _submitting;

        public void Configure(
            InputField field,
            Action<string> submit,
            Func<bool>? submitKeyDown = null,
            EventSystem? eventSystem = null)
        {
            Unsubscribe();
            _field = field;
            _submit = submit;
            _submitKeyDown = submitKeyDown ?? DefaultSubmitKeyDown;
            _eventSystemOverride = eventSystem;
            if (isActiveAndEnabled) Subscribe();
        }

        public void SubmitCurrent()
        {
            if (_field != null) Submit(_field.text);
        }

        private void OnEnable() => Subscribe();
        private void OnDisable() => Unsubscribe();

        private void Subscribe()
        {
            if (_subscribed || _field == null) return;
            _field.onEndEdit.AddListener(OnEndEdit);
            _subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!_subscribed || _field == null) return;
            _field.onEndEdit.RemoveListener(OnEndEdit);
            _subscribed = false;
        }

        private void OnEndEdit(string value)
        {
            if (_submitKeyDown?.Invoke() != true) return;
            Submit(value);
        }

        private void Submit(string value)
        {
            if (_submitting || _field == null) return;

            string text = (value ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                RestoreFocus();
                return;
            }

            _submitting = true;
            try
            {
                _submit?.Invoke(text);
                _field.text = string.Empty;
                RestoreFocus();
            }
            finally
            {
                _submitting = false;
            }
        }

        private void RestoreFocus()
        {
            if (_field == null || !_field.IsActive()) return;
            _field.Select();
            _field.ActivateInputField();
            EventSystem eventSystem = _eventSystemOverride ?? EventSystem.current;
            if (eventSystem != null)
                eventSystem.SetSelectedGameObject(_field.gameObject);
        }

        private static bool DefaultSubmitKeyDown()
            => Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
    }
}
