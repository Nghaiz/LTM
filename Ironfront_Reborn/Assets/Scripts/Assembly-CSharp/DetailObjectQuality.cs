using UnityEngine;

public class DetailObjectQuality : MonoBehaviour
{
	public static DetailObjectQuality instance;

	private float maxDensity;

	private float maxDistance;

	private void Awake()
	{
		instance = this;
		Terrain component = GetComponent<Terrain>();
		maxDensity = component.detailObjectDensity;
		maxDistance = component.detailObjectDistance;
		ApplyQuality();
	}

	public void ApplyQuality()
	{
		Terrain component = GetComponent<Terrain>();
		float num = Mathf.Clamp01(OptionsUi.GetOptions().vegetationDensity);
		// Trees are cover and are drawn at every density; a density of 0 means no grass, not a
		// bare map. drawTreesAndFoliage switched both off, and is what left integrated graphics
		// on Low without a single tree in v3.2.0 (Ironfront.Net.Unity.VegetationRules).
		component.drawTreesAndFoliage = true;
		component.detailObjectDistance = OptionsUi.GetOptions().vegetationDistance * maxDistance;
		component.detailObjectDensity = num * maxDensity;
		// One line per apply (a map load, an options save): a player's report of a bare map is
		// answered by their Player.log rather than by guessing at their preset and saved options.
		Debug.Log($"[vegetation] '{name}': preset {QualitySettings.names[QualitySettings.GetQualityLevel()]}, "
			+ $"grass density {component.detailObjectDensity:F2} of {maxDensity:F2}, "
			+ $"grass distance {component.detailObjectDistance:F0} m, trees and foliage drawn: {component.drawTreesAndFoliage}.");
	}
}
