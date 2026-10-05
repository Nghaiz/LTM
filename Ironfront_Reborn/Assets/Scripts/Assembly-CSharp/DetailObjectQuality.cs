using Ironfront.Net.Unity;
using UnityEngine;

public class DetailObjectQuality : MonoBehaviour
{
	public static DetailObjectQuality instance;

	// The presets' own grass values, kept for the process so applying the options again never
	// scales an already scaled value (Ironfront.Net.Unity.PresetGrassScale).
	private static readonly PresetGrassScale presetGrass = new PresetGrassScale();

	private float maxDensity;

	private float maxDistance;

	private int scaledLevel = -1;

	private void Awake()
	{
		instance = this;
		Terrain component = GetComponent<Terrain>();
		// The terrain's own values, for a terrain no preset overrides: while a preset is in force
		// the getters answer with the preset's values instead.
		bool ignoresPreset = component.ignoreQualitySettings;
		component.ignoreQualitySettings = true;
		maxDensity = component.detailObjectDensity;
		maxDistance = component.detailObjectDistance;
		component.ignoreQualitySettings = ignoresPreset;
		ApplyQuality();
	}

	private void Update()
	{
		// A preset picked mid-match brings its own grass values, unscaled until applied.
		if (QualitySettings.GetQualityLevel() != scaledLevel)
		{
			ApplyQuality();
		}
	}

	private void OnDestroy()
	{
		// A quality setting written in the Editor's Play mode outlives it: give the preset its own back.
		if (QualitySettings.GetQualityLevel() == scaledLevel && presetGrass.TryGetPreset(scaledLevel, out float distance, out float density))
		{
			QualitySettings.terrainDetailDistance = distance;
			QualitySettings.terrainDetailDensityScale = density;
		}
	}

	public void ApplyQuality()
	{
		Terrain component = GetComponent<Terrain>();
		float density = Mathf.Clamp01(OptionsUi.GetOptions().vegetationDensity);
		float distance = Mathf.Clamp01(OptionsUi.GetOptions().vegetationDistance);
		// Trees are cover and are drawn at every density; a density of 0 means no grass, not a
		// bare map. drawTreesAndFoliage switched both off, and is what left integrated graphics
		// on Low without a single tree in v3.2.0 (Ironfront.Net.Unity.VegetationRules).
		component.drawTreesAndFoliage = true;
		// The sliders scale what decides the grass: the preset's values, which override the
		// terrain's own on every preset, and the terrain's own for a terrain no preset overrides.
		component.detailObjectDistance = distance * maxDistance;
		component.detailObjectDensity = density * maxDensity;
		scaledLevel = QualitySettings.GetQualityLevel();
		(float Distance, float Density) scaled = presetGrass.Scale(scaledLevel, QualitySettings.terrainDetailDistance,
			QualitySettings.terrainDetailDensityScale, distance, density);
		QualitySettings.terrainDetailDistance = scaled.Distance;
		QualitySettings.terrainDetailDensityScale = scaled.Density;
		// One line per apply (a map load, an options save, a preset change): a player's report of
		// a bare map is answered by their Player.log rather than by guessing at their preset and
		// saved options.
		Debug.Log($"[vegetation] '{name}': preset {QualitySettings.names[scaledLevel]}, "
			+ $"grass density {scaled.Density:F2} ({density:P0} of the preset's), "
			+ $"grass distance {scaled.Distance:F0} m ({distance:P0} of the preset's), trees and foliage drawn: {component.drawTreesAndFoliage}.");
	}
}
