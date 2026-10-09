using System;
using Ironfront.Net.Replication.Movement;
using Pathfinding;
using UnityEngine;

/// <summary>
/// Marks the ground under swimming-deep water with the scene's own "Water" A* tag and makes bots on
/// foot and in land vehicles pay to cross it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it exists.</b> Forest Lake's recast graph runs across its lakebed: 637 walkable nodes of
/// the foot graph lie under the lake, 542 of them deeper than a body stands, and none carries the
/// "Water" tag the project already defines (they are "Basic Ground" or "Car Ground"). A bot's
/// seeker gives every tag a penalty of zero, so a straight line through the lake was the cheapest
/// path and bots swam across it several times a match (playtest 2026-10-03, item 3a). Island and
/// Dustbowl are unaffected: their graphs stop at the shore.
/// </para>
/// <para>
/// <b>A penalty, not a wall.</b> A bot whose goal or start is in the water, or which has no dry
/// route at all, still swims; one with a dry route takes it.
/// </para>
/// <para>
/// <b>A triangle is judged by its whole area (2026-10-08).</b> A node was water when its centre was,
/// and the recast graph spans the bay north of Forest Lake's Island with triangles whose centres
/// stand on the bank: a path ran straight across the bay through none tagged, every long swim of
/// three bot soaks was on it, and so were the five drownings of the v4.5.0 playtest. A triangle is
/// now sampled over its area (<see cref="WaterShare"/>): half or more under water is tagged, and a
/// shore triangle less under water costs a share of the water penalty.
/// </para>
/// <para>
/// <b>The mesh can float over the lake.</b> Those bay triangles do not even lie on the lakebed: their
/// corners stand 62 to 67 m up while the terrain under them falls to 55 m, six metres under the
/// surface (61.4 m). So a sample is judged by the ground a body would really stand on: the mesh's
/// own height where that is under water; where it is not but the terrain is, a short cast down from
/// the mesh looks for a bridge or pier holding it up, and finding none, the sample is water.
/// </para>
/// </remarks>
public static class WaterPathTags
{
	private const string WaterTagName = "Water";

	/// <summary>
	/// What entering one deep-water node costs: about a kilometre of dry ground (A* path costs are
	/// millimetres), more than any walk round Forest Lake's lake, so a bot with a dry route takes it.
	/// </summary>
	/// <remarks>
	/// It was a hundred metres, on the reasoning that a crossing enters many nodes. It enters few:
	/// the recast graph spans the lake with large triangles, and a node is water only when its
	/// centre is, so a crossing could cost two hundred metres of penalty against a three-hundred
	/// metre walk to a bridge. A bot soak on Forest Lake still counted 16 long swims, most of them
	/// to or from Island, which has a bridge on two sides.
	/// </remarks>
	public const int WaterPenalty = 1000000;

	// The graphs bodies on foot (0) and land vehicles (2) path on; the boat graph (1) is all water.
	private static readonly int[] LandGraphs = { 0, 2 };

	private static AstarPath _taggedFor;

	private static int _waterTag = -1;

	/// <summary>Nodes tagged on the current map, for the log and tests.</summary>
	public static int TaggedNodes { get; private set; }

	/// <summary>Nodes partly over deep water on the current map, given a share of the penalty.</summary>
	public static int ShoreNodes { get; private set; }

	private static readonly Func<float, float, float, bool> DeepAt = IsDeepUnderMesh;

	/// <summary>
	/// What a body can stand on above water: not the water itself, not people, seats or vehicles.
	/// </summary>
	private static readonly int StandMask = ~((1 << 1) | (1 << 2) | (1 << 4) | (1 << 5) | (1 << 8) | (1 << 9) | (1 << 10)
		| (1 << 11) | (1 << 12) | (1 << 13) | (1 << 14) | (1 << 16) | (1 << 17));

	/// <summary>Metres above the mesh a cast for what holds it up starts.</summary>
	private const float StandProbeAbove = 1f;

	/// <summary>
	/// Tags the current map's deep-water nodes once, then applies the water penalty to
	/// <paramref name="seeker"/> unless it is steering a boat.
	/// </summary>
	public static void Apply(Seeker seeker, bool steeringBoat)
	{
		EnsureTagged();
		if (_waterTag < 0 || seeker == null || seeker.tagPenalties == null) return;
		if (_waterTag >= seeker.tagPenalties.Length) return;

		seeker.tagPenalties[_waterTag] = steeringBoat ? 0 : WaterPenalty;
	}

	private static void EnsureTagged()
	{
		AstarPath astar = AstarPath.active;
		if (astar == null || ReferenceEquals(astar, _taggedFor)) return;

		_taggedFor = astar;
		_waterTag = System.Array.IndexOf(astar.GetTagNames(), WaterTagName);
		TaggedNodes = 0;
		ShoreNodes = 0;

		if (_waterTag < 0)
		{
			Debug.LogWarning("[ai] this map's A* settings name no \"" + WaterTagName + "\" tag, so bots "
				+ "path across deep water as if it were ground.");
			return;
		}

		var clock = System.Diagnostics.Stopwatch.StartNew();
		NavGraph[] graphs = astar.graphs;
		uint tag = (uint)_waterTag;
		bool bounded = WaterLevel.TryGetBoundedArea(out Rect lakes);
		float seaLine = MovementCore.WaterHeight - MovementCore.SwimStartDepth;
		foreach (int index in LandGraphs)
		{
			if (graphs == null || index >= graphs.Length || graphs[index] == null) continue;

			graphs[index].GetNodes(node =>
			{
				float share = DeepShare(node, bounded, lakes, seaLine);
				if (share >= WaterShare.TagShare || IsSwimmingDeep((Vector3)node.position))
				{
					node.Tag = tag;
					TaggedNodes++;
				}
				else if (share > 0f)
				{
					node.Penalty += WaterShare.ShorePenalty(share, WaterPenalty);
					ShoreNodes++;
				}
				return true;
			});
		}

		if (TaggedNodes > 0 || ShoreNodes > 0)
		{
			Debug.Log("[ai] " + TaggedNodes + " path node(s) under swimming-deep water tagged \""
				+ WaterTagName + "\" and " + ShoreNodes + " partly over it given a share of its cost; "
				+ "bots on foot and in land vehicles path around it (" + clock.ElapsedMilliseconds + " ms).");
		}
	}

	/// <summary>
	/// The share of <paramref name="node"/>'s triangle over swimming-deep water; zero, unsampled, for
	/// one that cannot reach any (outside the lakes' rectangle and above the sea), and for a node
	/// that is not a triangle.
	/// </summary>
	private static float DeepShare(GraphNode node, bool bounded, Rect lakes, float seaLine)
	{
		if (!(node is TriangleMeshNode triangle)) return 0f;

		Vector3 a = (Vector3)triangle.GetVertex(0);
		Vector3 b = (Vector3)triangle.GetVertex(1);
		Vector3 c = (Vector3)triangle.GetVertex(2);

		bool belowSea = Mathf.Min(a.y, Mathf.Min(b.y, c.y)) <= seaLine;
		bool overLakes = bounded
			&& Mathf.Max(a.x, Mathf.Max(b.x, c.x)) >= lakes.xMin && Mathf.Min(a.x, Mathf.Min(b.x, c.x)) <= lakes.xMax
			&& Mathf.Max(a.z, Mathf.Max(b.z, c.z)) >= lakes.yMin && Mathf.Min(a.z, Mathf.Min(b.z, c.z)) <= lakes.yMax;
		if (!belowSea && !overLakes) return 0f;

		return WaterShare.OfTriangle(new Vec3(a.x, a.y, a.z), new Vec3(b.x, b.y, b.z), new Vec3(c.x, c.y, c.z), DeepAt);
	}

	/// <summary>Whether a body standing at <paramref name="ground"/> would be swimming.</summary>
	public static bool IsSwimmingDeep(Vector3 ground)
	{
		float surface = MovementCore.SurfaceAt(ground.x, ground.z);
		return !float.IsNegativeInfinity(surface) && surface - ground.y >= MovementCore.SwimStartDepth;
	}

	/// <summary>
	/// Whether a body would swim at (<paramref name="x"/>, <paramref name="z"/>), where the path mesh
	/// claims ground at <paramref name="meshY"/>: the mesh is under water, or the terrain is and
	/// nothing solid holds the mesh up above it.
	/// </summary>
	private static bool IsDeepUnderMesh(float x, float meshY, float z)
	{
		float surface = MovementCore.SurfaceAt(x, z);
		if (float.IsNegativeInfinity(surface)) return false;

		float swimLine = surface - MovementCore.SwimStartDepth;
		if (meshY <= swimLine) return true;

		// Ground at or above the terrain is all a body stands on, so dry terrain is a dry sample.
		if (!Ironfront.Net.Unity.TerrainSurface.TryGetHeight(new Vector3(x, 0f, z), out float terrain) || terrain > swimLine)
		{
			return false;
		}

		// The terrain is under water and the mesh is not: a bridge or a pier holds it up, or nothing does.
		float top = meshY + StandProbeAbove;
		return !Physics.Raycast(new Vector3(x, top, z), Vector3.down, top - swimLine, StandMask, QueryTriggerInteraction.Ignore);
	}
}
