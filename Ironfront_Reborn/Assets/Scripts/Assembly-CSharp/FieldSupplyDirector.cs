using System;
using System.Collections.Generic;
using System.Text;
using Ironfront.Net.Replication.World;
using Ironfront.Net.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;
using Vec3 = Ironfront.Net.Replication.Movement.Vec3;

/// <summary>
/// Scatters a match's field vehicles (phase P32): one at every flag that is not an HQ, a few out in
/// the field, boats moored along a shore, all of a kind and at a place chosen at random each match,
/// so no two matches lay the map out alike. What a map scatters is its <see cref="FieldSupplyConfig"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> The owner's report of 2026-10-04: apart from the HQs, Forest Lake's flags had one
/// vehicle between them (a quadbike at Meadow, a jeep at Lumber Camp), so a squad that respawned at
/// a flag walked to the next fight. "Every flag must have vehicles, and vehicles and supplies should
/// be scattered at random each match, never the same, so the map and the flags always vary."
/// </para>
/// <para>
/// <b>Server-side only.</b> Each place becomes an ordinary <see cref="VehicleSpawner"/>, so the
/// vehicles reach every client through <c>S_VEHICLE_SPAWN</c> like any other and need no client
/// change; a networked client never runs this. Offline practice runs it as the server it is.
/// </para>
/// <para>
/// <b>Inside the vehicle-id budget.</b> <c>MAX_VEHICLES</c> is 24. Forest Lake authors 12 pads, four
/// of them <c>AfterMoved</c>, which held two ids while a replacement waited; six flag vehicles and two
/// field vehicles brought the worst case to 24. Since #544 no pad refills until its vehicle is
/// destroyed or abandoned, so each holds one, and the shore boat (2026-10-07) makes 21 pads. A pad
/// refused an id waits, and says so, rather than spawning an unaddressable vehicle
/// (<see cref="VehicleSpawner"/>).
/// </para>
/// <para>
/// <b>A place a vehicle can stand</b> is <see cref="FieldParking"/>'s to decide.
/// </para>
/// </remarks>
public sealed class FieldSupplyDirector : MonoBehaviour
{
	public static FieldSupplyDirector instance;

	private const string ConfigFolder = "FieldSupply/";

	/// <summary>Places tried for each vehicle before it is left out, and said to be.</summary>
	private const int ParkingAttempts = 64;

	/// <summary>Points tried for a shore boat: the shore is a thin band, and a miss costs a few heightmap reads.</summary>
	private const int ShoreAttempts = 4096;

	/// <summary>Metres a new pad keeps from any other pad, authored or scattered.</summary>
	private const float PadSeparation = 14f;

	/// <summary>How far above the ground the pad stands: the vehicle settles onto the terrain from there.</summary>
	private const float PadLift = 1f;

	/// <summary>Seconds between looks at the field pads for a wrecked vehicle.</summary>
	private const float CheckSeconds = 2f;


	private sealed class Pad
	{
		public VehicleSpawner Spawner;

		/// <summary>The flag it serves; null for a field vehicle.</summary>
		public SpawnPoint Flag;
	}

	private FieldSupplyConfig config;

	/// <summary>The map: the active scene when the match starts, not this component's own scene.</summary>
	private Scene mapScene;

	private System.Random random;

	private int matchNumber;

	private readonly List<Pad> flagPads = new List<Pad>();

	private readonly List<Pad> fieldPads = new List<Pad>();

	/// <summary>Boats moored along a shore (<see cref="FieldSupplyConfig.shoreVehicles"/>).</summary>
	private readonly List<Pad> shorePads = new List<Pad>();

	/// <summary>The flags that were HQs when the map loaded: a side's base never gets a scattered vehicle.</summary>
	private readonly HashSet<SpawnPoint> bases = new HashSet<SpawnPoint>();

	private readonly List<Vec3> padPositions = new List<Vec3>();

	private readonly List<Vec3> fieldPositions = new List<Vec3>();

	private float nextCheck;

	/// <summary>The crates out now (phase P32), as the server or the offline game placed them.</summary>
	private readonly List<GameObject> crates = new List<GameObject>();

	/// <summary>When the next crate may be placed: soon after the start, then a while after one runs out.</summary>
	private float nextCrate;

	/// <summary>The box a crate needs on the ground: the larger of the two crates, with room round it.</summary>
	private static readonly Bounds CrateFootprint = new Bounds(new Vector3(0f, 0.5f, 0f), new Vector3(1.6f, 1f, 1.6f));

	private FieldParking parking;

	public static FieldSupplyDirector EnsureOn(GameObject host)
	{
		FieldSupplyDirector director = host.GetComponent<FieldSupplyDirector>();
		if (director == null)
		{
			director = host.AddComponent<FieldSupplyDirector>();
		}
		return director;
	}

	private void Awake()
	{
		instance = this;
	}

	private void OnEnable()
	{
		ActorManager.RoundReset += OnWorldReset;
	}

	private void OnDisable()
	{
		ActorManager.RoundReset -= OnWorldReset;
	}

	/// <summary>Lays the map out for the first match: after <see cref="ActorManager"/>, whose flags it reads.</summary>
	public void StartGame()
	{
		if (NetContext.IsClient || flagPads.Count > 0 || fieldPads.Count > 0 || shorePads.Count > 0)
		{
			return;
		}

		mapScene = SceneManager.GetActiveScene();
		config = Resources.Load<FieldSupplyConfig>(ConfigFolder + mapScene.name);
		if (config == null)
		{
			Debug.Log("[supply] " + mapScene.name + " has no Resources/" + ConfigFolder + mapScene.name + ", so nothing is scattered.");
			return;
		}
		if (ActorManager.instance == null || ActorManager.instance.spawnPoints == null)
		{
			config = null;
			return;
		}
		if (config.padPrefab == null || config.padPrefab.GetComponent<VehicleSpawner>() == null)
		{
			Debug.LogError("[supply] " + mapScene.name + "'s field supply config has no pad prefab with a VehicleSpawner, so nothing is scattered.");
			config = null;
			return;
		}
		if (GameManager.instance != null && GameManager.instance.noVehicles)
		{
			Debug.Log("[supply] " + mapScene.name + ": vehicles are off for this match, none scattered.");
			return;
		}

		bases.Clear();
		foreach (SpawnPoint point in ActorManager.instance.spawnPoints)
		{
			if (point != null && point.owner >= 0)
			{
				bases.Add(point);
			}
		}
		parking = new FieldParking(config);
		Layout(firstMatch: true);
		// The first crates a few seconds in: the server's projectile table is up by then, so each
		// crate is replicated from its first frame rather than missed by the clients.
		nextCrate = Time.time + 5f;
	}

	private void Update()
	{
		if (config == null)
		{
			return;
		}
		TendCrates();
		if (Time.time < nextCheck)
		{
			return;
		}
		nextCheck = Time.time + CheckSeconds;

		// A wrecked field vehicle -- or, on a server, one left empty in the field (VehicleSpawner
		// reclaims it) -- is not replaced where it stood: the pad moves on, so the find turns up
		// somewhere else.
		foreach (Pad pad in fieldPads)
		{
			if (pad.Spawner == null || !pad.Spawner.IsSpent)
			{
				continue;
			}
			GameObject prefab = Choose(config.fieldVehicles);
			if (prefab != null && TryParkInField(prefab, pad.Spawner, out Vector3 at, out Quaternion facing))
			{
				pad.Spawner.Relocate(at, facing, prefab);
				Debug.Log("[supply] field vehicle wrecked or abandoned; a " + prefab.name + " will turn up at " + Describe(at) + " in " + config.fieldRespawnSeconds.ToString("F0") + " s.");
			}
			pad.Spawner.RespawnLater();
		}
		// A boat moves along the shore the same way.
		foreach (Pad pad in shorePads)
		{
			if (pad.Spawner == null || !pad.Spawner.IsSpent)
			{
				continue;
			}
			GameObject prefab = Choose(config.shoreVehicles);
			if (prefab != null && TryMoorAtShore(prefab, pad.Spawner, out Vector3 at, out Quaternion facing))
			{
				pad.Spawner.Relocate(at, facing, prefab);
				Debug.Log("[supply] shore boat wrecked or abandoned; a " + prefab.name + " will be moored at " + Describe(at) + " in " + config.fieldRespawnSeconds.ToString("F0") + " s.");
			}
			pad.Spawner.RespawnLater();
		}
	}

	/// <summary>A new match on the same map: every scattered vehicle gets a new kind and a new place, and the crates start over.</summary>
	private void OnWorldReset()
	{
		if (config == null || NetContext.IsClient)
		{
			return;
		}
		foreach (GameObject crate in crates)
		{
			if (crate != null)
			{
				UnityEngine.Object.Destroy(crate);
			}
		}
		crates.Clear();
		nextCrate = Time.time + 5f;
		Layout(firstMatch: false);
	}

	// ------------------------------------------------------------------------------ crates

	/// <summary>
	/// Keeps <see cref="FieldSupplyConfig.crateCount"/> crates out: one placed every few tenths of a
	/// second until the field is stocked, then each one that runs out replaced somewhere else after
	/// <see cref="FieldSupplyConfig.crateRespawnSeconds"/>.
	/// </summary>
	private void TendCrates()
	{
		int before = crates.Count;
		crates.RemoveAll(crate => crate == null);
		if (crates.Count < before)
		{
			nextCrate = Mathf.Max(nextCrate, Time.time + config.crateRespawnSeconds);
		}
		if (crates.Count >= config.crateCount || Time.time < nextCrate)
		{
			return;
		}
		PlaceCrate();
		nextCrate = Time.time + 0.4f;
	}

	private void PlaceCrate()
	{
		bool medical = random.NextDouble() < config.medicalShare;
		GameObject prefab = medical ? config.medicalCratePrefab : config.ammoCratePrefab;
		if (prefab == null)
		{
			return;
		}
		Bounds area = FieldParking.PlayArea();
		var taken = new Vec3[crates.Count];
		for (int i = 0; i < crates.Count; i++)
		{
			taken[i] = ToVec(crates[i].transform.position);
		}
		float fromFlag = config.crateMinFromFlag * config.crateMinFromFlag;
		for (int attempt = 0; attempt < ParkingAttempts * 4; attempt++)
		{
			var probe = new Vector3(
				Mathf.Lerp(area.min.x, area.max.x, (float)random.NextDouble()),
				0f,
				Mathf.Lerp(area.min.z, area.max.z, (float)random.NextDouble()));
			float yaw = (float)(random.NextDouble() * 360.0);
			if (NearAFlag(probe, fromFlag) || !parking.CanPark(probe, yaw, CrateFootprint, out Vector3 ground)
				|| !FieldSupplyLayout.FarFromAll(ToVec(ground), taken, config.crateSpacing * config.crateSpacing))
			{
				continue;
			}
			GameObject crate = UnityEngine.Object.Instantiate(prefab, ground + Vector3.up * 0.05f, Quaternion.Euler(0f, yaw, 0f));
			// The deployable's Awake throws it forward at its launch speed; a crate stands still.
			Rigidbody body = crate.GetComponent<Rigidbody>();
			if (body != null)
			{
				body.linearVelocity = Vector3.zero;
				body.angularVelocity = Vector3.zero;
			}
			ProjectileNetAnnouncer.AnnounceLaunch(crate.GetComponent<Projectile>(), crate.transform.position, Vector3.zero, null);
			crates.Add(crate);
			return;
		}
		Debug.Log("[supply] no place found for a " + prefab.name + " this time; trying again shortly.");
	}

	private void Layout(bool firstMatch)
	{
		matchNumber++;
		random = new System.Random(FieldSupplyLayout.MatchSeed(DateTime.UtcNow.Ticks, matchNumber, mapScene.name.GetHashCode()));

		padPositions.Clear();
		fieldPositions.Clear();
		foreach (VehicleSpawner authored in UnityEngine.Object.FindObjectsByType<VehicleSpawner>(FindObjectsSortMode.None))
		{
			if (!IsOurs(authored))
			{
				padPositions.Add(ToVec(authored.transform.position));
			}
		}

		var report = new StringBuilder();
		report.Append("[supply] ").Append(mapScene.name).Append(" match ").Append(matchNumber).Append(": flag vehicles");

		int flagIndex = 0;
		foreach (SpawnPoint point in ActorManager.instance.spawnPoints)
		{
			if (point == null || bases.Contains(point))
			{
				continue;
			}
			Pad pad = firstMatch ? null : (flagIndex < flagPads.Count ? flagPads[flagIndex] : null);
			flagIndex++;
			GameObject prefab = Choose(config.flagVehicles);
			Vector3 at = default;
			Quaternion facing = default;
			bool parked = prefab != null && TryParkNearFlag(point, prefab, out at, out facing);
			// The kind drawn did not fit (a helicopter needs a wide clearing): any other kind that does.
			for (int i = 0; !parked && i < config.flagVehicles.Length; i++)
			{
				GameObject other = config.flagVehicles[i].prefab;
				if (other != null && other != prefab && config.flagVehicles[i].weight > 0f && TryParkNearFlag(point, other, out at, out facing))
				{
					prefab = other;
					parked = true;
				}
			}
			if (!parked)
			{
				// Nowhere dry within reach of this flag: a boat on the water beside it, if the map has one.
				prefab = Choose(config.waterVehicles);
				if (prefab == null || !TryMoorNearFlag(point, prefab, out at, out facing))
				{
					report.Append(' ').Append(ShortName(point)).Append("=none");
					continue;
				}
			}
			if (pad == null)
			{
				pad = new Pad { Flag = point, Spawner = CreatePad("Flag Vehicle (" + ShortName(point) + ")", prefab, VehicleSpawner.RespawnType.AfterDestroyed, config.flagRespawnSeconds, at, facing) };
				flagPads.Add(pad);
			}
			else
			{
				pad.Spawner.Relocate(at, facing, prefab);
			}
			padPositions.Add(ToVec(at));
			report.Append(' ').Append(ShortName(point)).Append('=').Append(prefab.name)
				.Append(" (").Append(Vector3.Distance(at, point.transform.position).ToString("F0")).Append(" m)");
		}

		report.Append("; field vehicles");
		for (int i = 0; i < config.fieldVehicleCount; i++)
		{
			Pad pad = !firstMatch && i < fieldPads.Count ? fieldPads[i] : null;
			GameObject prefab = Choose(config.fieldVehicles);
			if (prefab == null || !TryParkInField(prefab, pad != null ? pad.Spawner : null, out Vector3 at, out Quaternion facing))
			{
				report.Append(" none");
				continue;
			}
			if (pad == null)
			{
				pad = new Pad { Spawner = CreatePad("Field Vehicle " + (i + 1), prefab, VehicleSpawner.RespawnType.Never, config.fieldRespawnSeconds, at, facing) };
				fieldPads.Add(pad);
			}
			else
			{
				pad.Spawner.Relocate(at, facing, prefab);
			}
			report.Append(' ').Append(prefab.name).Append(" at ").Append(Describe(at));
		}

		if (config.shoreVehicleCount > 0)
		{
			report.Append("; shore boats");
		}
		for (int i = 0; i < config.shoreVehicleCount; i++)
		{
			Pad pad = !firstMatch && i < shorePads.Count ? shorePads[i] : null;
			GameObject prefab = Choose(config.shoreVehicles);
			if (prefab == null || !TryMoorAtShore(prefab, pad != null ? pad.Spawner : null, out Vector3 at, out Quaternion facing))
			{
				report.Append(" none");
				continue;
			}
			if (pad == null)
			{
				pad = new Pad { Spawner = CreatePad("Shore Boat " + (i + 1), prefab, VehicleSpawner.RespawnType.Never, config.fieldRespawnSeconds, at, facing) };
				shorePads.Add(pad);
			}
			else
			{
				pad.Spawner.Relocate(at, facing, prefab);
			}
			report.Append(' ').Append(prefab.name).Append(" at ").Append(Describe(at));
		}

		Debug.Log(report.ToString());
	}

	private bool IsOurs(VehicleSpawner spawner)
	{
		foreach (Pad pad in flagPads)
		{
			if (pad.Spawner == spawner) return true;
		}
		foreach (Pad pad in fieldPads)
		{
			if (pad.Spawner == spawner) return true;
		}
		foreach (Pad pad in shorePads)
		{
			if (pad.Spawner == spawner) return true;
		}
		return false;
	}

	/// <summary>
	/// A pad from <see cref="FieldSupplyConfig.padPrefab"/>, saved inactive so its
	/// <see cref="VehicleSpawner"/> wakes only once it knows what to spawn.
	/// </summary>
	private VehicleSpawner CreatePad(string name, GameObject prefab, VehicleSpawner.RespawnType respawnType, float spawnTime, Vector3 at, Quaternion facing)
	{
		GameObject host = UnityEngine.Object.Instantiate(config.padPrefab, at, facing);
		host.SetActive(false);
		host.name = name;
		SceneManager.MoveGameObjectToScene(host, mapScene);
		VehicleSpawner spawner = host.GetComponent<VehicleSpawner>();
		spawner.prefab = prefab;
		spawner.respawnType = respawnType;
		spawner.spawnTime = spawnTime;
		host.SetActive(true);
		return spawner;
	}

	private GameObject Choose(FieldSupplyConfig.VehicleChoice[] choices)
	{
		if (choices == null || choices.Length == 0)
		{
			return null;
		}
		Span<float> weights = stackalloc float[choices.Length];
		for (int i = 0; i < choices.Length; i++)
		{
			weights[i] = choices[i].prefab != null ? choices[i].weight : 0f;
		}
		int pick = FieldSupplyLayout.Weighted(weights, random);
		return pick >= 0 ? choices[pick].prefab : null;
	}

	// ------------------------------------------------------------------------- where to park

	private bool TryParkNearFlag(SpawnPoint point, GameObject prefab, out Vector3 at, out Quaternion facing)
	{
		Bounds footprint = VehicleSpawner.FootprintOf(prefab);
		Vector3 centre = point.transform.position;
		for (int attempt = 0; attempt < ParkingAttempts; attempt++)
		{
			float angle = (float)(random.NextDouble() * Math.PI * 2.0);
			float radius = Mathf.Lerp(config.flagRingInner, config.flagRingOuter, (float)random.NextDouble());
			var outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
			Vector3 probe = centre + outward * radius;
			// Nose out, so a crew drives away from the walls rather than into them.
			float yaw = Mathf.Atan2(outward.x, outward.z) * Mathf.Rad2Deg;
			if (!parking.CanPark(probe, yaw, footprint, out Vector3 ground)
				|| !FieldSupplyLayout.FarFromAll(ToVec(ground), padPositions.ToArray(), PadSeparation * PadSeparation))
			{
				continue;
			}
			at = ground + Vector3.up * PadLift;
			facing = Quaternion.Euler(0f, yaw, 0f);
			return true;
		}
		at = default;
		facing = default;
		return false;
	}

	/// <summary>A place on water deep enough for a boat within reach of <paramref name="point"/>.</summary>
	private bool TryMoorNearFlag(SpawnPoint point, GameObject prefab, out Vector3 at, out Quaternion facing)
	{
		Bounds footprint = VehicleSpawner.FootprintOf(prefab);
		Vector3 centre = point.transform.position;
		for (int attempt = 0; attempt < ParkingAttempts; attempt++)
		{
			float angle = (float)(random.NextDouble() * Math.PI * 2.0);
			float radius = Mathf.Lerp(config.flagRingInner * 0.6f, config.flagRingOuter + 20f, (float)random.NextDouble());
			var outward = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
			float yaw = Mathf.Atan2(outward.x, outward.z) * Mathf.Rad2Deg;
			if (!parking.CanMoor(centre + outward * radius, yaw, footprint, out Vector3 surface)
				|| !FieldSupplyLayout.FarFromAll(ToVec(surface), padPositions.ToArray(), PadSeparation * PadSeparation))
			{
				continue;
			}
			at = surface + Vector3.up * 0.3f;
			facing = Quaternion.Euler(0f, yaw, 0f);
			return true;
		}
		at = default;
		facing = default;
		return false;
	}

	private bool TryParkInField(GameObject prefab, VehicleSpawner moving, out Vector3 at, out Quaternion facing)
	{
		Bounds footprint = VehicleSpawner.FootprintOf(prefab);
		Bounds area = FieldParking.PlayArea();
		Vec3[] others = OtherFieldPositions(moving);
		Vec3[] pads = padPositions.ToArray();
		float fromFlag = config.fieldMinFromFlag * config.fieldMinFromFlag;
		for (int attempt = 0; attempt < ParkingAttempts * 4; attempt++)
		{
			var probe = new Vector3(
				Mathf.Lerp(area.min.x, area.max.x, (float)random.NextDouble()),
				0f,
				Mathf.Lerp(area.min.z, area.max.z, (float)random.NextDouble()));
			if (NearAFlag(probe, fromFlag))
			{
				continue;
			}
			float yaw = (float)(random.NextDouble() * 360.0);
			if (!parking.CanPark(probe, yaw, footprint, out Vector3 ground))
			{
				continue;
			}
			Vec3 spot = ToVec(ground);
			if (!FieldSupplyLayout.FarFromAll(spot, others, config.spacing * config.spacing)
				|| !FieldSupplyLayout.FarFromAll(spot, pads, PadSeparation * PadSeparation))
			{
				continue;
			}
			at = ground + Vector3.up * PadLift;
			facing = Quaternion.Euler(0f, yaw, 0f);
			fieldPositions.Add(spot);
			return true;
		}
		at = default;
		facing = default;
		return false;
	}

	/// <summary>
	/// A place along a shore -- water at least <see cref="FieldParking.MinMooringDepth"/> deep with dry
	/// land within <see cref="FieldSupplyConfig.shoreReach"/> -- for a boat, nosed out onto the water,
	/// as far from the other finds as a field vehicle keeps.
	/// </summary>
	/// <remarks>
	/// Points are drawn over the map's lakes and rivers alone (<see cref="WaterLevel.TryGetBoundedArea"/>),
	/// or over the play area on a map with only a sea, and the heightmap test runs before the physics
	/// one: the shore is a thin band, so most points miss it and should cost almost nothing.
	/// </remarks>
	private bool TryMoorAtShore(GameObject prefab, VehicleSpawner moving, out Vector3 at, out Quaternion facing)
	{
		Bounds footprint = VehicleSpawner.FootprintOf(prefab);
		Bounds play = FieldParking.PlayArea();
		Rect area = Rect.MinMaxRect(play.min.x, play.min.z, play.max.x, play.max.z);
		if (WaterLevel.TryGetBoundedArea(out Rect water))
		{
			area = Rect.MinMaxRect(Mathf.Max(area.xMin, water.xMin), Mathf.Max(area.yMin, water.yMin), Mathf.Min(area.xMax, water.xMax), Mathf.Min(area.yMax, water.yMax));
		}
		Vec3[] others = OtherFieldPositions(moving);
		Vec3[] pads = padPositions.ToArray();
		if (area.width > 0f && area.height > 0f)
		{
			for (int attempt = 0; attempt < ShoreAttempts; attempt++)
			{
				var probe = new Vector3(
					Mathf.Lerp(area.xMin, area.xMax, (float)random.NextDouble()),
					0f,
					Mathf.Lerp(area.yMin, area.yMax, (float)random.NextDouble()));
				if (!FieldParking.NearShore(probe, config.shoreReach, out float yaw)
					|| !parking.CanMoor(probe, yaw, footprint, out Vector3 surface))
				{
					continue;
				}
				Vec3 spot = ToVec(surface);
				if (!FieldSupplyLayout.FarFromAll(spot, others, config.spacing * config.spacing)
					|| !FieldSupplyLayout.FarFromAll(spot, pads, PadSeparation * PadSeparation))
				{
					continue;
				}
				at = surface + Vector3.up * 0.3f;
				facing = Quaternion.Euler(0f, yaw, 0f);
				fieldPositions.Add(spot);
				return true;
			}
		}
		at = default;
		facing = default;
		return false;
	}

	private Vec3[] OtherFieldPositions(VehicleSpawner moving)
	{
		var others = new List<Vec3>(fieldPositions);
		foreach (Pad pad in fieldPads)
		{
			if (pad.Spawner != null && pad.Spawner != moving)
			{
				others.Add(ToVec(pad.Spawner.transform.position));
			}
		}
		foreach (Pad pad in shorePads)
		{
			if (pad.Spawner != null && pad.Spawner != moving)
			{
				others.Add(ToVec(pad.Spawner.transform.position));
			}
		}
		return others.ToArray();
	}

	private bool NearAFlag(Vector3 probe, float rangeSquared)
	{
		foreach (SpawnPoint point in ActorManager.instance.spawnPoints)
		{
			if (point == null) continue;
			Vector3 d = point.transform.position - probe;
			d.y = 0f;
			if (d.sqrMagnitude < rangeSquared) return true;
		}
		return false;
	}

	// ------------------------------------------------------------------------------ helpers

	private static Vec3 ToVec(Vector3 v) => new Vec3(v.x, v.y, v.z);

	private static string ShortName(SpawnPoint point) => point.name.Replace(" Capture Point", string.Empty);

	private static string Describe(Vector3 at) => "(" + at.x.ToString("F0") + ", " + at.z.ToString("F0") + ")";
}
