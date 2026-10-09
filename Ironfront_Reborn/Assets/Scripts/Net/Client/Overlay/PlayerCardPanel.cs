#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Ironfront.MasterClient;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Replication.Client;
using Ironfront.Net.Unity.Client.Hud;
using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// A player's card on the global ranking (achievements v2, section 6.2): their main numbers,
    /// their achievements by metal, and the three rarest this viewer may see, with COMPARE.
    /// Opened by a name on <see cref="RankingPage"/>; Esc or CLOSE puts it away.
    /// </summary>
    /// <remarks>Code-built, like the page that owns it. The master already kept the hidden rule.</remarks>
    public sealed class PlayerCardPanel
    {
        private const int RarestShown = 3;
        private const float FaceWidth = 960f;
        private const float FaceHeight = 580f;

        private static readonly (string Caption, string Icon)[] StatCells =
        {
            ("SCORE", "star"), ("KILLS", "crosshair"), ("DEATHS", "skull"),
            ("K/D", "ratio"), ("HEADSHOTS", "headshot"), ("BEST STREAK", "crown"),
            ("WINS", "trophy"), ("MATCHES", "flag"), ("WIN %", "percent"),
        };

        private readonly Text[] _stats = new Text[StatCells.Length];
        private readonly Text[] _tiers = new Text[AchievementTotals.TierCount];
        private readonly List<(Image Badge, Text Title, Text Rarity)> _rare = new List<(Image, Text, Text)>();

        private RectTransform _root = null!;
        private RectTransform _body = null!;
        private Text _name = null!;
        private Text _rank = null!;
        private Text _rankCaption = null!;
        private Text _count = null!;
        private Text _points = null!;
        private Text _hidden = null!;
        private Text _rareHeading = null!;
        private Text _message = null!;
        private Button _compare = null!;
        private int _playerId;
        private Action<int>? _onCompare;

        public bool IsOpen => _root != null && _root.gameObject.activeSelf;

        /// <summary>The player on the card, 0 when none.</summary>
        public int PlayerId => IsOpen ? _playerId : 0;

        /// <summary>Builds the card over <paramref name="content"/>, hidden.</summary>
        public static PlayerCardPanel Build(RectTransform content, Vector2 topLeft, Vector2 size, Action<int> compare)
        {
            var panel = new PlayerCardPanel { _onCompare = compare };
            panel.Compose(content, topLeft, size);
            panel.Hide();
            return panel;
        }

        public void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
        }

        /// <summary>The card while the master is asked, or after it could not answer.</summary>
        public void ShowMessage(int playerId, string name, string message)
        {
            _playerId = playerId;
            _name.text = name;
            _rank.text = string.Empty;
            _rankCaption.text = string.Empty;
            _body.gameObject.SetActive(false);
            _compare.gameObject.SetActive(false);
            _message.text = message;
            _message.gameObject.SetActive(true);
            Open();
        }

        /// <summary>The card for <paramref name="profile"/>; <paramref name="pairs"/> carry the viewer's rarity numbers.</summary>
        public void Show(PlayerProfile profile, IReadOnlyList<ComparePair> pairs, bool isViewer)
        {
            LeaderboardRow row = profile.Player ?? new LeaderboardRow();
            _playerId = row.PlayerId;
            _name.text = string.IsNullOrEmpty(row.Name) ? "#" + row.PlayerId.ToString(CultureInfo.InvariantCulture) : row.Name;
            _rankCaption.text = "GLOBAL RANK";
            _rank.text = row.Rank > 0 ? "#" + row.Rank.ToString("N0", CultureInfo.InvariantCulture) : "NOT RANKED";
            _rank.color = row.Rank >= 1 && row.Rank <= 3 ? HudStyle.MedalColour(row.Rank) : UiStyle.Gold;
            _rank.fontSize = row.Rank > 0 ? 34 : 20;

            int kills = Clamp(row.Kills);
            int deaths = Clamp(row.Deaths);
            float winRate = row.Matches > 0 ? row.Wins / (float)row.Matches : 0f;
            string[] values =
            {
                Number(row.Score), Number(row.Kills), Number(row.Deaths),
                ScoreboardWording.Ratio(kills, deaths), Number(row.Headshots), Number(row.BestStreak),
                Number(row.Wins), Number(row.Matches),
                row.Matches > 0 ? Mathf.RoundToInt(winRate * 100f).ToString(CultureInfo.InvariantCulture) + "%" : "-",
            };
            for (int i = 0; i < _stats.Length; i++) _stats[i].text = values[i];

            CompareTotals totals = AchievementCompare.TheirTotals(profile);
            _count.text = totals.Count.ToString(CultureInfo.InvariantCulture) + "<size=18><color=#8DA8BA> / "
                          + AchievementCatalog.All.Count.ToString(CultureInfo.InvariantCulture) + "</color></size>";
            _points.text = totals.Points.ToString("N0", CultureInfo.InvariantCulture) + "<color=#8DA8BA> / "
                           + AchievementCatalog.TotalPoints.ToString("N0", CultureInfo.InvariantCulture) + " POINTS</color>";
            for (int i = 0; i < _tiers.Length; i++) _tiers[i].text = totals.PerTier[i].ToString(CultureInfo.InvariantCulture);
            _hidden.text = "HIDDEN  " + totals.Hidden.ToString(CultureInfo.InvariantCulture);

            List<AchievementEntry> rarest = AchievementCompare.Rarest(pairs, RarestShown);
            _rareHeading.text = rarest.Count > 0 ? "RAREST ACHIEVEMENTS" : "NO ACHIEVEMENTS YET";
            for (int i = 0; i < _rare.Count; i++)
            {
                (Image badge, Text title, Text rarity) = _rare[i];
                bool shown = i < rarest.Count;
                badge.gameObject.SetActive(shown);
                title.gameObject.SetActive(shown);
                rarity.gameObject.SetActive(shown);
                if (!shown) continue;
                AchievementEntry entry = rarest[i];
                badge.sprite = AchievementArt.Badge(entry.Achievement, revealed: true);
                title.text = entry.Achievement.Title;
                title.color = AchievementArt.TierColour(entry.Achievement.Tier);
                rarity.text = entry.HasShare
                    ? AchievementBoard.ShareText(entry) + " OF PLAYERS  ·  " + AchievementBoard.RarityText(entry)
                    : AchievementBoard.TierName(entry.Achievement.Tier);
                rarity.color = AchievementDetailPanel.RarityColour(entry);
            }

            _body.gameObject.SetActive(true);
            _message.gameObject.SetActive(false);
            _compare.gameObject.SetActive(!isViewer);
            Open();
        }

        private void Open()
        {
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
        }

        private void Compose(RectTransform content, Vector2 topLeft, Vector2 size)
        {
            _root = Ui.Child(content, "Player Card");
            Ui.TopLeft(_root, topLeft, size);

            Image catcher = Ui.Fill(_root, "Catcher", new Color(0.01f, 0.03f, 0.05f, 0.72f));
            Ui.Stretch(catcher.rectTransform);
            catcher.raycastTarget = true;
            Button outside = catcher.gameObject.AddComponent<Button>();
            outside.transition = Selectable.Transition.None;
            outside.onClick.AddListener(Hide);

            AngularPanel panel = Ui.Panel(_root, "Face", new Color(0.03f, 0.08f, 0.13f, 0.98f), 14f,
                UiStyle.WithAlpha(UiStyle.Cyan, 0.7f), AngularEdge.All, 2f);
            panel.raycastTarget = true;
            var face = (RectTransform)panel.transform;
            Ui.TopLeft(face, new Vector2((size.x - FaceWidth) * 0.5f, Mathf.Max(0f, (size.y - FaceHeight) * 0.5f)),
                new Vector2(FaceWidth, FaceHeight));

            Image person = Ui.Icon(face, "Person", "person", UiStyle.CyanSoft);
            Ui.TopLeft(person.rectTransform, new Vector2(40f, 24f), new Vector2(16f, 16f));
            Text kicker = Ui.Label(face, "Kicker", "SOLDIER PROFILE", 12, Ui.Weight.Bold, UiStyle.CyanSoft);
            Ui.TopLeft(kicker.rectTransform, new Vector2(62f, 22f), new Vector2(300f, 20f));
            _name = Ui.Label(face, "Name", string.Empty, 36, Ui.Weight.Black, UiStyle.Ink);
            Ui.TopLeft(_name.rectTransform, new Vector2(40f, 44f), new Vector2(600f, 48f));
            _name.horizontalOverflow = HorizontalWrapMode.Overflow;
            _rankCaption = Ui.Label(face, "Rank Caption", string.Empty, 12, Ui.Weight.Bold, UiStyle.CyanSoft, TextAnchor.MiddleRight);
            Ui.TopLeft(_rankCaption.rectTransform, new Vector2(FaceWidth - 300f, 22f), new Vector2(260f, 20f));
            _rank = Ui.Label(face, "Rank", string.Empty, 34, Ui.Weight.Black, UiStyle.Gold, TextAnchor.MiddleRight);
            Ui.TopLeft(_rank.rectTransform, new Vector2(FaceWidth - 300f, 44f), new Vector2(260f, 48f));

            _body = Ui.Child(face, "Body");
            Ui.TopLeft(_body, Vector2.zero, new Vector2(FaceWidth, FaceHeight));

            Image rule = Ui.Fill(_body, "Rule", UiStyle.WithAlpha(UiStyle.Hairline, 0.5f));
            Ui.TopLeft(rule.rectTransform, new Vector2(40f, 106f), new Vector2(FaceWidth - 80f, 1f));
            for (int i = 0; i < StatCells.Length; i++)
            {
                (string caption, string icon) = StatCells[i];
                float x = 40f + (i % 3) * 296f;
                float y = 122f + (i / 3) * 62f;
                Image glyph = Ui.Icon(_body, caption + " Icon", icon, UiStyle.Muted);
                Ui.TopLeft(glyph.rectTransform, new Vector2(x, y + 3f), new Vector2(14f, 14f));
                Text head = Ui.Label(_body, caption + " Caption", caption, 12, Ui.Weight.Bold, UiStyle.Muted);
                Ui.TopLeft(head.rectTransform, new Vector2(x + 20f, y), new Vector2(240f, 20f));
                _stats[i] = Ui.Label(_body, caption + " Value", string.Empty, 26, Ui.Weight.Black, UiStyle.Ink);
                Ui.TopLeft(_stats[i].rectTransform, new Vector2(x, y + 20f), new Vector2(260f, 34f));
            }

            Image rule2 = Ui.Fill(_body, "Rule 2", UiStyle.WithAlpha(UiStyle.Hairline, 0.5f));
            Ui.TopLeft(rule2.rectTransform, new Vector2(40f, 316f), new Vector2(FaceWidth - 80f, 1f));
            Image trophy = Ui.Icon(_body, "Trophy", "trophy", UiStyle.Amber);
            Ui.TopLeft(trophy.rectTransform, new Vector2(40f, 336f), new Vector2(34f, 34f));
            _count = Ui.Label(_body, "Count", string.Empty, 32, Ui.Weight.Black, UiStyle.Amber);
            Ui.TopLeft(_count.rectTransform, new Vector2(84f, 326f), new Vector2(200f, 40f));
            _count.horizontalOverflow = HorizontalWrapMode.Overflow;
            _points = Ui.Label(_body, "Points", string.Empty, 14, Ui.Weight.Bold, UiStyle.Ink);
            Ui.TopLeft(_points.rectTransform, new Vector2(86f, 364f), new Vector2(300f, 20f));

            AchievementTier[] metals =
            {
                AchievementTier.Bronze, AchievementTier.Silver, AchievementTier.Gold, AchievementTier.Platinum, AchievementTier.Mythic,
            };
            for (int i = 0; i < metals.Length; i++)
            {
                Color metal = AchievementArt.TierColour(metals[i]);
                float x = 380f + i * 86f;
                Image medal = Ui.Icon(_body, "Medal " + metals[i], "medal", metal);
                Ui.TopLeft(medal.rectTransform, new Vector2(x, 340f), new Vector2(30f, 30f));
                _tiers[i] = Ui.Label(_body, "Tier " + metals[i], string.Empty, 22, Ui.Weight.Black, UiStyle.Ink);
                Ui.TopLeft(_tiers[i].rectTransform, new Vector2(x + 34f, 338f), new Vector2(50f, 34f));
            }
            Image eye = Ui.Icon(_body, "Hidden Icon", "eye", UiStyle.Muted);
            Ui.TopLeft(eye.rectTransform, new Vector2(820f, 346f), new Vector2(18f, 18f));
            _hidden = Ui.Label(_body, "Hidden", string.Empty, 13, Ui.Weight.Bold, UiStyle.Muted);
            Ui.TopLeft(_hidden.rectTransform, new Vector2(844f, 344f), new Vector2(100f, 22f));

            _rareHeading = Ui.Label(_body, "Rare Heading", string.Empty, 12, Ui.Weight.Bold, UiStyle.CyanSoft);
            Ui.TopLeft(_rareHeading.rectTransform, new Vector2(40f, 404f), new Vector2(400f, 20f));
            for (int i = 0; i < RarestShown; i++)
            {
                float x = 40f + i * 296f;
                Image badge = Ui.Art(_body, "Rare Badge " + i, null, Color.white);
                Ui.TopLeft(badge.rectTransform, new Vector2(x, 430f), new Vector2(56f, 56f));
                Text title = Ui.Label(_body, "Rare Title " + i, string.Empty, 16, Ui.Weight.Black, UiStyle.Ink);
                Ui.TopLeft(title.rectTransform, new Vector2(x + 66f, 434f), new Vector2(220f, 24f));
                title.horizontalOverflow = HorizontalWrapMode.Overflow;
                Text rarity = Ui.Label(_body, "Rare Share " + i, string.Empty, 11, Ui.Weight.Bold, UiStyle.Faint);
                Ui.TopLeft(rarity.rectTransform, new Vector2(x + 66f, 460f), new Vector2(220f, 18f));
                _rare.Add((badge, title, rarity));
            }

            _message = Ui.Label(face, "Message", string.Empty, 18, Ui.Weight.Bold, UiStyle.Muted, TextAnchor.MiddleCenter);
            Ui.TopLeft(_message.rectTransform, new Vector2(40f, 200f), new Vector2(FaceWidth - 80f, 120f));

            _compare = Ui.Button(face, "Compare", "COMPARE ACHIEVEMENTS", UiStyle.Primary, new Vector2(320f, 48f), "people");
            Ui.TopLeft((RectTransform)_compare.transform, new Vector2(FaceWidth - 550f, FaceHeight - 72f), new Vector2(320f, 48f));
            _compare.onClick.AddListener(() => _onCompare?.Invoke(_playerId));
            Button close = Ui.Button(face, "Close", "CLOSE", UiStyle.Secondary, new Vector2(180f, 48f), "arrow-left");
            Ui.TopLeft((RectTransform)close.transform, new Vector2(FaceWidth - 220f, FaceHeight - 72f), new Vector2(180f, 48f));
            close.onClick.AddListener(Hide);
        }

        private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

        private static int Clamp(long value) => value > int.MaxValue ? int.MaxValue : value < 0 ? 0 : (int)value;
    }
}
