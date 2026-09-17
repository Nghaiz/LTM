#nullable enable

using System;
using System.Collections;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>Keyboard focus, submit, and cancel semantics shared by every menu panel.</summary>
    [DisallowMultipleComponent]
    public sealed class MenuKeyboardNavigator : MonoBehaviour
    {
        [SerializeField] private Selectable[] _order = Array.Empty<Selectable>();
        [SerializeField] private Button? _primary;
        [SerializeField] private Button? _cancel;

        public void Configure(Selectable[] order, Button? primary, Button? cancel)
        {
            _order = order ?? Array.Empty<Selectable>();
            _primary = primary;
            _cancel = cancel;
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
            {
                bool backwards = Input.GetKey(KeyCode.LeftShift)
                                 || Input.GetKey(KeyCode.RightShift);
                Move(backwards);
            }
            else if (Input.GetKeyDown(KeyCode.Return)
                     || Input.GetKeyDown(KeyCode.KeypadEnter))
            {
                Submit();
            }
            else if (Input.GetKeyDown(KeyCode.Escape))
            {
                Cancel();
            }
        }

        public void FocusFirst()
        {
            bool[] selectable = Eligibility();
            int target = MenuNavigationCycle.NextIndex(-1, backwards: false, selectable);
            Select(target);
        }

        public void Move(bool backwards)
        {
            if (_order.Length == 0 || EventSystem.current == null) return;

            GameObject? selected = EventSystem.current.currentSelectedGameObject;
            int current = Array.FindIndex(
                _order,
                control => control != null && control.gameObject == selected);

            int target = MenuNavigationCycle.NextIndex(current, backwards, Eligibility());
            Select(target);
        }

        public void Submit()
        {
            if (EventSystem.current != null)
            {
                GameObject selected = EventSystem.current.currentSelectedGameObject;
                if (selected != null
                    && selected.TryGetComponent(out InputField field)
                    && field.lineType != InputField.LineType.SingleLine)
                {
                    return;
                }

                if (selected != null
                    && selected.TryGetComponent(out Button selectedButton)
                    && CanSelect(selectedButton))
                {
                    selectedButton.onClick.Invoke();
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
            for (int i = 0; i < _order.Length; i++) result[i] = CanSelect(_order[i]);
            return result;
        }

        private void Select(int index)
        {
            if (index < 0 || index >= _order.Length || EventSystem.current == null) return;
            EventSystem.current.SetSelectedGameObject(_order[index].gameObject);

            if (_order[index] is InputField field)
                field.ActivateInputField();
        }

        private static bool CanSelect(Selectable? control) =>
            control != null
            && control.gameObject.activeInHierarchy
            && control.IsActive()
            && control.IsInteractable();
    }
}
