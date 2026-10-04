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

	[Header("Enemies on the map")]
	[Tooltip("Metres the minimap and radar show an enemy at in the dark, without night vision: about what the eye makes out. With the goggles on, the usual radius.")]
	public float darkEnemyRevealRadius = 25f;

	[Tooltip("An enemy this close to a pumpkin or lamp stands in its light, and shows at the usual radius even in the dark.")]
	public float litEnemyRevealRadius = 10f;

	[Tooltip("A faint, very short light carried in front of the player's camera: it reaches the hands and the weapon and stops there, so a player can tell what they are holding without the night around them getting any lighter. Off with the goggles on.")]
	public GameObject viewmodelLightPrefab;

	[Tooltip("Where the viewmodel light sits in front of the camera, in the camera's own space.")]
	public Vector3 viewmodelLightOffset = new Vector3(0f, -0.15f, 0.35f);

	[Header("Night vision")]
	public AudioClip nightVisionOn;

	public AudioClip nightVisionOff;

	[Header("Dressing, client only")]
	[Tooltip("Pumpkins scattered over the whole map, a new layout every match: carved ones carry a candle light, plain ones none.")]
	public GameObject[] pumpkinPrefabs;

	[Tooltip("The play area is cut into squares this many metres wide, and every square gets one group of pumpkins at a random spot of open ground: even cover with no large dark holes, a new layout every match.")]
	public float pumpkinCellSize = 75f;

	[Tooltip("Across the battlefield (the ground the flags enclose) every square is split into this many by this many, each with its own group: the middle of the map is lit more densely than its outskirts.")]
	[Min(1)]
	public int battlefieldSubdivision = 2;

	[Tooltip("Metres past the flags' outline still counted as battlefield.")]
	public float battlefieldMargin = 40f;

	[Tooltip("Pumpkins in one group, at most; each group has between one and this many.")]
	[Min(1)]
	public int pumpkinsPerSpotMax = 2;

	[Tooltip("Metres kept between two groups, across the squares' edges too.")]
	public float pumpkinSpotSpacing = 25f;

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

	[Header("Base lamps, client only (NightBaseLighting)")]
	[Tooltip("The colour of every lamp that is not a flag's: a warm, plain light.")]
	public Color lampLight = new Color(1f, 0.85f, 0.62f);

	[Tooltip("Metres inside a gate post its lamp stands.")]
	public float gateLampInset = 2.5f;

	[Tooltip("Metres from an ammo cache or medical station its lamp stands.")]
	public float supplyLampOffset = 3f;

	[Tooltip("Hung in every watchtower, over its platform.")]
	public GameObject towerLanternPrefab;

	[Tooltip("Where up a watchtower the lantern hangs, as a share of its height.")]
	[Range(0f, 1f)]
	public float towerLanternHeight = 0.72f;

	[Tooltip("The beam switched on in every floodlight prop.")]
	public GameObject floodlightBeamPrefab;

	[Tooltip("Lamps ringing an HQ's compound, which has no gates of the blueprint's.")]
	[Min(0)]
	public int headquartersLamps = 8;

	[Tooltip("Metres outside an HQ's capture range its ring of lamps stands.")]
	public float headquartersLampMargin = 3f;

	[Tooltip("Candles flicker by this share of their brightness.")]
	[Range(0f, 1f)]
	public float flickerAmount = 0.3f;

	public float flickerSpeed = 6f;

	public Color neutralFlagLight = new Color(1f, 0.82f, 0.55f);

	public Color blueFlagLight = new Color(0.45f, 0.62f, 1f);

	public Color redFlagLight = new Color(1f, 0.42f, 0.36f);
}
