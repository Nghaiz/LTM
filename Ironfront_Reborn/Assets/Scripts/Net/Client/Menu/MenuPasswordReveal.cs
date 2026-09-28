#nullable enable

using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    [DisallowMultipleComponent]
    public sealed class MenuPasswordReveal : MonoBehaviour
    {
        [SerializeField] private InputField? _field;
        [SerializeField] private Button? _button;
        [SerializeField] private Text? _caption;
        private bool _wired;

        public void Configure(InputField field, Button button, Text caption)
        {
            _field = field;
            _button = button;
            _caption = caption;
            Wire();
            Draw();
        }

        private void Awake()
        {
            Wire();
            Draw();
        }

        private void Wire()
        {
            if (_wired || _button == null) return;
            _wired = true;
            _button.onClick.AddListener(Toggle);
        }

        private void Toggle()
        {
            if (_field == null) return;
            _field.contentType = _field.contentType == InputField.ContentType.Password
                ? InputField.ContentType.Standard
                : InputField.ContentType.Password;
            _field.ForceLabelUpdate();
            Draw();
        }

        private void Draw()
        {
            if (_caption != null && _field != null)
                _caption.text = _field.contentType == InputField.ContentType.Password ? "SHOW" : "HIDE";
        }
    }
}
