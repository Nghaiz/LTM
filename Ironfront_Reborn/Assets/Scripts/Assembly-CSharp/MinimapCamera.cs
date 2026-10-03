using System;
using UnityEngine;
using UnityEngine.Rendering;

public class MinimapCamera : MonoBehaviour
{
	// 2048, not the original 1024: the in-match map zooms in to a third of the picture, which at
	// 1024 would draw each texel over three screen pixels.
	private const int RESOLUTION = 2048;

	public static MinimapCamera instance;

	[NonSerialized]
	public Camera camera;

	[NonSerialized]
	public RenderTexture minimapRenderTexture;

	/// <summary>
	/// How much of each edge the framing leaves empty around the outermost spawn point.
	/// </summary>
	/// <remarks>
	/// Not zero: an icon has a width, and a capture point pinned to viewport 0.0 would be drawn
	/// half off the minimap. 0.08 puts Dustbowl's outermost point at viewport 0.08/0.92 with the
	/// icon whole.
	/// </remarks>
	[Range(0f, 0.4f)]
	public float frameMargin = 0.08f;

	/// <summary>
	/// Metres of ground kept round the outermost flag when the map frames its objectives instead of
	/// its whole play volume; 0 frames the play volume.
	/// </summary>
	/// <remarks>
	/// Owner report 2026-10-03 on Forest Lake: framed on its 2000 m play volume, the flags, the lake
	/// and the quarry filled the middle half of the map and everything was small. Narrowing to the
	/// flags plus a margin shows the same ground at about 1.4 times the size. The frame never grows
	/// past the play-volume frame and stays inside it; a vehicle beyond the narrowed frame keeps its
	/// icon on the map's edge, as <c>ActorBlip</c> already does past the terrain.
	/// </remarks>
	[Min(0f)]
	public float objectiveFrameMargin;

	/// <summary>
	/// A picture of the map drawn ahead of time by <c>Ironfront/Maps/Bake minimap picture</c>, used
	/// instead of a live render of the scene when it was drawn for this camera's frame.
	/// </summary>
	/// <remarks>
	/// The live render is a camera shot of the level from above: on Forest Lake it showed flat
	/// sand, no forest (the trees are drawn by the GPU instancer, which a one-off render misses),
	/// magenta where a shader was missing and snow glare at the edges (owner report 2026-10-03:
	/// "trông như mặt cắt"). The baked picture is drawn from the terrain, water, trees and
	/// structures themselves.
	/// </remarks>
	public Texture2D bakedPicture;

	/// <summary>The frame <see cref="bakedPicture"/> was drawn for: its centre (x, z)...</summary>
	public Vector2 bakedCentre;

	/// <summary>...and its half width in metres.</summary>
	public float bakedHalfSpan;

	/// <summary>Metres a frame may move before a baked picture no longer fits it.</summary>
	private const float BakedFrameTolerance = 0.5f;

	private bool usingBakedPicture;

	private void Awake()
	{
		instance = this;
		camera = GetComponent<Camera>();
		// Framed even with no graphics device: ActorBlip and MinimapMarker read this camera's
		// viewport wherever an actor exists. Aspect 1, as the square target below would give it.
		if (!CanRender)
		{
			camera.aspect = 1f;
			FrameThePlayableArea();
			return;
		}
		if (bakedPicture != null)
		{
			camera.aspect = 1f;
			FrameThePlayableArea();
			if (BakedPictureFits())
			{
				usingBakedPicture = true;
				camera.enabled = false;
				return;
			}
			Debug.LogWarning(
				"[minimap] the baked picture was drawn for a frame centred " + bakedCentre.ToString("F1")
				+ ", half width " + bakedHalfSpan.ToString("F1") + " m, and the map now frames "
				+ new Vector2(base.transform.position.x, base.transform.position.z).ToString("F1") + ", "
				+ camera.orthographicSize.ToString("F1") + " m; drawing it live instead. "
				+ "Bake it again: Ironfront/Maps/Bake minimap picture.");
		}
		minimapRenderTexture = new RenderTexture(RESOLUTION, RESOLUTION, 16);
		// Mip-mapped: the whole map is drawn at under half its texel size, and without mips the
		// minified picture shimmers into noise.
		minimapRenderTexture.useMipMap = true;
		minimapRenderTexture.autoGenerateMips = true;
		minimapRenderTexture.filterMode = FilterMode.Trilinear;
		camera.targetTexture = minimapRenderTexture;

		// Assigned BEFORE the framing: a camera rendering into a square target has aspect 1, so
		// the vertical field of view computed below is also the horizontal one. Framing first
		// would size against whatever aspect the game window happened to have.
		FrameThePlayableArea();
	}

	/// <summary>
	/// Centres and zooms this camera on the map's <see cref="LevelBounds"/>, or on its spawn
	/// points when it authors no bounds. P3 task 3.5; see <see cref="FrameTheLevelBounds"/> for
	/// why the bounds come first.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Measured, not authored.</b> Dustbowl's minimap camera was authored at a 22 degree
	/// field of view centred on (1500, 1419), which covers 1564 m of a 3000 m terrain — while
	/// the six capture points span 997 m by 860 m centred on (1587, 1385). So the playable area
	/// filled 64% of the minimap's width and 55% of its height, and the margin was lopsided:
	/// 0.233 of the frame wasted on one side against 0.129 on the other. The complaint was that
	/// the world drawn on the minimap is too small, and that is the arithmetic of it.
	/// </para>
	/// <para>
	/// <b>Why this is code and not a scene value.</b> A hand-tuned camera is correct for exactly
	/// one map and silently wrong for the next, and nothing would report it — the failure is a
	/// minimap that looks fine and is framed on empty desert. Reading the framing off the spawn
	/// points the level already authors means a new map is framed by construction
	/// (<c>rules/replicate-and-automate.md</c>, <c>rules/code-conventions.md</c> § Data-Driven).
	/// </para>
	/// <para>
	/// <b>Spawn points are the right subject.</b> <c>CapturePoint</c> extends
	/// <see cref="SpawnPoint"/>, so this bounds every objective AND every place a player can
	/// enter the world — which is the definition of the area a minimap is for. Terrain size is
	/// not: Dustbowl's terrain is 3000 m square and the match happens in the middle third of it.
	/// </para>
	/// <para>
	/// <b>Nothing else needs to change with it.</b> Every icon on this minimap —
	/// <c>ActorBlip</c>, <c>MinimapMarker</c>, and <c>MinimapUi</c>'s spawn buttons — positions
	/// itself with <c>WorldToViewportPoint</c> against THIS camera, so they follow the new
	/// framing without knowing it moved.
	/// </para>
	/// <para>
	/// <b>A map with no spawn points keeps its authored framing.</b> That is a test scene or a
	/// menu backdrop, and guessing at a framing for it would be worse than leaving the one a
	/// human chose.
	/// </para>
	/// </remarks>
	private void FrameThePlayableArea()
	{
		// Unqualified: this file carries `using System;`, so a bare `Object` is ambiguous.
		LevelBounds levelBounds = FindFirstObjectByType<LevelBounds>();
		if (levelBounds != null)
		{
			FrameTheLevelBounds(levelBounds.WorldBox);
			return;
		}

		SpawnPoint[] spawnPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None);
		if (spawnPoints.Length == 0)
		{
			return;
		}

		float minX = float.MaxValue, maxX = float.MinValue;
		float minZ = float.MaxValue, maxZ = float.MinValue;
		float sumY = 0f;

		foreach (SpawnPoint spawnPoint in spawnPoints)
		{
			Vector3 p = spawnPoint.transform.position;
			minX = Mathf.Min(minX, p.x);
			maxX = Mathf.Max(maxX, p.x);
			minZ = Mathf.Min(minZ, p.z);
			maxZ = Mathf.Max(maxZ, p.z);
			sumY += p.y;
		}

		// The square that holds both axes, because the render target is square.
		float halfSpan = Mathf.Max(maxX - minX, maxZ - minZ) * 0.5f;
		if (halfSpan <= 0f)
		{
			// One spawn point, or several stacked. There is no extent to frame.
			return;
		}

		halfSpan /= Mathf.Max(0.2f, 1f - 2f * frameMargin);

		// Mean spawn height, not zero and not the camera's own altitude: a perspective camera
		// frames a PLANE, and the plane the icons sit on is the ground under the spawn points.
		// Dustbowl's range from the Oasis at y=9 to the Fortress at y=103 is 2% of the throw,
		// which is why a mean is enough and a per-point correction would be noise.
		float groundY = sumY / spawnPoints.Length;

		Vector3 position = base.transform.position;
		position.x = (minX + maxX) * 0.5f;
		position.z = (minZ + maxZ) * 0.5f;
		base.transform.position = position;

		if (camera.orthographic)
		{
			camera.orthographicSize = halfSpan;
			return;
		}

		float distance = position.y - groundY;
		if (distance <= 1f)
		{
			// The camera is at or below the ground it is meant to be looking down on. Whatever
			// that scene is, a field of view derived from it would be meaningless.
			Debug.LogWarning(
				"[minimap] the minimap camera sits " + distance.ToString("F0")
				+ " m above the mean spawn height, so it cannot be framed by field of view. "
				+ "Raise it above the terrain, or make it orthographic.");
			return;
		}

		camera.fieldOfView = 2f * Mathf.Atan(halfSpan / distance) * Mathf.Rad2Deg;
	}

	/// <summary>
	/// Room each edge keeps around <see cref="LevelBounds"/> so an icon pinned at the boundary
	/// is still drawn whole.
	/// </summary>
	private const float LevelBoundsIconMargin = 0.03f;

	/// <summary>Height above the top of the box the camera looks down from.</summary>
	private const float LevelBoundsClearance = 10f;

	/// <summary>
	/// Frames the whole authored play volume, straight down and orthographic.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Why the play volume and not the spawn points.</b> The spawn-point framing below was
	/// written for Dustbowl, whose points sit in the middle of a much larger map, and on Island it
	/// framed 326 m of a 700 m play area -- tighter than the original's own 447 m. The island's
	/// edges and every metre of sea around it fell off the minimap, so a boat circling the island
	/// lost its icon as soon as it left the shore. <see cref="LevelBounds"/> is the box the server
	/// clamps every vehicle into (<c>Vehicle.KeepInsideLevelBounds</c>), so it is exactly the set
	/// of places an icon can be. On Dustbowl that is 1700 m, a little wider than the authored
	/// 1564 m.
	/// </para>
	/// <para>
	/// <b>Clipped to the ground drawn under it.</b> On Island the box is 700 m and the terrain
	/// 540 m, so framing the whole box drew the terrain as a small square inside a see-through
	/// border. The frame is now the part of the box with terrain under it, never wider than the
	/// terrain: Island frames its 540 m terrain square exactly, Dustbowl -- whose terrain holds
	/// the box with room to spare -- is unchanged. An icon whose owner is out beyond the terrain
	/// is pinned to the minimap's edge by <c>ActorBlip</c> instead of clipped away.
	/// </para>
	/// <para>
	/// <b>Why orthographic.</b> A perspective camera draws a point at altitude further from the
	/// centre than the ground under it -- from Island's authored camera, 552 m above the water, a
	/// helicopter 300 m up is drawn 2.2 times as far from the centre as the ground below it -- so
	/// even a map framed wide enough would push high flyers off it. Straight down and orthographic, every icon sits over its ground position,
	/// which is what <c>ActorBlip</c> and <c>MinimapMarker</c> assume when they rotate icons by
	/// yaw alone.
	/// </para>
	/// </remarks>
	private void FrameTheLevelBounds(Bounds box)
	{
		LevelFrame(box, objectiveFrameMargin, out Vector2 centre, out float halfSpan);
		base.transform.SetPositionAndRotation(
			new Vector3(centre.x, box.max.y + LevelBoundsClearance, centre.y),
			Quaternion.Euler(90f, 0f, 0f));

		camera.orthographic = true;
		camera.orthographicSize = halfSpan;
		camera.nearClipPlane = LevelBoundsClearance * 0.5f;
		camera.farClipPlane = box.size.y + LevelBoundsClearance * 2f;
	}

	/// <summary>
	/// The square, in world x and z, that the minimap frames for the scene's
	/// <see cref="LevelBounds"/> (and its <see cref="objectiveFrameMargin"/>); false for a map
	/// without one. What a baked picture is drawn for.
	/// </summary>
	public static bool TryGetLevelFrame(out Vector2 centre, out float halfSpan)
	{
		LevelBounds levelBounds = FindFirstObjectByType<LevelBounds>();
		if (levelBounds == null)
		{
			centre = Vector2.zero;
			halfSpan = 0f;
			return false;
		}
		MinimapCamera camera = FindFirstObjectByType<MinimapCamera>();
		LevelFrame(levelBounds.WorldBox, camera != null ? camera.objectiveFrameMargin : 0f, out centre, out halfSpan);
		return true;
	}

	private static void LevelFrame(Bounds box, float objectiveMargin, out Vector2 centre, out float halfSpan)
	{
		PlayVolumeFrame(box, out centre, out halfSpan);
		if (objectiveMargin > 0f)
		{
			NarrowToObjectives(objectiveMargin, ref centre, ref halfSpan);
		}
	}

	/// <summary>
	/// The square round every spawn point plus <paramref name="margin"/>, when that is smaller than
	/// the frame it narrows; moved only as far as it must to stay inside that frame.
	/// </summary>
	private static void NarrowToObjectives(float margin, ref Vector2 centre, ref float halfSpan)
	{
		SpawnPoint[] spawnPoints = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None);
		if (spawnPoints.Length == 0)
		{
			return;
		}
		float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
		foreach (SpawnPoint spawnPoint in spawnPoints)
		{
			Vector3 p = spawnPoint.transform.position;
			minX = Mathf.Min(minX, p.x);
			maxX = Mathf.Max(maxX, p.x);
			minZ = Mathf.Min(minZ, p.z);
			maxZ = Mathf.Max(maxZ, p.z);
		}
		float narrowed = Mathf.Max(maxX - minX, maxZ - minZ) * 0.5f + margin;
		if (narrowed >= halfSpan)
		{
			return;
		}
		float slack = halfSpan - narrowed;
		centre = new Vector2(
			Mathf.Clamp((minX + maxX) * 0.5f, centre.x - slack, centre.x + slack),
			Mathf.Clamp((minZ + maxZ) * 0.5f, centre.y - slack, centre.y + slack));
		halfSpan = narrowed;
	}

	private static void PlayVolumeFrame(Bounds box, out Vector2 centre, out float halfSpan)
	{
		float minX = box.min.x;
		float maxX = box.max.x;
		float minZ = box.min.z;
		float maxZ = box.max.z;
		halfSpan = Mathf.Max(box.size.x, box.size.z) * 0.5f / (1f - 2f * LevelBoundsIconMargin);

		// Only where ground is drawn. Island's play volume is 700 m of which the terrain covers
		// 540 m, and outside the terrain there is nothing but semi-transparent water over this
		// camera's transparent clear colour: the minimap drew the map as a small square inside a
		// thick see-through border (owner report 2026-09-28). Framing is clipped to the terrain,
		// never grown past it; ActorBlip keeps an icon on the edge when its owner goes beyond.
		if (TryGetGroundExtent(out float groundMinX, out float groundMaxX, out float groundMinZ, out float groundMaxZ))
		{
			minX = Mathf.Max(minX, groundMinX);
			maxX = Mathf.Min(maxX, groundMaxX);
			minZ = Mathf.Max(minZ, groundMinZ);
			maxZ = Mathf.Min(maxZ, groundMaxZ);
			if (maxX > minX && maxZ > minZ)
			{
				float centreX = (minX + maxX) * 0.5f;
				float centreZ = (minZ + maxZ) * 0.5f;
				float insideGround = Mathf.Min(
					Mathf.Min(centreX - groundMinX, groundMaxX - centreX),
					Mathf.Min(centreZ - groundMinZ, groundMaxZ - centreZ));
				halfSpan = Mathf.Min(
					Mathf.Max(maxX - minX, maxZ - minZ) * 0.5f / (1f - 2f * LevelBoundsIconMargin),
					insideGround);
			}
			else
			{
				minX = box.min.x;
				maxX = box.max.x;
				minZ = box.min.z;
				maxZ = box.max.z;
			}
		}

		centre = new Vector2((minX + maxX) * 0.5f, (minZ + maxZ) * 0.5f);
	}

	/// <summary>
	/// The ground plan the map draws: the union of every terrain's square in the scene.
	/// </summary>
	/// <remarks>
	/// <c>FindObjectsByType</c> rather than <c>Terrain.activeTerrains</c>, which only lists a terrain
	/// once its own <c>OnEnable</c> has run -- not guaranteed before this <c>Awake</c>. Water does
	/// not count: its plane is kilometres wide and drawn semi-transparent.
	/// </remarks>
	private static bool TryGetGroundExtent(out float minX, out float maxX, out float minZ, out float maxZ)
	{
		minX = float.MaxValue;
		maxX = float.MinValue;
		minZ = float.MaxValue;
		maxZ = float.MinValue;
		bool found = false;
		foreach (Terrain terrain in FindObjectsByType<Terrain>(FindObjectsSortMode.None))
		{
			if (terrain.terrainData == null)
			{
				continue;
			}
			Vector3 origin = terrain.GetPosition();
			Vector3 size = terrain.terrainData.size;
			minX = Mathf.Min(minX, origin.x);
			maxX = Mathf.Max(maxX, origin.x + size.x);
			minZ = Mathf.Min(minZ, origin.z);
			maxZ = Mathf.Max(maxZ, origin.z + size.z);
			found = true;
		}
		return found;
	}

	/// <summary>
	/// Whether this process can draw the map at all.
	/// </summary>
	/// <remarks>
	/// A dedicated server has no graphics device, and rendering this camera there at Start put two
	/// "Built-in Resource Error: dereference potentially before
	/// BuiltinResourceManager::InitializeAllResources()" lines and three "Trying to access a shader
	/// but no shaders were included in the build" lines into every server log (found with
	/// -stackTraceLogType Full: each came from Camera.Render), behind a 2048x2048 target nobody
	/// looks at. ReflectionProber makes the same test for the same reason.
	/// </remarks>
	private static bool CanRender => SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null;

	private void Start()
	{
		if (!CanRender || usingBakedPicture)
		{
			camera.enabled = false;
			return;
		}
		Render();
	}

	private void Render()
	{
		bool fog = RenderSettings.fog;
		RenderSettings.fog = false;
		camera.Render();
		RenderSettings.fog = fog;
		camera.enabled = false;
	}

	public Texture Minimap()
	{
		return usingBakedPicture ? bakedPicture : minimapRenderTexture;
	}

	/// <summary>Whether <see cref="bakedPicture"/> was drawn for the frame this camera now holds.</summary>
	private bool BakedPictureFits()
	{
		Vector3 position = base.transform.position;
		return camera.orthographic
			&& Mathf.Abs(position.x - bakedCentre.x) <= BakedFrameTolerance
			&& Mathf.Abs(position.z - bakedCentre.y) <= BakedFrameTolerance
			&& Mathf.Abs(camera.orthographicSize - bakedHalfSpan) <= BakedFrameTolerance;
	}
}
