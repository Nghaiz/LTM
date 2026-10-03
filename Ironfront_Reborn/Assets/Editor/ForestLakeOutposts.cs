using System.Collections.Generic;
using System.Text;
using Pathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ironfront
{
	/// <summary>
	/// Builds Forest Lake's outposts from one blueprint: every neutral flag becomes a walled firebase
	/// laid out on the axis toward the middle of the map, and each HQ gets a tidy supply yard on its
	/// own concrete pad. Supply caches (<see cref="SupplyCache"/>) serve whichever side holds the flag.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Owner rulings 2026-10-03.</b> The flags were bare ground; a first pass scattered props round
	/// them at random angles and the owner rejected it: crates on the HQ parking pad, a sandbag wall
	/// across the spawn grid, barrels round the helicopter, pieces in the middle of the walkways. This
	/// version places nothing by chance. Each piece has a fixed slot in a blueprint and a yaw on the
	/// base's own axes, the same at every flag; a slot that cannot take its piece stays empty.
	/// </para>
	/// <para>
	/// <b>A firebase, in its own frame</b> (z toward the middle of the map, x to the right): the spawn
	/// plaza round the flag stays open, and four walkways run straight from it to four gates in an
	/// octagonal hesco wall. The front gate stands under a concrete gate frame like the HQs'; every gate
	/// has a double hesco post either side. A wooden watchtower stands in each diagonal wall; guard huts
	/// hold the two front quadrants, sandbagged walls to the front; a logistics yard (two ammunition
	/// dumps) holds the rear left and a medical post the rear right, each behind a hesco revetment with
	/// a floodlight at its inner end. One themed stack stands against each side wall (logs at the
	/// lumber camp, stone at the quarry). Outside the front gate lie two sandbag forward positions and
	/// a belt of hedgehogs. The wall opens wherever a road crosses it; the trees inside it are cleared
	/// and the props the map was first authored with inside it are switched off.
	/// </para>
	/// <para>
	/// <b>Nothing is placed where something needs the ground</b>: the infantry spawns and their
	/// contested-spawn ring, every vehicle spawner with its exit lane, every road with a margin, the
	/// navigation's <see cref="RelevantGraphSurface"/>, trees, water, steep or uneven ground, and every
	/// collider already there. A module (an ammo dump, a medical post) is placed whole or not at all.
	/// </para>
	/// <para>
	/// <b>The relevant surface is load-bearing.</b> Both recast graphs keep only the regions connected
	/// to the map's one <see cref="RelevantGraphSurface"/>; the builder moves it onto an open road and
	/// <see cref="Rebake"/> refuses to write a graph that lost more than a tenth of its nodes.
	/// </para>
	/// </remarks>
	public static class ForestLakeOutposts
	{
		public const string ScenePath = "Assets/Scenes/ForestLake.unity";

		public const string RootName = "Outpost Dressing";

		private const string PropFolder = "Assets/MilitaryProps/Prefabs/";

		private const string GraphCachePath = "Assets/TextAsset/ForestLake_GraphCache.bytes";

		private const float MaxSlopeDegrees = 24f;

		/// <summary>Metres between the blocks of a wall: butted, not overlapping.</summary>
		private const float WallJoint = 0.05f;

		private const float MaxFootprintDrop = 1.3f;

		private const float TreeClearance = 1.2f;

		/// <summary>Road weight above which ground counts as road.</summary>
		private const float RoadWeight = 0.3f;

		/// <summary>Metres kept clear on each side of a road.</summary>
		private const float RoadMargin = 0.75f;

		/// <summary>Metres kept clear round a <see cref="RelevantGraphSurface"/>, beyond a piece's own size.</summary>
		private const float SurfaceClearance = 5f;

		/// <summary>Metres beyond a flag's capture range the relevant surface keeps clear of it.</summary>
		private const float OutpostClearance = 40f;

		/// <summary>A flag at least this big is an HQ: it already has a walled compound.</summary>
		private const float HeadquartersRadius = 28f;

		/// <summary>Metres kept open beyond the farthest infantry spawn of a firebase.</summary>
		private const float PlazaMargin = 3f;

		/// <summary>Half width of the four walkways from the plaza to the gates.</summary>
		private const float WalkwayHalfWidth = 3.5f;

		/// <summary>Half width of each gate in the firebase wall.</summary>
		private const float GateHalfWidth = 4.5f;

		/// <summary>Metres kept clear either side of a bridge's deck, all along it.</summary>
		private const float BridgeMargin = 1.5f;

		/// <summary>Metres kept clear beyond each end of a bridge, where its walkers step on and off.</summary>
		private const float BridgeApproach = 8f;

		/// <summary>Metres kept clear round one infantry spawn position.</summary>
		private const float SpawnClearance = 2.5f;

		/// <summary>Metres kept clear round one contested-spawn position, out beyond the walls.</summary>
		private const float ContestedClearance = 1.2f;

		/// <summary>Least metres between the plaza and the wall: room for a fighting position.</summary>
		private const float MinBand = 10f;

		/// <summary>The octagon's corner cut: |x| + |z| at most this many wall half-widths.</summary>
		private const float ChamferRatio = 1.55f;

		/// <summary>Percentage points of extra blocked wall a larger base may cost and still be chosen.</summary>
		private const float WallChoiceSlack = 3f;

		/// <summary>The wall piece that stands where a longer one cannot: 1.9 m square.</summary>
		private const string FallbackWall = "BF_Hesco";

		/// <summary>A rebake may lose this share of walkable nodes to the new pieces, and no more.</summary>
		private const float MinNodeShareAfterRebake = 0.9f;

		[MenuItem("Ironfront/Maps/Forest Lake/Build outposts")]
		public static void BuildFromMenu() => Debug.Log("[outposts]\n" + Build(rebake: false));

		[MenuItem("Ironfront/Maps/Forest Lake/Build outposts and rebake pathfinding")]
		public static void BuildAndRebake() => Debug.Log("[outposts]\n" + Build(rebake: true));

		public static string Build(bool rebake)
		{
			var log = new StringBuilder();
			if (EditorSceneManager.GetActiveScene().path != ScenePath)
			{
				EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
			}

			GameObject previous = GameObject.Find(RootName);
			if (previous != null)
			{
				Object.DestroyImmediate(previous);
				log.AppendLine("removed the previous '" + RootName + "'.");
			}

			var context = new Context(log);
			var root = new GameObject(RootName);
			try
			{
				context.Begin();
				CapturePoint[] points = Object.FindObjectsByType<CapturePoint>(FindObjectsSortMode.InstanceID);
				// First, so every piece keeps clear of where the surface ends up, run after run.
				MoveRelevantSurfaceToOpenRoad(context, points);
				context.KeepOutVehicles();
				context.KeepOutBridges();
				foreach (CapturePoint point in points)
				{
					var group = new GameObject("Outpost - " + point.name.Replace(" Capture Point", string.Empty));
					group.transform.SetParent(root.transform, false);
					int before = context.Placed;
					context.KeepOutSpawns(point);
					if (point.captureRange >= HeadquartersRadius)
					{
						DressHeadquarters(context, point, group.transform);
					}
					else
					{
						DressFirebase(context, point, group.transform);
					}
					log.AppendLine($"{point.name}: {context.Placed - before} pieces.");
				}
			}
			finally
			{
				context.End();
			}

			// Static, so a player build batches the props instead of drawing each crate on its own.
			foreach (Transform piece in root.GetComponentsInChildren<Transform>(true))
			{
				GameObjectUtility.SetStaticEditorFlags(piece.gameObject, StaticEditorFlags.BatchingStatic | StaticEditorFlags.OccluderStatic | StaticEditorFlags.OccludeeStatic);
			}
			// Supply caches stay non-static: they are behaviours, and a static flag says nothing to them.
			foreach (SupplyCache cache in root.GetComponentsInChildren<SupplyCache>(true))
			{
				GameObjectUtility.SetStaticEditorFlags(cache.gameObject, 0);
			}

			if (rebake)
			{
				Rebake(log);
			}

			EditorSceneManager.MarkSceneDirty(root.scene);
			EditorSceneManager.SaveScene(root.scene);
			// The cleared trees live in the terrain asset, which a scene save does not write.
			AssetDatabase.SaveAssets();
			log.AppendLine($"saved {ScenePath}: {context.Placed} pieces, {root.GetComponentsInChildren<SupplyCache>().Length} supply caches.");
			// The map picture draws every structure, so it follows the outposts.
			log.AppendLine("minimap: " + MinimapBaker.Bake());
			return log.ToString();
		}

		// ------------------------------------------------------------------ the firebase

		private static void DressFirebase(Context context, CapturePoint point, Transform group)
		{
			var frame = new Frame(point.transform.position, context.FrontYaw(point.transform.position));
			float plaza = context.SpawnReach(point) + PlazaMargin;
			float wall = context.ChooseWallHalf(frame, plaza + MinBand, point.captureRange + 1f);
			float chamfer = wall * ChamferRatio;
			// The corner towers stand on the 45-degree walls, at the middle of each.
			float corner = chamfer * 0.5f;
			float flat = chamfer - wall;
			float yard = plaza + 3.5f;
			int cleared = context.ClearTrees(frame, wall + 1.5f, chamfer + 2f);
			int retired = context.RetireAuthoredProps(point, frame, wall + 3f, chamfer + 4f);
			context.Log.AppendLine($"  firebase {wall * 2f:0} m across (plaza {plaza:0.#} m); {cleared} tree(s) cleared, {retired} authored prop(s) retired.");

			// Each gate stands in the middle of its wall, or where a bridge lands on that wall: a gate
			// the bridge's walkers can walk straight through.
			Gates gates = GatesFor(context, frame, wall, flat);
			if (gates.MovedBy != null)
			{
				context.Log.AppendLine("  gates on bridges: " + gates.MovedBy);
			}

			// The plaza and the four walkways stay open: modules that slide off their slot must not
			// slide into them.
			context.AddKeepOutCircle(frame.Origin, plaza);
			// They stop short of the wall, where the gates and their frames stand.
			float lane = (wall - 2f - plaza) * 0.5f;
			context.AddKeepOutBox(frame, new Vector2(gates.Front, plaza + lane), new Vector2(WalkwayHalfWidth, lane), 0f);
			context.AddKeepOutBox(frame, new Vector2(gates.Rear, -(plaza + lane)), new Vector2(WalkwayHalfWidth, lane), 0f);
			context.AddKeepOutBox(frame, new Vector2(plaza + lane, gates.Right), new Vector2(lane, WalkwayHalfWidth), 0f);
			context.AddKeepOutBox(frame, new Vector2(-(plaza + lane), gates.Left), new Vector2(lane, WalkwayHalfWidth), 0f);

			// The front gate: a concrete gate frame over the walkway, as at the HQs, with a double hesco
			// post either side. Built on a road or a bridge's landing as readily as off one: that is
			// what a gate is for, and its legs stand clear of a bridge's deck.
			context.IgnoreRoads = true;
			context.IgnoreKeepOutBoxes = true;
			context.TryPlaceModule(group, frame, new Vector2(gates.Front, wall), 90f, Blueprints.GateFrame, "Front Gate");
			context.IgnoreRoads = false;
			context.IgnoreKeepOutBoxes = false;
			foreach (float side in new[] { -1f, 1f })
			{
				float postAt = side * (GateHalfWidth + 0.95f);
				context.TryPlaceModule(group, frame, new Vector2(gates.Front + postAt, wall), 0f, Blueprints.GatePost, "Gate Post");
				context.TryPlaceModule(group, frame, new Vector2(gates.Rear + postAt, -wall), 0f, Blueprints.GatePost, "Gate Post");
				context.TryPlaceModule(group, frame, new Vector2(wall, gates.Right + postAt), 0f, Blueprints.GatePost, "Gate Post");
				context.TryPlaceModule(group, frame, new Vector2(-wall, gates.Left + postAt), 0f, Blueprints.GatePost, "Gate Post");
			}

			// Four corner towers: the walls are laid round them.
			foreach (float x in new[] { -1f, 1f })
			{
				foreach (float z in new[] { -1f, 1f })
				{
					PlaceAlong(context, group, frame, new Vector2(x * corner, z * corner), new Vector2(-x, -z) * 0.7f, 4, Blueprints.Watchtower, "Watchtower");
				}
			}

			// Guard huts either side of the front walkway, sandbagged walls to the front: the HQs' own
			// layout.
			foreach (float side in new[] { -1f, 1f })
			{
				PlaceNear(context, group, frame, new Vector2(gates.Front + side * (WalkwayHalfWidth + 5.6f), plaza + 3.6f), 0f, Blueprints.GuardHut, "Guard Hut", wide: true);
			}

			// The logistics yard (rear left) and the medical post (rear right) face the plaza, each
			// behind its own hesco revetment, with a floodlight at the inner end of each revetment.
			PlaceNear(context, group, frame, new Vector2(-(WalkwayHalfWidth + 3.6f), -yard), 0f, Blueprints.AmmoDump, "Ammo Cache", point, SupplyKind.Ammo);
			PlaceNear(context, group, frame, new Vector2(-(WalkwayHalfWidth + 9.2f), -yard), 0f, Blueprints.AmmoDump, "Ammo Cache", point, SupplyKind.Ammo);
			PlaceNear(context, group, frame, new Vector2(WalkwayHalfWidth + 5.2f, -yard), 0f, Blueprints.MedicalPost, "Medical Station", point, SupplyKind.Medical);
			WallRun(context, group, frame, new Vector2(-(WalkwayHalfWidth + 1.5f), -(yard + 3f)), new Vector2(-(WalkwayHalfWidth + 11.2f), -(yard + 3f)), new[] { "BF_Hesco" });
			WallRun(context, group, frame, new Vector2(WalkwayHalfWidth + 1.5f, -(yard + 3f)), new Vector2(WalkwayHalfWidth + 9.8f, -(yard + 3f)), new[] { "BF_Hesco" });
			foreach (float side in new[] { -1f, 1f })
			{
				context.TryPlaceModule(group, frame, new Vector2(side * (WalkwayHalfWidth + 0.6f), -(yard + 3f)), 0f, Blueprints.Floodlight, "Floodlight");
				context.TryPlaceModule(group, frame, new Vector2(gates.Front + side * (GateHalfWidth + 2.6f), wall - 2f), 0f, Blueprints.Floodlight, "Floodlight");
			}

			// One themed stack against each side wall, behind the guard hut.
			Piece[] stack = Blueprints.ThemeStack(point.name);
			foreach (float side in new[] { -1f, 1f })
			{
				PlaceNear(context, group, frame, new Vector2(side * (wall - 2.6f), GateHalfWidth + 4f), side > 0f ? 90f : -90f, stack, "Stores");
				PlaceNear(context, group, frame, new Vector2(side * (wall - 2.6f), -(GateHalfWidth + 4f)), side > 0f ? 90f : -90f, stack, "Stores");
			}

			// The octagonal wall, hesco all round: a field firebase, where the HQs are concrete
			// compounds. A gate on every walkway, an opening wherever a road crosses.
			float post = GateHalfWidth + 2.05f;
			foreach (float side in new[] { -1f, 1f })
			{
				float sideGate = side > 0f ? gates.Right : gates.Left;
				WallRun(context, group, frame, new Vector2(gates.Front + side * post, wall), new Vector2(side * flat, wall), new[] { "BF_Hesco" });
				WallRun(context, group, frame, new Vector2(side * flat, wall), new Vector2(side * wall, flat), new[] { "BF_Hesco" });
				WallRun(context, group, frame, new Vector2(side * wall, sideGate + post), new Vector2(side * wall, flat), new[] { "BF_Hesco" });
				WallRun(context, group, frame, new Vector2(side * wall, sideGate - post), new Vector2(side * wall, -flat), new[] { "BF_Hesco" });
				WallRun(context, group, frame, new Vector2(side * wall, -flat), new Vector2(side * flat, -wall), new[] { "BF_Hesco" });
				WallRun(context, group, frame, new Vector2(gates.Rear + side * post, -wall), new Vector2(side * flat, -wall), new[] { "BF_Hesco" });
			}

			// Forward sandbag positions outside the front gate, as at the HQs, and hedgehogs across the
			// approaches either side of them.
			foreach (float side in new[] { -1f, 1f })
			{
				PlaceNear(context, group, frame, new Vector2(gates.Front + side * (GateHalfWidth + 4.6f), wall + 4.9f), 0f, Blueprints.ForwardPosition, "Forward Position");
				for (float x = GateHalfWidth + 10.5f; x <= flat + 3f; x += 3.5f)
				{
					context.TryPlaceModule(group, frame, new Vector2(side * x, wall + 3.2f), 0f, Blueprints.Hedgehog, "Obstacle");
				}
			}
			context.Log.AppendLine("  wall gaps: " + context.TakeWallRefusals());
		}

		/// <summary>Where each of a firebase's four gates stands along its wall, in base-frame metres.</summary>
		private struct Gates
		{
			public float Front;
			public float Rear;
			public float Right;
			public float Left;

			/// <summary>The gates a bridge moved, for the log; null when none did.</summary>
			public string MovedBy;
		}

		/// <summary>
		/// The middle of each wall, unless a bridge's centre line crosses that wall's straight
		/// stretch with room for a gate and its posts; then the gate stands where it crosses.
		/// </summary>
		private static Gates GatesFor(Context context, Frame frame, float wall, float flat)
		{
			var gates = new Gates();
			float room = flat - GateHalfWidth - 2.05f;
			foreach ((Vector3 from, Vector3 to) in context.Bridges)
			{
				Vector2 a = frame.Local(from);
				Vector2 b = frame.Local(to);
				// A bridge reaches past its deck by the approach it keeps clear.
				Vector2 reach = (b - a).normalized * BridgeApproach;
				a -= reach;
				b += reach;
				if (Crossing(a.y, b.y, a.x, b.x, wall, room, out float at))
				{
					gates.Front = at;
					gates.MovedBy += $"front {at:0.#} m ";
				}
				if (Crossing(a.y, b.y, a.x, b.x, -wall, room, out at))
				{
					gates.Rear = at;
					gates.MovedBy += $"rear {at:0.#} m ";
				}
				if (Crossing(a.x, b.x, a.y, b.y, wall, room, out at))
				{
					gates.Right = at;
					gates.MovedBy += $"right {at:0.#} m ";
				}
				if (Crossing(a.x, b.x, a.y, b.y, -wall, room, out at))
				{
					gates.Left = at;
					gates.MovedBy += $"left {at:0.#} m ";
				}
			}
			return gates;
		}

		/// <summary>
		/// Where a segment from (<paramref name="ua"/>, <paramref name="va"/>) to
		/// (<paramref name="ub"/>, <paramref name="vb"/>) crosses the line u = <paramref name="line"/>,
		/// as its v there, when it crosses within |v| of at most <paramref name="room"/>.
		/// </summary>
		private static bool Crossing(float ua, float ub, float va, float vb, float line, float room, out float at)
		{
			at = 0f;
			if ((ua - line) * (ub - line) > 0f || Mathf.Approximately(ua, ub))
			{
				return false;
			}
			at = Mathf.Lerp(va, vb, (line - ua) / (ub - ua));
			return Mathf.Abs(at) <= room;
		}

		/// <summary>
		/// A module at its slot, or failing that at the nearest of a few slots on the base's own axes
		/// round it, so a road or a tree moves it a step instead of deleting it. A supply cache looks
		/// further (<see cref="WideNudges"/>), a base without one having nothing to give; so does a
		/// <paramref name="wide"/> module, a guard hut, too big for a step to clear a road.
		/// </summary>
		private static bool PlaceNear(Context context, Transform group, Frame frame, Vector2 at, float yaw, Piece[] pieces, string name, SpawnPoint servedPoint = null, SupplyKind kind = SupplyKind.Ammo, bool wide = false)
		{
			int tries = Nudges.Length + (wide || servedPoint != null ? WideNudges.Length : 0);
			for (int i = 0; i < tries; i++)
			{
				Vector2 nudge = i < Nudges.Length ? Nudges[i] : WideNudges[i - Nudges.Length];
				if (context.TryPlaceModule(group, frame, at + nudge, yaw, pieces, name, servedPoint, kind, quiet: i < tries - 1))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>A module at its slot, or stepped along <paramref name="step"/> up to <paramref name="tries"/> times.</summary>
		private static bool PlaceAlong(Context context, Transform group, Frame frame, Vector2 at, Vector2 step, int tries, Piece[] pieces, string name)
		{
			for (int i = 0; i <= tries; i++)
			{
				if (context.TryPlaceModule(group, frame, at + step * i, 0f, pieces, name, quiet: i < tries))
				{
					return true;
				}
			}
			return false;
		}

		private static readonly Vector2[] Nudges =
		{
			Vector2.zero,
			new Vector2(1.5f, 0f), new Vector2(-1.5f, 0f), new Vector2(0f, 1.5f), new Vector2(0f, -1.5f),
			new Vector2(3f, 0f), new Vector2(-3f, 0f), new Vector2(0f, 3f), new Vector2(0f, -3f),
			new Vector2(1.5f, 1.5f), new Vector2(-1.5f, 1.5f), new Vector2(1.5f, -1.5f), new Vector2(-1.5f, -1.5f),
		};

		/// <summary>
		/// Further slots a supply cache or a guard hut may take, out to 6 m: Island's medical post has
		/// a boulder in its slot and would otherwise not exist.
		/// </summary>
		private static readonly Vector2[] WideNudges =
		{
			new Vector2(4.5f, 0f), new Vector2(-4.5f, 0f), new Vector2(0f, 4.5f), new Vector2(0f, -4.5f),
			new Vector2(3f, 3f), new Vector2(-3f, 3f), new Vector2(3f, -3f), new Vector2(-3f, -3f),
			new Vector2(6f, 0f), new Vector2(-6f, 0f), new Vector2(0f, 6f), new Vector2(0f, -6f),
		};

		// ------------------------------------------------------------------ the HQ supply yard

		/// <summary>
		/// An HQ keeps its authored compound; its supply caches stand in one row along the back edge of
		/// its concrete pad, facing into the pad, with nothing else added.
		/// </summary>
		private static void DressHeadquarters(Context context, CapturePoint point, Transform group)
		{
			Transform pad = context.FindPad(point);
			if (pad == null)
			{
				context.Log.AppendLine($"  WARNING: {point.name} has no MP_Parking pad; no supply yard.");
				return;
			}
			Bounds local = context.LocalBounds(pad);
			Vector3 padCentre = pad.position + pad.rotation * local.center;
			// The pad's edge farthest from the flag is its back; the row faces the flag across the pad.
			Vector3 toFlag = point.transform.position - padCentre;
			float best = float.MinValue;
			Vector2 edge = Vector2.zero;
			float yaw = 0f;
			foreach (int quarter in new[] { 0, 1, 2, 3 })
			{
				float edgeYaw = pad.eulerAngles.y + quarter * 90f;
				Vector3 outward = Quaternion.Euler(0f, edgeYaw, 0f) * Vector3.forward;
				float away = -Vector3.Dot(outward, toFlag);
				if (away > best)
				{
					best = away;
					yaw = edgeYaw + 180f;
					float depth = quarter % 2 == 0 ? local.extents.z : local.extents.x;
					edge = new Vector2(padCentre.x, padCentre.z) + new Vector2(outward.x, outward.z) * (depth - 2.6f);
				}
			}
			var frame = new Frame(new Vector3(edge.x, padCentre.y, edge.y), yaw);
			// An HQ compound is painted as road from wall to wall; the pad is meant to be built on.
			context.IgnoreRoads = true;
			PlaceNear(context, group, frame, new Vector2(-5.3f, 0f), 0f, Blueprints.AmmoDump, "Ammo Cache", point, SupplyKind.Ammo);
			PlaceNear(context, group, frame, new Vector2(0f, 0f), 0f, Blueprints.MedicalPost, "Medical Station", point, SupplyKind.Medical);
			PlaceNear(context, group, frame, new Vector2(5.3f, 0f), 0f, Blueprints.AmmoDump, "Ammo Cache", point, SupplyKind.Ammo);
			context.IgnoreRoads = false;
		}

		// ------------------------------------------------------------------ walls

		/// <summary>
		/// A straight wall from <paramref name="from"/> to <paramref name="to"/> (base-frame metres),
		/// each piece laid along the run on its own long axis and butted against the last. A piece that
		/// cannot stand (a road, a slope, a tree, a keep-out) is tried as a hesco block, and failing that
		/// leaves a gap: a road crossing the wall becomes a gate.
		/// </summary>
		private static void WallRun(Context context, Transform group, Frame frame, Vector2 from, Vector2 to, string[] pieces)
		{
			Vector2 run = to - from;
			float length = run.magnitude;
			if (length < 0.5f)
			{
				return;
			}
			Vector2 along = run / length;
			float runYaw = Mathf.Atan2(along.x, along.y) * Mathf.Rad2Deg;
			float walked = 0f;
			int index = 0;
			while (walked < length - 0.5f)
			{
				string name = pieces[index++ % pieces.Length];
				foreach (string candidate in name == FallbackWall ? new[] { name } : new[] { name, FallbackWall })
				{
					Bounds footprint = context.FootprintOf(candidate);
					bool longAlongZ = footprint.size.z >= footprint.size.x;
					float size = longAlongZ ? footprint.size.z : footprint.size.x;
					if (walked + size > length + 0.3f && candidate != FallbackWall)
					{
						continue;
					}
					Vector2 at = from + along * (walked + size * 0.5f);
					float yaw = longAlongZ ? runYaw : runYaw - 90f;
					if (context.TryPlaceModule(group, frame, at, yaw, new[] { new Piece(candidate, 0f, 0f, 0f) }, null))
					{
						walked += size + WallJoint;
						goto next;
					}
				}
				context.NoteWallRefusal();
				// Half a metre on: the next piece starts right after a tower or a gate post, not 2 m on.
				walked += 0.5f;
			next:;
			}
		}

		// ------------------------------------------------------------------ pathfinding

		/// <summary>
		/// Puts the graphs' <see cref="RelevantGraphSurface"/> on an open stretch of road near the
		/// middle of the map, clear of every outpost.
		/// </summary>
		/// <remarks>
		/// Both recast graphs keep only the regions that connect to it (RequireForAll). It stood 10 m
		/// from the Meadow flag; once Meadow had walls and crates, the car graph (a 4 m agent) found the
		/// space round it sealed off and kept 5 nodes of 15315. A road is where the vehicle network is,
		/// by definition, so the surface belongs on one.
		/// </remarks>
		private static void MoveRelevantSurfaceToOpenRoad(Context context, CapturePoint[] points)
		{
			RelevantGraphSurface surface = Object.FindFirstObjectByType<RelevantGraphSurface>();
			if (surface == null)
			{
				return;
			}
			if (!context.TryFindOpenRoad(context.MapMiddle, points, OutpostClearance, out Vector3 road))
			{
				throw new System.InvalidOperationException("No open road for the RelevantGraphSurface; the navigation would not rebake.");
			}
			Vector3 before = surface.transform.position;
			surface.transform.position = road + Vector3.up * 0.5f;
			EditorUtility.SetDirty(surface.transform);
			context.NoteSurfaces();
			context.Log.AppendLine($"relevant surface at {surface.transform.position:F0} (was {before:F0}), an open road {Vector3.Distance(road, context.MapMiddle):0} m from the middle.");
		}

		/// <summary>
		/// Rescans the graphs with the outposts in place, regenerates cover and penalties, and writes
		/// the cache, refusing to write one that lost more than a tenth of the walkable nodes.
		/// </summary>
		private static void Rebake(StringBuilder log)
		{
			var placer = Object.FindFirstObjectByType<CoverPlacer>();
			var astar = Object.FindFirstObjectByType<AstarPath>();
			if (placer == null || astar == null)
			{
				throw new System.InvalidOperationException("Forest Lake has no AstarPath/CoverPlacer to rebake.");
			}
			var watch = System.Diagnostics.Stopwatch.StartNew();
			// In edit mode AstarPath never ran Awake: no singleton, no graphs. Its own Initialize sets
			// both up from the cache, with the graphs' settings, ready to rescan.
			typeof(AstarPath).GetMethod("Initialize", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
				.Invoke(astar, null);
			int infantryBefore = astar.graphs[0].CountNodes();
			int carBefore = astar.graphs[2].CountNodes();
			astar.Scan();
			int infantryAfter = astar.graphs[0].CountNodes();
			int carAfter = astar.graphs[2].CountNodes();
			log.AppendLine($"pathfinding: infantry {infantryBefore} -> {infantryAfter} nodes, cars {carBefore} -> {carAfter}.");
			if (infantryAfter < infantryBefore * MinNodeShareAfterRebake || carAfter < carBefore * MinNodeShareAfterRebake)
			{
				// Back to the cache on disk, so nothing in memory looks like a result.
				astar.astarData.LoadFromCache();
				throw new System.InvalidOperationException(
					$"Rebake lost walkable ground (infantry {infantryBefore} -> {infantryAfter}, cars {carBefore} -> {carAfter}); "
					+ "the cache was NOT written. Look for a piece over the RelevantGraphSurface or across a choke point.");
			}
			placer.Generate();
			byte[] bytes = astar.astarData.SerializeGraphs(Pathfinding.Serialization.SerializeSettings.All);
			System.IO.File.WriteAllBytes(GraphCachePath, bytes);
			AssetDatabase.ImportAsset(GraphCachePath, ImportAssetOptions.ForceUpdate);
			astar.astarData.file_cachedStartup = AssetDatabase.LoadAssetAtPath<TextAsset>(GraphCachePath);
			EditorUtility.SetDirty(astar);
			int covers = placer.GetComponentsInChildren<CoverPoint>().Length;
			log.AppendLine($"pathfinding: rescanned and cached ({bytes.Length / 1024} KB) in {watch.Elapsed.TotalSeconds:0} s; {covers} cover points.");
		}

		// ------------------------------------------------------------------ the blueprint pieces

		/// <summary>One prop of a module: its prefab, its place in the module's frame, its yaw, its lift.</summary>
		private readonly struct Piece
		{
			public readonly string Prefab;
			public readonly Vector2 At;
			public readonly float Yaw;
			/// <summary>Metres above the ground; above zero the piece stands on another and is not checked.</summary>
			public readonly float Lift;
			public readonly bool RedCross;

			public Piece(string prefab, float x, float z, float yaw, float lift = 0f, bool redCross = false)
			{
				Prefab = prefab;
				At = new Vector2(x, z);
				Yaw = yaw;
				Lift = lift;
				RedCross = redCross;
			}
		}

		/// <summary>
		/// The modules every base is built from, in their own frame: +z faces the walkway or the plaza,
		/// x runs along the row. Sandbag positions and towers are closed (+x of the prefab) toward +z.
		/// </summary>
		private static class Blueprints
		{
			/// <summary>Two stacked crate columns, a back row of netted crates, two drums: 4.8 x 3.4 m.</summary>
			public static readonly Piece[] AmmoDump =
			{
				new Piece("BF_Crate_Camo", -0.95f, 0.45f, 0f),
				new Piece("BF_Crate_Camo", -0.95f, 0.45f, 0f, lift: 1f),
				new Piece("BF_Crate_Camo", 0.85f, 0.45f, 0f),
				new Piece("BF_Crates_Netted", 0.85f, 0.45f, 0f, lift: 1f),
				new Piece("BF_Crates_Netted", -1.0f, -1.05f, 0f),
				new Piece("BF_Crates_Netted", 1.0f, -1.05f, 0f),
				new Piece("MP_Barrel", 2.25f, 0.6f, 0f),
				new Piece("MP_Barrel", 2.25f, -0.1f, 0f),
			};

			/// <summary>Three cabinets with red crosses, two stretchers laid in front: 3.6 x 3.4 m.</summary>
			public static readonly Piece[] MedicalPost =
			{
				new Piece("MP_Utility_Box", -1.0f, -1.0f, -90f, redCross: true),
				new Piece("MP_Utility_Box", 0f, -1.0f, -90f, redCross: true),
				new Piece("MP_Utility_Box", 1.0f, -1.0f, -90f, redCross: true),
				new Piece("BF_Sack_1", -0.9f, 0.9f, 90f),
				new Piece("BF_Sack_1", 0.9f, 0.9f, 90f),
			};

			/// <summary>A sandbag position outside the front gate, closed toward the front (+z).</summary>
			public static readonly Piece[] ForwardPosition = { new Piece("MP_Sandbag_Cover_2", 0f, 0f, -90f) };

			/// <summary>A guard hut, its sandbagged wall (+x of the prefab) toward +z: 9.9 x 6 m.</summary>
			public static readonly Piece[] GuardHut = { new Piece("MP_Outpost", 0f, 0f, -90f) };

			/// <summary>The concrete gate frame spanning x (the prefab spans its own z).</summary>
			public static readonly Piece[] GateFrame = { new Piece("MP_Gate_Frame", 0f, 0f, 0f) };

			/// <summary>Two hesco blocks, one on the other, either side of a gate.</summary>
			public static readonly Piece[] GatePost = { new Piece("BF_Hesco", 0f, 0f, 0f), new Piece("BF_Hesco", 0f, 0f, 0f, lift: 1.7f) };

			public static readonly Piece[] Watchtower = { new Piece("MP_Watchtower_Wood", 0f, 0f, 0f) };

			public static readonly Piece[] Floodlight = { new Piece("MP_Roadway_Light", 0f, 0f, 0f) };

			public static readonly Piece[] Hedgehog = { new Piece("BF_Hedgehog", 0f, 0f, 0f) };

			public static readonly Piece[] Wire = { new Piece("MP_Barbed_Wire", 0f, 0f, 0f) };

			/// <summary>One stack a place is known by, against a side wall: 3.6 x 2.4 m.</summary>
			public static Piece[] ThemeStack(string pointName)
			{
				string name = pointName.ToLowerInvariant();
				if (name.Contains("lumber"))
				{
					return new[]
					{
						new Piece("BF_Log", 0f, -0.35f, 0f), new Piece("BF_Log", 0f, 0f, 0f), new Piece("BF_Log", 0f, 0.35f, 0f),
						new Piece("BF_Log", 0f, -0.17f, 0f, lift: 0.3f), new Piece("BF_Log", 0f, 0.17f, 0f, lift: 0.3f),
						new Piece("BF_Beam", 0f, 0.95f, 0f),
					};
				}
				if (name.Contains("quarry"))
				{
					return new[]
					{
						new Piece("BF_Stone", -1.0f, 0f, 0f), new Piece("BF_Stone", -0.35f, 0f, 0f), new Piece("BF_Stone", 0.3f, 0f, 0f),
						new Piece("BF_Stone", -0.65f, 0f, 0f, lift: 0.3f), new Piece("BF_Stone", 0f, 0f, 0f, lift: 0.3f),
						new Piece("BF_Crates_Netted", 1.6f, 0f, 90f),
					};
				}
				if (name.Contains("meadow"))
				{
					return new[]
					{
						new Piece("BF_Crates_Netted", -0.95f, 0f, 0f), new Piece("BF_Crates_Netted", 0.95f, 0f, 0f),
						new Piece("BF_Crates_Netted", 0f, 0f, 0f, lift: 0.7f),
					};
				}
				return new[]
				{
					new Piece("BF_Sack_2", 0f, -0.6f, 0f), new Piece("BF_Sack_2", 0f, 0.6f, 0f),
					new Piece("BF_Sack_3", 0f, 0f, 0f, lift: 0.4f),
					new Piece("MP_Barrel", 1.4f, -0.3f, 0f), new Piece("MP_Barrel", 1.4f, 0.3f, 0f),
				};
			}
		}

		/// <summary>A base's frame: its origin and the yaw its +z points along.</summary>
		private readonly struct Frame
		{
			public readonly Vector3 Origin;
			public readonly float Yaw;

			public Frame(Vector3 origin, float yaw)
			{
				Origin = origin;
				Yaw = yaw;
			}

			public Vector3 World(Vector2 local) => Origin + Quaternion.Euler(0f, Yaw, 0f) * new Vector3(local.x, 0f, local.y);

			public Vector2 Local(Vector3 world)
			{
				Vector3 local = Quaternion.Euler(0f, -Yaw, 0f) * (world - Origin);
				return new Vector2(local.x, local.z);
			}
		}

		// ------------------------------------------------------------------ placement

		private sealed class Context
		{
			public readonly StringBuilder Log;

			public int Placed;

			public Vector3 MapMiddle;

			private Material redMaterial;

			private Terrain terrain;

			private TerrainData data;

			private int roadLayer = -1;

			private float[,,] splat;

			private readonly List<Vector3> trees = new List<Vector3>();

			private readonly List<Vector3> surfaces = new List<Vector3>();

			/// <summary>Ground nothing may stand on: (centre x, centre z, radius).</summary>
			private readonly List<Vector3> keepOutCircles = new List<Vector3>();

			/// <summary>Rectangles nothing may stand on: centre, half extents, yaw.</summary>
			private readonly List<(Vector2 centre, Vector2 half, float yaw)> keepOutBoxes = new List<(Vector2, Vector2, float)>();

			private readonly List<MeshCollider> waterColliders = new List<MeshCollider>();

			private readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();

			private readonly Dictionary<string, Bounds> footprints = new Dictionary<string, Bounds>();

			/// <summary>Why the last <see cref="CanPlace"/> said no.</summary>
			public string Refusal;

			/// <summary>Set while dressing an HQ, whose compound is painted as road throughout.</summary>
			public bool IgnoreRoads;

			/// <summary>Whether <see cref="CanPlace"/> lets a piece stand in a keep-out box: a gate over its walkway.</summary>
			public bool IgnoreKeepOutBoxes;

			/// <summary>Why wall pieces were refused since the last <see cref="TakeWallRefusals"/>, by reason.</summary>
			private readonly Dictionary<string, int> wallRefusals = new Dictionary<string, int>();

			public void NoteWallRefusal()
			{
				string reason = Refusal ?? "?";
				int cut = reason.IndexOf(' ');
				string key = reason.StartsWith("keep-out") || reason.StartsWith("uneven") ? reason.Substring(0, cut > 0 ? cut : reason.Length) : reason;
				wallRefusals[key] = wallRefusals.TryGetValue(key, out int n) ? n + 1 : 1;
			}

			public string TakeWallRefusals()
			{
				var parts = new List<string>();
				foreach (var pair in wallRefusals)
				{
					parts.Add(pair.Key + " " + pair.Value);
				}
				wallRefusals.Clear();
				return string.Join(", ", parts);
			}

			public Context(StringBuilder log)
			{
				Log = log;
			}

			public void Begin()
			{
				terrain = Terrain.activeTerrain != null ? Terrain.activeTerrain : Object.FindFirstObjectByType<Terrain>();
				data = terrain.terrainData;
				Vector3 origin = terrain.GetPosition();
				MapMiddle = origin + new Vector3(data.size.x * 0.5f, 0f, data.size.z * 0.5f);
				var flags = Object.FindObjectsByType<CapturePoint>(FindObjectsSortMode.None);
				if (flags.Length > 0)
				{
					Vector3 sum = Vector3.zero;
					foreach (CapturePoint flag in flags)
					{
						sum += flag.transform.position;
					}
					MapMiddle = sum / flags.Length;
				}
				TerrainLayer[] layers = data.terrainLayers;
				for (int i = 0; i < layers.Length; i++)
				{
					if (layers[i] != null && layers[i].name.ToLowerInvariant().Contains("path"))
					{
						roadLayer = i;
					}
				}
				splat = roadLayer >= 0 ? data.GetAlphamaps(0, 0, data.alphamapWidth, data.alphamapHeight) : null;
				foreach (TreeInstance tree in data.treeInstances)
				{
					trees.Add(Vector3.Scale(tree.position, data.size) + origin);
				}
				NoteSurfaces();
				// Water surfaces get a temporary collider so a ray can say whether a point is lake.
				foreach (WaterLevel water in Object.FindObjectsByType<WaterLevel>(FindObjectsSortMode.None))
				{
					MeshFilter filter = water.GetComponent<MeshFilter>();
					if (filter != null && filter.sharedMesh != null && water.GetComponent<Collider>() == null)
					{
						MeshCollider collider = water.gameObject.AddComponent<MeshCollider>();
						collider.sharedMesh = filter.sharedMesh;
						waterColliders.Add(collider);
					}
				}
				GameObject barrel = Prefab("MP_Barrel");
				Renderer barrelRenderer = barrel != null ? barrel.GetComponentInChildren<Renderer>() : null;
				redMaterial = barrelRenderer != null ? barrelRenderer.sharedMaterial : null;
				Sync();
				Log.AppendLine($"terrain {data.size.x:0} m, {trees.Count} trees, road layer {roadLayer}, {waterColliders.Count} water surface(s), {surfaces.Count} relevant surface(s).");
			}

			public void End()
			{
				foreach (MeshCollider collider in waterColliders)
				{
					if (collider != null)
					{
						Object.DestroyImmediate(collider);
					}
				}
				waterColliders.Clear();
			}

			public void Sync() => Physics.SyncTransforms();

			/// <summary>Reads where the relevant surfaces stand, for the clearance every piece keeps.</summary>
			public void NoteSurfaces()
			{
				surfaces.Clear();
				foreach (RelevantGraphSurface surface in Object.FindObjectsByType<RelevantGraphSurface>(FindObjectsSortMode.None))
				{
					surfaces.Add(surface.transform.position);
				}
			}

			/// <summary>The base axis: from the flag toward the middle of the map, snapped to 90 degrees for an HQ.</summary>
			public float FrontYaw(Vector3 at)
			{
				Vector3 toMiddle = MapMiddle - at;
				toMiddle.y = 0f;
				return toMiddle.sqrMagnitude > 1f ? Mathf.Atan2(toMiddle.x, toMiddle.z) * Mathf.Rad2Deg : 0f;
			}

			/// <summary>Metres from the flag to its farthest infantry spawn.</summary>
			public float SpawnReach(CapturePoint point)
			{
				float reach = 3f;
				if (point.spawnpointContainer != null)
				{
					foreach (Transform spawn in point.spawnpointContainer)
					{
						reach = Mathf.Max(reach, Flat(spawn.position - point.transform.position).magnitude);
					}
				}
				return reach;
			}

			/// <summary>Keeps every infantry spawn of a point clear, contested ones included, and the flag.</summary>
			public void KeepOutSpawns(CapturePoint point)
			{
				AddCircle(point.transform.position, 3f);
				foreach (Transform container in new[] { point.spawnpointContainer, point.contestedSpawnpointContainer })
				{
					if (container == null)
					{
						continue;
					}
					foreach (Transform spawn in container)
					{
						AddCircle(spawn.position, container == point.contestedSpawnpointContainer ? ContestedClearance : SpawnClearance);
					}
				}
			}

			/// <summary>
			/// Keeps every vehicle spawner clear, with a lane ahead of it to drive out on; a helicopter
			/// gets its rotor disc and no lane.
			/// </summary>
			public void KeepOutVehicles()
			{
				foreach (VehicleSpawner spawner in Object.FindObjectsByType<VehicleSpawner>(FindObjectsSortMode.None))
				{
					string name = spawner.name.ToLowerInvariant();
					float radius = 4.5f;
					float laneWidth = 5f;
					float laneLength = 14f;
					if (name.Contains("heli"))
					{
						radius = 10f;
						laneLength = 0f;
					}
					else if (name.Contains("tank"))
					{
						radius = 6.5f;
						laneWidth = 6.5f;
						laneLength = 18f;
					}
					else if (name.Contains("quad"))
					{
						radius = 3.5f;
						laneWidth = 4f;
						laneLength = 10f;
					}
					else if (name.Contains("boat") || name.Contains("rhib"))
					{
						radius = 6f;
						laneLength = 0f;
					}
					AddCircle(spawner.transform.position, radius);
					if (laneLength > 0f)
					{
						Vector3 forward = spawner.transform.forward;
						Vector3 centre = spawner.transform.position + forward * (laneLength * 0.5f);
						keepOutBoxes.Add((new Vector2(centre.x, centre.z), new Vector2(laneWidth * 0.5f, laneLength * 0.5f), spawner.transform.eulerAngles.y));
					}
				}
			}

			/// <summary>The centre line of every bridge's deck, end to end, in world space.</summary>
			public readonly List<(Vector3 from, Vector3 to)> Bridges = new List<(Vector3, Vector3)>();

			/// <summary>
			/// Keeps every bridge clear, with <see cref="BridgeMargin"/> either side and
			/// <see cref="BridgeApproach"/> beyond each end, and records its centre line so a firebase
			/// can put a gate where it lands.
			/// </summary>
			/// <remarks>
			/// A bridge is a road the terrain's road layer cannot show. Island's east bridge lands
			/// three metres from its firebase's wall; with nothing keeping it clear, a gate post and the
			/// gate frame's leg stood across its end, the island had no dry way out, and a bot soak
			/// counted 27 long swims where #499 had left none.
			/// </remarks>
			public void KeepOutBridges()
			{
				foreach (BoxCollider deck in Object.FindObjectsByType<BoxCollider>(FindObjectsSortMode.InstanceID))
				{
					if (deck.name != "Deck Collider" || deck.transform.parent == null || !deck.transform.parent.name.StartsWith("Bridge"))
					{
						continue;
					}
					Vector3 centre = deck.transform.TransformPoint(deck.center);
					Vector3 along = deck.transform.forward * (deck.size.z * 0.5f);
					Bridges.Add((centre - along, centre + along));
					keepOutBoxes.Add((new Vector2(centre.x, centre.z), new Vector2(deck.size.x * 0.5f + BridgeMargin, deck.size.z * 0.5f + BridgeApproach), deck.transform.eulerAngles.y));
				}
				Log.AppendLine($"{Bridges.Count} bridge(s) kept clear.");
			}

			private void AddCircle(Vector3 at, float radius) => keepOutCircles.Add(new Vector3(at.x, at.z, radius));

			/// <summary>
			/// Switches off the props the map was first authored with at a neutral flag (the
			/// "Outposts/&lt;flag&gt;" group) wherever they stand inside the base's octagon, and answers
			/// how many. Off, not deleted: they were a handful of crates, a tower and some sandbags round
			/// the spawn ring, the scattered look the owner rejected, and they would sit across the
			/// blueprint's walkways; switching them back on restores the old map.
			/// </summary>
			public int RetireAuthoredProps(CapturePoint point, Frame frame, float half, float chamfer)
			{
				string groupName = point.name.Replace(" Capture Point", string.Empty);
				Quaternion inverse = Quaternion.Euler(0f, -frame.Yaw, 0f);
				int retired = 0;
				foreach (Transform group in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None))
				{
					if (group.name != groupName || group.parent == null || group.parent.name != "Outposts" || group.parent.parent != null)
					{
						continue;
					}
					foreach (Transform prop in group)
					{
						Vector3 local = inverse * (prop.position - frame.Origin);
						if (Mathf.Abs(local.x) <= half && Mathf.Abs(local.z) <= half && Mathf.Abs(local.x) + Mathf.Abs(local.z) <= chamfer)
						{
							if (prop.gameObject.activeSelf)
							{
								prop.gameObject.SetActive(false);
								EditorUtility.SetDirty(prop.gameObject);
							}
							retired++;
						}
					}
				}
				Sync();
				return retired;
			}

			public void AddKeepOutCircle(Vector3 at, float radius) => AddCircle(at, radius);

			/// <summary>A rectangle in a base's frame that nothing may stand on.</summary>
			public void AddKeepOutBox(Frame frame, Vector2 at, Vector2 half, float yaw)
			{
				Vector3 centre = frame.World(at);
				keepOutBoxes.Add((new Vector2(centre.x, centre.z), half, frame.Yaw + yaw));
			}

			/// <summary>
			/// The firebase's wall half-width: the largest in [<paramref name="least"/>,
			/// <paramref name="most"/>] whose wall line crosses no more road, water or steep ground than
			/// the best size plus <see cref="WallChoiceSlack"/> points; so a road that rings a flag runs
			/// outside its walls instead of through them.
			/// </summary>
			public float ChooseWallHalf(Frame frame, float least, float most)
			{
				float bestShare = float.MaxValue;
				var shares = new List<(float wall, float share)>();
				for (float wall = least; wall <= most + 0.01f; wall += 1f)
				{
					int blocked = 0;
					int samples = 0;
					for (float s = -wall; s <= wall; s += 1f)
					{
						if (Mathf.Abs(s) < GateHalfWidth)
						{
							continue;
						}
						foreach (Vector2 local in new[] { new Vector2(s, wall), new Vector2(s, -wall), new Vector2(wall, s), new Vector2(-wall, s) })
						{
							Vector3 world = frame.World(local);
							samples++;
							float height = terrain.SampleHeight(world) + terrain.GetPosition().y;
							if (RoadWeightAt(world) > RoadWeight || Slope(world) > MaxSlopeDegrees || IsWater(new Vector3(world.x, height, world.z)))
							{
								blocked++;
							}
						}
					}
					float share = 100f * blocked / Mathf.Max(1, samples);
					shares.Add((wall, share));
					bestShare = Mathf.Min(bestShare, share);
				}
				float chosen = least;
				foreach (var (wall, share) in shares)
				{
					if (share <= bestShare + WallChoiceSlack)
					{
						chosen = Mathf.Max(chosen, wall);
					}
				}
				return chosen;
			}

			/// <summary>
			/// Removes the terrain's trees inside a base's octagon: |x| and |z| at most
			/// <paramref name="half"/> and |x| + |z| at most <paramref name="chamfer"/>, in its frame. A
			/// base is a clearing; a tree in its yard would also refuse half its blueprint.
			/// </summary>
			public int ClearTrees(Frame frame, float half, float chamfer)
			{
				Vector3 origin = terrain.GetPosition();
				Quaternion inverse = Quaternion.Euler(0f, -frame.Yaw, 0f);
				var kept = new List<TreeInstance>(data.treeInstanceCount);
				int removed = 0;
				foreach (TreeInstance tree in data.treeInstances)
				{
					Vector3 world = Vector3.Scale(tree.position, data.size) + origin;
					Vector3 local = inverse * (world - frame.Origin);
					if (Mathf.Abs(local.x) <= half && Mathf.Abs(local.z) <= half && Mathf.Abs(local.x) + Mathf.Abs(local.z) <= chamfer)
					{
						removed++;
						continue;
					}
					kept.Add(tree);
				}
				if (removed > 0)
				{
					data.treeInstances = kept.ToArray();
					EditorUtility.SetDirty(data);
					trees.Clear();
					foreach (TreeInstance tree in kept)
					{
						trees.Add(Vector3.Scale(tree.position, data.size) + origin);
					}
					terrain.Flush();
					Sync();
				}
				return removed;
			}


			/// <summary>The authored concrete pad (MP_Parking) inside an HQ's compound, if it has one.</summary>
			public Transform FindPad(CapturePoint point)
			{
				Transform best = null;
				float bestDistance = point.captureRange;
				foreach (Transform t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
				{
					if (!t.name.StartsWith("MP_Parking") || t.root.name == RootName || (t.parent != null && t.parent.name.StartsWith("MP_Parking")))
					{
						continue;
					}
					float distance = Flat(t.position - point.transform.position).magnitude;
					if (distance < bestDistance)
					{
						bestDistance = distance;
						best = t;
					}
				}
				return best;
			}

			/// <summary>An object's renderer bounds in its own unrotated frame, from its meshes.</summary>
			public Bounds LocalBounds(Transform t)
			{
				Quaternion rotation = t.rotation;
				Vector3 position = t.position;
				t.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
				Bounds bounds = new Bounds(Vector3.zero, Vector3.zero);
				bool first = true;
				foreach (Renderer renderer in t.GetComponentsInChildren<Renderer>())
				{
					if (first)
					{
						bounds = renderer.bounds;
						first = false;
					}
					else
					{
						bounds.Encapsulate(renderer.bounds);
					}
				}
				t.SetPositionAndRotation(position, rotation);
				return bounds;
			}

			/// <summary>
			/// Places a module (one or more pieces in the module's own frame) at <paramref name="at"/> in
			/// the base frame, turned by <paramref name="moduleYaw"/>: every grounded piece is checked
			/// first and the module goes down whole or not at all. A supply kind adds the cache.
			/// </summary>
			public bool TryPlaceModule(Transform group, Frame frame, Vector2 at, float moduleYaw, Piece[] pieces, string name, SpawnPoint servedPoint = null, SupplyKind kind = SupplyKind.Ammo, bool quiet = false)
			{
				Vector3 centre = frame.World(at);
				float yaw = frame.Yaw + moduleYaw;
				Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
				var poses = new (Vector3 at, float yaw, float ground)[pieces.Length];
				float lowestGround = float.MaxValue;
				for (int i = 0; i < pieces.Length; i++)
				{
					Vector3 world = centre + turn * new Vector3(pieces[i].At.x, 0f, pieces[i].At.y);
					float pieceYaw = yaw + pieces[i].Yaw;
					if (pieces[i].Lift > 0f)
					{
						poses[i] = (world, pieceYaw, float.NaN);
						continue;
					}
					if (!CanPlace(pieces[i].Prefab, world, pieceYaw, out float ground))
					{
						if (name != null && !quiet)
						{
							Log.AppendLine($"  {name} at ({at.x:0.#}, {at.y:0.#}) not placed: {pieces[i].Prefab} {Refusal}.");
						}
						return false;
					}
					poses[i] = (world, pieceYaw, ground);
					lowestGround = Mathf.Min(lowestGround, ground);
				}

				Transform parent = group;
				if (name != null)
				{
					var module = new GameObject(name);
					module.transform.SetParent(group, false);
					module.transform.SetPositionAndRotation(new Vector3(centre.x, lowestGround, centre.z), turn);
					parent = module.transform;
					if (servedPoint != null)
					{
						SupplyCache cache = module.AddComponent<SupplyCache>();
						cache.kind = kind;
						cache.point = servedPoint;
					}
				}
				var placed = new List<GameObject>();
				for (int i = 0; i < pieces.Length; i++)
				{
					GameObject prefab = Prefab(pieces[i].Prefab);
					var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
					float y = pieces[i].Lift > 0f ? StackHeight(placed, poses[i].at, pieces[i].Lift, lowestGround) : poses[i].ground;
					instance.transform.SetPositionAndRotation(new Vector3(poses[i].at.x, y, poses[i].at.z), Quaternion.Euler(0f, poses[i].yaw, 0f));
					placed.Add(instance);
					if (pieces[i].RedCross)
					{
						AddRedCross(instance.transform);
					}
					Placed++;
				}
				Sync();
				return true;
			}

			/// <summary>The top of whatever already stands under a lifted piece, or the lift above ground.</summary>
			private static float StackHeight(List<GameObject> placed, Vector3 at, float lift, float ground)
			{
				float top = float.MinValue;
				foreach (GameObject below in placed)
				{
					foreach (Renderer renderer in below.GetComponentsInChildren<Renderer>())
					{
						Bounds b = renderer.bounds;
						if (at.x >= b.min.x && at.x <= b.max.x && at.z >= b.min.z && at.z <= b.max.z)
						{
							top = Mathf.Max(top, b.max.y);
						}
					}
				}
				return top > float.MinValue ? top - 0.03f : ground + lift;
			}

			/// <summary>A red cross on the face of a medical cabinet that its own +x looks out of.</summary>
			private void AddRedCross(Transform cabinet)
			{
				if (redMaterial == null)
				{
					return;
				}
				Bounds local = LocalBounds(cabinet);
				Vector3 face = cabinet.position + cabinet.rotation * new Vector3(local.max.x + 0.01f, local.size.y * 0.62f, local.center.z);
				for (int i = 0; i < 2; i++)
				{
					GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
					bar.name = "Red Cross";
					Object.DestroyImmediate(bar.GetComponent<Collider>());
					bar.transform.SetParent(cabinet, true);
					bar.transform.SetPositionAndRotation(face, cabinet.rotation);
					bar.transform.localScale = i == 0 ? new Vector3(0.02f, 0.1f, 0.36f) : new Vector3(0.02f, 0.36f, 0.1f);
					bar.GetComponent<MeshRenderer>().sharedMaterial = redMaterial;
				}
			}

			/// <summary>
			/// Whether a prefab may stand at <paramref name="at"/> turned to <paramref name="yaw"/>, and
			/// the ground height it would sit at: dry land, gentle and even ground, no tree, no road, no
			/// keep-out, no collider.
			/// </summary>
			public bool CanPlace(string prefabName, Vector3 at, float yaw, out float ground)
			{
				ground = 0f;
				GameObject prefab = Prefab(prefabName);
				if (prefab == null)
				{
					Refusal = "no prefab";
					return false;
				}
				Bounds local = Footprint(prefabName, prefab);
				Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
				Vector2 centre = Flat(at + rotation * new Vector3(local.center.x, 0f, local.center.z));
				Vector2 half = new Vector2(local.extents.x, local.extents.z);

				foreach (Vector3 surface in surfaces)
				{
					if (DistanceToBox(new Vector2(surface.x, surface.z), centre, half, yaw) < SurfaceClearance)
					{
						Refusal = "relevant surface";
						return false;
					}
				}
				foreach (Vector3 circle in keepOutCircles)
				{
					if (DistanceToBox(new Vector2(circle.x, circle.y), centre, half, yaw) < circle.z)
					{
						Refusal = $"keep-out circle at ({circle.x:0},{circle.y:0}) r{circle.z:0.#}";
						return false;
					}
				}
				foreach (var box in keepOutBoxes)
				{
					if (!IgnoreKeepOutBoxes && BoxesOverlap(centre, half, yaw, box.centre, box.half, box.yaw))
					{
						Refusal = $"keep-out box at ({box.centre.x:0},{box.centre.y:0})";
						return false;
					}
				}
				if (!IgnoreRoads && OnRoad(centre, half + new Vector2(RoadMargin, RoadMargin), yaw))
				{
					Refusal = "road";
					return false;
				}

				// Every corner of the footprint, on dry, gentle ground.
				float lowest = float.MaxValue;
				float highest = float.MinValue;
				for (int i = 0; i < 5; i++)
				{
					Vector3 corner = i == 4
						? local.center
						: new Vector3(i % 2 == 0 ? local.min.x : local.max.x, 0f, i < 2 ? local.min.z : local.max.z);
					Vector3 world = at + rotation * new Vector3(corner.x, 0f, corner.z);
					float height = terrain.SampleHeight(world) + terrain.GetPosition().y;
					if (IsWater(new Vector3(world.x, height, world.z)) || Slope(world) > MaxSlopeDegrees)
					{
						Refusal = "water or slope";
						return false;
					}
					lowest = Mathf.Min(lowest, height);
					highest = Mathf.Max(highest, height);
				}
				if (highest - lowest > MaxFootprintDrop)
				{
					Refusal = $"uneven ({highest - lowest:0.0} m)";
					return false;
				}

				float reach = Mathf.Max(local.extents.x, local.extents.z) + TreeClearance;
				for (int i = 0; i < trees.Count; i++)
				{
					Vector2 d = Flat(trees[i]) - centre;
					if (d.sqrMagnitude < reach * reach && DistanceToBox(Flat(trees[i]), centre, half, yaw) < TreeClearance)
					{
						Refusal = "tree";
						return false;
					}
				}

				// Sit on the lowest corner, sunk a little so no edge floats on a slope; or on a paved
				// surface (an HQ pad) a few centimetres above the terrain, when one is under the piece.
				ground = lowest - 0.05f;
				Vector3 middle = at + rotation * new Vector3(local.center.x, 0f, local.center.z);
				if (Physics.Raycast(new Vector3(middle.x, lowest + 2f, middle.z), Vector3.down, out RaycastHit floor, 3f, ~0, QueryTriggerInteraction.Ignore)
					&& !(floor.collider is TerrainCollider) && !waterColliders.Contains(floor.collider as MeshCollider)
					&& floor.point.y >= lowest - 0.1f && floor.point.y <= lowest + 0.4f)
				{
					ground = floor.point.y;
				}
				if (InsideSolid(at, rotation, local, ground, out string solid))
				{
					Refusal = "inside " + solid;
					return false;
				}
				Vector3 position = new Vector3(at.x, ground, at.z);
				Vector3 boxCentre = position + rotation * new Vector3(local.center.x, local.center.y + 0.15f, local.center.z);
				Vector3 extents = new Vector3(local.extents.x * 0.95f, Mathf.Max(0.05f, local.extents.y - 0.15f), local.extents.z * 0.95f);
				foreach (Collider hit in Physics.OverlapBox(boxCentre, extents, rotation, ~0, QueryTriggerInteraction.Ignore))
				{
					if (hit is TerrainCollider || waterColliders.Contains(hit as MeshCollider))
					{
						continue;
					}
					Refusal = "collider " + hit.name;
					return false;
				}
				return true;
			}

			/// <summary>
			/// Whether any corner or the middle of a footprint lies inside a collider: an upward ray
			/// from just above the ground that first meets a face looking up has started inside it.
			/// </summary>
			/// <remarks>
			/// The overlap box cannot see this: against a non-convex mesh it reports only the faces it
			/// crosses, and a hesco block wholly inside a quarry rock crosses none. One was built that
			/// way, its upper block sitting on the rock's crown.
			/// </remarks>
			private bool InsideSolid(Vector3 at, Quaternion rotation, Bounds local, float ground, out string solid)
			{
				solid = null;
				bool backfaces = Physics.queriesHitBackfaces;
				Physics.queriesHitBackfaces = true;
				try
				{
					for (int i = 0; i < 5; i++)
					{
						Vector3 corner = i == 4
							? local.center
							: new Vector3(i % 2 == 0 ? local.min.x : local.max.x, 0f, i < 2 ? local.min.z : local.max.z);
						Vector3 world = at + rotation * new Vector3(corner.x, 0f, corner.z);
						if (Physics.Raycast(new Vector3(world.x, ground + 0.3f, world.z), Vector3.up, out RaycastHit hit, 40f, ~0, QueryTriggerInteraction.Ignore)
							&& !(hit.collider is TerrainCollider) && !waterColliders.Contains(hit.collider as MeshCollider)
							&& Vector3.Dot(hit.normal, Vector3.up) > 0f)
						{
							solid = hit.collider.name;
							return true;
						}
					}
					return false;
				}
				finally
				{
					Physics.queriesHitBackfaces = backfaces;
				}
			}

			/// <summary>A prefab's bounds in its own space, from its renderers.</summary>
			public Bounds FootprintOf(string name)
			{
				GameObject prefab = Prefab(name);
				return prefab != null ? Footprint(name, prefab) : new Bounds(Vector3.zero, Vector3.one * 2f);
			}

			/// <summary>
			/// The nearest point to <paramref name="near"/>, searched outward in a square spiral of
			/// 5 m steps, that is road, flat, dry, free of colliders for 8 m, and at least
			/// <paramref name="clearance"/> beyond every flag's capture range.
			/// </summary>
			public bool TryFindOpenRoad(Vector3 near, CapturePoint[] points, float clearance, out Vector3 road)
			{
				const float step = 5f;
				const int rings = 80;
				for (int ring = 0; ring <= rings; ring++)
				{
					for (int i = -ring; i <= ring; i++)
					{
						for (int side = 0; side < 4; side++)
						{
							int gx = side switch { 0 => i, 1 => ring, 2 => -i, _ => -ring };
							int gz = side switch { 0 => ring, 1 => -i, 2 => -ring, _ => i };
							if (ring > 0 && Mathf.Abs(gx) != ring && Mathf.Abs(gz) != ring)
							{
								continue;
							}
							Vector3 probe = near + new Vector3(gx * step, 0f, gz * step);
							if (IsOpenRoad(probe, points, clearance, out road))
							{
								return true;
							}
						}
					}
				}
				road = Vector3.zero;
				return false;
			}

			private bool IsOpenRoad(Vector3 probe, CapturePoint[] points, float clearance, out Vector3 road)
			{
				road = new Vector3(probe.x, terrain.SampleHeight(probe) + terrain.GetPosition().y, probe.z);
				if (RoadWeightAt(probe) <= RoadWeight || Slope(probe) > 8f || IsWater(road))
				{
					return false;
				}
				foreach (CapturePoint point in points)
				{
					if (Flat(point.transform.position - road).magnitude < point.captureRange + clearance)
					{
						return false;
					}
				}
				foreach (Collider hit in Physics.OverlapSphere(road + Vector3.up, 8f, ~0, QueryTriggerInteraction.Ignore))
				{
					if (!(hit is TerrainCollider) && !waterColliders.Contains(hit as MeshCollider))
					{
						return false;
					}
				}
				return true;
			}

			private bool OnRoad(Vector2 centre, Vector2 half, float yaw)
			{
				if (splat == null)
				{
					return false;
				}
				Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
				for (float x = -half.x; x <= half.x + 0.01f; x += Mathf.Max(0.5f, half.x / 3f))
				{
					for (float z = -half.y; z <= half.y + 0.01f; z += Mathf.Max(0.5f, half.y / 3f))
					{
						Vector3 world = new Vector3(centre.x, 0f, centre.y) + rotation * new Vector3(x, 0f, z);
						if (RoadWeightAt(world) > RoadWeight)
						{
							return true;
						}
					}
				}
				return false;
			}

			private float RoadWeightAt(Vector3 world)
			{
				if (splat == null)
				{
					return 0f;
				}
				Vector3 origin = terrain.GetPosition();
				int x = Mathf.Clamp(Mathf.RoundToInt((world.x - origin.x) / data.size.x * (data.alphamapWidth - 1)), 0, data.alphamapWidth - 1);
				int z = Mathf.Clamp(Mathf.RoundToInt((world.z - origin.z) / data.size.z * (data.alphamapHeight - 1)), 0, data.alphamapHeight - 1);
				return splat[z, x, roadLayer];
			}

			private bool IsWater(Vector3 ground)
			{
				Ray down = new Ray(new Vector3(ground.x, 2000f, ground.z), Vector3.down);
				foreach (MeshCollider water in waterColliders)
				{
					if (water.Raycast(down, out RaycastHit hit, 4000f) && hit.point.y > ground.y - 0.2f)
					{
						return true;
					}
				}
				return false;
			}

			private float Slope(Vector3 world)
			{
				Vector3 origin = terrain.GetPosition();
				float u = (world.x - origin.x) / data.size.x;
				float v = (world.z - origin.z) / data.size.z;
				return data.GetSteepness(u, v);
			}

			private GameObject Prefab(string name)
			{
				if (!prefabs.TryGetValue(name, out GameObject prefab))
				{
					prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PropFolder + name + ".prefab");
					if (prefab == null)
					{
						Log.AppendLine("  WARNING: no prefab " + PropFolder + name + ".prefab");
					}
					prefabs[name] = prefab;
				}
				return prefab;
			}

			private Bounds Footprint(string name, GameObject prefab)
			{
				if (footprints.TryGetValue(name, out Bounds bounds))
				{
					return bounds;
				}
				var probe = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
				probe.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
				Renderer[] renderers = probe.GetComponentsInChildren<Renderer>();
				bounds = renderers.Length > 0 ? renderers[0].bounds : new Bounds(Vector3.zero, Vector3.one);
				foreach (Renderer renderer in renderers)
				{
					bounds.Encapsulate(renderer.bounds);
				}
				Object.DestroyImmediate(probe);
				footprints[name] = bounds;
				return bounds;
			}

			private static Vector2 Flat(Vector3 v) => new Vector2(v.x, v.z);

			/// <summary>Distance from a point to a turned rectangle (0 inside).</summary>
			private static float DistanceToBox(Vector2 point, Vector2 centre, Vector2 half, float yaw)
			{
				Vector3 local = Quaternion.Euler(0f, -yaw, 0f) * new Vector3(point.x - centre.x, 0f, point.y - centre.y);
				float dx = Mathf.Max(Mathf.Abs(local.x) - half.x, 0f);
				float dz = Mathf.Max(Mathf.Abs(local.z) - half.y, 0f);
				return Mathf.Sqrt(dx * dx + dz * dz);
			}

			/// <summary>Whether two turned rectangles overlap (separating axis test).</summary>
			private static bool BoxesOverlap(Vector2 ca, Vector2 ha, float yawA, Vector2 cb, Vector2 hb, float yawB)
			{
				Vector2[] axes = { Axis(yawA, 0f), Axis(yawA, 90f), Axis(yawB, 0f), Axis(yawB, 90f) };
				Vector2 d = cb - ca;
				foreach (Vector2 axis in axes)
				{
					float ra = Projected(ha, yawA, axis);
					float rb = Projected(hb, yawB, axis);
					if (Mathf.Abs(Vector2.Dot(d, axis)) > ra + rb)
					{
						return false;
					}
				}
				return true;
			}

			private static Vector2 Axis(float yaw, float extra)
			{
				float radians = (yaw + extra) * Mathf.Deg2Rad;
				return new Vector2(Mathf.Sin(radians), Mathf.Cos(radians));
			}

			private static float Projected(Vector2 half, float yaw, Vector2 axis)
			{
				Vector2 x = Axis(yaw, 90f) * half.x;
				Vector2 z = Axis(yaw, 0f) * half.y;
				return Mathf.Abs(Vector2.Dot(x, axis)) + Mathf.Abs(Vector2.Dot(z, axis));
			}
		}
	}
}
