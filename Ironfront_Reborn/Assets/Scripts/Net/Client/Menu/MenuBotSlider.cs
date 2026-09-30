#nullable enable

using System;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The create-room form's bot slider: the match's total, 0 to 100 in steps of two, with a
    /// notch every ten, one-press presets, and the servers' remaining ceiling drawn on the track.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What may be chosen is <see cref="RoomBotChoice"/>'s to say</b>; this only draws it. The
    /// slider moves in units of <see cref="RoomBotChoice.Step"/> bots (its own range is 0 to 50),
    /// so an odd total cannot be picked at all rather than being rounded after the fact.
    /// </para>
    /// <para>
    /// <b>Past the ceiling the track is shaded and the handle stops.</b> The owner asked for the
    /// counts above what the servers can take to be visibly unavailable, so a player sees the
    /// limit before they press Create rather than learning it from a refusal. The master still
    /// checks the create, so a ceiling that went stale between the list and the press is refused
    /// with <c>ServerAtBotCapacity</c>, never exceeded.
    /// </para>
    /// <para>
    /// Built by <c>BuildMenuCanvas.BuildBotSlider</c>, which wires every reference below; a scene
    /// without one of them simply leaves that part undrawn.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class MenuBotSlider : MonoBehaviour
    {
        [Header("Track")]
        [SerializeField] private Slider? _slider;
        [SerializeField] private Image? _fill;
        [SerializeField] private Image? _handle;
        [SerializeField] private RectTransform? _capZone;
        [SerializeField] private RectTransform? _capMarker;

        [Header("Readout")]
        [SerializeField] private Text? _valueText;
        [SerializeField] private Text? _perSideText;
        [SerializeField] private Image? _tierChip;
        [SerializeField] private Text? _tierText;
        [SerializeField] private Text? _ceilingText;

        [Header("Notches and presets")]
        [SerializeField] private Text[] _tickLabels = Array.Empty<Text>();
        [SerializeField] private Image[] _tickMarks = Array.Empty<Image>();
        [SerializeField] private Button[] _presetButtons = Array.Empty<Button>();

        private static readonly Color TickInk = new Color(0.55f, 0.66f, 0.73f, 1f);
        private static readonly Color TickOff = new Color(0.25f, 0.33f, 0.40f, 1f);
        private static readonly Color CeilingOk = new Color(0.23f, 0.86f, 0.51f, 1f);
        private static readonly Color CeilingLimited = new Color(1f, 0.60f, 0.18f, 1f);
        private static readonly Color CeilingNone = new Color(1f, 0.32f, 0.40f, 1f);
        private static readonly Color CeilingUnknown = new Color(0.55f, 0.66f, 0.73f, 1f);

        private RoomCapacity? _capacity;
        private int _ceiling = ProtocolConstants.MAX_BOTS;
        private bool _interactable = true;

        /// <summary>Raised with the new total whenever it changes, by drag, key or preset.</summary>
        public event Action<int>? ValueChanged;

        /// <summary>The total chosen: even, 0 to the ceiling.</summary>
        public int Value => _slider != null ? Mathf.RoundToInt(_slider.value) * RoomBotChoice.Step : 0;

        /// <summary>The most the servers can take for this room right now.</summary>
        public int Ceiling => _ceiling;

        private void Awake()
        {
            if (_slider != null)
            {
                _slider.wholeNumbers = true;
                _slider.minValue = 0f;
                _slider.maxValue = ProtocolConstants.MAX_BOTS / RoomBotChoice.Step;
                _slider.onValueChanged.AddListener(OnSliderMoved);
            }

            for (int i = 0; i < _presetButtons.Length && i < RoomBotChoice.Presets.Length; i++)
            {
                int preset = RoomBotChoice.Presets[i];
                if (_presetButtons[i] != null)
                    _presetButtons[i].onClick.AddListener(() => Select(preset));
            }

            // Notified, so a form that subscribed before this Awake ran still hears the start
            // value; one that subscribes after reads Value itself.
            SetValue(RoomBotChoice.Default(_capacity), notify: true);
        }

        /// <summary>
        /// Takes the servers' latest answer: moves the ceiling, and pulls the handle back under it
        /// when it is now too high. Null is "not known yet".
        /// </summary>
        public void SetCapacity(RoomCapacity? capacity)
        {
            _capacity = capacity;
            _ceiling = RoomBotChoice.Ceiling(capacity);

            int value = Value;
            int allowed = RoomBotChoice.Snap(value, _ceiling);
            if (allowed != value) SetValue(allowed, notify: true);
            else Redraw();
        }

        /// <summary>Picks a total, clamped under the ceiling. What the presets call.</summary>
        public void Select(int bots) => SetValue(RoomBotChoice.Snap(bots, _ceiling), notify: true);

        /// <summary>Disables the slider and its presets while a request is in flight.</summary>
        public void SetInteractable(bool interactable)
        {
            _interactable = interactable;
            Redraw();
        }

        private void OnSliderMoved(float steps)
        {
            int bots = Mathf.RoundToInt(steps) * RoomBotChoice.Step;
            int allowed = RoomBotChoice.Snap(bots, _ceiling);
            if (allowed != bots && _slider != null)
                _slider.SetValueWithoutNotify(allowed / RoomBotChoice.Step);

            Redraw();
            ValueChanged?.Invoke(allowed);
        }

        private void SetValue(int bots, bool notify)
        {
            if (_slider != null) _slider.SetValueWithoutNotify(bots / RoomBotChoice.Step);
            Redraw();
            if (notify) ValueChanged?.Invoke(Value);
        }

        private void Redraw()
        {
            int bots = Value;
            RoomBotChoice.Tier tier = RoomBotChoice.TierOf(bots);
            Color tierColour = Hex(RoomBotChoice.TierHex(tier));

            if (_fill != null) _fill.color = tierColour;
            if (_handle != null) _handle.color = Color.Lerp(tierColour, Color.white, 0.55f);
            if (_slider != null) _slider.interactable = _interactable && _ceiling > 0 && RoomBotChoice.CanCreate(_capacity);

            if (_valueText != null)
            {
                string number = bots <= 0 ? "0" : bots.ToString();
                _valueText.text = $"<size=46><b>{number}</b></size>  {(bots == 1 ? "BOT" : "BOTS")}";
                _valueText.color = tierColour;
            }

            if (_perSideText != null) _perSideText.text = RoomBotChoice.PerSide(bots);
            if (_tierText != null) _tierText.text = RoomBotChoice.TierName(tier);
            if (_tierChip != null) _tierChip.color = new Color(tierColour.r, tierColour.g, tierColour.b, 0.22f);
            if (_tierText != null) _tierText.color = tierColour;

            DrawCeiling();
            DrawNotches();
            DrawPresets();
        }

        private void DrawCeiling()
        {
            float cut = (float)_ceiling / ProtocolConstants.MAX_BOTS;
            bool limited = _capacity != null && _ceiling < ProtocolConstants.MAX_BOTS;

            if (_capZone != null)
            {
                _capZone.gameObject.SetActive(limited);
                _capZone.anchorMin = new Vector2(cut, 0f);
                _capZone.anchorMax = new Vector2(1f, 1f);
                _capZone.offsetMin = Vector2.zero;
                _capZone.offsetMax = Vector2.zero;
            }

            if (_capMarker != null)
            {
                _capMarker.gameObject.SetActive(limited);
                _capMarker.anchorMin = new Vector2(cut, 0.5f);
                _capMarker.anchorMax = new Vector2(cut, 0.5f);
                _capMarker.anchoredPosition = Vector2.zero;
            }

            if (_ceilingText != null)
            {
                _ceilingText.text = RoomBotChoice.CeilingText(_capacity);
                _ceilingText.color = _capacity == null ? CeilingUnknown
                    : !RoomBotChoice.CanCreate(_capacity) || _ceiling == 0 ? CeilingNone
                    : limited ? CeilingLimited
                    : CeilingOk;
            }
        }

        private void DrawNotches()
        {
            for (int i = 0; i < _tickLabels.Length; i++)
            {
                int at = i * RoomBotChoice.TickEvery;
                bool reachable = at <= _ceiling;
                if (_tickLabels[i] != null) _tickLabels[i].color = reachable ? TickInk : TickOff;
                if (i < _tickMarks.Length && _tickMarks[i] != null)
                    _tickMarks[i].color = reachable ? TickInk : TickOff;
            }
        }

        private void DrawPresets()
        {
            int bots = Value;
            for (int i = 0; i < _presetButtons.Length && i < RoomBotChoice.Presets.Length; i++)
            {
                Button button = _presetButtons[i];
                if (button == null) continue;

                int preset = RoomBotChoice.Presets[i];
                bool reachable = preset <= _ceiling && RoomBotChoice.CanCreate(_capacity);
                button.interactable = _interactable && reachable;

                // The Button tints only its face; a caption left white reads as pressable.
                Text? caption = button.GetComponentInChildren<Text>(true);
                if (caption != null) caption.color = reachable ? Color.white : TickOff;

                // The chosen preset reads as pressed: its face takes the tier's colour.
                Image? face = button.targetGraphic as Image;
                if (face != null)
                {
                    Color colour = Hex(RoomBotChoice.TierHex(RoomBotChoice.TierOf(preset)));
                    face.color = preset == bots
                        ? new Color(colour.r, colour.g, colour.b, 0.55f)
                        : new Color(0.04f, 0.13f, 0.20f, 0.95f);
                }
            }
        }

        private static Color Hex(string rgb)
            => ColorUtility.TryParseHtmlString("#" + rgb, out Color colour) ? colour : Color.white;
    }
}
