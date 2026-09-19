#nullable enable

using System;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    [DisallowMultipleComponent]
    public sealed class MenuDevelopmentControls : MonoBehaviour
    {
        [SerializeField] private MenuToast? _toast;
        [SerializeField] private Button[] _buttons = Array.Empty<Button>();
        private bool _wired;

        public void Configure(MenuToast toast, params Button[] buttons)
        {
            _toast = toast;
            _buttons = buttons ?? Array.Empty<Button>();
            Wire();
        }

        private void Awake() => Wire();

        private void Wire()
        {
            if (_wired) return;
            _wired = true;
            foreach (Button button in _buttons)
                if (button != null) button.onClick.AddListener(ShowDevelopment);
        }

        private void ShowDevelopment() => _toast?.ShowDevelopment();
    }
}
