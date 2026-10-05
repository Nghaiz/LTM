#nullable enable

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// How much grass a player starts with, and the rule that trees are never switched off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists (owner report 2026-10-03).</b> A friend's old PC without a discrete card
    /// saw no trees and no grass on v3.2.0 at every preset from Low to Ultra, where v3.1.1 drew
    /// them. v3.2.0 started integrated graphics on Low (<see cref="GraphicsPresetRules.Recommended"/>),
    /// and the original game read Low's density from <c>"fast vegetation density"</c> with a
    /// default of <b>0</b>, which turned <c>Terrain.drawTreesAndFoliage</c> off: no trees, no
    /// grass.
    /// </para>
    /// <para>
    /// <b>Trees are cover, so they are drawn at every preset.</b> In a multiplayer match a player
    /// who cannot see a tree shoots at someone hidden behind it for everyone else. Density only
    /// thins the grass.
    /// </para>
    /// <para>
    /// <b>The sliders start full.</b> They scale the preset's own grass (<see cref="PresetGrassScale"/>);
    /// Low is already thinner and shorter by its preset (0.35 density, 40 m). Until P33 they did
    /// nothing, and every player saw the preset's full grass, so a default below 1 would take grass
    /// away from players who never touched them. They are read from new keys for the same reason:
    /// the old keys hold values (0.5, 0.7, saved by any options save) that never applied.
    /// </para>
    /// </remarks>
    public static class VegetationRules
    {
        /// <summary>The grass density slider when the player never set one: the preset's own.</summary>
        public const float DefaultDensity = 1f;

        /// <summary>The grass distance slider when the player never set one: the preset's own.</summary>
        public const float DefaultDistance = 1f;

        /// <summary>Where the density slider is saved (the original's keys held values that never applied).</summary>
        public const string DensityKey = "grass density";

        /// <summary>Where the distance slider is saved.</summary>
        public const string DistanceKey = "grass distance";
    }
}
