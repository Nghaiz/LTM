using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Where a vehicle can stand, or a boat float (phase P32): the physics and terrain tests
/// <see cref="FieldSupplyDirector"/> runs before it moves a pad somewhere.
/// </summary>
/// <remarks>
/// A place on land must have the terrain itself under the footprint's centre and four corners -- a
/// rock, a wall, a bridge or a parked vehicle under any of them is refused -- within
/// <see cref="FieldSupplyConfig.maxStep"/> and <see cref="FieldSupplyConfig.maxSlopeDegrees"/>, out of
/// the water, clear of trees, with nothing solid in the box the body will fill, on both the foot and the
/// car pathfinding graphs so bots can reach it and drive it, and with open ground straight ahead of its
/// nose to drive out on (<see cref="FieldSupplyConfig.exitLaneMetres"/>). A boat needs
/// <see cref="MinMooringDepth"/> of water under the whole hull and nothing solid where it floats.
/// </remarks>
public sealed class FieldParking
{
	/// <summary>Size of a tree-lookup cell, in metres.</summary>
	private const float TreeCell = 16f;

	/// <summary>Metres of water a boat is moored over, everywhere under its hull.</summary>
	public const float MinMooringDepth = 1.5f;

	/// <summary>Everything a parked vehicle could land on or in: not hitboxes, ragdolls, seats or actors.</summary>
	private static readonly int ParkingMask = ~((1 << 1) | (1 << 2) | (1 << 5) | (1 << 8) | (1 << 9) | (1 << 10) | (1 << 11) | (1 << 13) | (1 << 14) | (1 << 16) | (1 << 17));

	/// <summary><see cref="ParkingMask"/> without the water layer: what a hull must not touch.</summary>
	private static readonly int GroundOnlyMask = ParkingMask & ~(1 << 4);

	private readonly FieldSupplyConfig config;

	private readonly Dictionary<long, List<Vector3>> trees = new Dictionary<long, List<Vector3>>();

	private readonly Collider[] overlap = new Collider[8];

	/// <summary>Terrain trees the clearance tests know about. Zero on a wooded map is the defect <see cref="IndexTrees"/> records.</summary>
	public int IndexedTrees { get; private set; }

	public FieldParking(FieldSupplyConfig config)
	{
		this.config = config;
		IndexTrees();
	}

	/// <summary>Water deep enough under every corner of the hull, and nothing solid where it floats.</summary>
	public bool CanMoor(Vector3 probe, float yaw, Bounds footprint, out Vector3 surface)
	{
		surface = default;
		Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
		Vector3 c = footprint.center;
		Vector3 e = footprint.extents;
		float level = float.NegativeInfinity;
		for (int i = 0; i < 5; i++)
		{
			Vector3 local = i == 0 ? new Vector3(c.x, 0f, c.z)
				: new Vector3(c.x + ((i & 1) == 0 ? e.x : -e.x), 0f, c.z + (i < 3 ? e.z : -e.z));
			Vector3 at = probe + turn * local;
			float water = Mathf.Max(WaterLevel.height, WaterLevel.BoundedSurfaceAt(at.x, at.z));
			if (float.IsNegativeInfinity(water)
				|| !Physics.Raycast(new Vector3(at.x, 2000f, at.z), Vector3.down, out RaycastHit hit, 4000f, GroundOnlyMask, QueryTriggerInteraction.Ignore)
				|| !(hit.collider is TerrainCollider) || water - hit.point.y < MinMooringDepth)
			{
				return false;
			}
			if (i == 0)
			{
				level = water;
				surface = new Vector3(at.x, water, at.z);
			}
		}
		Vector3 boxCentre = new Vector3(surface.x, level + e.y + 0.3f, surface.z);
		int found = Physics.OverlapBoxNonAlloc(boxCentre, e, overlap, turn, GroundOnlyMask, QueryTriggerInteraction.Ignore);
		for (int i = 0; i < found; i++)
		{
			if (!(overlap[i] is TerrainCollider))
			{
				return false;
			}
		}
		return true;
	}

	/// <summary>Bearings round a mooring tested for dry land, on each of <see cref="ShoreRings"/> rings.</summary>
	private const int ShoreBearings = 16;

	private const int ShoreRings = 4;

	/// <summary>
	/// Whether <paramref name="probe"/> is water at least <see cref="MinMooringDepth"/> deep with dry
	/// land within <paramref name="reach"/> metres -- a lake's edge or an island's, not open water --
	/// and the heading, in degrees, that points away from the nearest of that land, so a boat moored
	/// there noses out onto the water.
	/// </summary>
	/// <remarks>
	/// Read off the terrain's heightmap and the water's own surface, nearest ring first, so the cheap
	/// test can run over many random points before <see cref="CanMoor"/>'s physics checks the hull.
	/// </remarks>
	public static bool NearShore(Vector3 probe, float reach, out float awayYaw)
	{
		awayYaw = 0f;
		if (!(reach > 0f))
		{
			return false;
		}
		float water = WaterSurfaceAt(probe.x, probe.z);
		if (float.IsNegativeInfinity(water))
		{
			return false;
		}
		Terrain terrain = FindTerrain();
		if (water - GroundAt(terrain, probe.x, probe.z) < MinMooringDepth)
		{
			return false;
		}
		for (int ring = 1; ring <= ShoreRings; ring++)
		{
			float radius = reach * ring / ShoreRings;
			for (int bearing = 0; bearing < ShoreBearings; bearing++)
			{
				float angle = bearing * (Mathf.PI * 2f / ShoreBearings);
				float x = probe.x + Mathf.Cos(angle) * radius;
				float z = probe.z + Mathf.Sin(angle) * radius;
				float surface = WaterSurfaceAt(x, z);
				if (float.IsNegativeInfinity(surface) || GroundAt(terrain, x, z) > surface)
				{
					awayYaw = Mathf.Atan2(-Mathf.Cos(angle), -Mathf.Sin(angle)) * Mathf.Rad2Deg;
					return true;
				}
			}
		}
		return false;
	}

	/// <summary>The highest water surface over (x, z): the sea or a lake or river; negative infinity where there is none.</summary>
	private static float WaterSurfaceAt(float x, float z) => Mathf.Max(WaterLevel.height, WaterLevel.BoundedSurfaceAt(x, z));

	/// <summary>
	/// The scene's terrain for heightmap reads, or null with none to read. Not
	/// <c>Terrain.activeTerrain</c> alone: on the dedicated server it answered nothing and no boat was
	/// ever moored (v4.5.0, <c>[supply] ... shore boats none</c> on every match), while the same code
	/// moored one every time in practice. <c>MinimapCamera.TryGetGroundExtent</c> finds terrains the
	/// same way for the reason it gives: the active list fills only as each terrain enables.
	/// </summary>
	/// <remarks>
	/// The heightmap is used only where it agrees with the terrain's collider, checked at the
	/// terrain's centre once a frame: the collider is what <see cref="CanPark"/> and
	/// <see cref="CanMoor"/> stand on, proven on the server, and a heightmap that read differently
	/// there would silently find no shore at all.
	/// </remarks>
	private static Terrain FindTerrain()
	{
		if (heightmapCheckedFrame == Time.frameCount)
		{
			return heightmapTerrain;
		}
		heightmapCheckedFrame = Time.frameCount;
		heightmapTerrain = null;
		Terrain found = Terrain.activeTerrain;
		if (found == null || found.terrainData == null)
		{
			found = null;
			foreach (Terrain terrain in Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None))
			{
				if (terrain.terrainData != null)
				{
					found = terrain;
					break;
				}
			}
		}
		if (found != null)
		{
			Vector3 centre = found.GetPosition() + found.terrainData.size * 0.5f;
			float fromHeightmap = found.SampleHeight(centre) + found.GetPosition().y;
			bool hit = Physics.Raycast(new Vector3(centre.x, 2000f, centre.z), Vector3.down, out RaycastHit ground, 4000f, GroundOnlyMask, QueryTriggerInteraction.Ignore)
				&& ground.collider is TerrainCollider;
			if (!hit || Mathf.Abs(ground.point.y - fromHeightmap) <= HeightmapTolerance)
			{
				heightmapTerrain = found;
			}
		}
		return heightmapTerrain;
	}

	/// <summary>Metres the heightmap and the collider may disagree by before the collider is trusted instead.</summary>
	private const float HeightmapTolerance = 1f;

	private static int heightmapCheckedFrame = -1;

	private static Terrain heightmapTerrain;

	/// <summary>
	/// The ground's height at (x, z): the heightmap when there is a terrain, else straight down onto
	/// the terrain's collider, the ground the vehicle tests stand on; negative infinity over nothing.
	/// </summary>
	private static float GroundAt(Terrain terrain, float x, float z)
	{
		if (terrain != null)
		{
			return terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.GetPosition().y;
		}
		return Physics.Raycast(new Vector3(x, 2000f, z), Vector3.down, out RaycastHit hit, 4000f, GroundOnlyMask, QueryTriggerInteraction.Ignore)
			&& hit.collider is TerrainCollider
				? hit.point.y
				: float.NegativeInfinity;
	}

	/// <summary>Which ground <see cref="NearShore"/> reads, for the director's report when no mooring is found.</summary>
	public static string ShoreGroundSource()
	{
		Terrain terrain = FindTerrain();
		return terrain != null
			? "heightmap of '" + terrain.name + "'" + (Terrain.activeTerrain == null ? " (not the active terrain)" : string.Empty)
			: "terrain collider (no terrain whose heightmap matches it)";
	}

	/// <summary>The play volume, shrunk by a margin; the terrain's bounds on a map without one.</summary>
	public static Bounds PlayArea()
	{
		Bounds area;
		if (LevelBounds.instance != null)
		{
			area = LevelBounds.instance.WorldBox;
		}
		else
		{
			Terrain terrain = Terrain.activeTerrain;
			area = terrain != null
				? new Bounds(terrain.GetPosition() + terrain.terrainData.size * 0.5f, terrain.terrainData.size)
				: new Bounds(Vector3.zero, Vector3.one * 2000f);
		}
		area.Expand(new Vector3(-80f, 0f, -80f));
		return area;
	}

	/// <summary>Headings tried at one place, <see cref="FacingStep"/> degrees apart.</summary>
	private const int Facings = 8;

	private const float FacingStep = 360f / Facings;

	/// <summary>Metres between the samples along an exit lane.</summary>
	private const float LaneStep = 3f;

	/// <summary>Largest rise or drop between two lane samples, in metres: a bank no vehicle takes.</summary>
	private const float LaneMaxStep = 2f;

	/// <summary>
	/// Whether a vehicle can stand at <paramref name="probe"/> facing some heading with a way out
	/// ahead of it, trying <paramref name="preferredYaw"/> first and then the others round the compass;
	/// <paramref name="yaw"/> is the heading found.
	/// </summary>
	public bool TryPark(Vector3 probe, float preferredYaw, Bounds footprint, out Vector3 ground, out float yaw)
	{
		for (int i = 0; i < Facings; i++)
		{
			// preferred, then +45, -45, +90, -90 ... so the first heading found stays near the wish.
			int step = (i + 1) / 2;
			yaw = preferredYaw + (i % 2 == 1 ? step : -step) * FacingStep;
			if (CanPark(probe, yaw, footprint, out ground))
			{
				return true;
			}
		}
		ground = default;
		yaw = preferredYaw;
		return false;
	}

	/// <summary>
	/// Whether a vehicle with <paramref name="footprint"/> can stand at <paramref name="probe"/> facing
	/// <paramref name="yaw"/>, and drive away from there; <paramref name="ground"/> is the terrain
	/// under its centre.
	/// </summary>
	public bool CanPark(Vector3 probe, float yaw, Bounds footprint, out Vector3 ground)
	{
		ground = default;
		Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
		Vector3 c = footprint.center;
		Vector3 e = footprint.extents;
		float minSlopeY = Mathf.Cos(config.maxSlopeDegrees * Mathf.Deg2Rad);
		float lowest = float.PositiveInfinity;
		float highest = float.NegativeInfinity;

		for (int i = 0; i < 5; i++)
		{
			Vector3 local = i == 0 ? new Vector3(c.x, 0f, c.z)
				: new Vector3(c.x + ((i & 1) == 0 ? e.x : -e.x), 0f, c.z + (i < 3 ? e.z : -e.z));
			Vector3 at = probe + turn * local;
			if (!Physics.Raycast(new Vector3(at.x, 2000f, at.z), Vector3.down, out RaycastHit hit, 4000f, ParkingMask, QueryTriggerInteraction.Ignore)
				|| !(hit.collider is TerrainCollider) || hit.normal.y < minSlopeY)
			{
				return false;
			}
			lowest = Mathf.Min(lowest, hit.point.y);
			highest = Mathf.Max(highest, hit.point.y);
			if (i == 0) ground = hit.point;
		}

		if (highest - lowest > config.maxStep || WaterLevel.InWater(ground + Vector3.up * 0.3f) || NearATree(ground, config.treeClearance + Mathf.Max(e.x, e.z)))
		{
			return false;
		}

		// Nothing solid in the box the body will fill, lifted clear of the slope under it.
		Vector3 boxCentre = new Vector3(ground.x, highest + e.y + 0.3f, ground.z) + turn * new Vector3(c.x, 0f, c.z);
		int found = Physics.OverlapBoxNonAlloc(boxCentre, e, overlap, turn, ParkingMask, QueryTriggerInteraction.Ignore);
		for (int i = 0; i < found; i++)
		{
			if (!(overlap[i] is TerrainCollider))
			{
				return false;
			}
		}

		// Somewhere a bot can walk to, and drive from: on the foot graph and on the car graph, which
		// is the one a bot driver paths on (AiActorController.Goto, graph mask 4). A place reached on
		// foot but off the car graph is a vehicle no bot can take anywhere.
		if (!OnGraph(ground, FootGraphMask) || !OnGraph(ground, CarGraphMask))
		{
			return false;
		}
		return HasWayOut(ground, turn, footprint);
	}

	/// <summary>The foot and car graphs' masks (<c>AiActorController.Goto</c>).</summary>
	private const int FootGraphMask = 1;

	private const int CarGraphMask = 4;

	private static bool OnGraph(Vector3 ground, int graphMask)
	{
		if (AstarPath.active == null)
		{
			return true;
		}
		Pathfinding.NNConstraint constraint = Pathfinding.NNConstraint.Default;
		constraint.graphMask = graphMask;
		Pathfinding.NNInfo nearest = AstarPath.active.GetNearest(ground, constraint);
		return nearest.node != null && nearest.node.Walkable && (nearest.clampedPosition - ground).sqrMagnitude <= 25f;
	}

	/// <summary>
	/// Whether the lane straight ahead of the nose, <see cref="FieldSupplyConfig.exitLaneMetres"/>
	/// long and the vehicle's width plus <see cref="FieldSupplyConfig.exitLaneMargin"/> either side,
	/// is open ground: terrain under every sample no steeper than
	/// <see cref="FieldSupplyConfig.maxLaneSlopeDegrees"/>, no step over <see cref="LaneMaxStep"/>,
	/// no water, no tree, and nothing solid standing in it.
	/// </summary>
	/// <remarks>
	/// The parking tests alone found a clearing a vehicle fits in, and the v4.5.0 vehicles were
	/// often in exactly that: a jeep in a gap among trees, nosed at a trunk, that a player had to
	/// back and fill out of and a bot never got out of. Owner, 2026-10-08.
	/// </remarks>
	private bool HasWayOut(Vector3 ground, Quaternion turn, Bounds footprint)
	{
		if (config.exitLaneMetres <= 0f)
		{
			return true;
		}
		Vector3 c = footprint.center;
		Vector3 e = footprint.extents;
		Vector3 forward = turn * Vector3.forward;
		float halfWidth = e.x + config.exitLaneMargin;
		float minSlopeY = Mathf.Cos(config.maxLaneSlopeDegrees * Mathf.Deg2Rad);
		Vector3 nose = ground + turn * new Vector3(c.x, 0f, c.z + e.z);
		float previous = ground.y;
		float top = ground.y;

		for (float along = LaneStep; along <= config.exitLaneMetres + 0.01f; along += LaneStep)
		{
			Vector3 at = nose + forward * along;
			if (!Physics.Raycast(new Vector3(at.x, 2000f, at.z), Vector3.down, out RaycastHit hit, 4000f, ParkingMask, QueryTriggerInteraction.Ignore)
				|| !(hit.collider is TerrainCollider) || hit.normal.y < minSlopeY
				|| Mathf.Abs(hit.point.y - previous) > LaneMaxStep
				|| WaterLevel.InWater(hit.point + Vector3.up * 0.3f)
				|| NearATree(hit.point, halfWidth))
			{
				return false;
			}
			previous = hit.point.y;
			top = Mathf.Max(top, hit.point.y);
		}

		// Nothing solid standing in the lane: a rock, a wall, a hedgehog, a parked vehicle.
		Vector3 laneCentre = nose + forward * (config.exitLaneMetres * 0.5f);
		laneCentre.y = top + e.y + 0.4f;
		var laneHalf = new Vector3(halfWidth, e.y, config.exitLaneMetres * 0.5f);
		int found = Physics.OverlapBoxNonAlloc(laneCentre, laneHalf, overlap, turn, ParkingMask, QueryTriggerInteraction.Ignore);
		for (int i = 0; i < found; i++)
		{
			if (!(overlap[i] is TerrainCollider))
			{
				return false;
			}
		}
		return true;
	}

	// ------------------------------------------------------------------------------- trees

	/// <remarks>
	/// <b>Every terrain in the scene, not <c>Terrain.activeTerrains</c></b>: a dedicated server's
	/// map has its Terrain components switched off (it renders nothing), so the active list is
	/// empty there and this indexed no tree at all. Every tree clearance test then passed, and the
	/// live servers placed flag and field vehicles in the middle of Forest Lake's woods, the
	/// owner's report of 2026-10-08 ("vehicles spawned at random are often stuck among trees").
	/// The same switched-off terrain hid the shore from the boat's mooring until #562. The trees
	/// still collide there: they are the TerrainCollider's, which stays on.
	/// </remarks>
	private void IndexTrees()
	{
		trees.Clear();
		IndexedTrees = 0;
		foreach (Terrain terrain in Object.FindObjectsByType<Terrain>(FindObjectsInactive.Include, FindObjectsSortMode.None))
		{
			if (terrain == null || terrain.terrainData == null) continue;
			Vector3 origin = terrain.GetPosition();
			Vector3 size = terrain.terrainData.size;
			foreach (TreeInstance tree in terrain.terrainData.treeInstances)
			{
				Vector3 at = origin + Vector3.Scale(tree.position, size);
				long key = CellKey(at.x, at.z);
				if (!trees.TryGetValue(key, out List<Vector3> cell))
				{
					cell = new List<Vector3>();
					trees.Add(key, cell);
				}
				cell.Add(at);
				IndexedTrees++;
			}
		}
	}

	private bool NearATree(Vector3 point, float range)
	{
		int reach = Mathf.CeilToInt(range / TreeCell);
		int cx = Mathf.FloorToInt(point.x / TreeCell);
		int cz = Mathf.FloorToInt(point.z / TreeCell);
		float rangeSquared = range * range;
		for (int x = cx - reach; x <= cx + reach; x++)
		{
			for (int z = cz - reach; z <= cz + reach; z++)
			{
				if (!trees.TryGetValue(Key(x, z), out List<Vector3> cell)) continue;
				foreach (Vector3 tree in cell)
				{
					float dx = tree.x - point.x, dz = tree.z - point.z;
					if (dx * dx + dz * dz < rangeSquared) return true;
				}
			}
		}
		return false;
	}

	private static long CellKey(float x, float z) => Key(Mathf.FloorToInt(x / TreeCell), Mathf.FloorToInt(z / TreeCell));

	private static long Key(int x, int z) => ((long)x << 32) ^ (uint)z;
}
