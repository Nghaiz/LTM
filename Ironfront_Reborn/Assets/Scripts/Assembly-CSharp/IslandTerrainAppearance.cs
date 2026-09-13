using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Restores the missing green ground surface on the imported Island terrain.</summary>
internal static class IslandTerrainAppearance
{
	private const int TextureSize = 128;

	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
	private static void Install()
	{
		// The player boots through Menu and loads Island later. AfterSceneLoad only runs for that
		// first scene, so checking the active scene there leaves Island untouched. Subscribe to
		// every actual scene transition and apply when the named map has finished loading.
		SceneManager.sceneLoaded -= OnSceneLoaded;
		SceneManager.sceneLoaded += OnSceneLoaded;
	}

	private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
	{
		if (scene.name != "Island") return;

		Terrain terrain = Terrain.activeTerrain;
		if (terrain == null || terrain.terrainData == null) return;

		// Island's extracted TerrainData retained heights and grass details but lost a usable
		// diffuse terrain layer in the Unity 6 import. The default terrain shader therefore draws
		// the entire heightmap white. Build a deterministic grass/soil albedo at load time so both
		// server and clients use the same geometry while rendered clients get the intended green
		// island instead of a snow-white fallback.
		Texture2D albedo = new Texture2D(TextureSize, TextureSize, TextureFormat.RGB24, true)
		{
			name = "Island Grass Albedo (runtime)",
			wrapMode = TextureWrapMode.Repeat,
			filterMode = FilterMode.Bilinear,
			hideFlags = HideFlags.DontSave
		};

		Color[] pixels = new Color[TextureSize * TextureSize];
		for (int y = 0; y < TextureSize; y++)
		{
			for (int x = 0; x < TextureSize; x++)
			{
				float broad = Mathf.PerlinNoise(x * 0.055f + 19.3f, y * 0.055f + 7.1f);
				float fine = Mathf.PerlinNoise(x * 0.23f + 41.7f, y * 0.23f + 13.9f);
				float variation = broad * 0.72f + fine * 0.28f;
				pixels[y * TextureSize + x] = Color.Lerp(
					new Color(0.19f, 0.27f, 0.09f),
					new Color(0.39f, 0.48f, 0.17f),
					variation);
			}
		}

		albedo.SetPixels(pixels);
		albedo.Apply(true, true);

		TerrainLayer grass = new TerrainLayer();
		grass.name = "Island Grass (runtime)";
		grass.diffuseTexture = albedo;
		grass.tileSize = new Vector2(14f, 14f);
		grass.metallic = 0f;
		grass.smoothness = 0.08f;
		grass.hideFlags = HideFlags.DontSave;

		TerrainData data = terrain.terrainData;
		data.terrainLayers = new[] { grass };

		float[,,] weights = new float[data.alphamapHeight, data.alphamapWidth, 1];
		for (int y = 0; y < data.alphamapHeight; y++)
		for (int x = 0; x < data.alphamapWidth; x++) weights[y, x, 0] = 1f;
		data.SetAlphamaps(0, 0, weights);
		terrain.Flush();
		Debug.Log("[island-terrain] restored green grass surface");
	}
}
