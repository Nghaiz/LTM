using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Ironfront
{
	/// <summary>
	/// Draws the open map scene's minimap picture from the scene's own data and gives it to the
	/// scene's <see cref="MinimapCamera"/>: shaded terrain coloured by its layers, faint contour
	/// lines, lakes and rivers by depth, every tree's crown, rocks, structures and bridges seen from
	/// above, and the ground outside the play area dimmed.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Owner request 2026-10-03:</b> Forest Lake's map was "cực kì xấu, trông như mặt cắt". The
	/// live render showed flat sand, no forest (the GPU tree instancer is not in a one-off camera
	/// render), magenta where a shader was missing and snow glare at the edges. A picture drawn from
	/// the data shows the map as it plays, and stays readable: the palette is muted, the terrain is
	/// lit from the north-west the way a printed map is, and contours stay faint.
	/// </para>
	/// <para>
	/// <b>Drawn for the camera's own frame.</b> The minimap camera looks straight down and is
	/// orthographic, framed on <see cref="LevelBounds"/> by <see cref="MinimapCamera.TryGetLevelFrame"/>,
	/// so a pixel is a fixed square of ground and every icon placed through the camera lands on the
	/// right spot of the picture. The frame is stored with the picture; a camera whose frame has
	/// moved since renders live again and says so.
	/// </para>
	/// </remarks>
	public static class MinimapBaker
	{
		public const int Size = 2048;

		private const string Folder = "Assets/Textures/Minimap/";

		private const float ContourInterval = 10f;

		private static readonly Vector3 Light = new Vector3(-1f, 1.6f, 1f).normalized;

		private static readonly Color Grass = Hex(0x768F4E);
		private static readonly Color ForestFloor = Hex(0x4A6238);
		private static readonly Color Path = Hex(0xCDB88C);
		private static readonly Color Rock = Hex(0x8F8B82);
		private static readonly Color OffTerrain = Hex(0x55604A);
		private static readonly Color ShallowWater = Hex(0x76A8C6);
		private static readonly Color DeepWater = Hex(0x2E5D82);
		private static readonly Color Shore = Hex(0xB9D3E2);
		private static readonly Color CrownShadow = Hex(0x26391D);
		private static readonly Color Crown = Hex(0x3E5B2E);
		private static readonly Color CrownLight = Hex(0x5C7D42);
		private static readonly Color Structure = Hex(0x5E636B);
		private static readonly Color Sandbag = Hex(0xA99B73);
		private static readonly Color Wood = Hex(0x8A6A45);
		private static readonly Color StoneFeature = Hex(0x9E998F);
		private static readonly Color Outline = Hex(0x2E3136);

		private enum Feature : byte
		{
			None,
			Rock,
			Structure,
			Sandbag,
			Wood,
		}

		[MenuItem("Ironfront/Maps/Bake minimap picture")]
		public static void BakeFromMenu() => Debug.Log("[minimap bake] " + Bake());

		/// <summary>Bakes the active scene's picture, saves it, assigns it and saves the scene.</summary>
		public static string Bake()
		{
			var watch = System.Diagnostics.Stopwatch.StartNew();
			MinimapCamera camera = Object.FindFirstObjectByType<MinimapCamera>();
			if (camera == null)
			{
				throw new System.InvalidOperationException("The open scene has no MinimapCamera.");
			}
			if (!MinimapCamera.TryGetLevelFrame(out Vector2 centre, out float halfSpan))
			{
				throw new System.InvalidOperationException("The open scene has no LevelBounds, so its minimap frame is not fixed; nothing to bake for.");
			}
			Terrain terrain = Object.FindFirstObjectByType<Terrain>();
			if (terrain == null)
			{
				throw new System.InvalidOperationException("The open scene has no terrain.");
			}
			var picture = new Picture(centre, halfSpan);
			picture.Ground(terrain);
			picture.Water();
			int features = picture.Features();
			picture.Trees(terrain);
			picture.PlayArea(Object.FindFirstObjectByType<LevelBounds>().WorldBox);

			string scene = EditorSceneManager.GetActiveScene().name;
			string path = Folder + scene + "_Minimap.png";
			Directory.CreateDirectory(Folder);
			File.WriteAllBytes(path, picture.Encode());
			AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
			var importer = (TextureImporter)AssetImporter.GetAtPath(path);
			importer.textureType = TextureImporterType.Default;
			importer.sRGBTexture = true;
			importer.mipmapEnabled = true;
			importer.filterMode = FilterMode.Trilinear;
			importer.wrapMode = TextureWrapMode.Clamp;
			importer.maxTextureSize = Size;
			importer.textureCompression = TextureImporterCompression.CompressedHQ;
			importer.alphaSource = TextureImporterAlphaSource.None;
			importer.SaveAndReimport();

			camera.bakedPicture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
			camera.bakedCentre = centre;
			camera.bakedHalfSpan = halfSpan;
			EditorUtility.SetDirty(camera);
			EditorSceneManager.MarkSceneDirty(camera.gameObject.scene);
			EditorSceneManager.SaveScene(camera.gameObject.scene);
			return $"{path}: {Size} px over {halfSpan * 2f:0} m ({halfSpan * 2f / Size:0.00} m per pixel), centred {centre:F1}; "
				+ $"{features} structures and rocks, {terrain.terrainData.treeInstanceCount} trees; {watch.Elapsed.TotalSeconds:0.0} s.";
		}

		private static Color Hex(int rgb) => new Color(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

		/// <summary>The picture being drawn, a pixel per (2 * halfSpan / Size) metres of ground.</summary>
		private sealed class Picture
		{
			private readonly Vector2 origin;

			private readonly float metresPerPixel;

			private readonly Color[] colour = new Color[Size * Size];

			private readonly float[] ground = new float[Size * Size];

			private readonly float[] water = new float[Size * Size];

			private readonly float[] featureTop = new float[Size * Size];

			private readonly Feature[] feature = new Feature[Size * Size];

			public Picture(Vector2 centre, float halfSpan)
			{
				origin = centre - new Vector2(halfSpan, halfSpan);
				metresPerPixel = halfSpan * 2f / Size;
				for (int i = 0; i < water.Length; i++)
				{
					water[i] = float.NegativeInfinity;
					featureTop[i] = float.NegativeInfinity;
				}
			}

			private Vector2 World(int x, int y) => origin + new Vector2((x + 0.5f) * metresPerPixel, (y + 0.5f) * metresPerPixel);

			private Vector2 Pixel(Vector3 world) => new Vector2((world.x - origin.x) / metresPerPixel - 0.5f, (world.z - origin.y) / metresPerPixel - 0.5f);

			/// <summary>Terrain colour by layer, lit from the north-west, with faint contours.</summary>
			public void Ground(Terrain terrain)
			{
				TerrainData data = terrain.terrainData;
				Vector3 terrainOrigin = terrain.GetPosition();
				Vector3 terrainSize = data.size;
				Color[] layerColour = LayerColours(data);
				int alphaWidth = data.alphamapWidth;
				int alphaHeight = data.alphamapHeight;
				float[,,] splat = data.GetAlphamaps(0, 0, alphaWidth, alphaHeight);
				int layers = layerColour.Length;
				for (int y = 0; y < Size; y++)
				{
					for (int x = 0; x < Size; x++)
					{
						int i = y * Size + x;
						Vector2 world = World(x, y);
						float u = (world.x - terrainOrigin.x) / terrainSize.x;
						float v = (world.y - terrainOrigin.z) / terrainSize.z;
						if (u < 0f || u > 1f || v < 0f || v > 1f)
						{
							ground[i] = float.NegativeInfinity;
							colour[i] = OffTerrain;
							continue;
						}
						ground[i] = data.GetInterpolatedHeight(u, v) + terrainOrigin.y;
						int ax = Mathf.Clamp(Mathf.RoundToInt(u * (alphaWidth - 1)), 0, alphaWidth - 1);
						int ay = Mathf.Clamp(Mathf.RoundToInt(v * (alphaHeight - 1)), 0, alphaHeight - 1);
						Color baseColour = Color.black;
						float total = 0f;
						for (int l = 0; l < layers; l++)
						{
							float w = splat[ay, ax, l];
							baseColour += layerColour[l] * w;
							total += w;
						}
						baseColour = total > 0.001f ? baseColour / total : Grass;
						Vector3 normal = data.GetInterpolatedNormal(u, v);
						float lit = 0.62f + 0.55f * Mathf.Clamp01(Vector3.Dot(normal, Light));
						colour[i] = Scale(baseColour, lit);
					}
				}
				// Contours: a pixel whose height band differs from its right or upper neighbour's.
				for (int y = 0; y < Size - 1; y++)
				{
					for (int x = 0; x < Size - 1; x++)
					{
						int i = y * Size + x;
						if (float.IsNegativeInfinity(ground[i]))
						{
							continue;
						}
						int band = Mathf.FloorToInt(ground[i] / ContourInterval);
						int right = Mathf.FloorToInt(ground[i + 1] / ContourInterval);
						int up = Mathf.FloorToInt(ground[i + Size] / ContourInterval);
						if (band != right || band != up)
						{
							bool index = Mathf.Max(band, Mathf.Max(right, up)) % 5 == 0;
							colour[i] = Scale(colour[i], index ? 0.84f : 0.92f);
						}
					}
				}
			}

			private static Color[] LayerColours(TerrainData data)
			{
				TerrainLayer[] layers = data.terrainLayers;
				var colours = new Color[layers.Length];
				for (int l = 0; l < layers.Length; l++)
				{
					string name = layers[l] != null ? layers[l].name.ToLowerInvariant() : string.Empty;
					colours[l] = name.Contains("path") || name.Contains("dirt") || name.Contains("road") || name.Contains("sand") || name.Contains("mud") || name.Contains("soil") || name.Contains("gravel")
						? Path
						: (name.Contains("rock") || name.Contains("cliff") || name.Contains("stone") ? Rock : Grass);
				}
				return colours;
			}

			/// <summary>Lakes and rivers: every water mesh's surface, where it lies over the ground.</summary>
			public void Water()
			{
				var meshes = new List<MeshFilter>();
				foreach (WaterLevel level in Object.FindObjectsByType<WaterLevel>(FindObjectsSortMode.None))
				{
					meshes.AddRange(level.GetComponentsInChildren<MeshFilter>());
				}
				foreach (MeshFilter filter in Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
				{
					string name = filter.name.ToLowerInvariant();
					if ((name.Contains("river") || name.Contains("water")) && !meshes.Contains(filter))
					{
						meshes.Add(filter);
					}
				}
				foreach (MeshFilter filter in meshes)
				{
					if (filter.sharedMesh == null || !filter.gameObject.activeInHierarchy)
					{
						continue;
					}
					Rasterise(filter.sharedMesh, filter.transform.localToWorldMatrix, (i, height, normal) =>
					{
						if (height > water[i])
						{
							water[i] = height;
						}
					});
				}
				for (int i = 0; i < colour.Length; i++)
				{
					float depth = water[i] - ground[i];
					if (depth > 0.05f)
					{
						colour[i] = Color.Lerp(ShallowWater, DeepWater, Mathf.Clamp01(depth / 7f));
					}
				}
				// A bright shore line on the water's edge.
				for (int y = 1; y < Size - 1; y++)
				{
					for (int x = 1; x < Size - 1; x++)
					{
						int i = y * Size + x;
						if (IsWater(i) && (!IsWater(i - 1) || !IsWater(i + 1) || !IsWater(i - Size) || !IsWater(i + Size)))
						{
							colour[i] = Color.Lerp(colour[i], Shore, 0.7f);
						}
					}
				}
			}

			private bool IsWater(int i) => water[i] - ground[i] > 0.05f;

			/// <summary>
			/// Rocks, structures, sandbags and bridges, seen from above: every mesh that stands on
			/// the ground and is not terrain, water, vegetation or an effect.
			/// </summary>
			public int Features()
			{
				int count = 0;
				foreach (MeshRenderer renderer in Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
				{
					if (!renderer.enabled || !renderer.gameObject.activeInHierarchy)
					{
						continue;
					}
					Feature kind = Classify(renderer);
					if (kind == Feature.None)
					{
						continue;
					}
					MeshFilter filter = renderer.GetComponent<MeshFilter>();
					if (filter == null || filter.sharedMesh == null)
					{
						continue;
					}
					Bounds bounds = renderer.bounds;
					// Off the picture, or a flat decal: not drawn.
					if (bounds.size.y < 0.15f || !Overlaps(bounds))
					{
						continue;
					}
					count++;
					Rasterise(filter.sharedMesh, renderer.transform.localToWorldMatrix, (i, height, normal) =>
					{
						if (height > featureTop[i] && height > ground[i] + 0.15f)
						{
							featureTop[i] = height;
							feature[i] = kind;
							colour[i] = Shade(kind, normal);
						}
					});
				}
				// A dark edge round every structure, so a base reads as a base and not as a smudge.
				var edged = new bool[colour.Length];
				for (int y = 1; y < Size - 1; y++)
				{
					for (int x = 1; x < Size - 1; x++)
					{
						int i = y * Size + x;
						if (feature[i] != Feature.Structure && feature[i] != Feature.Sandbag && feature[i] != Feature.Wood)
						{
							continue;
						}
						if (feature[i - 1] == Feature.None || feature[i + 1] == Feature.None || feature[i - Size] == Feature.None || feature[i + Size] == Feature.None)
						{
							edged[i] = true;
						}
					}
				}
				for (int i = 0; i < colour.Length; i++)
				{
					if (edged[i])
					{
						colour[i] = Color.Lerp(colour[i], Outline, 0.65f);
					}
				}
				return count;
			}

			private static Feature Classify(Renderer renderer)
			{
				string name = renderer.name.ToLowerInvariant();
				string root = renderer.transform.root.name.ToLowerInvariant();
				// No material is an invisible helper (Level Bounds is a box nobody sees).
				if (renderer.sharedMaterial == null || renderer.GetComponentInParent<LevelBounds>() != null
					|| renderer.GetComponentInParent<WaterLevel>() != null || name.Contains("water") || name.Contains("river")
					|| name.Contains("pine") || name.Contains("fern") || name.Contains("grass") || name.Contains("blueberry")
					|| name.Contains("flower") || name.Contains("bush") || name.Contains("tree") || name.Contains("sky")
					|| name.Contains("cover point") || name.Contains("red cross") || root.Contains("pathfinding")
					|| renderer.GetComponentInParent<Actor>() != null || renderer.GetComponentInParent<Vehicle>() != null
					|| renderer.GetComponentInParent<CapturePoint>() != null)
				{
					return Feature.None;
				}
				if (name.Contains("mountain") || name.Contains("rock") || name.Contains("stone") || name.Contains("cliff"))
				{
					return name.StartsWith("bf_") ? Feature.Structure : Feature.Rock;
				}
				if (name.Contains("sandbag") || name.Contains("sack") || name.Contains("hesco"))
				{
					return Feature.Sandbag;
				}
				if (name.Contains("bridge") || name.Contains("plank") || name.Contains("log") || name.Contains("beam")
					|| name.Contains("wood") || name.Contains("ladder") || name.Contains("dock") || name.Contains("pier"))
				{
					return Feature.Wood;
				}
				return Feature.Structure;
			}

			private static Color Shade(Feature kind, Vector3 normal)
			{
				float lit = 0.6f + 0.55f * Mathf.Clamp01(Vector3.Dot(normal, Light));
				switch (kind)
				{
				case Feature.Rock:
					return Scale(StoneFeature, lit);
				case Feature.Sandbag:
					return Scale(Sandbag, 0.82f + 0.25f * lit);
				case Feature.Wood:
					return Scale(Wood, 0.82f + 0.25f * lit);
				default:
					return Scale(Structure, 0.8f + 0.3f * lit);
				}
			}

			/// <summary>Every tree's crown: a shadow to the south-east, the crown, a lit north-west side.</summary>
			public void Trees(Terrain terrain)
			{
				TerrainData data = terrain.terrainData;
				Vector3 terrainOrigin = terrain.GetPosition();
				TreePrototype[] prototypes = data.treePrototypes;
				var crownRadius = new float[prototypes.Length];
				var isRock = new bool[prototypes.Length];
				for (int p = 0; p < prototypes.Length; p++)
				{
					crownRadius[p] = CrownRadius(prototypes[p].prefab);
					isRock[p] = prototypes[p].prefab != null && prototypes[p].prefab.name.ToLowerInvariant().Contains("rock");
				}
				var crowns = new List<Vector3>(data.treeInstanceCount + 64);
				var stones = new List<Vector3>();
				foreach (TreeInstance tree in data.treeInstances)
				{
					Vector3 at = Vector3.Scale(tree.position, data.size) + terrainOrigin;
					int p = Mathf.Clamp(tree.prototypeIndex, 0, crownRadius.Length - 1);
					float radius = crownRadius[p] * tree.widthScale;
					// The terrain's "trees" include boulders painted with the same brush.
					(isRock[p] ? stones : crowns).Add(new Vector3(at.x, isRock[p] ? radius * 0.8f : radius, at.z));
				}
				foreach (Vector3 stone in stones)
				{
					Disc(stone.x, stone.z, stone.y, StoneFeature, 0.85f);
				}
				// Trees placed as scene objects rather than painted on the terrain.
				foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
				{
					string name = renderer.name.ToLowerInvariant();
					if (renderer.enabled && name.Contains("pine") && Overlaps(renderer.bounds))
					{
						Bounds b = renderer.bounds;
						crowns.Add(new Vector3(b.center.x, 0.38f * Mathf.Max(b.size.x, b.size.z), b.center.z));
					}
				}
				Woodland(crowns);
				float shadowShift = 0.3f;
				float lightShift = 0.28f;
				foreach (Vector3 crown in crowns)
				{
					Disc(crown.x + crown.y * shadowShift, crown.z - crown.y * shadowShift, crown.y * 1.05f, CrownShadow, 0.45f);
				}
				foreach (Vector3 crown in crowns)
				{
					Disc(crown.x, crown.z, crown.y, Crown, 0.92f);
				}
				foreach (Vector3 crown in crowns)
				{
					Disc(crown.x - crown.y * lightShift, crown.z + crown.y * lightShift, crown.y * 0.5f, CrownLight, 0.55f);
				}
			}

			/// <summary>
			/// Tints the ground under dense trees darker, so a wood reads as a wood and not as a
			/// scatter of dots: crowns counted on an 8 m grid, blurred, mapped to a tint.
			/// </summary>
			private void Woodland(List<Vector3> crowns)
			{
				const float cell = 8f;
				int cells = Mathf.CeilToInt(Size * metresPerPixel / cell);
				var density = new float[cells * cells];
				foreach (Vector3 crown in crowns)
				{
					int cx = Mathf.FloorToInt((crown.x - origin.x) / cell);
					int cz = Mathf.FloorToInt((crown.z - origin.y) / cell);
					if (cx >= 0 && cz >= 0 && cx < cells && cz < cells)
					{
						density[cz * cells + cx] += crown.y * crown.y;
					}
				}
				density = Blur(density, cells, 2);
				density = Blur(density, cells, 2);
				for (int y = 0; y < Size; y++)
				{
					for (int x = 0; x < Size; x++)
					{
						int i = y * Size + x;
						if (feature[i] != Feature.None || IsWater(i) || float.IsNegativeInfinity(ground[i]))
						{
							continue;
						}
						Vector2 world = World(x, y);
						int cx = Mathf.Clamp(Mathf.FloorToInt((world.x - origin.x) / cell), 0, cells - 1);
						int cz = Mathf.Clamp(Mathf.FloorToInt((world.y - origin.y) / cell), 0, cells - 1);
						// Crown area per cell area: about 1 where the canopy closes.
						float cover = Mathf.Clamp01(density[cz * cells + cx] * Mathf.PI / (cell * cell));
						colour[i] = Color.Lerp(colour[i], Scale(ForestFloor, colour[i].grayscale / Grass.grayscale), 0.75f * Mathf.SmoothStep(0f, 1f, cover));
					}
				}
			}

			private static float[] Blur(float[] source, int cells, int radius)
			{
				var horizontal = new float[source.Length];
				var result = new float[source.Length];
				float weight = 1f / (radius * 2 + 1);
				for (int z = 0; z < cells; z++)
				{
					for (int x = 0; x < cells; x++)
					{
						float sum = 0f;
						for (int k = -radius; k <= radius; k++)
						{
							sum += source[z * cells + Mathf.Clamp(x + k, 0, cells - 1)];
						}
						horizontal[z * cells + x] = sum * weight;
					}
				}
				for (int z = 0; z < cells; z++)
				{
					for (int x = 0; x < cells; x++)
					{
						float sum = 0f;
						for (int k = -radius; k <= radius; k++)
						{
							sum += horizontal[Mathf.Clamp(z + k, 0, cells - 1) * cells + x];
						}
						result[z * cells + x] = sum * weight;
					}
				}
				return result;
			}

			private static float CrownRadius(GameObject prefab)
			{
				if (prefab == null)
				{
					return 3f;
				}
				// The meshes' own bounds: a prefab asset's Renderer.bounds is empty until it is placed.
				float width = 0f;
				foreach (MeshFilter filter in prefab.GetComponentsInChildren<MeshFilter>())
				{
					if (filter.sharedMesh == null)
					{
						continue;
					}
					Vector3 size = Vector3.Scale(filter.sharedMesh.bounds.size, filter.transform.lossyScale);
					width = Mathf.Max(width, Mathf.Max(size.x, size.z));
				}
				return width > 0f ? Mathf.Clamp(0.36f * width, 1f, 6f) : 3f;
			}

			/// <summary>Dims everything outside the play area and draws its edge.</summary>
			public void PlayArea(Bounds box)
			{
				for (int y = 0; y < Size; y++)
				{
					for (int x = 0; x < Size; x++)
					{
						int i = y * Size + x;
						Vector2 world = World(x, y);
						float outside = Mathf.Max(
							Mathf.Max(box.min.x - world.x, world.x - box.max.x),
							Mathf.Max(box.min.z - world.y, world.y - box.max.z));
						if (outside > 0f)
						{
							Color c = colour[i];
							float grey = c.grayscale;
							colour[i] = Scale(Color.Lerp(c, new Color(grey, grey, grey), 0.55f), 0.7f);
						}
						if (Mathf.Abs(outside) < metresPerPixel * 0.75f)
						{
							colour[i] = Color.Lerp(colour[i], new Color(0.92f, 0.9f, 0.82f), 0.6f);
						}
					}
				}
			}

			public byte[] Encode()
			{
				var texture = new Texture2D(Size, Size, TextureFormat.RGB24, false);
				texture.SetPixels(colour);
				texture.Apply();
				byte[] png = texture.EncodeToPNG();
				Object.DestroyImmediate(texture);
				return png;
			}

			private bool Overlaps(Bounds bounds)
			{
				Vector2 min = Pixel(bounds.min);
				Vector2 max = Pixel(bounds.max);
				return max.x >= 0f && max.y >= 0f && min.x < Size && min.y < Size;
			}

			private void Disc(float worldX, float worldZ, float radius, Color ink, float alpha)
			{
				Vector2 at = Pixel(new Vector3(worldX, 0f, worldZ));
				float r = radius / metresPerPixel;
				int x0 = Mathf.Max(0, Mathf.FloorToInt(at.x - r - 1f));
				int x1 = Mathf.Min(Size - 1, Mathf.CeilToInt(at.x + r + 1f));
				int y0 = Mathf.Max(0, Mathf.FloorToInt(at.y - r - 1f));
				int y1 = Mathf.Min(Size - 1, Mathf.CeilToInt(at.y + r + 1f));
				for (int y = y0; y <= y1; y++)
				{
					for (int x = x0; x <= x1; x++)
					{
						int i = y * Size + x;
						if (feature[i] != Feature.None || IsWater(i))
						{
							continue;
						}
						float d = Mathf.Sqrt((x - at.x) * (x - at.x) + (y - at.y) * (y - at.y));
						float coverage = Mathf.Clamp01(r - d + 0.5f) * alpha;
						if (coverage > 0f)
						{
							colour[i] = Color.Lerp(colour[i], ink, coverage);
						}
					}
				}
			}

			/// <summary>Fills a mesh's triangles from above, calling back per pixel with height and face normal.</summary>
			private void Rasterise(Mesh mesh, Matrix4x4 toWorld, System.Action<int, float, Vector3> plot)
			{
				Vector3[] vertices = mesh.vertices;
				int[] triangles = mesh.triangles;
				var world = new Vector3[vertices.Length];
				var pixel = new Vector2[vertices.Length];
				for (int v = 0; v < vertices.Length; v++)
				{
					world[v] = toWorld.MultiplyPoint3x4(vertices[v]);
					pixel[v] = Pixel(world[v]);
				}
				for (int t = 0; t + 2 < triangles.Length; t += 3)
				{
					int a = triangles[t], b = triangles[t + 1], c = triangles[t + 2];
					Vector3 normal = Vector3.Cross(world[b] - world[a], world[c] - world[a]);
					if (normal.sqrMagnitude < 1e-10f)
					{
						continue;
					}
					normal.Normalize();
					if (normal.y < 0f)
					{
						normal = -normal;
					}
					Vector2 pa = pixel[a], pb = pixel[b], pc = pixel[c];
					int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(pa.x, Mathf.Min(pb.x, pc.x))));
					int x1 = Mathf.Min(Size - 1, Mathf.CeilToInt(Mathf.Max(pa.x, Mathf.Max(pb.x, pc.x))));
					int y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(pa.y, Mathf.Min(pb.y, pc.y))));
					int y1 = Mathf.Min(Size - 1, Mathf.CeilToInt(Mathf.Max(pa.y, Mathf.Max(pb.y, pc.y))));
					if (x1 < x0 || y1 < y0)
					{
						continue;
					}
					float area = (pb.x - pa.x) * (pc.y - pa.y) - (pc.x - pa.x) * (pb.y - pa.y);
					if (Mathf.Abs(area) < 1e-6f)
					{
						continue;
					}
					for (int y = y0; y <= y1; y++)
					{
						for (int x = x0; x <= x1; x++)
						{
							float wa = ((pb.x - x) * (pc.y - y) - (pc.x - x) * (pb.y - y)) / area;
							float wb = ((pc.x - x) * (pa.y - y) - (pa.x - x) * (pc.y - y)) / area;
							float wc = 1f - wa - wb;
							if (wa < -0.001f || wb < -0.001f || wc < -0.001f)
							{
								continue;
							}
							float height = wa * world[a].y + wb * world[b].y + wc * world[c].y;
							plot(y * Size + x, height, normal);
						}
					}
				}
			}

			private static Color Scale(Color c, float k) => new Color(Mathf.Clamp01(c.r * k), Mathf.Clamp01(c.g * k), Mathf.Clamp01(c.b * k), 1f);
		}
	}
}
