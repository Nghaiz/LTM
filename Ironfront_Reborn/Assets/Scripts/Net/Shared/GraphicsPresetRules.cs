#nullable enable

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// Which graphics preset a machine starts on, and how a preset saved under the old six Unity
    /// levels carries over to the four this game ships.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Owner report 2026-10-01 (v3.1.1): "the client is badly optimised; Forest Lake stutters on
    /// weaker machines".</b> Every player started on Unity's "Fantastic" level -- the Standalone
    /// default, and what Reset chose -- which draws shadows 380 m out in four cascades, keeps every
    /// LOD at twice its authored distance and multisamples, over a forest of 12,412 trees. The game
    /// never applied a saved choice at start-up either, so a player who picked a lower level got
    /// Fantastic back on the next launch. The levels are now this game's own -- Low, Medium, High,
    /// Ultra (<c>ProjectSettings/QualitySettings.asset</c>) -- and a machine with no saved choice
    /// starts on the one its graphics card suits (<see cref="Recommended"/>).
    /// </para>
    /// <para>
    /// <b>Engine-free</b>, so <c>Ironfront.Client.Flow.Tests</c> compiles it; the
    /// <c>SystemInfo</c> reads that feed it are <c>GraphicsSettingsBoot</c>'s.
    /// </para>
    /// </remarks>
    public static class GraphicsPresetRules
    {
        public const int Low = 0;
        public const int Medium = 1;
        public const int High = 2;
        public const int Ultra = 3;

        /// <summary>How many presets the game ships.</summary>
        public const int LevelCount = 4;

        /// <summary>
        /// The settings layout a saved quality belongs to. Absent means the six Unity levels of
        /// v3.1.1 and before (<see cref="FromLegacyLevel"/>).
        /// </summary>
        public const int SettingsVersion = 2;

        /// <summary>Video memory, MB, under which a card starts on <see cref="Low"/>.</summary>
        public const int LowVideoMemoryMb = 3000;

        /// <summary>Video memory, MB, under which a card starts on <see cref="Medium"/>.</summary>
        public const int MediumVideoMemoryMb = 6500;

        /// <summary>Logical processors at or under which a machine starts no higher than <see cref="Medium"/>.</summary>
        public const int FewCores = 4;

        /// <summary>
        /// The preset a machine with no saved choice starts on. <see cref="Ultra"/> is never
        /// chosen for anybody: it is the player's to pick.
        /// </summary>
        /// <param name="videoMemoryMb"><c>SystemInfo.graphicsMemorySize</c>.</param>
        /// <param name="integratedGpu">Whether the card shares system memory (<see cref="IsIntegrated"/>).</param>
        /// <param name="logicalProcessors"><c>SystemInfo.processorCount</c>.</param>
        public static int Recommended(int videoMemoryMb, bool integratedGpu, int logicalProcessors)
        {
            if (integratedGpu || videoMemoryMb < LowVideoMemoryMb) return Low;

            int level = videoMemoryMb < MediumVideoMemoryMb ? Medium : High;
            if (logicalProcessors > 0 && logicalProcessors <= FewCores && level > Medium) level = Medium;
            return level;
        }

        /// <summary>
        /// Whether a graphics device is an integrated one, from its name: Intel's UHD, HD and Iris
        /// graphics and AMD's APU graphics. Intel Arc is a discrete card.
        /// </summary>
        public static bool IsIntegrated(string? deviceName)
        {
            if (string.IsNullOrEmpty(deviceName)) return false;

            string name = deviceName!.ToUpperInvariant();
            if (name.Contains("INTEL"))
                return !name.Contains("ARC");

            // AMD APUs report "AMD Radeon(TM) Graphics", "Radeon Vega 8 Graphics", "Radeon 780M".
            if (name.Contains("RADEON"))
                return name.Contains("(TM) GRAPHICS") || name.Contains("VEGA") || name.Contains("M GRAPHICS")
                       || name.Contains("RADEON GRAPHICS") || EndsWithApuModel(name);

            return false;
        }

        /// <summary>
        /// A preset saved under the six Unity levels (Fastest, Fast, Simple, Good, Beautiful,
        /// Fantastic) as one of this game's four. Fantastic, which nearly everybody had because it
        /// was the default, becomes <see cref="High"/>: the point of the change is that the default
        /// no longer costs what Fantastic did.
        /// </summary>
        public static int FromLegacyLevel(int legacyLevel)
        {
            if (legacyLevel <= 1) return Low;
            if (legacyLevel <= 3) return Medium;
            return High;
        }

        /// <summary>A saved or chosen level brought inside the shipped range.</summary>
        public static int Clamp(int level) => level < Low ? Low : level > Ultra ? Ultra : level;

        // "RADEON 780M", "RADEON 680M", "RADEON 760M": the APU parts, named by a model and an M.
        private static bool EndsWithApuModel(string name)
        {
            string trimmed = name.TrimEnd();
            if (trimmed.Length < 4 || trimmed[trimmed.Length - 1] != 'M') return false;

            int digits = 0;
            for (int i = trimmed.Length - 2; i >= 0 && char.IsDigit(trimmed[i]); i--) digits++;
            return digits == 3 && trimmed.Length - 2 - digits >= 0 && trimmed[trimmed.Length - 2 - digits] == ' ';
        }
    }
}
