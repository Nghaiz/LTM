#nullable enable
using Ironfront.Net.Protocol;

namespace Ironfront.Net.Unity.Client.Menu
{
    /// <summary>
    /// The practice screen's settings, read from what the player chose, or what is wrong with them
    /// in a sentence for the screen's error line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The room half is <see cref="RoomSettingsChoice"/>'s</b>: the same modes, rules, ranges
    /// and wording as the create-room form, so practice and multiplayer cannot disagree about what
    /// a lead of 200 or a Night Mode battery means.
    /// </para>
    /// <para>
    /// Engine-free so <c>Ironfront.Client.Flow.Tests</c> can compile it, as <see cref="RoomSettingsChoice"/> is.
    /// </para>
    /// </remarks>
    public static class PracticeChoice
    {
        /// <summary>The sides in the order the team dropdown lists them.</summary>
        public static readonly string[] TeamOptions = { "BLUE TEAM", "RED TEAM", "BLUE, ALONE VS ALL BOTS", "RED, ALONE VS ALL BOTS" };

        /// <summary>The vehicle options in the order the vehicles dropdown lists them.</summary>
        public static readonly string[] VehicleOptions = { "VEHICLES ON", "VEHICLES OFF" };

        /// <summary>The respawn field's placeholder: what the number is, and its range.</summary>
        public static string RespawnPlaceholder()
            => $"Respawn time ({PracticeSettings.MinRespawnSeconds}-{PracticeSettings.MaxRespawnSeconds})";

        /// <summary>
        /// Reads the screen. The points, battery and respawn fields read as their defaults when
        /// empty; <paramref name="vehicleOption"/> is the index into <see cref="VehicleOptions"/>.
        /// </summary>
        public static bool TryRead(
            GameMode mode, VictoryRule rule, string pointsText, string visionText, ushort mapId,
            int bots, int team, int vehicleOption, string respawnText,
            out PracticeSettings settings, out string error)
        {
            settings = PracticeSettings.Default;

            if (!RoomSettingsChoice.TryRead(mode, rule, pointsText, visionText, mapId,
                    out RoomSettings rules, out error))
            {
                return false;
            }

            if (bots < 0 || bots > ProtocolConstants.MAX_BOTS)
            {
                error = $"Bots must be between 0 and {ProtocolConstants.MAX_BOTS}.";
                return false;
            }

            string typed = respawnText.Trim();
            int respawn = PracticeSettings.DefaultRespawnSeconds;
            if (typed.Length > 0 && !int.TryParse(typed, out respawn))
            {
                error = "The respawn time must be a number of seconds.";
                return false;
            }

            if (respawn < PracticeSettings.MinRespawnSeconds || respawn > PracticeSettings.MaxRespawnSeconds)
            {
                error = $"The respawn time must be between {PracticeSettings.MinRespawnSeconds} and "
                        + $"{PracticeSettings.MaxRespawnSeconds} seconds.";
                return false;
            }

            settings = new PracticeSettings(
                rules, bots, team == 1 || team == 3 ? 1 : 0, vehicles: vehicleOption != 1, respawn, alone: team >= 2);
            return true;
        }

        /// <summary>
        /// The deployment line under the parameters: "LEAD BY 200  ·  50 BOTS  ·  BLUE  ·  RESPAWN 5 S".
        /// </summary>
        public static string Summary(in PracticeSettings settings)
        {
            RoomSettings rules = settings.Rules;
            string side = settings.PlayerTeam == 1 ? "RED" : "BLUE";
            string vehicles = settings.Vehicles ? string.Empty : "  ·  NO VEHICLES";
            string bots = settings.Alone
                ? "ALONE VS " + (settings.Team0Bots + settings.Team1Bots) + " BOTS"
                : RoomBotChoice.Readout(settings.Bots);
            return RoomSettingsChoice.Describe(in rules) + "  ·  " + bots
                   + "  ·  " + side + vehicles + "  ·  RESPAWN " + settings.RespawnSeconds + " S";
        }
    }
}
