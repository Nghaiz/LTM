using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// One name plate over one person's head: the holo frame the owner chose on 2026-09-29.
    /// Playtest 2026-09-28, feature 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The frame.</b> A pane of dark glass tinted with the side's colour and ruled with faint
    /// scanlines, four glowing corner brackets, and a pointer beneath it over the head. The name
    /// is drawn as the player wrote it (owner, 2026-09-29: not in capitals).
    /// </para>
    /// <para>
    /// <b>The icons, each one a fact.</b> A shield for a teammate or the hostile diamond for an
    /// enemy, so friend and foe differ in shape and not only in colour; a gold star when the
    /// player tops their side's board; the silhouette of the weapon in their hands, or a wheel
    /// when they sit in a vehicle and a wave when they swim; a medic cross before the bar, which
    /// pulses red below a quarter; a map pin before the metres.
    /// </para>
    /// <para>
    /// <b>It arrives, and it reacts.</b> A plate that appears draws its brackets in from outside
    /// the frame as it fades up, so a player coming into view reads as being picked up. A hit
    /// flashes the brackets white and leaves a white trail on the bar that holds a moment and
    /// then drains, so a burst shows how much it took; a heal simply grows the bar.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class NameplateView : MonoBehaviour
    {
        private const float FillRate = 18f;
        private const float TrailHoldSeconds = 0.35f;
        private const float TrailDrainPerSecond = 0.9f;
        private const float LowHealth = 0.25f;

        private const float AppearSeconds = 0.22f;
        private const float FlashSeconds = 0.2f;

        /// <summary>How far outside the frame the brackets start when a plate appears.</summary>
        private const float BracketTravel = 10f;

        /// <summary>How far the glass is pulled toward the side's colour.</summary>
        private const float GlassTint = 0.25f;

        /// <summary>How far the brackets, emblem and pointer are lifted toward white.</summary>
        private const float FrameLift = 0.2f;

        private const float ScanlineAlpha = 0.1f;

        /// <summary>The weapon silhouette's height and the widest a long rifle may draw.</summary>
        public const float WeaponHeight = 18f;
        public const float WeaponMaxWidth = 66f;

        private static readonly Color Trail = new Color(1f, 1f, 1f, 0.82f);

        /// <summary>"24 m", built once per distance ever shown rather than once per frame.</summary>
        private static readonly string[] MetreLabels = new string[1000];

        [SerializeField] private Image _glass;
        [SerializeField] private Image _scanlines;
        [SerializeField] private Image _bracketTopLeft;
        [SerializeField] private Image _bracketTopRight;
        [SerializeField] private Image _bracketBottomLeft;
        [SerializeField] private Image _bracketBottomRight;
        [SerializeField] private Image _emblem;
        [SerializeField] private Text _name;
        [SerializeField] private Image _star;
        [SerializeField] private Image _state;
        [SerializeField] private Image _weapon;
        [SerializeField] private LayoutElement _weaponSize;
        [SerializeField] private Image _medic;
        [SerializeField] private Image _trail;
        [SerializeField] private Image _fill;
        [SerializeField] private Image _pin;
        [SerializeField] private Text _distance;
        [SerializeField] private Image _pointer;

        private RectTransform _rect;
        private CanvasGroup _group;
        private bool _initialized;

        private float _health = -1f;
        private float _trailHealth;
        private float _trailHold;
        private float _time;
        private float _appear;
        private float _flash;

        private string _shownName;
        private int _shownMetres = -1;
        private int _shownWeapon = -1;
        private int _shownState = -1;
        private int _shownSide = -1;

        /// <summary>The actor this plate follows, 0 while it follows nobody.</summary>
        public ushort ActorId { get; private set; }

        public bool IsComplete { get; private set; }

        private void Awake() => Initialize();

        /// <summary>
        /// Resolves the plate's parts. <c>Awake</c> calls it, and so does the layer right after
        /// cloning a plate, because edit mode -- the capture tool -- runs no <c>Awake</c>.
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            _rect = (RectTransform)transform;
            _group = GetComponent<CanvasGroup>();

            IsComplete = _glass != null && _scanlines != null
                         && _bracketTopLeft != null && _bracketTopRight != null
                         && _bracketBottomLeft != null && _bracketBottomRight != null
                         && _emblem != null && _name != null && _star != null && _state != null
                         && _weapon != null && _weaponSize != null && _medic != null
                         && _trail != null && _fill != null && _pin != null && _distance != null
                         && _pointer != null && _group != null;

            if (!IsComplete)
            {
                Debug.LogError(
                    "[hud] " + name + " is missing an authored part; run \"Ironfront/Net/Build "
                    + "in-match readout\" to rebuild the name plates.", this);
                return;
            }

            // Drawn in code (HudSprites), so they are handed over here rather than authored.
            _scanlines.sprite = HudSprites.Scanlines();
            Sprite bracket = HudSprites.Bracket();
            _bracketTopLeft.sprite = bracket;
            _bracketTopRight.sprite = bracket;
            _bracketBottomLeft.sprite = bracket;
            _bracketBottomRight.sprite = bracket;
            _star.sprite = HudSprites.Star();
            _star.color = HudStyle.Gold;
            _medic.sprite = HudSprites.Medic();
            _pin.sprite = HudSprites.Pin();
            _pointer.sprite = HudSprites.Caret();
            _trail.color = Trail;
        }

        /// <summary>
        /// Draws <paramref name="plate"/> at <paramref name="local"/> on the layer and advances
        /// its animations by <paramref name="deltaSeconds"/>.
        /// </summary>
        public void Show(in Nameplate plate, Vector2 local, Color teamInk, float deltaSeconds)
        {
            if (!IsComplete) return;

            if (plate.ActorId != ActorId) Follow(in plate);

            _time += deltaSeconds;
            _appear = Mathf.Min(1f, _appear + deltaSeconds / AppearSeconds);
            float arrived = HudStyle.EaseOut(_appear);

            _rect.anchoredPosition = local;
            _rect.localScale = new Vector3(plate.Scale, plate.Scale, 1f);
            _group.alpha = plate.Opacity * arrived;

            if (!ReferenceEquals(_shownName, plate.Name))
            {
                _shownName = plate.Name;
                _name.text = plate.Name;
            }

            ShowSide(in plate, teamInk);
            ShowHands(in plate);
            ShowDistance(plate.Distance);

            if (TickHealth(plate.Health01, deltaSeconds)) _flash = 1f;
            else _flash = Mathf.MoveTowards(_flash, 0f, deltaSeconds / FlashSeconds);

            bool low = _health < LowHealth;
            float pulse = 0.55f + 0.45f * (0.5f + 0.5f * Mathf.Sin(_time * Mathf.PI * 4f));

            Color fill = teamInk;
            if (low) fill.a = pulse;
            _fill.color = fill;

            Color medic = low ? HudStyle.Blood : HudStyle.Ink;
            if (low) medic.a = pulse;
            _medic.color = medic;

            Color frame = Color.Lerp(teamInk, Color.white, FrameLift);
            PlaceBrackets(BracketTravel * (1f - arrived), Color.Lerp(frame, Color.white, _flash));
        }

        /// <summary>Takes the plate down and frees it for another actor.</summary>
        public void Hide()
        {
            ActorId = 0;
            gameObject.SetActive(false);
        }

        /// <summary>
        /// Starts following a new actor: its own health, no trail from whoever came before, and
        /// the arrival played again.
        /// </summary>
        private void Follow(in Nameplate plate)
        {
            ActorId = plate.ActorId;
            _health = plate.Health01;
            _trailHealth = _health;
            _trailHold = 0f;
            _appear = 0f;
            _flash = 0f;
            _shownName = null;
            _shownMetres = -1;
            _shownWeapon = -1;
            _shownState = -1;
            _shownSide = -1;
            gameObject.SetActive(true);
        }

        /// <summary>The side's colour through the frame, and the emblem that says friend or foe.</summary>
        private void ShowSide(in Nameplate plate, Color teamInk)
        {
            Color glass = Color.Lerp(HudStyle.PlateGlass, teamInk, GlassTint);
            glass.a = HudStyle.PlateGlass.a;
            _glass.color = glass;

            Color scan = teamInk;
            scan.a = ScanlineAlpha;
            _scanlines.color = scan;

            Color frame = Color.Lerp(teamInk, Color.white, FrameLift);
            _emblem.color = frame;
            _pointer.color = frame;

            int side = plate.IsTeammate ? 1 : 0;
            if (side != _shownSide)
            {
                _shownSide = side;
                _emblem.sprite = plate.IsTeammate ? HudSprites.Shield() : HudSprites.Hostile();
            }

            if (_star.gameObject.activeSelf != plate.IsLeader) _star.gameObject.SetActive(plate.IsLeader);
        }

        /// <summary>What the player has in hand: a weapon's silhouette, or where they are instead.</summary>
        private void ShowHands(in Nameplate plate)
        {
            int state = plate.IsSeated ? 1 : plate.IsInWater ? 2 : 0;
            if (state != _shownState)
            {
                _shownState = state;
                _state.sprite = state == 1 ? HudSprites.Wheel() : state == 2 ? HudSprites.Wave() : null;
                _state.gameObject.SetActive(state != 0);
            }

            // A seat hides the weapon: in a vehicle, the vehicle is what the player fights with.
            int weapon = plate.IsSeated ? 0 : plate.WeaponId;
            if (weapon == _shownWeapon) return;

            _shownWeapon = weapon;
            Sprite picture = weapon != 0 && NetClientBindings.WeaponIcon != null
                ? NetClientBindings.WeaponIcon((byte)weapon)
                : null;

            if (picture != null) HudStyle.FitPicture(_weapon, _weaponSize, picture, WeaponHeight, WeaponMaxWidth);
            _weapon.gameObject.SetActive(picture != null);
        }

        private void ShowDistance(float metres)
        {
            int rounded = Mathf.Clamp(Mathf.RoundToInt(metres), 0, MetreLabels.Length - 1);
            if (rounded == _shownMetres) return;

            _shownMetres = rounded;
            _distance.text = MetreLabels[rounded]
                             ?? (MetreLabels[rounded] = rounded.ToString(CultureInfo.InvariantCulture) + " m");
        }

        /// <summary>The corners, <paramref name="travel"/> outside the frame, in one colour.</summary>
        private void PlaceBrackets(float travel, Color colour)
        {
            PlaceBracket(_bracketTopLeft, -travel, travel, colour);
            PlaceBracket(_bracketTopRight, travel, travel, colour);
            PlaceBracket(_bracketBottomLeft, -travel, -travel, colour);
            PlaceBracket(_bracketBottomRight, travel, -travel, colour);
        }

        private static void PlaceBracket(Image bracket, float x, float y, Color colour)
        {
            bracket.rectTransform.anchoredPosition = new Vector2(x, y);
            bracket.color = colour;
        }

        /// <returns>Whether health fell this frame: a hit.</returns>
        private bool TickHealth(float target, float deltaSeconds)
        {
            bool hit = target < _health - 0.001f;
            if (hit)
            {
                // The trail keeps the length the bar had before the hit, for a moment.
                _trailHealth = Mathf.Max(_trailHealth, _health);
                _trailHold = TrailHoldSeconds;
            }

            _health = Mathf.Lerp(_health, target, 1f - Mathf.Exp(-FillRate * deltaSeconds));

            if (_trailHold > 0f) _trailHold -= deltaSeconds;
            else _trailHealth = Mathf.MoveTowards(_trailHealth, _health, TrailDrainPerSecond * deltaSeconds);

            // A heal grows the bar and leaves no trail behind it.
            if (_trailHealth < _health) _trailHealth = _health;

            SetShare(_fill.rectTransform, _health);
            SetShare(_trail.rectTransform, _trailHealth);
            return hit;
        }

        /// <summary>Stretches a bar part over the first <paramref name="share"/> of its track.</summary>
        private static void SetShare(RectTransform part, float share)
        {
            float x = Mathf.Clamp01(share);
            Vector2 max = part.anchorMax;
            if (Mathf.Abs(max.x - x) < 0.0005f) return;

            part.anchorMax = new Vector2(x, max.y);
        }
    }
}
