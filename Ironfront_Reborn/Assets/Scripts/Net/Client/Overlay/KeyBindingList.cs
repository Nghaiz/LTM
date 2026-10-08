#nullable enable

using System;
using System.Collections.Generic;
using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// The CONTROLS tab: every bindable action under its heading, with a primary and a secondary key
    /// cap. Click a cap, press the new key; Backspace or Delete clears the slot and Esc cancels.
    /// </summary>
    /// <remarks>
    /// The capture starts on the frame AFTER the click, so the mouse button that clicked the cap is
    /// not itself taken as the new key; after that a mouse button binds like any key. A key another
    /// action holds moves here (<see cref="KeyBindingSet.Bind"/>), and the notice says which action
    /// lost it, the way shooters handle a clash.
    /// </remarks>
    public sealed class KeyBindingList : MonoBehaviour
    {
        private const float CapWidth = 170f;

        private readonly List<(GameAction Action, int Slot, Button Cap, Text Text)> _caps =
            new List<(GameAction, int, Button, Text)>();

        private Func<KeyBindingSet> _keys = () => new KeyBindingSet();
        private Action _changed = () => { };
        private Action<string> _notice = _ => { };
        private bool _capturing;
        private int _captureFrom;
        private GameAction _captureAction;
        private int _captureSlot;

        public static KeyBindingList Build(RectTransform rows, float width, Func<KeyBindingSet> keys, Action changed,
            Action<string> notice)
        {
            KeyBindingList list = rows.gameObject.AddComponent<KeyBindingList>();
            list._keys = keys;
            list._changed = changed;
            list._notice = notice;

            float y = 0f;
            GameActionGroup? heading = null;
            foreach (GameActionInfo info in GameActionCatalog.All)
            {
                if (heading != info.Group)
                {
                    heading = info.Group;
                    Text title = Ui.Label(rows, "Heading " + info.Group, GameActionCatalog.GroupName(info.Group), 14,
                        Ui.Weight.Bold, UiStyle.CyanSoft);
                    Ui.TopLeft(title.rectTransform, new Vector2(4f, y + 10f), new Vector2(width - 8f, 24f));
                    if (y == 0f)
                    {
                        // Over the two key columns, once: which key is which.
                        ColumnHeading(rows, "PRIMARY", width - 14f - 2f * CapWidth - 12f, y);
                        ColumnHeading(rows, "SECONDARY", width - 14f - CapWidth, y);
                    }
                    Image rule = Ui.Fill(rows, "Rule " + info.Group, UiStyle.WithAlpha(UiStyle.Hairline, 0.4f));
                    Ui.TopLeft(rule.rectTransform, new Vector2(4f, y + 36f), new Vector2(width - 8f, 1f));
                    y += 46f;
                }

                SettingRow row = SettingRow.Create(rows, info.Name, info.Description, width, 2f * CapWidth + 12f);
                Ui.TopLeft((RectTransform)row.transform, new Vector2(0f, y), new Vector2(width, SettingRow.Height - 6f));
                for (int slot = 0; slot < KeyBindingSet.Slots; slot++)
                {
                    (Button cap, Text text) = Cap(row.Control, slot);
                    GameAction action = info.Action;
                    int chosen = slot;
                    cap.onClick.AddListener(() => list.BeginCapture(action, chosen));
                    list._caps.Add((info.Action, slot, cap, text));
                }
                y += SettingRow.Height;
            }

            rows.sizeDelta = new Vector2(0f, y);
            list.Refresh();
            return list;
        }

        private static void ColumnHeading(RectTransform rows, string caption, float left, float y)
        {
            Text text = Ui.Label(rows, "Column " + caption, caption, 12, Ui.Weight.Bold, UiStyle.Faint, TextAnchor.MiddleCenter);
            Ui.TopLeft(text.rectTransform, new Vector2(left, y + 10f), new Vector2(CapWidth, 24f));
        }

        private static (Button, Text) Cap(RectTransform area, int slot)
        {
            AngularPanel face = Ui.Panel(area, slot == 0 ? "Primary" : "Secondary",
                UiStyle.WithAlpha(UiStyle.Hex("0E2639"), 0.95f), 6f, UiStyle.Hex("5E89A9"));
            face.raycastTarget = true;
            var rect = (RectTransform)face.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(1f, 0.5f);
            rect.anchoredPosition = new Vector2(slot == 0 ? -(CapWidth + 12f) : 0f, 0f);
            rect.sizeDelta = new Vector2(CapWidth, 42f);
            Button button = face.gameObject.AddComponent<Button>();
            button.targetGraphic = face;
            button.colors = UiStyle.ButtonColours(button.colors, UiStyle.Secondary);
            Text text = Ui.Label(face.transform, "Key", string.Empty, 15, Ui.Weight.Bold, UiStyle.Ink, TextAnchor.MiddleCenter);
            Ui.Stretch(text.rectTransform, 8f, 8f, 0f, 0f);
            return (button, text);
        }

        /// <summary>Redraws every cap from the draft's keys.</summary>
        public void Refresh()
        {
            KeyBindingSet keys = _keys();
            foreach (var (action, slot, _, text) in _caps)
            {
                bool waiting = _capturing && action == _captureAction && slot == _captureSlot;
                KeyCode key = keys.Get(action, slot);
                text.text = waiting ? "PRESS A KEY" : key == KeyCode.None ? "—" : GameKeys.KeyName(key);
                text.color = waiting ? UiStyle.Orange
                    : key == KeyCode.None ? (slot == 0 && keys.IsUnbound(action) ? UiStyle.Red : UiStyle.Faint)
                    : UiStyle.Ink;
            }
        }

        /// <summary>Stops waiting for a key, if it was. True when a capture was cancelled.</summary>
        public bool CancelCapture()
        {
            if (!_capturing) return false;
            _capturing = false;
            Refresh();
            return true;
        }

        private void BeginCapture(GameAction action, int slot)
        {
            _capturing = true;
            _captureAction = action;
            _captureSlot = slot;
            _captureFrom = Time.frameCount + 1;
            _notice(string.Empty);
            Refresh();
        }

        private void Update()
        {
            if (!_capturing || Time.frameCount < _captureFrom) return;

            // Esc is the overlay's: OverlayHost hands it to the page, which cancels here.
            if (Input.GetKeyDown(KeyCode.Escape)) return;

            if (Input.GetKeyDown(KeyCode.Backspace) || Input.GetKeyDown(KeyCode.Delete))
            {
                _keys().Clear(_captureAction, _captureSlot);
                _capturing = false;
                Finish(string.Empty);
                return;
            }

            if (!GameKeys.TryReadPressedKey(out KeyCode key)) return;

            KeyBindingSet.BindResult result = _keys().Bind(_captureAction, _captureSlot, key);
            _capturing = false;
            string notice = string.Empty;
            if (result.Displaced)
            {
                GameActionInfo lost = GameActionCatalog.Get(result.DisplacedAction);
                notice = $"{GameKeys.KeyName(key)} moved here from {lost.Name.ToUpperInvariant()}."
                         + (_keys().IsUnbound(result.DisplacedAction)
                             ? $" {lost.Name.ToUpperInvariant()} now has no key: give it one before you play."
                             : string.Empty);
            }
            Finish(notice);
        }

        private void Finish(string notice)
        {
            Refresh();
            _notice(notice);
            _changed();
        }
    }
}
