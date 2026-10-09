#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Ironfront.Net.Protocol.Achievements;
using Ironfront.Net.Unity.Client.Menu;
using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>
    /// One achievement up close (achievements v2, section 6.1): the big badge, the rule or the hint,
    /// how rare it is, when it was earned, the parts done, and who earned a Mythic first. Opened by
    /// a card on <see cref="AchievementsPage"/>; Esc or CLOSE puts it away.
    /// </summary>
    /// <remarks>Built in code like the page that owns it (the overlay's pages are code-built).</remarks>
    public sealed class AchievementDetailPanel
    {
        private const int MaxParts = 12;

        private RectTransform _root = null!;
        private AngularPanel _face = null!;
        private Image _glow = null!;
        private Image _badge = null!;
        private Text _title = null!;
        private Text _tags = null!;
        private Text _rule = null!;
        private Text _rarity = null!;
        private Text _earned = null!;
        private Text _first = null!;
        private Image _track = null!;
        private Image _fill = null!;
        private Text _progress = null!;
        private Text _partsHeading = null!;
        private readonly List<Text> _parts = new List<Text>(MaxParts);

        /// <summary>Whether it is on screen.</summary>
        public bool IsOpen => _root != null && _root.gameObject.activeSelf;

        /// <summary>Builds the panel over the page's list area, hidden.</summary>
        public static AchievementDetailPanel Build(RectTransform content, Vector2 topLeft, Vector2 size)
        {
            var panel = new AchievementDetailPanel();
            panel.Compose(content, topLeft, size);
            panel.Hide();
            return panel;
        }

        /// <summary>"GOLD · 50 PTS · ONLINE · NIGHT MODE · HIDDEN": what a card says under its name.</summary>
        public static string TagLine(Achievement achievement)
        {
            string line = AchievementBoard.TierName(achievement.Tier) + "  ·  " + achievement.Points + " PTS  ·  "
                          + (achievement.IsPractice ? "PRACTICE" : "ONLINE");
            if (achievement.IsNight) line += "  ·  NIGHT MODE";
            if (achievement.Hidden) line += "  ·  HIDDEN";
            return line;
        }

        /// <summary>The rarity label's colour: grey common, through blue and purple, to gold legendary.</summary>
        public static Color RarityColour(in AchievementEntry entry)
        {
            if (!entry.HasShare) return UiStyle.Faint;
            double share = entry.Share;
            return share < 0.01 ? UiStyle.Gold
                : share < 0.05 ? UiStyle.Hex("C77DFF")
                : share < 0.2 ? UiStyle.Cyan
                : share < 0.5 ? UiStyle.Green
                : UiStyle.Muted;
        }

        public void Hide()
        {
            if (_root != null) _root.gameObject.SetActive(false);
        }

        public void Show(in AchievementEntry entry)
        {
            Achievement achievement = entry.Achievement;
            Color metal = AchievementArt.TierColour(achievement.Tier);
            bool mythic = achievement.Tier == AchievementTier.Mythic;

            _face.color = mythic ? new Color(0.09f, 0.03f, 0.04f, 0.98f) : new Color(0.03f, 0.08f, 0.13f, 0.98f);
            _face.Configure(14f, AngularEdge.All, 2f, UiStyle.WithAlpha(metal, 0.85f));
            _glow.color = UiStyle.WithAlpha(metal, entry.Earned ? 0.45f : 0.15f);
            _badge.sprite = AchievementArt.Badge(achievement, entry.IsRevealed);
            _badge.color = entry.Earned || !entry.IsRevealed ? Color.white : AchievementArt.LockedTint;

            _title.text = achievement.Title;
            _title.color = entry.Earned ? metal : UiStyle.Ink;
            _tags.text = TagLine(achievement);
            _tags.color = UiStyle.WithAlpha(metal, 0.9f);
            _rule.text = AchievementBoard.DescriptionText(entry);
            _rule.fontStyle = entry.IsRevealed ? FontStyle.Normal : FontStyle.Italic;

            _rarity.text = entry.HasShare
                ? AchievementBoard.ShareText(entry) + " OF PLAYERS  ·  " + AchievementBoard.RarityText(entry)
                : "RARITY UNKNOWN  ·  SIGN IN TO SEE HOW RARE IT IS";
            _rarity.color = RarityColour(entry);
            _earned.text = entry.Earned ? AchievementBoard.EarnedText(entry) : "NOT YET EARNED";
            _earned.color = entry.Earned ? UiStyle.Green : UiStyle.Faint;
            string first = AchievementBoard.FirstText(entry);
            _first.text = first;
            _first.gameObject.SetActive(first.Length > 0);

            bool progress = entry.ShowsProgress;
            bool best = achievement.Progress == AchievementProgress.Best;
            _track.gameObject.SetActive(progress && !best);
            _progress.gameObject.SetActive(progress);
            if (progress)
            {
                _fill.rectTransform.sizeDelta = new Vector2(520f * AchievementBoard.ProgressFraction(entry), 0f);
                _progress.text = best
                    ? AchievementBoard.ProgressText(entry) + "  ·  TARGET " + achievement.Target.ToString("N0", CultureInfo.InvariantCulture)
                      + (achievement.Unit.Length > 0 ? " " + achievement.Unit.ToUpperInvariant() : string.Empty)
                    : AchievementBoard.ProgressText(entry);
            }

            bool parts = achievement.Parts.Count > 0 && entry.IsRevealed;
            _partsHeading.gameObject.SetActive(parts);
            for (int i = 0; i < _parts.Count; i++)
            {
                bool shown = parts && i < achievement.Parts.Count;
                _parts[i].gameObject.SetActive(shown);
                if (!shown) continue;
                AchievementPart part = achievement.Parts[i];
                bool done = entry.Earned || (entry.PartsDone & (1L << part.Bit)) != 0;
                _parts[i].text = (done ? "<color=#3BDB83>✓</color>  " : "<color=#6F8CA2>○</color>  ") + part.Label;
                _parts[i].color = done ? UiStyle.Ink : UiStyle.Muted;
            }

            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
        }

        private void Compose(RectTransform content, Vector2 topLeft, Vector2 size)
        {
            _root = Ui.Child(content, "Achievement Detail");
            Ui.TopLeft(_root, topLeft, size);

            // A catcher over the list, so a click outside the card closes it rather than opening another.
            Image catcher = Ui.Fill(_root, "Catcher", new Color(0.01f, 0.03f, 0.05f, 0.7f));
            Ui.Stretch(catcher.rectTransform);
            catcher.raycastTarget = true;
            Button outside = catcher.gameObject.AddComponent<Button>();
            outside.transition = Selectable.Transition.None;
            outside.onClick.AddListener(Hide);

            _face = Ui.Panel(_root, "Face", Color.white, 14f, Color.white, AngularEdge.All, 2f);
            _face.raycastTarget = true;
            var face = (RectTransform)_face.transform;
            Ui.TopLeft(face, new Vector2(150f, 10f), new Vector2(size.x - 300f, size.y - 20f));

            _glow = Ui.Fill(face, "Glow", Color.white);
            _glow.sprite = Ironfront.Net.Unity.Client.Hud.HudSprites.Glow();
            Ui.TopLeft(_glow.rectTransform, new Vector2(10f, 30f), new Vector2(300f, 300f));
            _badge = Ui.Art(face, "Badge", null, Color.white);
            Ui.TopLeft(_badge.rectTransform, new Vector2(50f, 70f), new Vector2(220f, 220f));

            float x = 320f;
            float w = size.x - 300f - x - 40f;
            _title = Ui.Label(face, "Title", string.Empty, 34, Ui.Weight.Black, UiStyle.Ink);
            Ui.TopLeft(_title.rectTransform, new Vector2(x, 26f), new Vector2(w, 44f));
            _tags = Ui.Label(face, "Tags", string.Empty, 13, Ui.Weight.Bold, UiStyle.Faint);
            Ui.TopLeft(_tags.rectTransform, new Vector2(x, 72f), new Vector2(w, 20f));
            _rule = Ui.Label(face, "Rule", string.Empty, 18, Ui.Weight.Regular, UiStyle.Ink, TextAnchor.UpperLeft);
            Ui.TopLeft(_rule.rectTransform, new Vector2(x, 104f), new Vector2(w, 64f));
            _rarity = Ui.Label(face, "Rarity", string.Empty, 13, Ui.Weight.Bold, UiStyle.Faint);
            Ui.TopLeft(_rarity.rectTransform, new Vector2(x, 176f), new Vector2(w, 20f));
            _earned = Ui.Label(face, "Earned", string.Empty, 13, Ui.Weight.Bold, UiStyle.Green);
            Ui.TopLeft(_earned.rectTransform, new Vector2(x, 198f), new Vector2(w, 20f));
            _first = Ui.Label(face, "First", string.Empty, 13, Ui.Weight.Bold, UiStyle.Gold);
            Ui.TopLeft(_first.rectTransform, new Vector2(x, 220f), new Vector2(w, 20f));

            _track = Ui.Fill(face, "Track", UiStyle.WithAlpha(UiStyle.Hairline, 0.3f));
            Ui.TopLeft(_track.rectTransform, new Vector2(x, 250f), new Vector2(520f, 8f));
            _fill = Ui.Fill(_track.transform, "Fill", UiStyle.CyanSoft);
            _fill.rectTransform.anchorMin = Vector2.zero;
            _fill.rectTransform.anchorMax = new Vector2(0f, 1f);
            _fill.rectTransform.pivot = new Vector2(0f, 0.5f);
            _fill.rectTransform.anchoredPosition = Vector2.zero;
            _progress = Ui.Label(face, "Progress", string.Empty, 14, Ui.Weight.Bold, UiStyle.Ink);
            Ui.TopLeft(_progress.rectTransform, new Vector2(x, 262f), new Vector2(w, 22f));

            _partsHeading = Ui.Label(face, "Parts Heading", "PARTS", 12, Ui.Weight.Bold, UiStyle.CyanSoft);
            Ui.TopLeft(_partsHeading.rectTransform, new Vector2(x, 292f), new Vector2(200f, 18f));
            for (int i = 0; i < MaxParts; i++)
            {
                Text part = Ui.Label(face, "Part " + i, string.Empty, 14, Ui.Weight.Regular, UiStyle.Ink);
                Ui.TopLeft(part.rectTransform, new Vector2(x + (i / 6) * 300f, 312f + (i % 6) * 19f), new Vector2(290f, 20f));
                part.supportRichText = true;
                _parts.Add(part);
            }

            Button close = Ui.Button(face, "Close", "CLOSE", UiStyle.Secondary, new Vector2(160f, 44f), "arrow-left");
            Ui.TopLeft((RectTransform)close.transform, new Vector2(size.x - 300f - 190f, size.y - 20f - 64f), new Vector2(160f, 44f));
            close.onClick.AddListener(Hide);
        }
    }
}
