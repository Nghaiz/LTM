#nullable enable

using Ironfront.Net.Configuration;
using Ironfront.Net.Protocol;
using Ironfront.Net.Protocol.Achievements;
using UnityEngine;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The words and colours of a room's chips, shared by the room browser and the waiting room
    /// (owner's run of 2026-10-12, item 4: icons, colours, badges and chips, and more about each
    /// room).
    /// </summary>
    /// <remarks>
    /// Everything here is computed from what the master already sends about a room, so the two
    /// screens cannot disagree about one room, and nothing is invented that the room does not say.
    /// </remarks>
    public static class RoomBadges
    {
        private static readonly AchievementTier[] TiersHighFirst =
        {
            AchievementTier.Mythic, AchievementTier.Platinum, AchievementTier.Gold,
            AchievementTier.Silver, AchievementTier.Bronze,
        };

        /// <summary>
        /// The largest achievement size a round in this room counts for: a Gold room counts for
        /// Bronze, Silver and Gold round achievements. Null below Bronze (LEAD BY under 100,
        /// FIRST TO under 250), where no round achievement counts.
        /// </summary>
        public static AchievementTier? MedalTier(in RoomSettings settings)
        {
            foreach (AchievementTier tier in TiersHighFirst)
            {
                if (CareerRules.IsRoundOf(tier, settings.Rule, settings.VictoryPoints)) return tier;
            }
            return null;
        }

        /// <summary>The medal chip's word: the tier, or NO MEDALS for a room too small for any.</summary>
        public static string MedalLabel(AchievementTier? tier)
            => tier.HasValue ? tier.Value.ToString().ToUpperInvariant() : "NO MEDALS";

        /// <summary>The tier's own colour, the one its badges wear on the achievements page.</summary>
        public static Color MedalColour(AchievementTier? tier) => tier switch
        {
            AchievementTier.Bronze => UiStyle.Hex("D7925A"),
            AchievementTier.Silver => UiStyle.Hex("C9D4DE"),
            AchievementTier.Gold => UiStyle.Gold,
            AchievementTier.Platinum => UiStyle.Hex("7FE3EC"),
            AchievementTier.Mythic => UiStyle.Hex("C99BFF"),
            _ => UiStyle.Faint,
        };

        /// <summary>The room plays Night Mode.</summary>
        public static bool IsNight(in RoomSettings settings) => settings.Mode == GameMode.Night;

        /// <summary>The mode chip's word, beside its sun or moon.</summary>
        public static string ModeLabel(in RoomSettings settings) => IsNight(settings) ? "NIGHT" : "DAY";

        /// <summary>The rule chip: LEAD BY 200, FIRST TO 500.</summary>
        public static string RuleLabel(in RoomSettings settings) => RoomSettingsChoice.DescribeRule(settings);

        /// <summary>The bots chip in a browser row: the room's total.</summary>
        public static string BotsLabel(int bots) => bots <= 0 ? "NO BOTS" : bots + " BOTS";

        /// <summary>The bots chip in the waiting room, where the split matters: 32 BOTS · 16/SIDE.</summary>
        public static string BotsPerSideLabel(int bots) => bots <= 0 ? "NO BOTS" : $"{bots} BOTS · {bots / 2}/SIDE";

        /// <summary>Players over seats: 3/8.</summary>
        public static string SeatsLabel(int players, int seats) => $"{players}/{seats}";

        /// <summary>How full the room is, 0 to 1, for the bar under the count.</summary>
        public static float SeatsFill(int players, int seats)
            => seats <= 0 ? 0f : Mathf.Clamp01(players / (float)seats);

        /// <summary>Green with room to spare, amber three quarters full, red full.</summary>
        public static Color SeatsColour(int players, int seats)
        {
            if (seats > 0 && players >= seats) return UiStyle.Red;
            return SeatsFill(players, seats) >= 0.75f ? UiStyle.Amber : UiStyle.Green;
        }

        /// <summary>
        /// The status chip of a listed room: its lifecycle, or FULL for a waiting room with no seat
        /// left, the one state a player looking for a game needs to read before anything else.
        /// </summary>
        public static string StatusLabel(RoomLifecycleState state, int players, int seats)
        {
            switch (state)
            {
                case RoomLifecycleState.Waiting: return seats > 0 && players >= seats ? "FULL" : "WAITING";
                case RoomLifecycleState.Starting: return "STARTING";
                case RoomLifecycleState.InMatch: return "IN MATCH";
                case RoomLifecycleState.Ending: return "ENDING";

                // A lifecycle byte from a newer master: shown as itself, a list row must render.
                default: return $"STATE {(byte)state}";
            }
        }

        /// <summary>The status chip's colour: green to join, amber starting, orange playing, red full.</summary>
        public static Color StatusColour(RoomLifecycleState state, int players, int seats)
        {
            switch (state)
            {
                case RoomLifecycleState.Waiting: return seats > 0 && players >= seats ? UiStyle.Red : UiStyle.Green;
                case RoomLifecycleState.Starting: return UiStyle.Amber;
                case RoomLifecycleState.InMatch:
                case RoomLifecycleState.Ending: return UiStyle.Orange;
                default: return UiStyle.Faint;
            }
        }

        /// <summary>
        /// The map's display name, as the create and practice screens show it; the id itself for a
        /// map this build cannot name, because a newer master can send one and the number is what
        /// makes it reportable.
        /// </summary>
        public static string MapName(ushort mapId)
        {
            for (int i = 0; i < MapCatalog.All.Count; i++)
            {
                if (MapCatalog.All[i].Id == mapId) return MapCatalog.All[i].DisplayName;
            }

            return $"map {mapId}";
        }
    }
}
