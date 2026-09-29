using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// One player's row on the Tab scoreboard. Feature 2, 2026-09-29; rebuilt for the owner's report
    /// of 2026-09-30: a rank medal, a player or bot mark, the name with its chips, whether the
    /// player is alive or in a vehicle, and K, D, K/D, headshots, streak, best, score and ping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Cloned from one authored template</b> by <see cref="ScoreboardTeamView"/>, which owns the
    /// layout: a row is told where it sits and how tall it is, because the column shrinks its rows
    /// to fit a full side rather than running off the screen.
    /// </para>
    /// <para>
    /// <b>Players and bots read differently at a glance.</b> A player's row is taller, tinted in
    /// the side's colour with an accent bar, and names them in bold white; a bot's is compact and
    /// quiet, marks it with a robot and a BOT chip, and names it by its callsign. Your row is drawn
    /// like your killfeed line -- the warm backing and gold edge.
    /// </para>
    /// <para>
    /// <b>Colour carries meaning, not decoration</b>: the top three wear gold, silver and bronze;
    /// K/D runs from red to green; a live streak burns orange; ping is green, amber or red; a dead
    /// player's row dims until they are back.
    /// </para>
    /// </remarks>
    [DisallowMultipleComponent]
    public sealed class ScoreboardRowView : MonoBehaviour
    {
        private const float AppearSeconds = 0.26f;
        private const float AppearSlide = 14f;
        private const float FlashSeconds = 1.1f;
        private const float DeadOpacity = 0.62f;

        private static readonly Color BotZebraEven = new Color(1f, 1f, 1f, 0.035f);
        private static readonly Color BotZebraOdd = new Color(1f, 1f, 1f, 0.07f);
        private static readonly Color Scored = new Color(1f, 0.85f, 0.4f, 0.38f);

        [SerializeField] private Image _backing;
        [SerializeField] private Outline _edge;
        [SerializeField] private Image _accent;
        [SerializeField] private Image _medal;
        [SerializeField] private Text _rank;
        [SerializeField] private Image _kind;
        [SerializeField] private Text _name;
        [SerializeField] private Image _star;
        [SerializeField] private GameObject _youChip;
        [SerializeField] private GameObject _botChip;
        [SerializeField] private Image _status;
        [SerializeField] private Text _kills;
        [SerializeField] private Text _deaths;
        [SerializeField] private Text _ratio;
        [SerializeField] private Text _headshots;
        [SerializeField] private Image _streakIcon;
        [SerializeField] private Text _streak;
        [SerializeField] private Text _best;
        [SerializeField] private Text _score;
        [SerializeField] private Image _pingIcon;
        [SerializeField] private Text _ping;

        private RectTransform _rect;
        private CanvasGroup _group;
        private RectTransform _streakRect;

        private Color _rest;
        private float _appearDelay;
        private float _age;
        private float _flash;
        private float _opacity = 1f;
        private bool _burning;

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

            IsComplete = _backing != null && _edge != null && _accent != null && _medal != null
                         && _rank != null && _kind != null && _name != null && _star != null
                         && _youChip != null && _botChip != null && _status != null
                         && _kills != null && _deaths != null && _ratio != null
                         && _headshots != null && _streakIcon != null && _streak != null
                         && _best != null && _score != null && _pingIcon != null && _ping != null
                         && _group != null;

            if (!IsComplete)
            {
                Debug.LogError(
                    "[hud] " + name + " is missing an authored part; run \"Ironfront/Net/Build "
                    + "in-match readout\" to rebuild the scoreboard.", this);
                return;
            }

            _streakRect = (RectTransform)_streakIcon.transform;
            _star.sprite = HudSprites.Star();
            _star.color = HudStyle.Gold;
            _medal.sprite = HudSprites.Medal();
            _streakIcon.sprite = HudSprites.ToneIcon(Ironfront.Net.Replication.Client.KillfeedTone.Streak);
            _streakIcon.color = HudStyle.StreakInk;
        }

        /// <summary>
        /// Fills the row. <paramref name="team"/> is the side's colour; <paramref name="stripe"/>
        /// alternates the quiet rows; <paramref name="scored"/> lights it: this player just scored.
        /// </summary>
        public void Bind(in ScoreboardRow row, Color team, int stripe, bool scored)
        {
            bool human = !row.IsBot;
            Color teamInk = HudStyle.TeamInk(team);

            _rank.text = row.Rank > 0 ? row.Rank.ToString(CultureInfo.InvariantCulture) : "–";
            BindMedal(row.Rank);

            _kind.sprite = human ? HudSprites.Person() : HudSprites.Bot();
            _kind.color = human ? teamInk : HudStyle.BotMark;

            _name.text = row.Name;
            _name.color = row.IsLocal ? HudStyle.GoldInk : human ? HudStyle.Ink : HudStyle.BotInk;
            _name.fontStyle = human ? FontStyle.Bold : FontStyle.Normal;

            _star.gameObject.SetActive(row.IsLeader);
            _youChip.SetActive(row.IsLocal);
            _botChip.SetActive(!human);

            BindStatus(in row, teamInk);

            _kills.text = Number(row.Kills);
            _kills.color = row.IsLocal ? HudStyle.GoldInk : HudStyle.Ink;
            _deaths.text = Number(row.Deaths);
            _ratio.text = row.Ratio;
            _ratio.color = HudStyle.RatioInk(row.RatioValue, row.Kills + row.Deaths > 0);

            BindStats(in row, teamInk);

            _rest = row.IsLocal ? HudStyle.LocalKillBacking
                : human ? new Color(team.r * 0.35f, team.g * 0.35f, team.b * 0.35f, 0.55f)
                : stripe % 2 == 0 ? BotZebraEven : BotZebraOdd;

            _accent.gameObject.SetActive(human);
            _accent.color = row.IsLocal ? HudStyle.Gold : teamInk;

            _edge.enabled = row.IsLocal;
            _edge.effectColor = HudStyle.Gold;

            _opacity = row.HasStats && !row.IsAlive ? DeadOpacity : 1f;

            if (scored) _flash = 1f;
            _backing.color = Color.Lerp(_rest, Scored, _flash);
        }

        /// <summary>Gold, silver and bronze for the side's top three; a plain number below them.</summary>
        private void BindMedal(int rank)
        {
            bool medal = rank >= 1 && rank <= 3;
            _medal.gameObject.SetActive(medal);
            if (medal) _medal.color = HudStyle.MedalColour(rank);
            _rank.color = medal ? HudStyle.MedalInk : HudStyle.BoardFaint;
            _rank.fontStyle = medal ? FontStyle.Bold : FontStyle.Normal;
        }

        /// <summary>A green dot for a live player, a red skull for a dead one, a wheel for one in a vehicle.</summary>
        private void BindStatus(in ScoreboardRow row, Color teamInk)
        {
            _status.gameObject.SetActive(row.HasStats);
            if (!row.HasStats) return;

            if (!row.IsAlive)
            {
                _status.sprite = HudSprites.Skull();
                _status.color = HudStyle.DeadInk;
            }
            else if (row.IsSeated)
            {
                _status.sprite = HudSprites.Wheel();
                _status.color = teamInk;
            }
            else
            {
                _status.sprite = HudSprites.Dot();
                _status.color = HudStyle.AliveInk;
            }
        }

        /// <summary>The server's numbers, or a dash for each when the server did not send them.</summary>
        private void BindStats(in ScoreboardRow row, Color teamInk)
        {
            if (!row.HasStats)
            {
                _headshots.text = _streak.text = _best.text = _score.text = _ping.text = "–";
                _headshots.color = _streak.color = _best.color = _score.color = _ping.color = HudStyle.BoardFaint;
                _streakIcon.gameObject.SetActive(false);
                _pingIcon.gameObject.SetActive(false);
                _burning = false;
                return;
            }

            _headshots.text = Number(row.Headshots);
            _headshots.color = row.Headshots > 0 ? HudStyle.HeadshotInk : HudStyle.BoardFaint;

            _burning = row.Streak >= 3;
            _streakIcon.gameObject.SetActive(_burning);
            _streak.text = Number(row.Streak);
            _streak.color = _burning ? HudStyle.StreakInk : row.Streak > 0 ? HudStyle.Ink : HudStyle.BoardFaint;

            _best.text = Number(row.BestStreak);
            _best.color = row.BestStreak >= 5 ? HudStyle.GoldInk : HudStyle.BoardMuted;

            _score.text = Number(row.Points);
            _score.color = row.Points > 0 ? teamInk : HudStyle.BoardFaint;

            bool human = !row.IsBot;
            _pingIcon.gameObject.SetActive(human && row.PingMs > 0);
            if (human && row.PingMs > 0)
            {
                int level = row.PingMs < 80 ? 3 : row.PingMs < 150 ? 2 : 1;
                _pingIcon.sprite = HudSprites.PingBars(level);
                _pingIcon.color = HudStyle.PingInk(row.PingMs);
                _ping.text = Number(row.PingMs);
                _ping.color = HudStyle.PingInk(row.PingMs);
            }
            else
            {
                // A bot has no connection; its row already says BOT beside its name.
                _ping.text = "–";
                _ping.color = HudStyle.BoardFaint;
            }
        }

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

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
            _headshots.fontSize = small + 1;
            _streak.fontSize = small + 1;
            _best.fontSize = small + 1;
            _score.fontSize = fontSize;
            _ping.fontSize = small;

            float mark = Mathf.Min(height - 8f, 18f);
            SetSquare(_star, mark);
            SetSquare(_kind, Mathf.Min(height - 6f, 20f));
            SetSquare(_medal, Mathf.Min(height - 4f, 26f));
            SetSquare(_status, Mathf.Min(height - 12f, 14f));
            SetSquare(_streakIcon, Mathf.Min(height - 10f, 16f));
            SetSquare(_pingIcon, Mathf.Min(height - 10f, 16f));
        }

        private static void SetSquare(Image image, float size)
            => ((RectTransform)image.transform).sizeDelta = new Vector2(size, size);

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
            _group.alpha = t * _opacity;

            Vector2 position = _rect.anchoredPosition;
            _rect.anchoredPosition = new Vector2((1f - t) * AppearSlide, position.y);

            // A live streak's flame flickers.
            if (_burning)
            {
                float flicker = 1f + 0.12f * Mathf.Sin(_age * 17f) * Mathf.Sin(_age * 5.3f);
                _streakRect.localScale = new Vector3(flicker, flicker, 1f);
            }

            if (_flash <= 0f) return;

            _flash = Mathf.Max(0f, _flash - deltaSeconds / FlashSeconds);
            _backing.color = Color.Lerp(_rest, Scored, _flash * _flash);
        }
    }
}
