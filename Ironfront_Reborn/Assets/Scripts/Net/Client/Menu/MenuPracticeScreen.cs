#nullable enable

using System.Linq;
using System.Collections.Generic;
using Ironfront.Net.Configuration;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    [DisallowMultipleComponent]
    public sealed class MenuPracticeScreen : MonoBehaviour
    {
        [SerializeField] private MenuScreenController? _controller;
        [SerializeField] private Dropdown? _mapDropdown;
        [SerializeField] private Button? _startButton;
        [SerializeField] private Button[] _unsupportedControls = System.Array.Empty<Button>();
        [SerializeField] private MenuToast? _toast;
        private readonly List<string> _sceneNames = new List<string>();

        private void Awake()
        {
            if (_mapDropdown != null)
            {
                _mapDropdown.ClearOptions();
                _mapDropdown.AddOptions(CurrentMapLabels().ToList());
            }
            _sceneNames.Clear();
            _sceneNames.AddRange(CurrentMapScenes());

            if (_startButton != null)
                _startButton.onClick.AddListener(StartPracticeConfiguration);
            foreach (Button control in _unsupportedControls)
                if (control != null) control.onClick.AddListener(ShowDevelopment);
        }

        internal static string[] CurrentMapLabels()
            => MapCatalog.All.Select(entry => entry.DisplayName).ToArray();

        internal static string[] CurrentMapScenes()
            => MapCatalog.All.Select(entry => entry.SceneName).ToArray();

        private void StartPracticeConfiguration()
        {
            if (_mapDropdown == null || _sceneNames.Count == 0) return;
            int index = Mathf.Clamp(_mapDropdown.value, 0, _sceneNames.Count - 1);
            _controller?.LaunchPracticeMap(_sceneNames[index]);
        }

        private void ShowDevelopment() => _toast?.ShowDevelopment();
    }
}
