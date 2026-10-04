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

	[Tooltip("The moonlight's shadows in Night Mode: the moon, not the ambient, is what gives the night its shapes.")]
	public LightShadows moonShadows = LightShadows.Soft;

	[Range(0f, 1f)]
	public float moonShadowStrength = 0.85f;

	[Tooltip("The moon's height above the horizon in degrees, and its compass bearing; a low moon throws long shadows.")]
	[Range(5f, 90f)]
	public float moonElevation = 35f;

	public float moonBearing = 221f;

	[Tooltip("The share of the night's fog density night vision leaves.")]
	[Range(0.05f, 1f)]
	public float nightVisionFogFactor = 0.35f;

	[Tooltip("The night's ambient loop, in place of the scene's own; empty keeps it.")]
	public AudioClip ambience;

	[Header("Night vision")]
	public AudioClip nightVisionOn;

	public AudioClip nightVisionOff;

	[Header("Dressing, client only")]
	[Tooltip("Pumpkins scattered over the whole map, a new layout every match: carved ones carry a candle light, plain ones none.")]
	public GameObject[] pumpkinPrefabs;

	[Tooltip("Places across the play area a small group of pumpkins is set out at.")]
	public int pumpkinSpots = 220;

	[Tooltip("Pumpkins in one group, at most; each group has between one and this many.")]
	[Min(1)]
	public int pumpkinsPerSpotMax = 3;

	[Tooltip("Metres kept between two groups.")]
	public float pumpkinSpotSpacing = 12f;

	[Tooltip("Metres a group's pumpkins sit from its centre, at most.")]
	public float pumpkinGroupRadius = 1.8f;

	[Tooltip("Metres from the camera past which a pumpkin is not drawn: the fog has hidden it long before.")]
	public float pumpkinDrawDistance = 140f;

	[Tooltip("How many of the dressing's lights, the nearest to the camera, shine at once. The rest stay dark until the camera comes near: a forward renderer pays for every light on everything it touches.")]
	[Min(0)]
	public int nearLights = 12;

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
