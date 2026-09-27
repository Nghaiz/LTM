using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// One body of water.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Coverage.Everywhere"/> is a sea level</b>: everything in the world below this
/// transform's height is water. It is the default, so every map authored before bounded water
/// existed (Dustbowl, Island) keeps exactly the rule it was built for.
/// </para>
/// <para>
/// <b><see cref="Coverage.SurfaceMesh"/> is a lake or a river</b>: water exists only under the
/// triangles of this object's mesh, at the mesh's own height there, which may slope. A single sea
/// level cannot describe a lake whose surrounding valleys lie lower than its surface -- every one
/// of those dry valleys would count as submerged, and the server's drowning rule would kill a
/// player standing in them.
/// </para>
/// </remarks>
public class WaterLevel : MonoBehaviour
{
	public enum Coverage
	{
		Everywhere,
		SurfaceMesh
	}

	/// <summary>
	/// Height of the loaded map's sea level, or negative infinity when it has none. Gameplay code
	/// asks <see cref="InWater"/>; the field stays public for the editor harness that floods a
	/// test scene by raising it.
	/// </summary>
	/// <remarks>
	/// Cleared when the sea that set it is destroyed. It used to survive the scene change, so a map
	/// without a sea inherited whichever height the previous map had set.
	/// </remarks>
	public static float height = float.NegativeInfinity;

	private static WaterLevel sea;

	private static readonly List<WaterLevel> surfaces = new List<WaterLevel>();

	public Coverage coverage;

	private SurfaceGrid grid;

	public static bool InWater(Vector3 position)
	{
		return Depth(position) >= 0f;
	}

	/// <summary>
	/// How far below the highest water surface over <paramref name="position"/> it lies; negative
	/// when above it, negative infinity when no water covers that spot at all.
	/// </summary>
	public static float Depth(Vector3 position)
	{
		float depth = height - position.y;
		for (int i = 0; i < surfaces.Count; i++)
		{
			float surface = surfaces[i].SurfaceHeight(position);
			if (surface - position.y > depth)
			{
				depth = surface - position.y;
			}
		}
		return depth;
	}

	/// <summary>
	/// Whether this one body covers <paramref name="position"/>. Works in edit mode, where Awake
	/// has not run, for bake-time tools that inspect the scene's water directly.
	/// </summary>
	public bool Contains(Vector3 position)
	{
		return position.y <= SurfaceHeight(position);
	}

	private float SurfaceHeight(Vector3 position)
	{
		if (coverage == Coverage.Everywhere)
		{
			return base.transform.position.y;
		}
		if (grid == null)
		{
			grid = SurfaceGrid.Build(this);
		}
		return grid.HeightAt(position.x, position.z);
	}

	private void Awake()
	{
		if (coverage == Coverage.Everywhere)
		{
			sea = this;
			height = base.transform.position.y;
		}
		else
		{
			grid = SurfaceGrid.Build(this);
		}
	}

	private void OnEnable()
	{
		if (coverage == Coverage.SurfaceMesh && !surfaces.Contains(this))
		{
			surfaces.Add(this);
		}
	}

	private void OnDisable()
	{
		surfaces.Remove(this);
	}

	private void OnDestroy()
	{
		if (sea == this)
		{
			sea = null;
			height = float.NegativeInfinity;
		}
	}

	/// <summary>
	/// The water surface's height sampled on a regular XZ grid, so a query is one array read
	/// rather than a search through the mesh.
	/// </summary>
	/// <remarks>
	/// Built from the mesh once, at load, and never rebuilt: water does not move. The mesh must be
	/// readable in a player build, which is why a failure here is loud rather than an empty grid
	/// that would silently turn the lake into dry land.
	/// </remarks>
	private sealed class SurfaceGrid
	{
		private const float CellSize = 2f;

		private static readonly SurfaceGrid Empty = new SurfaceGrid(0f, 0f, 0, 0);

		private readonly float minX;

		private readonly float minZ;

		private readonly int width;

		private readonly int depth;

		private readonly float[] heights;

		private SurfaceGrid(float minX, float minZ, int width, int depth)
		{
			this.minX = minX;
			this.minZ = minZ;
			this.width = width;
			this.depth = depth;
			heights = new float[width * depth];
			for (int i = 0; i < heights.Length; i++)
			{
				heights[i] = float.NegativeInfinity;
			}
		}

		public float HeightAt(float x, float z)
		{
			int cx = Mathf.FloorToInt((x - minX) / CellSize);
			int cz = Mathf.FloorToInt((z - minZ) / CellSize);
			if (cx < 0 || cz < 0 || cx >= width || cz >= depth)
			{
				return float.NegativeInfinity;
			}
			return heights[cz * width + cx];
		}

		public static SurfaceGrid Build(WaterLevel body)
		{
			MeshFilter filter = body.GetComponent<MeshFilter>();
			Mesh mesh = (filter != null) ? filter.sharedMesh : null;
			if (mesh == null || !mesh.isReadable)
			{
				Debug.LogError("WaterLevel '" + body.name + "' covers its surface mesh, but has no readable mesh -- this water will not exist.", body);
				return Empty;
			}
			Matrix4x4 toWorld = body.transform.localToWorldMatrix;
			Vector3[] vertices = mesh.vertices;
			for (int i = 0; i < vertices.Length; i++)
			{
				vertices[i] = toWorld.MultiplyPoint3x4(vertices[i]);
			}
			Bounds bounds = new Bounds(vertices[0], Vector3.zero);
			for (int i = 1; i < vertices.Length; i++)
			{
				bounds.Encapsulate(vertices[i]);
			}
			int width = Mathf.CeilToInt(bounds.size.x / CellSize) + 1;
			int depth = Mathf.CeilToInt(bounds.size.z / CellSize) + 1;
			SurfaceGrid surfaceGrid = new SurfaceGrid(bounds.min.x, bounds.min.z, width, depth);
			int[] triangles = mesh.triangles;
			for (int i = 0; i < triangles.Length; i += 3)
			{
				surfaceGrid.Rasterize(vertices[triangles[i]], vertices[triangles[i + 1]], vertices[triangles[i + 2]]);
			}
			return surfaceGrid;
		}

		/// <summary>
		/// Writes the triangle's height at every cell centre it covers, keeping the higher surface
		/// where two triangles overlap.
		/// </summary>
		private void Rasterize(Vector3 a, Vector3 b, Vector3 c)
		{
			float area = (b.x - a.x) * (c.z - a.z) - (c.x - a.x) * (b.z - a.z);
			if (Mathf.Abs(area) < 1E-06f)
			{
				return;
			}
			int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, Mathf.Min(b.x, c.x)) - minX) / CellSize));
			int x1 = Mathf.Min(width - 1, Mathf.CeilToInt((Mathf.Max(a.x, Mathf.Max(b.x, c.x)) - minX) / CellSize));
			int z0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.z, Mathf.Min(b.z, c.z)) - minZ) / CellSize));
			int z1 = Mathf.Min(depth - 1, Mathf.CeilToInt((Mathf.Max(a.z, Mathf.Max(b.z, c.z)) - minZ) / CellSize));
			for (int cz = z0; cz <= z1; cz++)
			{
				float pz = minZ + (cz + 0.5f) * CellSize;
				for (int cx = x0; cx <= x1; cx++)
				{
					float px = minX + (cx + 0.5f) * CellSize;
					float wa = ((b.x - px) * (c.z - pz) - (c.x - px) * (b.z - pz)) / area;
					float wb = ((c.x - px) * (a.z - pz) - (a.x - px) * (c.z - pz)) / area;
					float wc = 1f - wa - wb;
					if (wa < -0.001f || wb < -0.001f || wc < -0.001f)
					{
						continue;
					}
					float y = wa * a.y + wb * b.y + wc * c.y;
					int index = cz * width + cx;
					if (y > heights[index])
					{
						heights[index] = y;
					}
				}
			}
		}
	}
}
