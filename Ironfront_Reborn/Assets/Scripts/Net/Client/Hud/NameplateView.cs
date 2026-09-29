using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// One name and health bar over one head. Playtest 2026-09-28, feature 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The name sits on the bar</b>, as the owner asked, and both are drawn larger and thicker
    /// than a plate usually is: a 24 px name and a 14 px bar at close range, shrinking with
    /// distance to no less than 60 %.
    /// </para>
    /// <para>
    /// <b>A person and a bot read differently at a glance.</b> A person's name is large, in the
    /// side's colour, behind a diamond; a bot's is smaller and paler with the same BOT tag the
    /// scoreboard uses, so one look says whether that is somebody to talk to.
    /// </para>
    /// <para>
    /// <b>The bar remembers a hit.</b> When health drops, a white trail keeps the old length for
    /// a moment and then drains to the new one, so a burst shows how much it took; a heal simply
    /// grows. Below a quarter the bar pulses.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class NameplateView : MonoBehaviour
    {
        /// <summary>The bar's outer width at full size. The builder authors the track this wide.</summary>
        public const float TrackWidth = 120f;

        /// <summary>The bar's outer height at full size.</summary>
        public const float TrackHeight = 14f;

        /// <summary>The track's border, around the fill.</summary>
        public const float TrackInset = 2f;

        private const int PersonFontSize = 24;
        private const int BotFontSize = 19;

        private const float FillRate = 18f;
        private const float TrailHoldSeconds = 0.35f;
        private const float TrailDrainPerSecond = 0.9f;
        private const float LowHealth = 0.25f;

        private static readonly Color Trail = new Color(1f, 1f, 1f, 0.9f);

        [SerializeField] private Image _marker;
        [SerializeField] private Text _name;
        [SerializeField] private GameObject _bot;
        [SerializeField] private RectTransform _track;
        [SerializeField] private Image _trail;
        [SerializeField] private Image _fill;

        private RectTransform _rect;
        private CanvasGroup _group;
        private bool _initialized;

        private float _health = -1f;
        private float _trailHealth;
        private float _trailHold;
        private float _time;

        private string _shownName;
        private int _shownKind = -1;

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

            IsComplete = _marker != null && _name != null && _bot != null && _track != null
                         && _trail != null && _fill != null && _group != null;

            if (!IsComplete)
            {
                Debug.LogError(
                    "[hud] " + name + " is missing an authored part; run \"Ironfront/Net/Build "
                    + "in-match readout\" to rebuild the name plates.", this);
                return;
            }

            _marker.sprite = HudSprites.Diamond();
            _trail.color = Trail;
        }

        /// <summary>
        /// Draws <paramref name="plate"/> at <paramref name="local"/> on the layer and advances
        /// the bar by <paramref name="deltaSeconds"/>.
        /// </summary>
        public void Show(in Nameplate plate, Vector2 local, Color teamInk, float deltaSeconds)
        {
            if (!IsComplete) return;

            if (plate.ActorId != ActorId)
            {
                // A plate taken over by a new actor starts from that actor's own health, rather
                // than draining from whoever it followed last.
                ActorId = plate.ActorId;
                _health = plate.Health01;
                _trailHealth = _health;
                _trailHold = 0f;
                _shownName = null;
                _shownKind = -1;
                gameObject.SetActive(true);
            }

            _time += deltaSeconds;
            _rect.anchoredPosition = local;
            _rect.localScale = new Vector3(plate.Scale, plate.Scale, 1f);
            _group.alpha = plate.Opacity;

            if (!ReferenceEquals(_shownName, plate.Name))
            {
                _shownName = plate.Name;
                _name.text = plate.Name;
            }

            int kind = plate.IsBot ? 1 : 0;
            if (kind != _shownKind)
            {
                _shownKind = kind;
                _bot.SetActive(plate.IsBot);
                _marker.gameObject.SetActive(!plate.IsBot);
                _name.fontSize = plate.IsBot ? BotFontSize : PersonFontSize;
            }

            _name.color = plate.IsBot ? Color.Lerp(teamInk, HudStyle.Muted, 0.45f) : teamInk;
            _marker.color = teamInk;

            TickHealth(plate.Health01, deltaSeconds);

            Color fill = teamInk;
            if (_health < LowHealth) fill.a = 0.55f + 0.45f * (0.5f + 0.5f * Mathf.Sin(_time * Mathf.PI * 4f));
            _fill.color = fill;
        }

        /// <summary>Takes the plate down and frees it for another actor.</summary>
        public void Hide()
        {
            ActorId = 0;
            gameObject.SetActive(false);
        }

        private void TickHealth(float target, float deltaSeconds)
        {
            if (target < _health - 0.001f)
            {
                // A hit: the trail keeps the length the bar had before it, for a moment.
                _trailHealth = Mathf.Max(_trailHealth, _health);
                _trailHold = TrailHoldSeconds;
            }

            _health = Mathf.Lerp(_health, target, 1f - Mathf.Exp(-FillRate * deltaSeconds));

            if (_trailHold > 0f) _trailHold -= deltaSeconds;
            else _trailHealth = Mathf.MoveTowards(_trailHealth, _health, TrailDrainPerSecond * deltaSeconds);

            // A heal grows the bar and leaves no trail behind it.
            if (_trailHealth < _health) _trailHealth = _health;

            float inner = TrackWidth - 2f * TrackInset;
            SetWidth(_fill.rectTransform, inner * Mathf.Clamp01(_health));
            SetWidth(_trail.rectTransform, inner * Mathf.Clamp01(_trailHealth));
        }

        private static void SetWidth(RectTransform rect, float width)
            => rect.sizeDelta = new Vector2(width, rect.sizeDelta.y);
    }
}
