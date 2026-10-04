using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Ironfront
{
	/// <summary>
	/// Renders the open map at night, as <see cref="NightModeConfig"/> sets it, through the scene's
	/// own post-processed camera: what a player sees, without starting a match.
	/// </summary>
	/// <remarks>
	/// Judges the dark the way it ships, so tune <c>Resources/NightMode/&lt;scene&gt;</c> and the
	/// night prefabs, then shoot again. Leaves the scene changed (the Night child shown, the camera
	/// moved): revert it with <c>git checkout</c> or reopen it without saving.
	/// </remarks>
	public static class NightModePreview
	{
		public const int Width = 1280;
		public const int Height = 720;

		/// <summary>
		/// Shoots from <paramref name="eye"/> (x, z; on the ground at eye height) facing
		/// <paramref name="yaw"/>, with <paramref name="pumpkins"/> carved pumpkins set out ahead,
		/// and returns the path and the picture's mean luminance.
		/// </summary>
		public static string Shot(string file, Vector3 eye, float yaw, float pitch = 4f, int pumpkins = 4, bool nightVision = false)
		{
			string scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().name;
			var config = Resources.Load<NightModeConfig>("NightMode/" + scene);
			if (config == null)
			{
				throw new System.InvalidOperationException(scene + " has no Resources/NightMode config.");
			}
			TimeOfDay time = Object.FindFirstObjectByType<TimeOfDay>();
			time.transform.Find("Day").gameObject.SetActive(false);
			GameObject night = time.transform.Find("Night").gameObject;
			night.SetActive(true);
			TimeOfDay.Atmosphere a = config.atmosphere;
			RenderSettings.ambientMode = AmbientMode.Trilight;
			RenderSettings.ambientSkyColor = a.sky;
			RenderSettings.ambientEquatorColor = a.equator;
			RenderSettings.ambientGroundColor = a.ground;
			RenderSettings.fogColor = a.fog;
			RenderSettings.fogDensity = a.fogDensity * (nightVision ? config.nightVisionFogFactor : 1f);
			RenderSettings.fog = true;
			RenderSettings.skybox = a.skyboxMaterial;
			Light moon = night.transform.Find("Moonlight").GetComponent<Light>();
			moon.intensity = config.moonIntensity;
			moon.shadows = config.moonShadows;
			moon.shadowStrength = config.moonShadowStrength;
			moon.transform.rotation = Quaternion.Euler(config.moonElevation, config.moonBearing, 0f);
			RenderSettings.sun = moon;

			Terrain terrain = Terrain.activeTerrain;
			var root = new GameObject("Night Preview Pumpkins");
			GameObject carved = config.pumpkinPrefabs != null && config.pumpkinPrefabs.Length > 0 ? config.pumpkinPrefabs[0] : null;
			Quaternion facing = Quaternion.Euler(0f, yaw, 0f);
			for (int i = 0; carved != null && i < pumpkins; i++)
			{
				Vector3 p = eye + facing * new Vector3((i % 2 == 0 ? -1f : 1f) * (2.5f + i), 0f, 6f + i * i * 3f);
				p.y = terrain.SampleHeight(p) + terrain.GetPosition().y;
				var pumpkin = (GameObject)PrefabUtility.InstantiatePrefab(carved, root.transform);
				pumpkin.transform.SetPositionAndRotation(p, Quaternion.LookRotation(new Vector3(eye.x - p.x, 0f, eye.z - p.z)));
				foreach (Light candle in pumpkin.GetComponentsInChildren<Light>())
				{
					candle.renderMode = LightRenderMode.ForcePixel;
				}
			}

			Camera camera = Camera.main;
			Vector3 at = eye;
			at.y = terrain.SampleHeight(eye) + terrain.GetPosition().y + 1.7f;
			camera.transform.SetPositionAndRotation(at, Quaternion.Euler(pitch, yaw, 0f));
			var target = new RenderTexture(Width, Height, 24);
			camera.targetTexture = target;
			camera.Render();
			camera.targetTexture = null;
			RenderTexture.active = target;
			var picture = new Texture2D(Width, Height, TextureFormat.RGB24, false);
			picture.ReadPixels(new Rect(0, 0, Width, Height), 0, 0);
			RenderTexture.active = null;
			Directory.CreateDirectory(Path.GetDirectoryName(file));
			File.WriteAllBytes(file, picture.EncodeToPNG());
			Object.DestroyImmediate(root);
			Object.DestroyImmediate(target);
			float sum = 0f;
			foreach (Color c in picture.GetPixels())
			{
				sum += c.grayscale;
			}
			Object.DestroyImmediate(picture);
			return file + ": mean luminance " + (sum / (Width * Height)).ToString("0.000");
		}
	}
}
