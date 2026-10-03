using System.Collections.Generic;
using System.Text;
using Pathfinding;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ironfront
{
	/// <summary>
	/// Dresses every flag and HQ of Forest Lake as a fortified outpost: a sandbag nest round the
	/// flag, ammunition dumps and a medical post (<see cref="SupplyCache"/>s that serve whichever
	/// side holds the flag), a hard point and towers to fight from, a continuous perimeter of hesco,
	/// concrete and sandbag walls that leaves the roads open as gates, obstacles on the approach,
	/// floodlights and themed clutter.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Owner request 2026-10-03:</b> the flags were a pole on bare ground and holding one meant
	/// nothing beyond a spawn point. This is a builder, not hand placement, so the layout is a
	/// function of the code and the terrain: re-running it deletes <see cref="RootName"/> and
	/// rebuilds the same thing (every random draw is seeded by the flag's name).
	/// </para>
	/// <para>
	/// <b>Every piece is checked before it is kept</b>: no water under any corner, no slope steeper
	/// than <see cref="MaxSlopeDegrees"/>, no more than <see cref="MaxFootprintDrop"/> of height
	/// change across the footprint, no tree trunk inside it, no overlap with any collider already
	/// there, perimeter pieces never on a road, and nothing near the navigation's
	/// <see cref="RelevantGraphSurface"/>.
	/// </para>
	/// <para>
	/// <b>The relevant surface is load-bearing.</b> The infantry graph keeps only the regions that
	/// connect to Forest Lake's one <see cref="RelevantGraphSurface"/> (mode RequireForAll), which
	/// stands 10 m from the Meadow flag. The first version of this builder put an outpost house on
	/// it, the scan kept no region at all, and the cache came out 4 KB with ten cover points.
	/// <see cref="Rebake"/> therefore refuses to write a graph smaller than the one it replaces.
	/// </para>
	/// </remarks>
	public static class ForestLakeOutposts
	{
		public const string ScenePath = "Assets/Scenes/ForestLake.unity";

		public const string RootName = "Outpost Dressing";

		private const string PropFolder = "Assets/MilitaryProps/Prefabs/";

		private const string GraphCachePath = "Assets/TextAsset/ForestLake_GraphCache.bytes";

		private const float MaxSlopeDegrees = 24f;

		private const float MaxFootprintDrop = 1.3f;

		private const float TreeClearance = 1.2f;

		private const float RoadWeight = 0.35f;

		/// <summary>Metres kept clear round a <see cref="RelevantGraphSurface"/>, beyond a piece's own size.</summary>
		private const float SurfaceClearance = 5f;

		/// <summary>A flag at least this big is an HQ: it already has a walled compound.</summary>
		private const float HeadquartersRadius = 28f;

		/// <summary>Metres beyond a flag's capture range the relevant surface keeps clear of it.</summary>
		private const float OutpostClearance = 40f;

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
				foreach (CapturePoint point in points)
				{
					var group = new GameObject("Outpost - " + point.name.Replace(" Capture Point", string.Empty));
					group.transform.SetParent(root.transform, false);
					DressPoint(context, point, group.transform);
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
			log.AppendLine($"saved {ScenePath}: {context.Placed} pieces, {root.GetComponentsInChildren<SupplyCache>().Length} supply caches.");
			return log.ToString();
		}

		// ------------------------------------------------------------------ the layout

		private static void DressPoint(Context context, CapturePoint point, Transform group)
		{
			var random = new System.Random(StableHash(point.name));
			Vector3 centre = point.transform.position;
			float radius = point.captureRange;
			bool headquarters = radius >= HeadquartersRadius;
			// Which way the battle comes from: the middle of the map. "front" faces it.
			Vector3 toMiddle = context.MapMiddle - centre;
			toMiddle.y = 0f;
			float front = toMiddle.sqrMagnitude > 1f ? Mathf.Atan2(toMiddle.x, toMiddle.z) * Mathf.Rad2Deg : 0f;
			float rear = front + 180f;
			int before = context.Placed;

			// 1. Supplies first, so they get the best ground: on the sheltered half of the point.
			int ammoDumps = headquarters ? 3 : 2;
			int medicalPosts = headquarters ? 2 : 1;
			float depot = headquarters ? 12f : Mathf.Clamp(radius * 0.45f, 8f, 11f);
			for (int i = 0; i < ammoDumps; i++)
			{
				PlaceStation(context, point, group, SupplyKind.Ammo, centre, depot, rear - 60f + 120f * i / Mathf.Max(1, ammoDumps - 1) + Jitter(random, 8f), random);
			}
			for (int i = 0; i < medicalPosts; i++)
			{
				PlaceStation(context, point, group, SupplyKind.Medical, centre, depot + 2f, rear + (medicalPosts == 1 ? 0f : -25f + 50f * i) + Jitter(random, 8f), random);
			}

			// 2. A ring of sandbag nests round the flag, a little off it so the flag stays reachable.
			string[] nests = { "MP_Sandbag_Cover_1", "MP_Sandbag_Cover_2", "MP_Sandbag_Cover_3" };
			float nest = headquarters ? 9f : 7.5f;
			for (int i = 0; i < 3; i++)
			{
				float angle = front + 60f + 120f * i + Jitter(random, 10f);
				TryPlaceAround(context, group, nests[i], centre, nest, angle, angle, 6, random, perimeter: false);
			}

			if (!headquarters)
			{
				// 3. Hard points on both flanks: a building to hold and a tall sandbag tower.
				string[] houses = { "MP_Outpost", "BF_Bunker", "MP_Sandbag_Storage" };
				TryPlaceAround(context, group, houses[random.Next(houses.Length)], centre, radius * 0.62f, front + 95f + Jitter(random, 15f), front + 180f, 10, random, perimeter: false);
				string[] towers = { "MP_Sandbag_Watchtower", "MP_Sandbag_Tower", "BF_Bunker" };
				TryPlaceAround(context, group, towers[random.Next(towers.Length)], centre, radius * 0.66f, front - 95f + Jitter(random, 15f), front, 10, random, perimeter: false);

				// 4. The perimeter: a hard front of hesco and concrete, sandbag walls down the
				// flanks, the rear left open. Roads break every arc into gates.
				float wall = radius + 1.5f;
				WallArc(context, group, centre, wall, front - 50f, front + 50f, new[] { "BF_Hesco", "BF_Hesco", "MP_Fence" }, random);
				WallArc(context, group, centre, wall, front + 62f, front + 125f, new[] { "MP_Sandbag_Line_2", "BF_Hesco", "MP_Sandbag_Line_1" }, random);
				WallArc(context, group, centre, wall, front - 125f, front - 62f, new[] { "MP_Sandbag_Line_1", "BF_Hesco", "MP_Sandbag_Line_2" }, random);
				WallArc(context, group, centre, wall, front + 138f, front + 160f, new[] { "BF_Hesco" }, random);
				WallArc(context, group, centre, wall, front - 160f, front - 138f, new[] { "BF_Hesco" }, random);

				// 5. Obstacles on the approach: staggered hedgehogs and wire.
				for (int row = 0; row < 2; row++)
				{
					float distance = radius + 8f + row * 5f;
					for (int i = 0; i < 6; i++)
					{
						float angle = front - 55f + 22f * i + (row % 2) * 11f + Jitter(random, 4f);
						string obstacle = (i + row) % 3 == 0 ? "MP_Barbed_Wire" : ((i + row) % 3 == 1 ? "BF_Hedgehog" : "BF_Wire_Stands");
						float yaw = obstacle == "MP_Barbed_Wire" ? angle + 90f : angle;
						TryPlaceAround(context, group, obstacle, centre, distance, angle, yaw, 3, random, perimeter: true);
					}
				}

				// 6. A wooden lookout at the rear corner.
				TryPlaceAround(context, group, "MP_Watchtower_Wood", centre, radius * 0.85f, rear + 40f + Jitter(random, 15f), front, 8, random, perimeter: false);
			}
			else
			{
				// The compound has its walls; give its yard a second line of sandbags facing the front.
				TryPlaceAround(context, group, "MP_Sandbag_Line_3", centre, 16f, front + Jitter(random, 10f), front + 90f, 8, random, perimeter: false);
				TryPlaceAround(context, group, "MP_Sandbag_Storage", centre, 18f, front + 70f + Jitter(random, 15f), front + 180f, 8, random, perimeter: false);
			}

			// 7. Floodlights over the depot.
			for (int i = 0; i < 2; i++)
			{
				TryPlaceAround(context, group, "MP_Roadway_Light", centre, depot + 4.5f, rear - 35f + 70f * i, front, 6, random, perimeter: false);
			}

			// 8. Clutter between it all: crates, barrels, sacks and a theme per place.
			string[] clutter = Clutter(point.name);
			int clutterCount = headquarters ? 12 : 18;
			for (int i = 0; i < clutterCount; i++)
			{
				string prop = clutter[random.Next(clutter.Length)];
				float distance = (float)(random.NextDouble() * (radius * 0.9f - 5f) + 5f);
				float angle = (float)(random.NextDouble() * 360.0);
				TryPlaceAround(context, group, prop, centre, distance, angle, (float)(random.NextDouble() * 360.0), 4, random, perimeter: false);
			}

			context.Log.AppendLine($"{point.name}: {context.Placed - before} pieces ({(headquarters ? "HQ" : "outpost")}, radius {radius:0} m).");
		}

		/// <summary>
		/// A continuous wall round <paramref name="centre"/> from one bearing to another, each piece
		/// laid along the arc on its own long axis and butted against the last. A piece that cannot
		/// stand (a road, water, a slope, a tree) leaves a gap the width of a hesco block.
		/// </summary>
		private static void WallArc(Context context, Transform group, Vector3 centre, float radius, float fromDegrees, float toDegrees, string[] pieces, System.Random random)
		{
			float arcLength = (toDegrees - fromDegrees) * Mathf.Deg2Rad * radius;
			float walked = 0f;
			int index = random.Next(pieces.Length);
			while (walked < arcLength)
			{
				string piece = pieces[index++ % pieces.Length];
				Bounds footprint = context.FootprintOf(piece);
				bool longAlongX = footprint.size.x >= footprint.size.z;
				float length = longAlongX ? footprint.size.x : footprint.size.z;
				float angle = fromDegrees + (walked + length * 0.5f) / radius * Mathf.Rad2Deg;
				Vector3 at = centre + Quaternion.Euler(0f, angle, 0f) * Vector3.forward * radius;
				// The tangent at this bearing is the bearing itself for an x-long piece.
				float yaw = longAlongX ? angle : angle + 90f;
				if (context.TryGround(at, out at) && context.TryPlace(group, piece, at, yaw, perimeter: true))
				{
					walked += length + 0.25f;
					continue;
				}
				// A long wall that cannot lie on this slope: a hesco block can, so the line holds.
				Bounds block = context.FootprintOf(FallbackWall);
				float blockAngle = fromDegrees + (walked + block.size.x * 0.5f) / radius * Mathf.Rad2Deg;
				Vector3 blockAt = centre + Quaternion.Euler(0f, blockAngle, 0f) * Vector3.forward * radius;
				bool held = piece != FallbackWall && context.TryGround(blockAt, out blockAt)
					&& context.TryPlace(group, FallbackWall, blockAt, blockAngle, perimeter: true);
				walked += held ? block.size.x + 0.25f : 2f;
			}
		}

		private static string[] Clutter(string pointName)
		{
			string name = pointName.ToLowerInvariant();
			if (name.Contains("lumber"))
			{
				return new[] { "BF_Log", "BF_Log", "BF_Log", "BF_Beam", "BF_Beam", "BF_Crate_Camo", "MP_Barrel", "BF_Sack_1" };
			}
			if (name.Contains("quarry"))
			{
				return new[] { "BF_Stone", "BF_Stone", "BF_Stone", "BF_Sack_2", "MP_Barrel", "BF_Crates_Netted", "BF_Hesco", "MP_Transformer" };
			}
			if (name.Contains("ford") || name.Contains("lakeshore") || name.Contains("island"))
			{
				return new[] { "BF_Sack_1", "BF_Sack_3", "BF_Crate_Camo", "MP_Barrel", "BF_Log", "BF_Crates_Netted", "BF_Hesco" };
			}
			return new[] { "BF_Crate_Camo", "BF_Crates_Netted", "MP_Barrel", "BF_Sack_1", "BF_Sack_2", "BF_Hesco", "MP_Utility_Box" };
		}

		/// <summary>
		/// An ammunition dump (camo crates two high, netted crates, a stand of barrels) or a medical
		/// post (white cabinets with a red cross, stretchers), with its <see cref="SupplyCache"/>.
		/// </summary>
		private static void PlaceStation(Context context, CapturePoint point, Transform group, SupplyKind kind, Vector3 centre, float distance, float angle, System.Random random)
		{
			for (int attempt = 0; attempt < 18; attempt++)
			{
				float tryDistance = distance + (attempt / 2) * 0.8f;
				float tryAngle = angle + (attempt % 2 == 0 ? 1 : -1) * attempt * 9f;
				Vector3 at = centre + Quaternion.Euler(0f, tryAngle, 0f) * Vector3.forward * tryDistance;
				// The station's front faces the flag.
				float yaw = tryAngle + 180f;
				if (!context.TryGround(at, out at))
				{
					continue;
				}
				var station = new GameObject(kind == SupplyKind.Ammo ? "Ammo Cache" : "Medical Station");
				station.transform.SetParent(group, false);
				station.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, yaw, 0f));
				Vector3 right = station.transform.right;
				Vector3 back = -station.transform.forward;
				bool ok;
				if (kind == SupplyKind.Ammo)
				{
					GameObject left = context.Place(station.transform, "BF_Crate_Camo", at - right * 0.9f, yaw);
					GameObject rightCrate = left != null ? context.Place(station.transform, "BF_Crate_Camo", at + right * 0.95f, yaw + 4f) : null;
					ok = left != null && rightCrate != null;
					if (ok)
					{
						context.Stack(station.transform, "BF_Crate_Camo", left.transform, yaw + 90f);
						context.Stack(station.transform, "BF_Crates_Netted", rightCrate.transform, yaw - 6f);
						context.Place(station.transform, "BF_Crates_Netted", at + back * 1.7f, yaw + 2f);
						for (int b = 0; b < 2; b++)
						{
							context.Place(station.transform, "MP_Barrel", at + right * 2.4f + back * (0.3f + 0.6f * b), yaw);
						}
						context.Place(station.transform, "BF_Sack_2", at - right * 2.9f + back * 0.4f, yaw + 90f);
					}
				}
				else
				{
					GameObject cabinet = context.Place(station.transform, "MP_Utility_Box", at, yaw);
					GameObject second = cabinet != null ? context.Place(station.transform, "MP_Utility_Box", at + right * 0.7f, yaw) : null;
					ok = cabinet != null && second != null;
					if (ok)
					{
						context.Place(station.transform, "MP_Utility_Box", at - right * 0.7f, yaw);
						AddRedCross(context, station.transform, at, yaw);
						AddRedCross(context, station.transform, at + right * 0.7f, yaw);
						context.Place(station.transform, "BF_Sack_3", at + back * 2.2f, yaw);
						context.Place(station.transform, "BF_Sack_1", at + back * 2.2f + right * 2.2f, yaw + 8f);
						context.Place(station.transform, "BF_Crate_Camo", at - right * 2.3f + back * 0.6f, yaw + 15f);
					}
				}
				if (!ok)
				{
					Object.DestroyImmediate(station);
					context.Sync();
					continue;
				}
				SupplyCache cache = station.AddComponent<SupplyCache>();
				cache.kind = kind;
				cache.point = point;
				context.Log.AppendLine($"  {station.name} at {at:F0}, {Vector3.Distance(at, centre):0.0} m from the flag.");
				return;
			}
			context.Log.AppendLine($"  WARNING: no ground for a {kind} station at {point.name}.");
		}

		/// <summary>A red cross on the front of a medical cabinet, in the red barrel's own material.</summary>
		private static void AddRedCross(Context context, Transform station, Vector3 at, float yaw)
		{
			Material red = context.RedMaterial;
			if (red == null)
			{
				return;
			}
			Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);
			// The cabinet faces its local +z after the yaw (toward the flag); the cross sits on that face.
			Vector3 face = at + rotation * new Vector3(0f, 0.62f, 0.47f);
			for (int i = 0; i < 2; i++)
			{
				GameObject bar = GameObject.CreatePrimitive(PrimitiveType.Cube);
				bar.name = "Red Cross";
				Object.DestroyImmediate(bar.GetComponent<Collider>());
				bar.transform.SetParent(station, true);
				bar.transform.SetPositionAndRotation(face, rotation);
				bar.transform.localScale = i == 0 ? new Vector3(0.36f, 0.1f, 0.02f) : new Vector3(0.1f, 0.36f, 0.02f);
				bar.GetComponent<MeshRenderer>().sharedMaterial = red;
			}
		}

		private static void TryPlaceAround(Context context, Transform group, string prefab, Vector3 centre, float distance, float angle, float yaw, int attempts, System.Random random, bool perimeter)
		{
			for (int attempt = 0; attempt < attempts; attempt++)
			{
				float a = angle + (attempt == 0 ? 0f : Jitter(random, 6f * attempt));
				float d = distance + (attempt == 0 ? 0f : Jitter(random, 1.5f * attempt));
				Vector3 at = centre + Quaternion.Euler(0f, a, 0f) * Vector3.forward * d;
				if (!context.TryGround(at, out at))
				{
					continue;
				}
				if (context.TryPlace(group, prefab, at, yaw + (a - angle), perimeter))
				{
					return;
				}
			}
		}

		private static float Jitter(System.Random random, float amount) => (float)((random.NextDouble() * 2.0 - 1.0) * amount);

		private static int StableHash(string text)
		{
			unchecked
			{
				int hash = 23;
				foreach (char c in text)
				{
					hash = hash * 31 + c;
				}
				return hash;
			}
		}

		// ------------------------------------------------------------------ pathfinding

		/// <summary>
		/// Puts the graphs' <see cref="RelevantGraphSurface"/> on an open stretch of road near the
		/// middle of the map, clear of every outpost.
		/// </summary>
		/// <remarks>
		/// Both recast graphs keep only the regions that connect to it (RequireForAll). It stood 10 m
		/// from the Meadow flag; once Meadow had walls and crates, the car graph (a 4 m agent) found
		/// the space round it sealed off and kept 5 nodes of 15315. A road is where the vehicle
		/// network is, by definition, so the surface belongs on one.
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
			context.Log.AppendLine($"relevant surface moved {before:F0} -> {surface.transform.position:F0}, an open road {Vector3.Distance(road, context.MapMiddle):0} m from the middle.");
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
			// In edit mode AstarPath never ran Awake: no singleton, no graphs. Its own Initialize
			// sets both up from the cache, with the graphs' settings, ready to rescan.
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

		// ------------------------------------------------------------------ placement checks

		private sealed class Context
		{
			public readonly StringBuilder Log;

			public int Placed;

			public Vector3 MapMiddle;

			public Material RedMaterial;

			private Terrain terrain;

			private TerrainData data;

			private int roadLayer = -1;

			private float[,,] splat;

			private readonly List<Vector3> trees = new List<Vector3>();

			private readonly List<Vector3> surfaces = new List<Vector3>();

			private readonly List<MeshCollider> waterColliders = new List<MeshCollider>();

			private readonly Dictionary<string, GameObject> prefabs = new Dictionary<string, GameObject>();

			private readonly Dictionary<string, Bounds> footprints = new Dictionary<string, Bounds>();

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
				RedMaterial = barrelRenderer != null ? barrelRenderer.sharedMaterial : null;
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

			/// <summary>Drops a point onto the terrain; false over water.</summary>
			public bool TryGround(Vector3 at, out Vector3 ground)
			{
				float height = terrain.SampleHeight(at) + terrain.GetPosition().y;
				ground = new Vector3(at.x, height, at.z);
				return !IsWater(ground);
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
				road = Vector3.zero;
				if (!IsRoad(probe) || Slope(probe) > 8f || !TryGround(probe, out road))
				{
					return false;
				}
				foreach (CapturePoint point in points)
				{
					Vector3 d = point.transform.position - road;
					d.y = 0f;
					if (d.magnitude < point.captureRange + clearance)
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

			/// <summary>A prefab's bounds in its own space, from its renderers.</summary>
			public Bounds FootprintOf(string name)
			{
				GameObject prefab = Prefab(name);
				return prefab != null ? Footprint(name, prefab) : new Bounds(Vector3.zero, Vector3.one * 2f);
			}

			/// <summary>
			/// A small piece of a station, at its spot or not at all: the same ground and overlap
			/// checks as <see cref="TryPlace"/>, without the search.
			/// </summary>
			public GameObject Place(Transform parent, string prefabName, Vector3 at, float yaw)
			{
				if (!TryGround(at, out Vector3 ground))
				{
					return null;
				}
				return TryPlace(parent, prefabName, ground, yaw, false) ? parent.GetChild(parent.childCount - 1).gameObject : null;
			}

			/// <summary>A piece set on top of another, its base on the other's top face.</summary>
			public void Stack(Transform parent, string prefabName, Transform below, float yaw)
			{
				GameObject prefab = Prefab(prefabName);
				if (prefab == null || below == null)
				{
					return;
				}
				float top = 0f;
				foreach (Renderer renderer in below.GetComponentsInChildren<Renderer>())
				{
					top = Mathf.Max(top, renderer.bounds.max.y);
				}
				var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
				instance.transform.SetPositionAndRotation(new Vector3(below.position.x, top - 0.02f, below.position.z), Quaternion.Euler(0f, yaw, 0f));
				Placed++;
				Sync();
			}

			public bool TryPlace(Transform parent, string prefabName, Vector3 at, float yaw, bool perimeter)
			{
				GameObject prefab = Prefab(prefabName);
				if (prefab == null)
				{
					return false;
				}
				Bounds local = Footprint(prefabName, prefab);
				Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);

				// Never near the navigation's relevant surface (see the class remarks).
				float keep = Mathf.Max(local.extents.x, local.extents.z) + SurfaceClearance;
				for (int i = 0; i < surfaces.Count; i++)
				{
					Vector3 d = surfaces[i] - at;
					d.y = 0f;
					if (d.sqrMagnitude < keep * keep)
					{
						return false;
					}
				}

				// Every corner of the footprint, on the ground.
				float lowest = float.MaxValue;
				float highest = float.MinValue;
				for (int i = 0; i < 5; i++)
				{
					Vector3 corner = i == 4
						? local.center
						: new Vector3(i % 2 == 0 ? local.min.x : local.max.x, 0f, i < 2 ? local.min.z : local.max.z);
					Vector3 world = at + rotation * new Vector3(corner.x, 0f, corner.z);
					float height = terrain.SampleHeight(world) + terrain.GetPosition().y;
					Vector3 ground = new Vector3(world.x, height, world.z);
					if (IsWater(ground) || Slope(world) > MaxSlopeDegrees)
					{
						return false;
					}
					if (perimeter && IsRoad(world))
					{
						return false;
					}
					lowest = Mathf.Min(lowest, height);
					highest = Mathf.Max(highest, height);
				}
				if (highest - lowest > MaxFootprintDrop)
				{
					return false;
				}

				float reach = Mathf.Max(local.extents.x, local.extents.z) + TreeClearance;
				Vector3 centre = at + rotation * new Vector3(local.center.x, 0f, local.center.z);
				for (int i = 0; i < trees.Count; i++)
				{
					Vector3 d = trees[i] - centre;
					d.y = 0f;
					if (d.sqrMagnitude < reach * reach && IsInsideFootprint(trees[i], at, rotation, local, TreeClearance))
					{
						return false;
					}
				}

				// Sit on the lowest corner, sunk a little so no edge floats on a slope.
				Vector3 position = new Vector3(at.x, lowest - 0.05f, at.z);
				Vector3 boxCentre = position + rotation * new Vector3(local.center.x, local.center.y + 0.15f, local.center.z);
				Vector3 half = new Vector3(local.extents.x * 0.92f, Mathf.Max(0.05f, local.extents.y - 0.15f), local.extents.z * 0.92f);
				foreach (Collider hit in Physics.OverlapBox(boxCentre, half, rotation, ~0, QueryTriggerInteraction.Ignore))
				{
					if (hit is TerrainCollider || waterColliders.Contains(hit as MeshCollider))
					{
						continue;
					}
					return false;
				}

				var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
				instance.transform.SetPositionAndRotation(position, rotation);
				Placed++;
				Sync();
				return true;
			}

			private static bool IsInsideFootprint(Vector3 point, Vector3 at, Quaternion rotation, Bounds local, float margin)
			{
				Vector3 inLocal = Quaternion.Inverse(rotation) * (point - at);
				return inLocal.x > local.min.x - margin && inLocal.x < local.max.x + margin
					&& inLocal.z > local.min.z - margin && inLocal.z < local.max.z + margin;
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

			private bool IsRoad(Vector3 world)
			{
				if (splat == null)
				{
					return false;
				}
				Vector3 origin = terrain.GetPosition();
				int x = Mathf.Clamp(Mathf.RoundToInt((world.x - origin.x) / data.size.x * (data.alphamapWidth - 1)), 0, data.alphamapWidth - 1);
				int z = Mathf.Clamp(Mathf.RoundToInt((world.z - origin.z) / data.size.z * (data.alphamapHeight - 1)), 0, data.alphamapHeight - 1);
				return splat[z, x, roadLayer] > RoadWeight;
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
		}
	}
}
