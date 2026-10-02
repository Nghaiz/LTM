#nullable enable

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// How much grass a preset starts with, and the rule that trees are never switched off.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists (owner report 2026-10-03).</b> A friend's old PC without a discrete card
    /// saw no trees and no grass on v3.2.0 at every preset from Low to Ultra, where v3.1.1 drew
    /// them. v3.2.0 started integrated graphics on Low (<see cref="GraphicsPresetRules.Recommended"/>),
    /// and the original game read Low's density from <c>"fast vegetation density"</c> with a
    /// default of <b>0</b>, which turned <c>Terrain.drawTreesAndFoliage</c> off: no trees, no
    /// grass. The options are cached for the process, so picking Ultra afterwards changed nothing
    /// until a restart.
    /// </para>
    /// <para>
    /// <b>Trees are cover, so they are drawn at every preset.</b> In a multiplayer match a player
    /// who cannot see a tree shoots at someone hidden behind it for everyone else. Density only
    /// thins the grass.
    /// </para>
    /// </remarks>
    public static class VegetationRules
    {
        /// <summary>Grass density on Medium and above when the player never set one.</summary>
        public const float DefaultDensity = 0.5f;

        /// <summary>Grass density on Low when the player never set one: thinner, never none.</summary>
        public const float LowPresetDefaultDensity = 0.3f;

        /// <summary>The density the starting <paramref name="qualityLevel"/> gets by default.</summary>
        public static float DefaultDensityFor(int qualityLevel)
            => qualityLevel <= GraphicsPresetRules.Low ? LowPresetDefaultDensity : DefaultDensity;
    }
}
