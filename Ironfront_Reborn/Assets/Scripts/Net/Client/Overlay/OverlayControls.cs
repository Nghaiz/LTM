#nullable enable

using System;
using System.Globalization;
using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// One row of a settings list: a label on the left with its description underneath, a control
    /// on the right. Hovering or selecting the row raises <see cref="Focused"/>, which the page's
    /// info panel shows.
    /// </summary>
    public sealed class SettingRow : MonoBehaviour, IPointerEnterHandler, ISelectHandler
    {
        public const float Height = 64f;

        public string Title { get; private set; } = string.Empty;
        public string Description { get; private set; } = string.Empty;
        public RectTransform Control { get; private set; } = null!;

        /// <summary>The row the pointer or the keyboard is on.</summary>
        public static event Action<SettingRow>? Focused;

        public void OnPointerEnter(PointerEventData eventData) => Focused?.Invoke(this);
        public void OnSelect(BaseEventData eventData) => Focused?.Invoke(this);

        /// <summary>A row <paramref name="width"/> wide with its control area <paramref name="controlWidth"/> on the right.</summary>
        public static SettingRow Create(Transform parent, string title, string description, float width, float controlWidth)
        {
            AngularPanel backing = Ui.Panel(parent, title, UiStyle.WithAlpha(UiStyle.Hex("0A1C2C"), 0.72f), 0f,
                UiStyle.WithAlpha(UiStyle.Hairline, 0.35f));
            backing.raycastTarget = true;
            ((RectTransform)backing.transform).sizeDelta = new Vector2(width, Height - 6f);

            SettingRow row = backing.gameObject.AddComponent<SettingRow>();
            row.Title = title;
            row.Description = description;

            Text label = Ui.Label(backing.transform, "Label", title.ToUpperInvariant(), 16, Ui.Weight.Bold, UiStyle.Ink);
            label.rectTransform.anchorMin = new Vector2(0f, 0.5f);
            label.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            label.rectTransform.pivot = new Vector2(0f, 0.5f);
            label.rectTransform.anchoredPosition = new Vector2(20f, 0f);
            label.rectTransform.sizeDelta = new Vector2(width - controlWidth - 40f, Height - 10f);

            row.Control = Ui.Child(backing.transform, "Control");
            row.Control.anchorMin = new Vector2(1f, 0.5f);
            row.Control.anchorMax = new Vector2(1f, 0.5f);
            row.Control.pivot = new Vector2(1f, 0.5f);
            row.Control.anchoredPosition = new Vector2(-14f, 0f);
            row.Control.sizeDelta = new Vector2(controlWidth, Height - 20f);
            return row;
        }
    }

    /// <summary>The pack's slider: a track, an orange fill, a square handle and the value beside it.</summary>
    public static class OverlaySlider
    {
        public static Slider Create(RectTransform area, float min, float max, Func<float, string> format, out Text value)
        {
            float width = area.sizeDelta.x;
            const float readout = 74f;

            RectTransform root = Ui.Child(area, "Slider");
            root.anchorMin = new Vector2(0f, 0.5f);
            root.anchorMax = new Vector2(0f, 0.5f);
            root.pivot = new Vector2(0f, 0.5f);
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = new Vector2(width - readout - 12f, 28f);

            Image track = Ui.Fill(root, "Track", UiStyle.WithAlpha(UiStyle.Hex("163049"), 0.95f));
            Ui.Stretch(track.rectTransform, 0f, 0f, 11f, 11f);
            track.raycastTarget = true;

            RectTransform fillArea = Ui.Child(root, "Fill Area");
            Ui.Stretch(fillArea, 0f, 0f, 11f, 11f);
            Image fill = Ui.Fill(fillArea, "Fill", UiStyle.Orange);
            fill.rectTransform.anchorMin = Vector2.zero;
            fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            fill.rectTransform.sizeDelta = Vector2.zero;

            RectTransform handleArea = Ui.Child(root, "Handle Area");
            Ui.Stretch(handleArea, 7f, 7f, 0f, 0f);
            AngularPanel handle = Ui.Panel(handleArea, "Handle", UiStyle.Ink, 3f, UiStyle.Orange, AngularEdge.All, 2f);
            handle.raycastTarget = true;
            handle.rectTransform.sizeDelta = new Vector2(14f, 0f);
            handle.rectTransform.anchorMin = new Vector2(0f, 0f);
            handle.rectTransform.anchorMax = new Vector2(0f, 1f);

            Slider slider = root.gameObject.AddComponent<Slider>();
            slider.fillRect = fill.rectTransform;
            slider.handleRect = handle.rectTransform;
            slider.targetGraphic = handle;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.colors = UiStyle.ButtonColours(slider.colors, UiStyle.Secondary);

            value = Ui.Label(area, "Value", string.Empty, 16, Ui.Weight.Bold, UiStyle.Amber, TextAnchor.MiddleRight);
            value.rectTransform.anchorMin = new Vector2(1f, 0.5f);
            value.rectTransform.anchorMax = new Vector2(1f, 0.5f);
            value.rectTransform.pivot = new Vector2(1f, 0.5f);
            value.rectTransform.anchoredPosition = Vector2.zero;
            value.rectTransform.sizeDelta = new Vector2(readout, 28f);

            Text readoutText = value;
            slider.onValueChanged.AddListener(v => readoutText.text = format(v));
            readoutText.text = format(slider.value);
            return slider;
        }

        public static string Percent(float v) => Mathf.RoundToInt(v * 100f).ToString(CultureInfo.InvariantCulture) + "%";
        public static string Degrees(float v) => Mathf.RoundToInt(v).ToString(CultureInfo.InvariantCulture) + "°";
        public static string TwoPlaces(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>An ON / OFF switch: a pill with a sliding knob that lights orange when on.</summary>
    public static class OverlaySwitch
    {
        public static Toggle Create(RectTransform area)
        {
            RectTransform root = Ui.Child(area, "Switch");
            root.anchorMin = root.anchorMax = root.pivot = new Vector2(1f, 0.5f);
            root.anchoredPosition = Vector2.zero;
            root.sizeDelta = new Vector2(120f, 34f);

            AngularPanel track = Ui.Panel(root, "Track", UiStyle.WithAlpha(UiStyle.Hex("163049"), 0.95f), 6f, UiStyle.Hex("5E89A9"));
            Ui.Stretch(track.rectTransform);
            track.raycastTarget = true;

            AngularPanel knob = Ui.Panel(root, "Knob", UiStyle.Muted, 4f, UiStyle.Ink);
            knob.rectTransform.anchorMin = knob.rectTransform.anchorMax = new Vector2(0f, 0.5f);
            knob.rectTransform.pivot = new Vector2(0f, 0.5f);
            knob.rectTransform.sizeDelta = new Vector2(52f, 26f);

            Text state = Ui.Label(root, "State", "OFF", 13, Ui.Weight.Bold, UiStyle.Muted, TextAnchor.MiddleCenter);
            Ui.Stretch(state.rectTransform);

            Toggle toggle = root.gameObject.AddComponent<Toggle>();
            toggle.targetGraphic = track;
            toggle.colors = UiStyle.ButtonColours(toggle.colors, UiStyle.Secondary);
            toggle.transition = Selectable.Transition.ColorTint;

            void Draw(bool on)
            {
                knob.rectTransform.anchoredPosition = new Vector2(on ? 64f : 4f, 0f);
                knob.color = on ? UiStyle.Orange : UiStyle.Muted;
                state.text = on ? "ON" : "OFF";
                state.alignment = on ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
                state.rectTransform.offsetMin = new Vector2(on ? 14f : 0f, 0f);
                state.rectTransform.offsetMax = new Vector2(on ? 0f : -14f, 0f);
                state.color = on ? UiStyle.Ink : UiStyle.Muted;
            }

            toggle.onValueChanged.AddListener(Draw);
            Draw(false);
            return toggle;
        }
    }

    /// <summary>
    /// A choice among named values: ◀ value ▶. Either arrow, or a click on the value, steps it;
    /// the ends wrap, as a console menu does.
    /// </summary>
    public sealed class OverlayStepper : MonoBehaviour
    {
        private string[] _options = Array.Empty<string>();
        private Text? _value;
        private Button? _left;
        private Button? _right;
        private int _index;

        public event Action<int>? Changed;

        public int Index => _index;

        public static OverlayStepper Create(RectTransform area)
        {
            RectTransform root = Ui.Child(area, "Stepper");
            Ui.Stretch(root);
            OverlayStepper stepper = root.gameObject.AddComponent<OverlayStepper>();

            AngularPanel face = Ui.Panel(root, "Face", UiStyle.WithAlpha(UiStyle.Hex("071523"), 0.92f), 0f, UiStyle.Hex("5E89A9"));
            Ui.Stretch(face.rectTransform, 46f, 46f, 0f, 0f);
            face.raycastTarget = true;
            Button cycle = face.gameObject.AddComponent<Button>();
            cycle.targetGraphic = face;
            cycle.colors = UiStyle.ButtonColours(cycle.colors, UiStyle.Secondary);
            cycle.onClick.AddListener(() => stepper.Step(1));

            stepper._value = Ui.Label(face.transform, "Value", string.Empty, 15, Ui.Weight.Bold, UiStyle.Ink, TextAnchor.MiddleCenter);
            Ui.Stretch(stepper._value.rectTransform, 8f, 8f, 0f, 0f);

            stepper._left = Arrow(root, "Previous", "arrow-left", new Vector2(0f, 0.5f));
            stepper._left.onClick.AddListener(() => stepper.Step(-1));
            stepper._right = Arrow(root, "Next", "arrow-right", new Vector2(1f, 0.5f));
            stepper._right.onClick.AddListener(() => stepper.Step(1));
            return stepper;
        }

        private static Button Arrow(RectTransform parent, string name, string icon, Vector2 anchor)
        {
            AngularPanel face = Ui.Panel(parent, name, UiStyle.WithAlpha(UiStyle.Hex("0A1927"), 0.92f), 4f, UiStyle.Hex("7FB5DB"));
            face.rectTransform.anchorMin = face.rectTransform.anchorMax = face.rectTransform.pivot = anchor;
            face.rectTransform.anchoredPosition = Vector2.zero;
            face.rectTransform.sizeDelta = new Vector2(40f, 40f);
            face.raycastTarget = true;
            Image glyph = Ui.Icon(face.transform, "Icon", icon, UiStyle.Ink);
            Ui.Centre(glyph.rectTransform, Vector2.zero, new Vector2(16f, 16f));
            Button button = face.gameObject.AddComponent<Button>();
            button.targetGraphic = face;
            button.colors = UiStyle.ButtonColours(button.colors, UiStyle.Secondary);
            return button;
        }

        public void SetOptions(string[] options, int index)
        {
            _options = options ?? Array.Empty<string>();
            SetIndex(index, notify: false);
        }

        public void SetIndex(int index, bool notify)
        {
            if (_options.Length == 0) return;
            _index = Mathf.Clamp(index, 0, _options.Length - 1);
            if (_value != null) _value.text = _options[_index];
            if (notify) Changed?.Invoke(_index);
        }

        public void Step(int direction)
        {
            if (_options.Length == 0) return;
            SetIndex((_index + direction + _options.Length) % _options.Length, notify: true);
        }

        public void SetInteractable(bool interactable)
        {
            if (_left != null) _left.interactable = interactable;
            if (_right != null) _right.interactable = interactable;
            Button? cycle = _value != null ? _value.transform.parent.GetComponent<Button>() : null;
            if (cycle != null) cycle.interactable = interactable;
            if (_value != null) _value.color = interactable ? UiStyle.Ink : UiStyle.Faint;
        }
    }
}
