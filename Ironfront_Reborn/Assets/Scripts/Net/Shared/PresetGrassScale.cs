#nullable enable

using System.Collections.Generic;

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// The options' grass sliders as fractions of the grass distance and density the graphics
    /// preset gives the terrain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> Every preset overrides the terrain's own grass distance and density
    /// (<c>terrainQualityOverrides</c> 255 since #428), and the sliders wrote the terrain's own
    /// values, so they did nothing at any preset (P33, 2026-10-05: zeroing the terrain's own
    /// distance changed 0 pixels). They now scale what the preset gives, so a slider moves the
    /// grass whether the terrain or the GPU (<c>InstancedDetailRenderer</c>) draws it.
    /// </para>
    /// <para>
    /// The preset's own values are taken the first time a quality level is scaled, before anything
    /// is written, and kept for the process: applying the options again, or coming back to a
    /// level scaled before, never compounds.
    /// </para>
    /// </remarks>
    public sealed class PresetGrassScale
    {
        private readonly Dictionary<int, (float Distance, float Density)> _presets = new Dictionary<int, (float, float)>();

        /// <summary>
        /// What <paramref name="level"/> should give the terrain, its current values being
        /// <paramref name="currentDistance"/> and <paramref name="currentDensity"/>.
        /// </summary>
        public (float Distance, float Density) Scale(int level, float currentDistance, float currentDensity,
                                                     float distanceFraction, float densityFraction)
        {
            if (!_presets.TryGetValue(level, out (float Distance, float Density) preset))
            {
                preset = (currentDistance, currentDensity);
                _presets[level] = preset;
            }
            return (preset.Distance * Fraction(distanceFraction), preset.Density * Fraction(densityFraction));
        }

        /// <summary>The preset's own values for <paramref name="level"/>, if it was ever scaled.</summary>
        public bool TryGetPreset(int level, out float distance, out float density)
        {
            bool known = _presets.TryGetValue(level, out (float Distance, float Density) preset);
            distance = preset.Distance;
            density = preset.Density;
            return known;
        }

        private static float Fraction(float value) => value > 1f ? 1f : value > 0f ? value : 0f;
    }
}
