using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// One player's row on the Tab scoreboard: rank, star, name, BOT tag, kills, deaths, ratio.
    /// Feature 2, 2026-09-29.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cloned from one authored template</b> by <see cref="ScoreboardTeamView"/>, which owns the
    /// layout: a row is told where it sits and how tall it is, because the column shrinks its rows
    /// to fit a full side rather than running off the screen.
    /// </para>
    /// <para>
    /// <b>Your row is drawn like your killfeed line</b> -- the same warm backing and gold edge --
    /// so the board and the feed point at you the same way.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ScoreboardRowView : MonoBehaviour
    {
        private const float AppearSeconds = 0.26f;
        private const float AppearSlide = 14f;
        private const float FlashSeconds = 1.1f;

        private static readonly Color ZebraEven = new Color(1f, 1f, 1f, 0.05f);
        private static readonly Color ZebraOdd = new Color(1f, 1f, 1f, 0.1f);
        private static readonly Color Scored = new Color(1f, 0.85f, 0.4f, 0.38f);
        private static readonly Color BotInk = new Color(0.86f, 0.88f, 0.92f);

        [SerializeField] private Image _backing;
        [SerializeField] private Outline _edge;
        [SerializeField] private Text _rank;
        [SerializeField] private Image _star;
        [SerializeField] private Text _name;
        [SerializeField] private Text _kills;
        [SerializeField] private Text _deaths;
        [SerializeField] private Text _ratio;

        private RectTransform _rect;
        private CanvasGroup _group;

        private Color _rest;
        private float _appearDelay;
        private float _age;
        private float _flash;

        /// <summary>Whether the template was authored whole. A clone of a broken one is not used.</summary>
        public bool IsComplete { get; private set; }

        private bool _initialized;

        private void Awake() => Initialize();

        /// <summary>
        /// Resolves the row's parts. <c>Awake</c> calls it, and so does the column right after
        /// cloning a row, because edit mode -- the capture tool -- runs no <c>Awake</c>.
        /// </summary>
        public void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            _rect = (RectTransform)transform;
            _group = GetComponent<CanvasGroup>();

            IsComplete = _backing != null && _edge != null && _rank != null && _star != null
                         && _name != null && _kills != null
                         && _deaths != null && _ratio != null && _group != null;

            if (!IsComplete)
            {
                Debug.LogError(
                    "[hud] " + name + " is missing an authored part; run \"Ironfront/Net/Build "
                    + "in-match readout\" to rebuild the scoreboard.", this);
                return;
            }

            _star.sprite = HudSprites.Star();
            _star.color = HudStyle.Gold;
        }

        /// <summary>Fills the row. <paramref name="scored"/> lights it: this player just scored.</summary>
        public void Bind(in ScoreboardRow row, int rank, bool mvp, bool scored)
        {
            _rank.text = rank.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _name.text = row.Name;
            _kills.text = row.Kills.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _deaths.text = row.Deaths.ToString(System.Globalization.CultureInfo.InvariantCulture);
            _ratio.text = row.Ratio;

            _star.gameObject.SetActive(mvp);

            _name.color = row.IsLocal ? HudStyle.GoldInk : row.IsBot ? BotInk : HudStyle.Ink;
            _kills.color = row.IsLocal ? HudStyle.GoldInk : HudStyle.Ink;
            _rank.color = row.IsLocal ? HudStyle.GoldInk : HudStyle.BoardFaint;

            _rest = row.IsLocal ? HudStyle.LocalKillBacking : rank % 2 == 0 ? ZebraEven : ZebraOdd;
            _edge.enabled = row.IsLocal;
            _edge.effectColor = HudStyle.Gold;

            if (scored) _flash = 1f;
            _backing.color = Color.Lerp(_rest, Scored, _flash);
        }

        /// <summary>
        /// Puts the row at <paramref name="top"/> in its column, <paramref name="height"/> tall,
        /// with text sized to match.
        /// </summary>
        public void Place(float top, float height, int fontSize)
        {
            _rect.anchoredPosition = new Vector2(0f, top);
            _rect.sizeDelta = new Vector2(_rect.sizeDelta.x, height);

            int small = Mathf.Max(10, fontSize - 3);
            _rank.fontSize = small;
            _name.fontSize = fontSize;
            _kills.fontSize = fontSize;
            _deaths.fontSize = fontSize;
            _ratio.fontSize = small + 1;

            float mark = Mathf.Min(height - 8f, 18f);
            ((RectTransform)_star.transform).sizeDelta = new Vector2(mark, mark);
        }

        /// <summary>Plays the row in after <paramref name="delay"/> seconds: the board's stagger.</summary>
        public void Appear(float delay)
        {
            _appearDelay = delay;
            _age = 0f;
            _group.alpha = 0f;
        }

        /// <summary>Advances the row's motion. Called by its column once a frame.</summary>
        public void Tick(float deltaSeconds)
        {
            _age += deltaSeconds;

            float t = HudStyle.EaseOut((_age - _appearDelay) / AppearSeconds);
            _group.alpha = t;

            Vector2 position = _rect.anchoredPosition;
            _rect.anchoredPosition = new Vector2((1f - t) * AppearSlide, position.y);

            if (_flash <= 0f) return;

            _flash = Mathf.Max(0f, _flash - deltaSeconds / FlashSeconds);
            _backing.color = Color.Lerp(_rest, Scored, _flash * _flash);
        }
    }
}
