#nullable enable

using Ironfront.Net.Protocol.Achievements;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Overlay
{
    /// <summary>How an achievement is drawn wherever it appears: its badge and its metal's colour.</summary>
    public static class AchievementArt
    {
        /// <summary>The badge a hidden achievement shows until it is earned.</summary>
        public const string HiddenBadge = "_hidden";

        /// <summary>A badge not yet earned: the art, dimmed to a silhouette of itself.</summary>
        public static readonly Color LockedTint = new Color(0.48f, 0.53f, 0.6f, 0.9f);

        /// <summary>Bronze, silver, gold and platinum: the badges' rims, the toast's edge, the card's accent.</summary>
        public static Color TierColour(AchievementTier tier) => tier switch
        {
            AchievementTier.Silver => new Color(0.8f, 0.85f, 0.92f),
            AchievementTier.Gold => new Color(1f, 0.8f, 0.24f),
            AchievementTier.Platinum => new Color(0.56f, 0.9f, 1f),
            AchievementTier.Mythic => new Color(1f, 0.24f, 0.24f),
            _ => new Color(0.8f, 0.5f, 0.2f),
        };

        /// <summary>
        /// The badge to show: the achievement's own, or, for a hidden one not yet earned, the black
        /// silhouette of that same art (<c>&lt;id&gt;_shadow</c>), falling back to the generic seal.
        /// </summary>
        public static Sprite? Badge(Achievement achievement, bool revealed)
            => revealed
                ? UiSkin.Badge(achievement.Id)
                : UiSkin.Badge(achievement.Id + ShadowSuffix) ?? UiSkin.Badge(HiddenBadge);

        /// <summary>The suffix of a hidden achievement's silhouette badge.</summary>
        public const string ShadowSuffix = "_shadow";
    }
}
