#nullable enable

using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    public enum MenuNavigationAction
    {
        MainMenu,
        Settings,
        RoomBrowser,
    }

    /// <summary>Serializable runtime binding for links in the shared HTML-style top bar.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Button))]
    public sealed class MenuNavigationButton : MonoBehaviour
    {
        [SerializeField] private MenuScreenController? _controller;
        [SerializeField] private MenuNavigationAction _action;
        private bool _wired;

        public void Configure(MenuScreenController controller, MenuNavigationAction action)
        {
            _controller = controller;
            _action = action;
            Wire();
        }

        private void Awake() => Wire();

        private void Wire()
        {
            if (_wired) return;
            _wired = true;
            GetComponent<Button>().onClick.AddListener(Navigate);
        }

        private void Navigate()
        {
            if (_controller == null) return;

            switch (_action)
            {
                case MenuNavigationAction.MainMenu:
                    _controller.ReturnToMainMenu();
                    break;
                case MenuNavigationAction.Settings:
                    _controller.OpenSettings();
                    break;
                case MenuNavigationAction.RoomBrowser:
                    _controller.ShowRoomBrowser();
                    break;
            }
        }
    }
}
