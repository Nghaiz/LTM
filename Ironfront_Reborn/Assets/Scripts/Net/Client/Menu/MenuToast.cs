#nullable enable

using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    [DisallowMultipleComponent]
    public sealed class MenuToast : MonoBehaviour
    {
        public const string DevelopmentMessage = "Tính năng đang được phát triển";

        [SerializeField] private Text? _label;
        [SerializeField, Min(0.25f)] private float _visibleSeconds = 2.5f;
        private Coroutine? _hideRoutine;

        public void Configure(Text label, float visibleSeconds = 2.5f)
        {
            _label = label;
            _visibleSeconds = visibleSeconds;
        }

        public void ShowDevelopment() => Show(DevelopmentMessage);

        public void Show(string message)
        {
            if (_label != null) _label.text = message ?? string.Empty;
            gameObject.SetActive(true);
            if (!Application.isPlaying) return;
            if (_hideRoutine != null) StopCoroutine(_hideRoutine);
            _hideRoutine = StartCoroutine(HideAfterDelay());
        }

        private IEnumerator HideAfterDelay()
        {
            yield return new WaitForSecondsRealtime(_visibleSeconds);
            _hideRoutine = null;
            gameObject.SetActive(false);
        }
    }
}
