#nullable enable
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The create-room form's game-mode settings: the victory rule and the points it is played to,
    /// read from what the host typed, and how a room's settings are put into words (phase P32).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The ranges are <see cref="RoomRules"/>'s, the master's own: the form refuses what the
    /// master would refuse, in words the host can act on, instead of a round trip to error 2007.
    /// </para>
    /// <para>
    /// Engine-free so <c>Ironfront.Client.Flow.Tests</c> can compile it, as <see cref="RoomBotChoice"/> is.
    /// </para>
    /// </remarks>
    public static class RoomSettingsChoice
    {
        /// <summary>The rules in the order the form's rule dropdown lists them.</summary>
        public static readonly VictoryRule[] Rules = { VictoryRule.Margin, VictoryRule.Target };

        /// <summary>The modes in the order the form's mode dropdown lists them.</summary>
        public static readonly GameMode[] Modes = { GameMode.PointMatch, GameMode.Night };

        /// <summary>The rule dropdown's option for <paramref name="rule"/>.</summary>
        public static string RuleOption(VictoryRule rule)
            => rule == VictoryRule.Target ? "FIRST TO POINTS" : "LEAD BY POINTS";

        /// <summary>The mode dropdown's option for <paramref name="mode"/>.</summary>
        public static string ModeOption(GameMode mode)
            => mode == GameMode.Night ? "NIGHT MODE" : "POINT MATCH";

        /// <summary>The points field's placeholder: what the number means under the rule, and its range.</summary>
        public static string PointsPlaceholder(VictoryRule rule)
            => (rule == VictoryRule.Target ? "Points to reach" : "Points to lead by")
               + $" ({RoomRules.MinPoints(rule)}-{RoomRules.MaxPoints(rule)})";

        /// <summary>The night-vision field's placeholder: what the number is, and its range.</summary>
        public static string VisionPlaceholder()
            => $"Night vision battery, seconds ({RoomRules.MinNightVisionSeconds}-{RoomRules.MaxNightVisionSeconds})";

        /// <summary>
        /// What the points field should hold after the rule changes: the new rule's default when it
        /// is empty or still holds the other rule's default, and what the host typed otherwise.
        /// </summary>
        public static string PointsAfterRuleChange(string current, VictoryRule rule)
        {
            string text = current.Trim();
            VictoryRule other = rule == VictoryRule.Target ? VictoryRule.Margin : VictoryRule.Target;
            return text.Length == 0 || text == RoomRules.DefaultPoints(other).ToString()
                ? RoomRules.DefaultPoints(rule).ToString()
                : text;
        }

        /// <summary>
        /// Reads the form's settings, or says what is wrong in a sentence for the form's error line.
        /// An empty points field is the rule's default.
        /// </summary>
        public static bool TryRead(
            GameMode mode, VictoryRule rule, string pointsText, ushort mapId,
            out RoomSettings settings, out string error)
            => TryRead(mode, rule, pointsText, string.Empty, mapId, out settings, out error);

        /// <summary>
        /// <see cref="TryRead(GameMode, VictoryRule, string, ushort, out RoomSettings, out string)"/>
        /// with the night-vision battery the host typed (Night Mode only; empty is the default).
        /// </summary>
        public static bool TryRead(
            GameMode mode, VictoryRule rule, string pointsText, string visionText, ushort mapId,
            out RoomSettings settings, out string error)
        {
            settings = RoomSettings.Default;
            error = string.Empty;

            string text = pointsText.Trim();
            ushort min = RoomRules.MinPoints(rule), max = RoomRules.MaxPoints(rule);
            int points = RoomRules.DefaultPoints(rule);
            if (text.Length > 0 && !int.TryParse(text, out points))
            {
                error = "Points must be a number.";
                return false;
            }

            if (points < min || points > max)
            {
                error = rule == VictoryRule.Target
                    ? $"The points to reach must be between {min} and {max}."
                    : $"The lead to win by must be between {min} and {max} points.";
                return false;
            }

            if (!RoomRules.ModeAllowedOn(mode, mapId))
            {
                error = "Night Mode is played on Forest Lake only.";
                return false;
            }

            byte vision = 0;
            if (mode == GameMode.Night)
            {
                string typed = visionText.Trim();
                int seconds = RoomRules.DefaultNightVisionSeconds;
                if (typed.Length > 0 && !int.TryParse(typed, out seconds))
                {
                    error = "The night vision battery must be a number of seconds.";
                    return false;
                }
                if (seconds < RoomRules.MinNightVisionSeconds || seconds > RoomRules.MaxNightVisionSeconds)
                {
                    error = $"The night vision battery must be between {RoomRules.MinNightVisionSeconds} and {RoomRules.MaxNightVisionSeconds} seconds.";
                    return false;
                }
                vision = (byte)seconds;
            }

            settings = new RoomSettings(mode, rule, (ushort)points, vision);
            return true;
        }

        /// <summary>A room's rule as the lobby says it: "Lead by 200 points to win." / "First side to 500 points wins."</summary>
        public static string Sentence(in RoomSettings settings)
        {
            string rule = settings.Rule == VictoryRule.Target
                ? $"First side to {settings.VictoryPoints} points wins."
                : $"Lead by {settings.VictoryPoints} points to win.";
            return settings.Mode == GameMode.Night ? "Night Mode. " + rule : rule;
        }

        /// <summary>The rule alone, for a cell too narrow for the mode: "LEAD BY 200", "FIRST TO 500".</summary>
        public static string DescribeRule(in RoomSettings settings)
            => (settings.Rule == VictoryRule.Target ? "FIRST TO " : "LEAD BY ") + settings.VictoryPoints;

        /// <summary>A map's name with the room's mode beside it at night: "Forest Lake  ·  NIGHT".</summary>
        public static string MapTitle(string mapName, GameMode mode)
            => mode == GameMode.Night ? mapName + "  ·  NIGHT" : mapName;

        /// <summary>A room's rule in words for the browser, the lobby and the match: "LEAD BY 200", "FIRST TO 500".</summary>
        public static string Describe(in RoomSettings settings)
        {
            string rule = (settings.Rule == VictoryRule.Target ? "FIRST TO " : "LEAD BY ") + settings.VictoryPoints;
            return settings.Mode == GameMode.Night ? "NIGHT  ·  " + rule : rule;
        }
    }
}
