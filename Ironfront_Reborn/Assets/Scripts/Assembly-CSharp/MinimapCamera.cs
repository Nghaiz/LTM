using System;
using UnityEngine;

public class MinimapCamera : MonoBehaviour
{
	private const int RESOLUTION = 1024;

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

	private void Awake()
	{
		instance = this;
		camera = GetComponent<Camera>();
		minimapRenderTexture = new RenderTexture(1024, 1024, 16);
		camera.targetTexture = minimapRenderTexture;

		// Assigned BEFORE the framing: a camera rendering into a square target has aspect 1, so
		// the vertical field of view computed below is also the horizontal one. Framing first
		// would size against whatever aspect the game window happened to have.
		FrameThePlayableArea();
	}

	/// <summary>
	/// Centres and zooms this camera on the map's spawn points. P3 task 3.5.
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

	private void Start()
	{
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
		return minimapRenderTexture;
	}
}
