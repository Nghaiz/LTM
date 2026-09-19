#nullable enable

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    [DisallowMultipleComponent]
    public sealed class MenuKeyboardNavigator : MonoBehaviour
    {
        [SerializeField] private Selectable[] _order = Array.Empty<Selectable>();
        [SerializeField] private Button? _primary;
        [SerializeField] private Button? _cancel;
        private EventSystem? _eventSystemOverride;

        public void Configure(Selectable[] order, Button? primary, Button? cancel,
            EventSystem? eventSystem = null)
        {
            _order = order ?? Array.Empty<Selectable>();
            _primary = primary;
            _cancel = cancel;
            _eventSystemOverride = eventSystem;
        }

        private void OnEnable()
        {
            if (Application.isPlaying) StartCoroutine(FocusOnNextFrame());
            else FocusFirst();
        }

        private IEnumerator FocusOnNextFrame()
        {
            yield return null;
            FocusFirst();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.Tab))
                Move(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                Submit();
            else if (Input.GetKeyDown(KeyCode.Escape))
                Cancel();
        }

        public void FocusFirst()
            => Select(MenuNavigationCycle.NextIndex(-1, false, Eligibility()));

        public void Move(bool backwards)
        {
            EventSystem? eventSystem = ActiveEventSystem;
            if (_order.Length == 0 || eventSystem == null) return;
            GameObject? selected = eventSystem.currentSelectedGameObject;
            int current = Array.FindIndex(_order,
                control => control != null && control.gameObject == selected);
            Select(MenuNavigationCycle.NextIndex(current, backwards, Eligibility()));
        }

        public void Submit()
        {
            EventSystem? eventSystem = ActiveEventSystem;
            if (eventSystem != null)
            {
                GameObject selected = eventSystem.currentSelectedGameObject;
                if (selected != null && selected.TryGetComponent(out InputField field)
                    && (field.lineType != InputField.LineType.SingleLine
                        || field.GetComponentInParent<MenuChatInput>() != null))
                    return;
                if (selected != null && selected.TryGetComponent(out Button button)
                    && CanSelect(button))
                {
                    button.onClick.Invoke();
                    return;
                }
            }

            if (CanSelect(_primary)) _primary!.onClick.Invoke();
        }

        public void Cancel()
        {
            if (CanSelect(_cancel)) _cancel!.onClick.Invoke();
        }

        private bool[] Eligibility()
        {
            var result = new bool[_order.Length];
            for (int i = 0; i < result.Length; i++) result[i] = CanSelect(_order[i]);
            return result;
        }

        private void Select(int index)
        {
            EventSystem? eventSystem = ActiveEventSystem;
            if (index < 0 || index >= _order.Length || eventSystem == null) return;
            eventSystem.SetSelectedGameObject(_order[index].gameObject);
            if (_order[index] is InputField field) field.ActivateInputField();
        }

        private static bool CanSelect(Selectable? control) => control != null
            && control.gameObject.activeInHierarchy && control.IsActive() && control.IsInteractable();

        private EventSystem? ActiveEventSystem => _eventSystemOverride ?? EventSystem.current;
    }
}
