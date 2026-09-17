#nullable enable

using System.Collections;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>Short, interruption-safe fade and settle animation for a menu panel.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(CanvasGroup))]
    public sealed class MenuScreenTransition : MonoBehaviour
    {
        [SerializeField, Min(0.01f)] private float _duration = 0.18f;
        [SerializeField, Min(0f)] private float _slideDistance = 18f;

        private CanvasGroup? _group;
        private RectTransform? _rect;
        private Coroutine? _animation;
        private Vector2 _shownPosition;

        public bool IsTargetVisible { get; private set; }

        private void Awake()
        {
            CacheReferences();
            IsTargetVisible = gameObject.activeSelf;
            ApplyInput(IsTargetVisible);
        }

        public void SetVisible(bool visible, bool immediate = false)
        {
            CacheReferences();
            IsTargetVisible = visible;

            if (_animation != null)
            {
                StopCoroutine(_animation);
                _animation = null;
            }

            if (!visible && !gameObject.activeSelf)
            {
                ApplyFrame(MenuTransitionState.Evaluate(0f, false, 1f));
                return;
            }

            if (visible && !gameObject.activeSelf)
            {
                gameObject.SetActive(true);
                _group!.alpha = 0f;
            }

            ApplyInput(false);
            if (immediate || !Application.isPlaying)
            {
                ApplyFrame(MenuTransitionState.Evaluate(_group!.alpha, visible, 1f));
                return;
            }

            _animation = StartCoroutine(Animate(_group!.alpha, visible));
        }

        private IEnumerator Animate(float startAlpha, bool targetVisible)
        {
            float elapsed = 0f;
            while (elapsed < _duration)
            {
                elapsed += Time.unscaledDeltaTime;
                ApplyFrame(MenuTransitionState.Evaluate(
                    startAlpha,
                    targetVisible,
                    elapsed / _duration));
                yield return null;
            }

            _animation = null;
            ApplyFrame(MenuTransitionState.Evaluate(startAlpha, targetVisible, 1f));
        }

        private void ApplyFrame(MenuTransitionFrame frame)
        {
            if (frame.Active && !gameObject.activeSelf) gameObject.SetActive(true);

            _group!.alpha = frame.Alpha;
            _group.interactable = frame.Interactable;
            _group.blocksRaycasts = frame.Interactable;

            if (_rect != null)
                _rect.anchoredPosition = _shownPosition
                                         + (Vector2.right * ((1f - frame.Alpha) * _slideDistance));

            if (!frame.Active && gameObject.activeSelf) gameObject.SetActive(false);
        }

        private void ApplyInput(bool enabled)
        {
            _group!.interactable = enabled;
            _group.blocksRaycasts = enabled;
        }

        private void CacheReferences()
        {
            if (_group == null) _group = GetComponent<CanvasGroup>();
            if (_rect != null) return;

            _rect = GetComponent<RectTransform>();
            if (_rect != null) _shownPosition = _rect.anchoredPosition;
        }
    }
}
