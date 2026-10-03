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
/// the water, clear of trees, with nothing solid in the box the body will fill, and beside a walkable
/// pathfinding node so bots can reach it. A boat needs <see cref="MinMooringDepth"/> of water under the
/// whole hull and nothing solid where it floats.
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

	/// <summary>
	/// Whether a vehicle with <paramref name="footprint"/> can stand at <paramref name="probe"/> facing
	/// <paramref name="yaw"/>; <paramref name="ground"/> is the terrain under its centre.
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

		// Somewhere a bot can walk to.
		if (AstarPath.active != null)
		{
			Pathfinding.NNInfo nearest = AstarPath.active.GetNearest(ground, Pathfinding.NNConstraint.Default);
			if (nearest.node == null || !nearest.node.Walkable || (nearest.clampedPosition - ground).sqrMagnitude > 25f)
			{
				return false;
			}
		}
		return true;
	}

	// ------------------------------------------------------------------------------- trees

	private void IndexTrees()
	{
		trees.Clear();
		foreach (Terrain terrain in Terrain.activeTerrains)
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
