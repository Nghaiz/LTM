using UnityEngine;
using UnityEngine.UI;

namespace Ironfront.Net.Unity.Client.Hud
{
    /// <summary>
    /// The heading over one group of a side's rows -- "PLAYERS · 2", "BOTS · 16" -- with its mark and
    /// a rule in the side's colour. Owner's report of 2026-09-30: players apart from bots.
    /// </summary>
    /// <remarks>Authored by <c>BuildMatchHud</c>; <see cref="ScoreboardTeamView"/> places and fills it.</remarks>
    [DisallowMultipleComponent]
    public sealed class ScoreboardSectionView : MonoBehaviour
    {
        [SerializeField] private Image _icon;
        [SerializeField] private Text _label;
        [SerializeField] private Image _rule;

        private RectTransform _rect;

        /// <summary>Whether the heading was authored whole.</summary>
        public bool IsComplete => _icon != null && _label != null && _rule != null;

        /// <summary>Shows the heading with <paramref name="icon"/>, <paramref name="label"/> and the side's colour.</summary>
        public void Show(Sprite icon, string label, Color team)
        {
            if (!IsComplete) return;
            if (!gameObject.activeSelf) gameObject.SetActive(true);

            Color ink = HudStyle.TeamInk(team);
            _icon.sprite = icon;
            _icon.color = ink;
            _label.text = label;
            _label.color = ink;
            _rule.color = new Color(ink.r, ink.g, ink.b, 0.35f);
        }

        /// <summary>Puts the heading at <paramref name="top"/> in its column.</summary>
        public void Place(float top, float height)
        {
            if (_rect == null) _rect = (RectTransform)transform;
            _rect.anchoredPosition = new Vector2(0f, top);
            _rect.sizeDelta = new Vector2(_rect.sizeDelta.x, height);
        }

        public void Hide()
        {
            if (gameObject.activeSelf) gameObject.SetActive(false);
        }
    }
}
