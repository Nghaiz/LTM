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
/// <b>Client only:</b> the pumpkins and candles around every flag and a lamp beside it in the
/// colour of the side holding it (all without colliders, so a client's world never differs from
/// the server's where it matters), the night's ambience, and the goggles
/// (<see cref="NightVisionGoggles"/>) with the room's battery.
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
	private const float FlagColourEverySeconds = 0.5f;
	private const float GroundProbeHeight = 60f;

	public static NightModeDirector instance;

	private NightModeConfig config;
	private string mapName = string.Empty;
	private bool started;
	private bool night;

	private readonly List<GameObject> dressing = new List<GameObject>();
	private readonly List<Light> candles = new List<Light>();
	private readonly List<float> candleIntensity = new List<float>();
	private readonly List<SpawnPoint> flagPoints = new List<SpawnPoint>();
	private readonly List<Light> flagLights = new List<Light>();
	private float nextFlagColour;

	private Light moon;
	private float moonAuthored;
	private AudioSource ambience;
	private AudioClip ambienceAuthored;

	private NightVisionGoggles goggles;

	/// <summary>Whether this map is in Night Mode now.</summary>
	public bool IsNight => night;

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

		SpawnPoint[] points = ActorManager.instance.spawnPoints;
		for (int index = 0; index < points.Length; index++)
		{
			SpawnPoint point = points[index];
			if (point == null)
			{
				continue;
			}
			// The same layout on every client: seeded by the map and the flag, not by the clock.
			System.Random random = new System.Random(mapName.GetHashCode() ^ (index * 7919));
			Vector3 flag = point.transform.position;
			PlacePumpkins(random, flag);
			PlaceFlagLight(random, point, flag);
		}
	}

	private void PlacePumpkins(System.Random random, Vector3 flag)
	{
		if (config.pumpkinPrefabs == null || config.pumpkinPrefabs.Length == 0)
		{
			return;
		}
		int placed = 0;
		for (int attempt = 0; attempt < config.pumpkinsPerFlag * 4 && placed < config.pumpkinsPerFlag; attempt++)
		{
			float angle = (float)(random.NextDouble() * Mathf.PI * 2.0);
			float radius = Mathf.Lerp(config.pumpkinRingMin, config.pumpkinRingMax, (float)random.NextDouble());
			Vector3 around = flag + new Vector3(Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);
			if (!TryGround(around, out Vector3 at))
			{
				continue;
			}
			GameObject prefab = config.pumpkinPrefabs[random.Next(config.pumpkinPrefabs.Length)];
			if (prefab == null)
			{
				continue;
			}
			// Facing the flag, so a carved face looks at whoever is fighting for it.
			Vector3 toFlag = flag - at;
			toFlag.y = 0f;
			Quaternion facing = toFlag.sqrMagnitude > 0.01f ? Quaternion.LookRotation(toFlag) : Quaternion.identity;
			GameObject pumpkin = Instantiate(prefab, at, facing * Quaternion.Euler(0f, (float)(random.NextDouble() * 40.0 - 20.0), 0f));
			dressing.Add(pumpkin);
			foreach (Light candle in pumpkin.GetComponentsInChildren<Light>(true))
			{
				candles.Add(candle);
				candleIntensity.Add(candle.intensity);
			}
			placed++;
		}
	}

	private void PlaceFlagLight(System.Random random, SpawnPoint point, Vector3 flag)
	{
		if (config.flagLightPrefab == null)
		{
			return;
		}
		for (int attempt = 0; attempt < 8; attempt++)
		{
			float angle = (float)(random.NextDouble() * Mathf.PI * 2.0);
			Vector3 around = flag + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * config.flagLightDistance;
			if (!TryGround(around, out Vector3 at))
			{
				continue;
			}
			Vector3 toFlag = flag - at;
			toFlag.y = 0f;
			GameObject lamp = Instantiate(config.flagLightPrefab, at, Quaternion.LookRotation(toFlag.normalized));
			dressing.Add(lamp);
			Light light = lamp.GetComponentInChildren<Light>(true);
			if (light != null)
			{
				flagPoints.Add(point);
				flagLights.Add(light);
			}
			return;
		}
	}

	// Open ground under a spot: the terrain itself, not a roof, a rock or a tree, and not a slope.
	private static bool TryGround(Vector3 around, out Vector3 at)
	{
		Vector3 origin = new Vector3(around.x, around.y + GroundProbeHeight, around.z);
		if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, GroundProbeHeight * 3f, ~0, QueryTriggerInteraction.Ignore)
			&& hit.collider is TerrainCollider && hit.normal.y > 0.85f)
		{
			at = hit.point;
			return true;
		}
		at = around;
		return false;
	}

	private void Update()
	{
		if (!night || candles.Count + flagLights.Count == 0)
		{
			return;
		}
		float t = Time.time * config.flickerSpeed;
		for (int i = 0; i < candles.Count; i++)
		{
			Light candle = candles[i];
			if (candle != null)
			{
				candle.intensity = candleIntensity[i] * (1f - (config.flickerAmount * Mathf.PerlinNoise(t, i * 3.7f)));
			}
		}
		if (Time.time < nextFlagColour)
		{
			return;
		}
		nextFlagColour = Time.time + FlagColourEverySeconds;
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
		candles.Clear();
		candleIntensity.Clear();
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
