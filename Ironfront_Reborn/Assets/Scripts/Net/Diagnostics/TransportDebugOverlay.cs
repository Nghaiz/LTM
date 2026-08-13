using Ironfront.Net.Protocol;
using Ironfront.Net.Transport;
using Ironfront.Net.Transport.Diagnostics;
using UnityEngine;

namespace Ironfront.Net.Unity.Diagnostics
{
    /// <summary>
    /// Optional IMGUI overlay for the transport metrics required by Phase 3.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Bind the real client after it is created:
    /// <c>overlay.Bind(transportClient);</c>. The component does not create a socket, poll a
    /// transport, or invent values when it is unbound.
    /// </para>
    /// <para>
    /// The default shortcut is Shift+F3. The legacy project already uses bare F3 for vehicle
    /// seat selection, so using the modifier preserves that gameplay binding while keeping the
    /// requested F3 diagnostic workflow one deliberate modifier away.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class TransportDebugOverlay : MonoBehaviour
    {
        [SerializeField] private bool _visible;
        [SerializeField] private bool _requireShift = true;
        [SerializeField] private KeyCode _toggleKey = KeyCode.F3;
        [SerializeField] private float _refreshSeconds = 0.25f;

        private ITransportClient _client;
        private string _text = "Transport overlay: unbound";
        private float _nextRefresh;
        private GUIStyle _style;

        /// <summary>Whether the overlay is currently drawn.</summary>
        public bool Visible => _visible;

        /// <summary>Binds the live transport client used by this overlay.</summary>
        public void Bind(ITransportClient client)
        {
            _client = client;
            _text = client == null
                ? "Transport overlay: unbound"
                : TransportDiagnosticsFormatter.Format(client.State, client.Stats);
            _nextRefresh = 0f;
        }

        /// <summary>Removes the current client binding without disposing it.</summary>
        public void Unbind()
        {
            _client = null;
            _text = "Transport overlay: unbound";
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey)
                && (!_requireShift || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
                _visible = !_visible;

            if (!_visible || _client == null || Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + Mathf.Max(0.05f, _refreshSeconds);
            _text = TransportDiagnosticsFormatter.Format(_client.State, _client.Stats);
        }

        private void OnGUI()
        {
            if (!_visible) return;
            if (_style == null)
            {
                _style = new GUIStyle(GUI.skin.box);
                _style.alignment = TextAnchor.UpperLeft;
                _style.fontSize = 13;
                _style.normal.textColor = Color.white;
            }

            GUI.Box(new Rect(12f, 12f, 360f, 142f), _text, _style);
        }
    }
}
