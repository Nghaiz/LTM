using UnityEngine;

/// <summary>
/// What a map looks like in Night Mode (phase P32): its dark, the light that is left, and the
/// dressing put out around its flags. Loaded from <c>Resources/NightMode/&lt;scene&gt;</c> by
/// <see cref="NightModeDirector"/>; a map without one has no Night Mode (the lobby offers it on
/// Forest Lake only, <c>RoomRules.NightModeMapId</c>).
/// </summary>
[CreateAssetMenu(menuName = "Ironfront/Night Mode Config", fileName = "NightMode")]
public sealed class NightModeConfig : ScriptableObject
{
	[Header("Dark")]
	[Tooltip("Replaces the scene's own night (TimeOfDay.nightAtmosphere). The fog is exponential-squared: at density d a thing r metres away keeps exp(-(r*d)^2) of itself, and the bots see by the same formula.")]
	public TimeOfDay.Atmosphere atmosphere;

	[Tooltip("The moonlight's intensity in Night Mode, against the scene's own.")]
	public float moonIntensity = 0.08f;

	[Tooltip("The share of the night's fog density night vision leaves.")]
	[Range(0.05f, 1f)]
	public float nightVisionFogFactor = 0.35f;

	[Tooltip("The night's ambient loop, in place of the scene's own; empty keeps it.")]
	public AudioClip ambience;

	[Header("Night vision")]
	public AudioClip nightVisionOn;

	public AudioClip nightVisionOff;

	[Header("Dressing, client only")]
	[Tooltip("Pumpkins set out around every flag: carved ones carry a candle light, plain ones none.")]
	public GameObject[] pumpkinPrefabs;

	public int pumpkinsPerFlag = 5;

	public float pumpkinRingMin = 4f;

	public float pumpkinRingMax = 11f;

	[Tooltip("A dim lamp on a stand beside every flag, its light in the colour of the side that holds it.")]
	public GameObject flagLightPrefab;

	public float flagLightDistance = 7f;

	[Tooltip("Candles flicker by this share of their brightness.")]
	[Range(0f, 1f)]
	public float flickerAmount = 0.3f;

	public float flickerSpeed = 6f;

	public Color neutralFlagLight = new Color(1f, 0.82f, 0.55f);

	public Color blueFlagLight = new Color(0.45f, 0.62f, 1f);

	public Color redFlagLight = new Color(1f, 0.42f, 0.36f);
}
