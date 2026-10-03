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

		if (_waterTag < 0)
		{
			Debug.LogWarning("[ai] this map's A* settings name no \"" + WaterTagName + "\" tag, so bots "
				+ "path across deep water as if it were ground.");
			return;
		}

		NavGraph[] graphs = astar.graphs;
		uint tag = (uint)_waterTag;
		foreach (int index in LandGraphs)
		{
			if (graphs == null || index >= graphs.Length || graphs[index] == null) continue;

			graphs[index].GetNodes(node =>
			{
				if (IsSwimmingDeep((Vector3)node.position))
				{
					node.Tag = tag;
					TaggedNodes++;
				}
				return true;
			});
		}

		if (TaggedNodes > 0)
		{
			Debug.Log("[ai] " + TaggedNodes + " path node(s) under swimming-deep water tagged \""
				+ WaterTagName + "\"; bots on foot and in land vehicles path around it.");
		}
	}

	/// <summary>Whether a body standing at <paramref name="ground"/> would be swimming.</summary>
	public static bool IsSwimmingDeep(Vector3 ground)
	{
		float surface = MovementCore.SurfaceAt(ground.x, ground.z);
		return !float.IsNegativeInfinity(surface) && surface - ground.y >= MovementCore.SwimStartDepth;
	}
}
