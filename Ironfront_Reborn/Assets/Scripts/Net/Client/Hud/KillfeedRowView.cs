using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// One killfeed row: its parts, and how it arrives, moves down and leaves. Playtest
    /// 2026-09-28, feature 2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Authored by <c>BuildMatchHud</c>, animated here.</b> The builder lays the row out once
    /// -- a dark rounded backing sized to its content, a team-coloured accent, the killer, a chip
    /// saying how, a headshot mark, the victim -- and this component only fills it and moves it.
    /// A death nobody scored hides the killer, chip and mark and shows the sentence instead:
    /// "Minh drowned" where the feed used to say "The world → Minh".
    /// </para>
    /// <para>
    /// <b>Keyed on the kill, not the slot.</b> <see cref="MatchHud"/> hands the same kill back to
    /// the same row as newer kills push it down, so a row slides in once, glides to each new slot
    /// and fades out when the model drops it -- instead of five rows re-typing themselves on
    /// every kill.
    /// </para>
    /// <para>
    /// <b>Unscaled time.</b> The feed keeps moving while the game is paused or slowed, because
    /// it reports what already happened rather than taking part in it.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class KillfeedRowView : MonoBehaviour
    {
        /// <summary>A row's height at the 1920x1080 reference resolution.</summary>
        public const float RowHeight = 36f;

        /// <summary>From one row's top to the next one's.</summary>
        public const float RowPitch = RowHeight + 6f;

        private const float SlideDistance = 64f;
        private const float ArriveSeconds = 0.24f;
        private const float LeaveSeconds = 0.42f;
        private const float FlashSeconds = 0.7f;

        /// <summary>Per second: how fast a pushed-down row closes on its new slot.</summary>
        private const float SettleRate = 14f;

        /// <summary>The backing at rest: dark enough to read over snow and sky alike.</summary>
        public static readonly Color RestBacking = new Color(0.05f, 0.06f, 0.08f, 0.78f);

        /// <summary>The chip's backing, a lighter pane on the row's own.</summary>
        public static readonly Color HowBacking = new Color(1f, 1f, 1f, 0.12f);

        public static readonly Color HowInk = new Color(0.9f, 0.92f, 0.95f);
        public static readonly Color SentenceInk = new Color(0.8f, 0.82f, 0.86f);
        public static readonly Color HeadshotInk = new Color(1f, 0.36f, 0.28f);

        /// <summary>Your kill: a warm backing and a gold edge.</summary>
        private static readonly Color LocalKillBacking = new Color(0.24f, 0.19f, 0.05f, 0.9f);
        private static readonly Color LocalKillEdge = new Color(1f, 0.78f, 0.25f, 0.95f);

        /// <summary>Your death: a dark red backing and a red edge.</summary>
        private static readonly Color LocalDeathBacking = new Color(0.28f, 0.06f, 0.06f, 0.9f);
        private static readonly Color LocalDeathEdge = new Color(0.96f, 0.32f, 0.26f, 0.95f);

        /// <summary>Shown in the chip when the server named no weapon (a 1.0 server).</summary>
        private const string NoLabel = "›";

        [SerializeField] private Image _backing;
        [SerializeField] private Outline _edge;
        [SerializeField] private Image _accent;
        [SerializeField] private Text _killer;
        [SerializeField] private GameObject _how;
        [SerializeField] private Text _howText;
        [SerializeField] private Image _headshot;
        [SerializeField] private Text _victim;
        [SerializeField] private Text _sentence;

        private RectTransform _rect;
        private CanvasGroup _group;

        private float _y;
        private float _slotY;
        private float _age;
        private float _leaveAge = -1f;
        private Color _restBacking = RestBacking;
        private bool _flashSettled = true;

        private static Sprite _headshotSprite;

        /// <summary>The kill this row shows, 0 when it shows none.</summary>
        public long Sequence { get; private set; }

        public bool IsFree => Sequence == 0;

        public bool IsLeaving => _leaveAge >= 0f;

        /// <summary>How visible the row is now, 0 to 1.</summary>
        public float Opacity => _group != null ? _group.alpha : 0f;

        private void Awake()
        {
            _rect = (RectTransform)transform;
            _group = GetComponent<CanvasGroup>();

            if (_backing == null || _edge == null || _accent == null || _killer == null
                || _how == null || _howText == null || _headshot == null || _victim == null
                || _sentence == null || _group == null)
            {
                // The builder authors all of these. A missing one is a stale prefab, and a row
                // drawing half a line would read as a wording bug rather than a wiring one.
                Debug.LogError(
                    "[hud] " + name + " is missing an authored part; run \"Ironfront/Net/Build "
                    + "in-match readout\" to rebuild the killfeed rows.", this);
                enabled = false;
                return;
            }

            _headshot.sprite = HeadshotSprite();
            _headshot.color = HeadshotInk;
        }

        /// <summary>
        /// Shows <paramref name="line"/> in <paramref name="slot"/>. A new kill slides in; the kill
        /// this row already shows only moves to its slot and takes any new names.
        /// </summary>
        public void Show(in KillfeedLine line, int slot, Color killerInk, Color victimInk)
        {
            _slotY = -slot * RowPitch;

            if (line.Sequence != Sequence)
            {
                Sequence = line.Sequence;
                _age = 0f;
                _y = _slotY;
                _flashSettled = false;

                // Rows are authored inactive, so the first activation is also what runs Awake.
                gameObject.SetActive(true);
            }

            // Awake found a part missing and said so; the row stays invisible rather than
            // drawing half a line.
            if (!enabled) return;

            _leaveAge = -1f;

            bool sentence = line.IsSentence;
            _killer.gameObject.SetActive(!sentence);
            _how.SetActive(!sentence);
            _headshot.gameObject.SetActive(!sentence && line.Headshot);
            _sentence.gameObject.SetActive(sentence);

            _killer.text = line.KillerName;
            _killer.color = killerInk;
            _howText.text = line.Label.Length > 0 ? line.Label : NoLabel;
            _victim.text = line.VictimName;
            _victim.color = victimInk;
            _sentence.text = line.Sentence;

            // The side that acted: the killer's for a kill, the victim's for a death nobody scored.
            _accent.color = sentence ? victimInk : killerInk;

            _restBacking = line.LocalIsKiller ? LocalKillBacking
                : line.LocalIsVictim ? LocalDeathBacking
                : RestBacking;

            _edge.enabled = line.LocalIsKiller || line.LocalIsVictim;
            _edge.effectColor = line.LocalIsKiller ? LocalKillEdge : LocalDeathEdge;

            if (_flashSettled) _backing.color = _restBacking;
        }

        /// <summary>Starts fading this row out. Its kill has left the model.</summary>
        public void Leave()
        {
            if (IsFree || IsLeaving) return;
            _leaveAge = 0f;
        }

        /// <summary>Frees the row at once, with no fade. For a HUD being reset.</summary>
        public void Release()
        {
            Sequence = 0;
            _leaveAge = -1f;
            if (_group != null) _group.alpha = 0f;
            gameObject.SetActive(false);
        }

        /// <summary>Advances the row's motion. Called once a frame by <see cref="MatchHud"/>.</summary>
        public void Tick(float deltaSeconds)
        {
            if (IsFree || !enabled) return;

            _age += deltaSeconds;
            _y = Mathf.Lerp(_y, _slotY, 1f - Mathf.Exp(-SettleRate * deltaSeconds));

            float arrive = EaseOut(_age / ArriveSeconds);
            float x = (1f - arrive) * SlideDistance;
            float alpha = arrive;

            if (IsLeaving)
            {
                _leaveAge += deltaSeconds;
                float leave = Mathf.Clamp01(_leaveAge / LeaveSeconds);

                if (leave >= 1f)
                {
                    Release();
                    return;
                }

                alpha *= 1f - leave;
                x += leave * leave * SlideDistance * 0.5f;
            }

            _rect.anchoredPosition = new Vector2(x, _y);
            _group.alpha = alpha;

            if (_flashSettled) return;

            // A short light on arrival, so a new line catches the eye without a sound.
            float flash = 1f - Mathf.Clamp01(_age / FlashSeconds);
            Color lit = Color.Lerp(_restBacking, new Color(1f, 1f, 1f, 0.92f), 0.32f);
            _backing.color = Color.Lerp(_restBacking, lit, flash * flash);
            _flashSettled = flash <= 0f;
        }

        private static float EaseOut(float t)
        {
            t = Mathf.Clamp01(t);
            float remaining = 1f - t;
            return 1f - remaining * remaining * remaining;
        }

        /// <summary>
        /// A crosshair drawn once in code: a ring, a centre dot and four ticks through the ring.
        /// </summary>
        /// <remarks>
        /// Drawn rather than imported because the project has no icon for it and a glyph cannot
        /// be trusted to exist in Roboto; a tinted white shape also takes the headshot colour
        /// from <see cref="HeadshotInk"/> instead of carrying one of its own.
        /// </remarks>
        private static Sprite HeadshotSprite()
        {
            if (_headshotSprite != null) return _headshotSprite;

            const int size = 48;
            const float ringRadius = 15f;
            const float ringHalfWidth = 2.2f;
            const float dotRadius = 4.5f;
            const float tickHalfWidth = 1.6f;
            const float tickInner = 9f;
            const float tickOuter = 22f;

            var pixels = new Color32[size * size];
            float centre = (size - 1) * 0.5f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x - centre;
                    float dy = y - centre;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);

                    float ring = Mathf.Clamp01(ringHalfWidth + 0.5f - Mathf.Abs(distance - ringRadius));
                    float dot = Mathf.Clamp01(dotRadius + 0.5f - distance);

                    bool inTickSpan = distance > tickInner && distance < tickOuter;
                    float tick = inTickSpan
                        ? Mathf.Max(
                            Mathf.Clamp01(tickHalfWidth + 0.5f - Mathf.Abs(dx)),
                            Mathf.Clamp01(tickHalfWidth + 0.5f - Mathf.Abs(dy)))
                        : 0f;

                    float alpha = Mathf.Max(ring, Mathf.Max(dot, tick));
                    pixels[y * size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                name = "Killfeed Headshot",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            texture.SetPixels32(pixels);
            texture.Apply(false, true);

            _headshotSprite = Sprite.Create(
                texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f);
            return _headshotSprite;
        }
    }
}
