namespace Ironfront.Net.Protocol
{
    /// <summary>How a room plays (protocol 14, phase P32).</summary>
    public enum GameMode : byte
    {
        /// <summary>Conquest by day: the match the game has always had.</summary>
        PointMatch = 0,

        /// <summary>Conquest at night, on every map: dark, night vision on a battery.</summary>
        Night = 1,
    }

    /// <summary>When a side has won (protocol 14, phase P32).</summary>
    public enum VictoryRule : byte
    {
        /// <summary>Lead the other side by the room's points: the rule the game has always had.</summary>
        Margin = 0,

        /// <summary>Be the first side to reach the room's points.</summary>
        Target = 1,
    }

    /// <summary>A room's game-mode settings as one value: what the master stores and forwards, and the server plays by.</summary>
    public readonly struct RoomSettings
    {
        public RoomSettings(GameMode mode, VictoryRule rule, ushort victoryPoints, byte nightVisionSeconds)
        {
            Mode = mode;
            Rule = rule;
            VictoryPoints = victoryPoints;
            NightVisionSeconds = nightVisionSeconds;
        }

        public GameMode Mode { get; }
        public VictoryRule Rule { get; }
        public ushort VictoryPoints { get; }
        public byte NightVisionSeconds { get; }

        /// <summary>Today's game: Point Match, lead by 200, no night vision.</summary>
        public static RoomSettings Default => new RoomSettings(GameMode.PointMatch, VictoryRule.Margin, RoomRules.DefaultMarginPoints, 0);

        /// <summary>
        /// The settings a v13 sender meant by leaving the fields out: every missing or zero field is
        /// its default (zero points is never a legal room, so it can only mean "not sent").
        /// </summary>
        public static RoomSettings FromWire(byte mode, byte rule, ushort victoryPoints, byte nightVisionSeconds)
        {
            var gameMode = (GameMode)mode;
            var victoryRule = (VictoryRule)rule;
            ushort points = victoryPoints == 0 ? RoomRules.DefaultPoints(victoryRule) : victoryPoints;
            byte vision = gameMode == GameMode.Night && nightVisionSeconds == 0 ? RoomRules.DefaultNightVisionSeconds : nightVisionSeconds;
            return new RoomSettings(gameMode, victoryRule, points, vision);
        }

        public override string ToString()
            => Mode + ", " + (Rule == VictoryRule.Target ? "first to " : "lead by ") + VictoryPoints
               + (Mode == GameMode.Night ? ", night vision " + NightVisionSeconds + " s" : string.Empty);
    }

    /// <summary>
    /// The settings a host chooses for a room, and the one place their ranges and defaults are
    /// written down (protocol 14, phase P32). The lobby form offers them, the master refuses
    /// anything outside them, and the game server plays by them.
    /// </summary>
    /// <remarks>
    /// Contract: <c>plans/phases/phase-p32-mode-contract.md</c>.
    /// </remarks>
    public static class RoomRules
    {
        public const ushort MinMarginPoints = 50;
        public const ushort MaxMarginPoints = 1000;
        public const ushort MarginPointsStep = 10;
        public const ushort DefaultMarginPoints = 200;

        public const ushort MinTargetPoints = 100;
        public const ushort MaxTargetPoints = 3000;
        public const ushort TargetPointsStep = 50;
        public const ushort DefaultTargetPoints = 500;

        /// <summary>Seconds of night vision a full battery holds.</summary>
        public const byte MinNightVisionSeconds = 10;
        public const byte MaxNightVisionSeconds = 180;
        public const byte NightVisionSecondsStep = 5;
        public const byte DefaultNightVisionSeconds = 45;

        /// <summary>The smallest number of points <paramref name="rule"/> may be played to.</summary>
        public static ushort MinPoints(VictoryRule rule) => rule == VictoryRule.Target ? MinTargetPoints : MinMarginPoints;

        public static ushort MaxPoints(VictoryRule rule) => rule == VictoryRule.Target ? MaxTargetPoints : MaxMarginPoints;

        public static ushort PointsStep(VictoryRule rule) => rule == VictoryRule.Target ? TargetPointsStep : MarginPointsStep;

        public static ushort DefaultPoints(VictoryRule rule) => rule == VictoryRule.Target ? DefaultTargetPoints : DefaultMarginPoints;

        /// <summary>Whether <paramref name="mode"/> may be played on <paramref name="mapId"/>.</summary>
        /// <remarks>
        /// Night Mode is on every map (owner's run of 2026-10-10: Island and Dustbowl get Forest
        /// Lake's night); 0 is no map at all. Each map's night is its
        /// <c>Resources/NightMode/&lt;scene&gt;</c> config, and an EditMode test fails a catalogue map
        /// without one, so the lobby cannot offer a night the map does not have.
        /// </remarks>
        public static bool ModeAllowedOn(GameMode mode, ushort mapId)
            => mode == GameMode.PointMatch || (mode == GameMode.Night && mapId != 0);

        /// <summary>
        /// Whether a room may be made with these settings: a known mode on a map it allows, a known
        /// rule with its points in range, and a night-vision battery in range at night and zero by day.
        /// </summary>
        public static bool AreValid(ushort mapId, in RoomSettings settings)
            => AreValid(mapId, settings.Mode, settings.Rule, settings.VictoryPoints, settings.NightVisionSeconds);

        public static bool AreValid(ushort mapId, GameMode mode, VictoryRule rule, ushort points, byte nightVisionSeconds)
        {
            if (mode != GameMode.PointMatch && mode != GameMode.Night) return false;
            if (rule != VictoryRule.Margin && rule != VictoryRule.Target) return false;
            if (!ModeAllowedOn(mode, mapId)) return false;
            if (points < MinPoints(rule) || points > MaxPoints(rule)) return false;
            return mode == GameMode.Night
                ? nightVisionSeconds >= MinNightVisionSeconds && nightVisionSeconds <= MaxNightVisionSeconds
                : nightVisionSeconds == 0;
        }
    }
}
