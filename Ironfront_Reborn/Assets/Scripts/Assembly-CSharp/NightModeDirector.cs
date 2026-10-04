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
	private const int SpotAttemptsPerSpot = 6;
	private const int FillerTries = 8;

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

		// Offline the original's own switch decides (GameManager.nightMode), with the default battery.
		if (NetContext.IsOffline)
		{
			bool offlineNight = GameManager.instance != null && GameManager.instance.nightMode;
			Apply(new RoomSettings(offlineNight ? GameMode.Night : GameMode.PointMatch, VictoryRule.Margin,
				RoomRules.DefaultMarginPoints, offlineNight ? RoomRules.DefaultNightVisionSeconds : (byte)0));
			return;
		}
		Apply(NetRoomRules.Current);
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
		int seed = NetRoomRules.RoomId != 0
			? StableHash(mapName) ^ (NetRoomRules.RoomId * 7919)
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
	/// Small groups of pumpkins where the match is fought: most scattered evenly and at random over
	/// the ground round the flags and the routes between them, some along those routes, a few round
	/// the flags and HQs; never out at the empty edges of the map, and not heaped on the bases
	/// either (owner reports 2026-10-04: a blend of the first two layouts). Never on a slope, in water, under a roof or on a tree, and
	/// kept <see cref="NightModeConfig.pumpkinSpotSpacing"/> apart.
	/// </summary>
	private void ScatterPumpkins(System.Random random)
	{
		if (config.pumpkinPrefabs == null || config.pumpkinPrefabs.Length == 0 || config.pumpkinSpots <= 0)
		{
			return;
		}
		List<Vector3> flags = FlagPositions();
		if (!TryPlayArea(out Rect area) || flags.Count == 0)
		{
			Debug.LogWarning("[night] " + mapName + " has no LevelBounds, terrain or flags; no pumpkins.");
			return;
		}
		area = FightingArea(area, flags, config.pumpkinScatterReach);
		List<Vector3> routes = Routes(flags);
		var spots = new List<Vector3>(config.pumpkinSpots);
		float spacingSqr = config.pumpkinSpotSpacing * config.pumpkinSpotSpacing;
		int attempts = config.pumpkinSpots * SpotAttemptsPerSpot;
		for (int attempt = 0; attempt < attempts && spots.Count < config.pumpkinSpots; attempt++)
		{
			Vector3 around = Candidate(random, flags, routes, area);
			if (!area.Contains(new Vector2(around.x, around.z))
				|| !TryGround(around, out Vector3 centre) || TooClose(spots, centre, spacingSqr))
			{
				continue;
			}
			spots.Add(centre);
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
		Debug.Log("[night] " + mapName + ": " + pumpkins.Count + " pumpkin(s) in " + spots.Count + " group(s), " + candles.Count + " candle(s).");
	}

	/// <summary>
	/// Where to try a group: on a route between two flags, around a flag, or anywhere in the
	/// fighting area, in the config's shares.
	/// </summary>
	private Vector3 Candidate(System.Random random, List<Vector3> flags, List<Vector3> routes, Rect area)
	{
		double roll = random.NextDouble();
		if (roll < config.pumpkinRouteShare && routes.Count >= 2)
		{
			int route = random.Next(routes.Count / 2) * 2;
			Vector3 from = routes[route];
			Vector3 to = routes[route + 1];
			Vector3 along = to - from;
			along.y = 0f;
			Vector3 side = new Vector3(-along.z, 0f, along.x).normalized;
			// A bell across the route: most close to the line players walk, a few out to its edge.
			float offset = (float)((random.NextDouble() + random.NextDouble() - 1.0) * config.pumpkinRouteHalfWidth);
			return Vector3.Lerp(from, to, (float)random.NextDouble()) + (side * offset);
		}
		if (roll < config.pumpkinRouteShare + config.pumpkinFlagShare)
		{
			Vector3 flag = flags[random.Next(flags.Count)];
			float angle = (float)(random.NextDouble() * Mathf.PI * 2.0);
			float radius = Mathf.Lerp(config.pumpkinFlagRingMin, config.pumpkinFlagReach, Mathf.Sqrt((float)random.NextDouble()));
			return flag + (new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius);
		}
		// Anywhere near the play: within the scatter reach of a flag or half of it of a route, so
		// the open ground between the bases fills in and the far corners stay dark.
		Vector3 spot = Vector3.zero;
		for (int tries = 0; tries < FillerTries; tries++)
		{
			spot = new Vector3(
				area.xMin + ((float)random.NextDouble() * area.width), 0f,
				area.yMin + ((float)random.NextDouble() * area.height));
			if (NearestSqr(flags, spot) <= config.pumpkinScatterReach * config.pumpkinScatterReach
				|| NearestRouteSqr(routes, spot) <= 0.25f * config.pumpkinScatterReach * config.pumpkinScatterReach)
			{
				break;
			}
		}
		return spot;
	}

	private static float NearestSqr(List<Vector3> points, Vector3 at)
	{
		float best = float.MaxValue;
		foreach (Vector3 p in points)
		{
			float dx = p.x - at.x;
			float dz = p.z - at.z;
			best = Mathf.Min(best, (dx * dx) + (dz * dz));
		}
		return best;
	}

	private static float NearestRouteSqr(List<Vector3> routes, Vector3 at)
	{
		float best = float.MaxValue;
		for (int i = 0; i + 1 < routes.Count; i += 2)
		{
			Vector2 a = new Vector2(routes[i].x, routes[i].z);
			Vector2 b = new Vector2(routes[i + 1].x, routes[i + 1].z);
			Vector2 p = new Vector2(at.x, at.z);
			Vector2 ab = b - a;
			float t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
			best = Mathf.Min(best, (a + (ab * t) - p).sqrMagnitude);
		}
		return best;
	}

	private static List<Vector3> FlagPositions()
	{
		var flags = new List<Vector3>();
		if (ActorManager.instance != null && ActorManager.instance.spawnPoints != null)
		{
			foreach (SpawnPoint point in ActorManager.instance.spawnPoints)
			{
				if (point != null)
				{
					flags.Add(point.transform.position);
				}
			}
		}
		return flags;
	}

	/// <summary>
	/// Every flag's link to its two nearest flags, once each, as start/end pairs: the lines a match
	/// moves along.
	/// </summary>
	private static List<Vector3> Routes(List<Vector3> flags)
	{
		var routes = new List<Vector3>();
		var linked = new HashSet<long>();
		for (int i = 0; i < flags.Count; i++)
		{
			int first = -1;
			int second = -1;
			float firstSqr = float.MaxValue;
			float secondSqr = float.MaxValue;
			for (int j = 0; j < flags.Count; j++)
			{
				if (j == i)
				{
					continue;
				}
				float d = (flags[j] - flags[i]).sqrMagnitude;
				if (d < firstSqr)
				{
					second = first;
					secondSqr = firstSqr;
					first = j;
					firstSqr = d;
				}
				else if (d < secondSqr)
				{
					second = j;
					secondSqr = d;
				}
			}
			foreach (int j in new[] { first, second })
			{
				if (j < 0 || !linked.Add(((long)Mathf.Min(i, j) << 32) | (uint)Mathf.Max(i, j)))
				{
					continue;
				}
				routes.Add(flags[i]);
				routes.Add(flags[j]);
			}
		}
		return routes;
	}

	/// <summary>The play area cut down to the box round every flag plus <paramref name="reach"/>.</summary>
	private static Rect FightingArea(Rect playArea, List<Vector3> flags, float reach)
	{
		float xMin = float.MaxValue, xMax = float.MinValue, zMin = float.MaxValue, zMax = float.MinValue;
		foreach (Vector3 flag in flags)
		{
			xMin = Mathf.Min(xMin, flag.x);
			xMax = Mathf.Max(xMax, flag.x);
			zMin = Mathf.Min(zMin, flag.z);
			zMax = Mathf.Max(zMax, flag.z);
		}
		return Rect.MinMaxRect(
			Mathf.Max(playArea.xMin, xMin - reach), Mathf.Max(playArea.yMin, zMin - reach),
			Mathf.Min(playArea.xMax, xMax + reach), Mathf.Min(playArea.yMax, zMax + reach));
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

	// A new map: the last one's dressing went with its scene, and its lights with it.
	private void ForgetMap()
	{
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
