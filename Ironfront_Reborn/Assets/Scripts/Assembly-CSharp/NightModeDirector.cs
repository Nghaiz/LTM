using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Night Mode (phase P32): puts the map in the dark when the room's mode says so, on the game
/// server and on every client, and lights it the way the original's Halloween night did.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same night on both ends.</b> The server needs it as much as the clients: the original's
/// bots see by the fog (<c>AiActorController.CanSeeActor</c> scales a sighting by
/// exp(-(r*fog)^2)), so the night's dense fog is what makes them half-blind, exactly as it does the
/// players. The settings come from <see cref="NetRoomRules"/>: on the server from the master's
/// room assignment, on a client from the room it joined and the match state.
/// </para>
/// <para>
/// <b>Client only:</b> pumpkins scattered over the whole map, a new layout every match but the same
/// for everyone in it (seeded by the map and the room), their candles lit at the original's
/// strength, a lamp beside every flag in the colour of the side holding it (all without colliders,
/// so a client's world never differs from the server's where it matters), the night's ambience,
/// and the goggles (<see cref="NightVisionGoggles"/>) with the room's battery.
/// </para>
/// <para>
/// Lives on <c>ActorManager</c>'s object, which outlives a map load, so <see cref="StartGame"/>
/// forgets the last map's dressing. A map with no <c>Resources/NightMode/&lt;scene&gt;</c> config
/// has no night, whatever the room says.
/// </para>
/// </remarks>
public sealed class NightModeDirector : MonoBehaviour
{
	private const string ConfigFolder = "NightMode/";
	private const float GroundProbeHeight = 60f;
	private const float LightsEverySeconds = 0.25f;
	private const float MaxGroundSlopeY = 0.85f;
	private const float OnTerrainTolerance = 0.5f;
	private const float DryMargin = 0.3f;
	private const int SpotAttemptsPerCell = 8;

	public static NightModeDirector instance;

	private NightModeConfig config;
	private string mapName = string.Empty;
	private bool started;
	private bool night;

	private readonly List<GameObject> dressing = new List<GameObject>();
	private readonly List<Light> candles = new List<Light>();
	private readonly List<float> candleIntensity = new List<float>();
	private readonly List<GameObject> pumpkins = new List<GameObject>();
	private readonly List<Light> pooledLights = new List<Light>();
	private readonly List<Vector3> glowPoints = new List<Vector3>();
	private float[] lightDistance = new float[0];
	private float[] sortedDistance = new float[0];
	private float nextLights;
	private readonly List<SpawnPoint> flagPoints = new List<SpawnPoint>();
	private readonly List<Light> flagLights = new List<Light>();

	private Light moon;
	private float moonAuthored;
	private LightShadows moonShadowsAuthored;
	private float moonShadowStrengthAuthored;
	private Quaternion moonRotationAuthored;
	private Light skySunAuthored;
	private AudioSource ambience;
	private AudioClip ambienceAuthored;

	private NightVisionGoggles goggles;

	// The light on the player's own hands and weapon (NightModeConfig.viewmodelLightPrefab), moved
	// to whichever camera is drawing: the on-foot camera, a seat's, a vehicle's.
	private Light viewmodelLight;

	/// <summary>Whether this map is in Night Mode now.</summary>
	public bool IsNight => night;

	/// <summary>Whether this client sees through its night-vision goggles now.</summary>
	public bool NightVisionOn => night && goggles != null && goggles.enabled && goggles.Battery != null && goggles.Battery.IsOn;

	/// <summary>Where the night map draws a glow: every lit pumpkin and every flag lamp.</summary>
	public IReadOnlyList<Vector3> GlowPoints => glowPoints;

	public static NightModeDirector EnsureOn(GameObject host)
	{
		NightModeDirector director = host.GetComponent<NightModeDirector>();
		if (director == null)
		{
			director = host.AddComponent<NightModeDirector>();
		}
		return director;
	}

	private void Awake()
	{
		instance = this;
	}

	private void OnEnable()
	{
		NetRoomRules.Changed += OnRoomRulesChanged;
	}

	private void OnDisable()
	{
		NetRoomRules.Changed -= OnRoomRulesChanged;
	}

	/// <summary>
	/// A map is up: after <see cref="ActorManager"/>, whose flags it reads, and before the scene's
	/// <see cref="TimeOfDay"/> starts, so the night it starts with is already the right one.
	/// </summary>
	public void StartGame()
	{
		ForgetMap();
		started = true;
		mapName = SceneManager.GetActiveScene().name;
		config = Resources.Load<NightModeConfig>(ConfigFolder + mapName);

		// Offline the original's own switch decides (GameManager.nightMode), with the battery the
		// practice screen chose (GameManager.nightVisionSeconds).
		if (NetContext.IsOffline)
		{
			bool offlineNight = GameManager.instance != null && GameManager.instance.nightMode;
			Apply(new RoomSettings(offlineNight ? GameMode.Night : GameMode.PointMatch, VictoryRule.Margin,
				RoomRules.DefaultMarginPoints, offlineNight ? OfflineBatterySeconds() : (byte)0));
			return;
		}
		Apply(NetRoomRules.Current);
	}

	/// <summary>The offline night's battery: the practice screen's, held to the rooms' own range.</summary>
	private static byte OfflineBatterySeconds()
	{
		int seconds = GameManager.instance != null ? GameManager.instance.nightVisionSeconds : RoomRules.DefaultNightVisionSeconds;
		return (byte)Mathf.Clamp(seconds, RoomRules.MinNightVisionSeconds, RoomRules.MaxNightVisionSeconds);
	}

	private void OnRoomRulesChanged(RoomSettings settings)
	{
		if (started && !NetContext.IsOffline)
		{
			Apply(settings);
		}
	}

	private void Apply(RoomSettings settings)
	{
		bool wantNight = settings.Mode == GameMode.Night && config != null;
		if (settings.Mode == GameMode.Night && config == null)
		{
			Debug.LogWarning("[night] the room is in Night Mode but " + mapName + " has no Resources/" + ConfigFolder + mapName + "; playing it by day.");
		}
		if (wantNight != night)
		{
			if (wantNight)
			{
				GoNight(settings);
			}
			else
			{
				GoDay();
			}
		}
		else if (night && goggles != null)
		{
			goggles.Configure(settings.NightVisionSeconds, config.nightVisionOn, config.nightVisionOff);
		}
		// The commander plans by the same dark, every time: a new map starts it by day.
		if (BotCommander.instance != null)
		{
			BotCommander.instance.UseNightTactics(NightTactics.IsNight);
		}
	}

	private void GoNight(RoomSettings settings)
	{
		night = true;
		if (GameManager.instance != null)
		{
			GameManager.instance.nightMode = true;
		}

		TimeOfDay time = TimeOfDay.instance;
		if (time != null)
		{
			time.nightAtmosphere = config.atmosphere;
			time.nightVisionFogFactor = config.nightVisionFogFactor;
			FindNightLights(time);
			if (moon != null)
			{
				moon.intensity = config.moonIntensity;
				moon.shadows = config.moonShadows;
				moon.shadowStrength = config.moonShadowStrength;
				moon.transform.rotation = Quaternion.Euler(config.moonElevation, config.moonBearing, 0f);
				// The procedural sky draws its disc opposite RenderSettings.sun, which is still the
				// hidden sunlight: make it the moon, so the moon in the sky casts the shadows.
				skySunAuthored = RenderSettings.sun;
				RenderSettings.sun = moon;
			}
			if (ambience != null && config.ambience != null)
			{
				ambience.clip = config.ambience;
				if (ambience.isActiveAndEnabled)
				{
					ambience.Play();
				}
			}
			// Started already (a room assigned to a running server, or a match state that came
			// late): switch now. Otherwise its own Start reads GameManager.nightMode.
			if (time.Started)
			{
				time.SetNight(true);
			}
		}

		// Everything a player sees and uses; a game server has nobody to show it to.
		if (!NetContext.IsServer && !Application.isBatchMode)
		{
			Dress();
			if (goggles == null)
			{
				goggles = gameObject.AddComponent<NightVisionGoggles>();
			}
			goggles.enabled = true;
			goggles.Configure(settings.NightVisionSeconds, config.nightVisionOn, config.nightVisionOff);
			if (viewmodelLight == null && config.viewmodelLightPrefab != null)
			{
				viewmodelLight = Instantiate(config.viewmodelLightPrefab).GetComponentInChildren<Light>(true);
			}
		}
		Debug.Log("[night] " + mapName + " is in Night Mode: fog " + config.atmosphere.fogDensity.ToString("0.000")
			+ ", night vision " + settings.NightVisionSeconds + " s, " + dressing.Count + " dressing piece(s).");
	}

	private void GoDay()
	{
		night = false;
		if (GameManager.instance != null)
		{
			GameManager.instance.nightMode = false;
		}
		if (moon != null)
		{
			moon.intensity = moonAuthored;
			moon.shadows = moonShadowsAuthored;
			moon.shadowStrength = moonShadowStrengthAuthored;
			moon.transform.rotation = moonRotationAuthored;
			RenderSettings.sun = skySunAuthored;
		}
		if (ambience != null && ambienceAuthored != null)
		{
			ambience.clip = ambienceAuthored;
		}
		if (goggles != null)
		{
			goggles.enabled = false;
		}
		foreach (GameObject piece in dressing)
		{
			if (piece != null)
			{
				piece.SetActive(false);
			}
		}
		TimeOfDay time = TimeOfDay.instance;
		if (time != null && time.Started)
		{
			time.SetNight(false);
		}
		Debug.Log("[night] " + mapName + " is back to day.");
	}

	// The Night child's moonlight and ambience, found even while it is hidden.
	private void FindNightLights(TimeOfDay time)
	{
		if (moon == null)
		{
			Transform moonlight = time.transform.Find("Night/Moonlight");
			moon = moonlight != null ? moonlight.GetComponent<Light>() : null;
			if (moon != null)
			{
				moonAuthored = moon.intensity;
				moonShadowsAuthored = moon.shadows;
				moonShadowStrengthAuthored = moon.shadowStrength;
				moonRotationAuthored = moon.transform.rotation;
			}
		}
		if (ambience == null)
		{
			Transform sound = time.transform.Find("Night/Ambient Sound");
			ambience = sound != null ? sound.GetComponent<AudioSource>() : null;
			if (ambience != null)
			{
				ambienceAuthored = ambience.clip;
			}
		}
	}

	private void Dress()
	{
		if (dressing.Count > 0)
		{
			foreach (GameObject piece in dressing)
			{
				if (piece != null)
				{
					piece.SetActive(true);
				}
			}
			return;
		}
		if (ActorManager.instance == null || ActorManager.instance.spawnPoints == null)
		{
			return;
		}

		// A new layout every match and the same for everyone in it: the room is new for every match
		// (the master never reuses one), and every client in it knows its id. Practice and a direct
		// connect have no room, so they take the clock.
		// The UTC day is mixed in because room ids start again at 1 whenever the master restarts:
		// without it room 1 after a deploy laid out the same pumpkins as room 1 before it.
		int seed = NetRoomRules.RoomId != 0
			? StableHash(mapName) ^ (NetRoomRules.RoomId * 7919) ^ StableHash(System.DateTime.UtcNow.ToString("yyyyMMdd"))
			: System.Environment.TickCount;
		System.Random random = new System.Random(seed);
		ScatterPumpkins(random);

		// Lamps by every flag and HQ, by the base's own plan (NightBaseLighting).
		int lamps = 0;
		foreach (SpawnPoint point in ActorManager.instance.spawnPoints)
		{
			lamps += NightBaseLighting.Dress(config, point, piece => dressing.Add(piece), AddBaseLight);
		}
		Debug.Log("[night] " + mapName + ": " + lamps + " lamp(s), lantern(s) and floodlight(s) at the flags and HQs.");
		lightDistance = new float[pooledLights.Count];
		nextLights = 0f;
	}

	// FNV-1a: string.GetHashCode is not promised to agree between processes.
	private static int StableHash(string text)
	{
		unchecked
		{
			uint hash = 2166136261;
			foreach (char c in text)
			{
				hash = (hash ^ c) * 16777619;
			}
			return (int)hash;
		}
	}

	/// <summary>
	/// Pumpkins over the whole play area, evenly and at random: the area is cut into
	/// <see cref="NightModeConfig.pumpkinCellSize"/> squares and every square gets one small group
	/// at a random dry, level spot of open ground inside it (never on a slope, in water, under a
	/// roof or on a tree), groups kept <see cref="NightModeConfig.pumpkinSpotSpacing"/> apart.
	/// </summary>
	/// <remarks>
	/// Owner reports 2026-10-04, three in a row: a plain random scatter left whole regions dark and
	/// heaped others; weighting it toward the flags and routes emptied the rest of the map. One
	/// group per square cannot leave a hole wider than about two squares, and the spot inside each
	/// square is still a fresh roll every match. Across the battlefield -- the ground the flags
	/// enclose, plus <see cref="NightModeConfig.battlefieldMargin"/> -- each square is split into
	/// <see cref="NightModeConfig.battlefieldSubdivision"/> squared smaller ones, so the middle of
	/// the map, where the fighting is, is lit more densely (owner, same day).
	/// </remarks>
	private void ScatterPumpkins(System.Random random)
	{
		if (config.pumpkinPrefabs == null || config.pumpkinPrefabs.Length == 0 || config.pumpkinCellSize <= 1f)
		{
			return;
		}
		if (!TryPlayArea(out Rect area))
		{
			Debug.LogWarning("[night] " + mapName + " has no LevelBounds or terrain; no pumpkins.");
			return;
		}
		List<Vector2> battlefield = FlagHull();
		int columns = Mathf.Max(1, Mathf.RoundToInt(area.width / config.pumpkinCellSize));
		int rows = Mathf.Max(1, Mathf.RoundToInt(area.height / config.pumpkinCellSize));
		float cellWidth = area.width / columns;
		float cellHeight = area.height / rows;
		var spots = new List<Vector3>(columns * rows);
		float spacingSqr = config.pumpkinSpotSpacing * config.pumpkinSpotSpacing;
		float denseSpacingSqr = spacingSqr / (config.battlefieldSubdivision * config.battlefieldSubdivision);
		int emptyCells = 0;
		int cells = 0;
		int denseCells = 0;
		for (int row = 0; row < rows; row++)
		{
			for (int column = 0; column < columns; column++)
			{
				float x0 = area.xMin + (column * cellWidth);
				float z0 = area.yMin + (row * cellHeight);
				Vector2 middle = new Vector2(x0 + (cellWidth * 0.5f), z0 + (cellHeight * 0.5f));
				int split = InsideOrNear(battlefield, middle, config.battlefieldMargin)
					? Mathf.Max(1, config.battlefieldSubdivision)
					: 1;
				if (split > 1)
				{
					denseCells++;
				}
				float subWidth = cellWidth / split;
				float subHeight = cellHeight / split;
				for (int sz = 0; sz < split; sz++)
				{
					for (int sx = 0; sx < split; sx++)
					{
						cells++;
						if (!TryCell(random, x0 + (sx * subWidth), z0 + (sz * subHeight), subWidth, subHeight,
							spots, split > 1 ? denseSpacingSqr : spacingSqr))
						{
							emptyCells++;
						}
					}
				}
			}
		}
		Debug.Log("[night] " + mapName + ": " + pumpkins.Count + " pumpkin(s) in " + spots.Count + " group(s) over "
			+ cells + " cell(s) of " + config.pumpkinCellSize.ToString("0") + " m, " + denseCells + " of them split "
			+ config.battlefieldSubdivision + "x" + config.battlefieldSubdivision + " across the battlefield ("
			+ emptyCells + " with no open ground), " + candles.Count + " candle(s).");
	}

	/// <summary>One group at a random spot of open ground in the cell; false when none was found.</summary>
	private bool TryCell(System.Random random, float x0, float z0, float width, float height, List<Vector3> spots, float spacingSqr)
	{
		for (int attempt = 0; attempt < SpotAttemptsPerCell; attempt++)
		{
			Vector3 around = new Vector3(
				x0 + ((float)random.NextDouble() * width), 0f,
				z0 + ((float)random.NextDouble() * height));
			if (!TryGround(around, out Vector3 centre) || TooClose(spots, centre, spacingSqr))
			{
				continue;
			}
			spots.Add(centre);
			PlaceGroup(random, centre);
			return true;
		}
		return false;
	}

	/// <summary>The convex hull of the flags and HQs on the ground plane (x, z), counter-clockwise.</summary>
	private static List<Vector2> FlagHull()
	{
		var points = new List<Vector2>();
		if (ActorManager.instance != null && ActorManager.instance.spawnPoints != null)
		{
			foreach (SpawnPoint point in ActorManager.instance.spawnPoints)
			{
				if (point != null)
				{
					points.Add(new Vector2(point.transform.position.x, point.transform.position.z));
				}
			}
		}
		points.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
		if (points.Count < 3)
		{
			return points;
		}
		var hull = new List<Vector2>();
		for (int pass = 0; pass < 2; pass++)
		{
			int start = hull.Count;
			for (int i = 0; i < points.Count; i++)
			{
				Vector2 p = points[pass == 0 ? i : points.Count - 1 - i];
				while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f)
				{
					hull.RemoveAt(hull.Count - 1);
				}
				hull.Add(p);
			}
			hull.RemoveAt(hull.Count - 1);
		}
		return hull;
	}

	private static float Cross(Vector2 o, Vector2 a, Vector2 b) => ((a.x - o.x) * (b.y - o.y)) - ((a.y - o.y) * (b.x - o.x));

	/// <summary>Whether <paramref name="p"/> is inside the hull or within <paramref name="margin"/> of its edge.</summary>
	private static bool InsideOrNear(List<Vector2> hull, Vector2 p, float margin)
	{
		if (hull.Count < 3)
		{
			return false;
		}
		bool inside = true;
		float nearestSqr = float.MaxValue;
		for (int i = 0; i < hull.Count; i++)
		{
			Vector2 a = hull[i];
			Vector2 b = hull[(i + 1) % hull.Count];
			if (Cross(a, b, p) < 0f)
			{
				inside = false;
			}
			Vector2 ab = b - a;
			float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
			nearestSqr = Mathf.Min(nearestSqr, (a + (ab * t) - p).sqrMagnitude);
		}
		return inside || nearestSqr <= margin * margin;
	}

	private void PlaceGroup(System.Random random, Vector3 centre)
	{
		int inGroup = 1 + random.Next(config.pumpkinsPerSpotMax);
		for (int i = 0; i < inGroup; i++)
		{
			Vector3 at = centre;
			if (i > 0)
			{
				float angle = (float)(random.NextDouble() * Mathf.PI * 2.0);
				float radius = config.pumpkinGroupRadius * (0.4f + 0.6f * (float)random.NextDouble());
				if (!TryGround(centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius, out at))
				{
					continue;
				}
			}
			PlacePumpkin(random, at);
		}
	}

	private static bool TooClose(List<Vector3> spots, Vector3 at, float spacingSqr)
	{
		for (int i = 0; i < spots.Count; i++)
		{
			Vector3 d = spots[i] - at;
			if ((d.x * d.x) + (d.z * d.z) < spacingSqr)
			{
				return true;
			}
		}
		return false;
	}

	// The play area with ground under it: the minimap's own rule (MinimapCamera.PlayVolumeFrame).
	private static bool TryPlayArea(out Rect area)
	{
		area = default;
		Terrain terrain = Terrain.activeTerrain;
		if (LevelBounds.instance == null || terrain == null || terrain.terrainData == null)
		{
			return false;
		}
		Bounds box = LevelBounds.instance.WorldBox;
		Vector3 origin = terrain.GetPosition();
		Vector3 size = terrain.terrainData.size;
		float xMin = Mathf.Max(box.min.x, origin.x);
		float xMax = Mathf.Min(box.max.x, origin.x + size.x);
		float zMin = Mathf.Max(box.min.z, origin.z);
		float zMax = Mathf.Min(box.max.z, origin.z + size.z);
		if (xMax <= xMin || zMax <= zMin)
		{
			return false;
		}
		area = Rect.MinMaxRect(xMin, zMin, xMax, zMax);
		return true;
	}

	private void PlacePumpkin(System.Random random, Vector3 at)
	{
		GameObject prefab = config.pumpkinPrefabs[random.Next(config.pumpkinPrefabs.Length)];
		if (prefab == null)
		{
			return;
		}
		Quaternion facing = Quaternion.Euler(0f, (float)(random.NextDouble() * 360.0), 0f);
		GameObject pumpkin = Instantiate(prefab, at, facing);
		dressing.Add(pumpkin);
		pumpkins.Add(pumpkin);
		foreach (Light candle in pumpkin.GetComponentsInChildren<Light>(true))
		{
			candles.Add(candle);
			candleIntensity.Add(candle.intensity);
			AddPooledLight(candle, onMap: true);
		}
	}

	/// <param name="onMap">
	/// Whether the night map draws a glow for it: every pumpkin and a flag's own lamp, but not a
	/// base's gate, tower and floodlight lamps, which would paint every base as one bright blob
	/// (owner report 2026-10-04). Every light still lights the enemies near it (EnemyMapReveal).
	/// </param>
	private void AddPooledLight(Light light, bool onMap)
	{
		// Only the nearest few are lit at a time (UpdateLights), and those always per pixel: a
		// candle drawn per vertex on Low lights nothing a player can see.
		light.renderMode = LightRenderMode.ForcePixel;
		light.enabled = false;
		pooledLights.Add(light);
		Vector3 at = light.transform.position;
		if (onMap)
		{
			glowPoints.Add(at);
		}
		EnemyMapReveal.AddLight(at.x, at.z);
	}

	// A base's light: pooled like every other, and coloured by its flag's holder when it has one.
	private void AddBaseLight(Light light, SpawnPoint colouredBy)
	{
		if (colouredBy != null)
		{
			flagPoints.Add(colouredBy);
			flagLights.Add(light);
		}
		// A flag lamp carries a spot and a small glow light: one map glow for the lamp.
		AddPooledLight(light, onMap: colouredBy != null && light.type == LightType.Spot);
	}

	// Open ground under a spot: the terrain itself, not a roof, a rock or a tree (trees share the
	// terrain's collider, so the hit must also sit on the terrain's surface), not a slope and not
	// under water.
	private static bool TryGround(Vector3 around, out Vector3 at)
	{
		at = around;
		Terrain terrain = Terrain.activeTerrain;
		if (terrain == null)
		{
			return false;
		}
		float surface = terrain.SampleHeight(around) + terrain.GetPosition().y;
		Vector3 origin = new Vector3(around.x, surface + GroundProbeHeight, around.z);
		if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, GroundProbeHeight * 3f, ~0, QueryTriggerInteraction.Ignore)
			|| !(hit.collider is TerrainCollider) || hit.normal.y <= MaxGroundSlopeY
			|| Mathf.Abs(hit.point.y - surface) > OnTerrainTolerance
			|| WaterLevel.Depth(hit.point) > -DryMargin)
		{
			return false;
		}
		at = hit.point;
		return true;
	}

	private void Update()
	{
		UpdateViewmodelLight();
		// The map shows enemies by what this player can see (EnemyMapReveal): the dark hides them,
		// the goggles bring the usual radius back.
		if (night && !NightVisionOn)
		{
			EnemyMapReveal.SetDark(config.darkEnemyRevealRadius, config.litEnemyRevealRadius);
		}
		else if (EnemyMapReveal.IsDark)
		{
			EnemyMapReveal.SetSeeing();
		}
		if (!night || pooledLights.Count == 0)
		{
			return;
		}
		if (Time.time >= nextLights)
		{
			nextLights = Time.time + LightsEverySeconds;
			UpdateLights();
			UpdateFlagColours();
		}
		float t = Time.time * config.flickerSpeed;
		for (int i = 0; i < candles.Count; i++)
		{
			Light candle = candles[i];
			if (candle != null && candle.enabled)
			{
				candle.intensity = candleIntensity[i] * (1f - (config.flickerAmount * Mathf.PerlinNoise(t, i * 3.7f)));
			}
		}
	}

	/// <summary>
	/// Lights the <see cref="NightModeConfig.nearLights"/> nearest the camera and darkens the rest;
	/// draws only the pumpkins inside <see cref="NightModeConfig.pumpkinDrawDistance"/>.
	/// </summary>
	private void UpdateLights()
	{
		Camera viewer = Camera.main;
		if (viewer == null)
		{
			return;
		}
		Vector3 eye = viewer.transform.position;
		int count = pooledLights.Count;
		if (lightDistance.Length != count)
		{
			lightDistance = new float[count];
		}
		for (int i = 0; i < count; i++)
		{
			Light light = pooledLights[i];
			lightDistance[i] = light != null ? (light.transform.position - eye).sqrMagnitude : float.MaxValue;
		}
		// The K-th nearest distance: lights at or inside it shine.
		int wanted = Mathf.Min(config.nearLights, count);
		float cutoff = float.MaxValue;
		if (wanted < count)
		{
			if (sortedDistance.Length != count)
			{
				sortedDistance = new float[count];
			}
			System.Array.Copy(lightDistance, sortedDistance, count);
			System.Array.Sort(sortedDistance);
			cutoff = wanted > 0 ? sortedDistance[wanted - 1] : -1f;
		}
		int lit = 0;
		for (int i = 0; i < count; i++)
		{
			Light light = pooledLights[i];
			if (light == null)
			{
				continue;
			}
			bool on = lit < wanted && lightDistance[i] <= cutoff;
			if (on)
			{
				lit++;
			}
			if (light.enabled != on)
			{
				light.enabled = on;
			}
		}
		float drawSqr = config.pumpkinDrawDistance * config.pumpkinDrawDistance;
		for (int i = 0; i < pumpkins.Count; i++)
		{
			GameObject pumpkin = pumpkins[i];
			if (pumpkin == null)
			{
				continue;
			}
			bool show = (pumpkin.transform.position - eye).sqrMagnitude <= drawSqr;
			if (pumpkin.activeSelf != show)
			{
				pumpkin.SetActive(show);
			}
		}
	}

	private void UpdateFlagColours()
	{
		for (int i = 0; i < flagLights.Count; i++)
		{
			if (flagLights[i] == null || flagPoints[i] == null)
			{
				continue;
			}
			int owner = flagPoints[i].owner;
			flagLights[i].color = owner == 0 ? config.blueFlagLight : owner == 1 ? config.redFlagLight : config.neutralFlagLight;
		}
	}

	/// <summary>
	/// Keeps the viewmodel light on the camera that is drawing, on only by night without goggles.
	/// </summary>
	private void UpdateViewmodelLight()
	{
		if (viewmodelLight == null)
		{
			return;
		}
		Camera viewer = Camera.main;
		bool on = night && !NightVisionOn && viewer != null;
		if (on && viewmodelLight.transform.parent != viewer.transform)
		{
			viewmodelLight.transform.SetParent(viewer.transform, false);
			viewmodelLight.transform.localPosition = config.viewmodelLightOffset;
			viewmodelLight.transform.localRotation = Quaternion.identity;
		}
		if (viewmodelLight.enabled != on)
		{
			viewmodelLight.enabled = on;
		}
	}

	// A new map: the last one's dressing went with its scene, and its lights with it.
	private void ForgetMap()
	{
		if (viewmodelLight != null)
		{
			Destroy(viewmodelLight.gameObject);
			viewmodelLight = null;
		}
		foreach (GameObject piece in dressing)
		{
			if (piece != null)
			{
				Destroy(piece);
			}
		}
		dressing.Clear();
		EnemyMapReveal.Clear();
		candles.Clear();
		candleIntensity.Clear();
		pumpkins.Clear();
		pooledLights.Clear();
		glowPoints.Clear();
		flagPoints.Clear();
		flagLights.Clear();
		moon = null;
		ambience = null;
		night = false;
		if (goggles != null)
		{
			goggles.enabled = false;
		}
	}
}
