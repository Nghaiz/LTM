using System;
using UnityEngine;

public class QualitySwitcher : MonoBehaviour
{
	public int hqLevel = 5;

	public GameObject hqObject;

	public GameObject lqObject;

	[NonSerialized]
	public GameObject activeObject;

	private void Awake()
	{
		// hqLevel is authored against the original's six levels (5 = Fantastic in every scene and
		// prefab); the game ships four since 2026-10-02, so it is read through the same mapping.
		bool flag = QualitySettings.GetQualityLevel() >= Ironfront.Net.Unity.GraphicsPresetRules.FromLegacyLevel(hqLevel);
		hqObject.SetActive(flag);
		if (lqObject != null)
		{
			lqObject.SetActive(!flag);
		}
		if (flag)
		{
			activeObject = hqObject;
		}
		else
		{
			activeObject = lqObject;
		}
	}
}
